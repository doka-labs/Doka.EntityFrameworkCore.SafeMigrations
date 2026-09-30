namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Does not mistake a newly added NULL column for SQL Server NULL-distinct uniqueness.</summary>
    [SqlServerLiveTheory]
    [InlineData(false, 0, SafeMigrationReportStatus.Ready)]
    [InlineData(false, 1, SafeMigrationReportStatus.Ready)]
    [InlineData(false, 2, SafeMigrationReportStatus.Blocked)]
    [InlineData(true, 0, SafeMigrationReportStatus.Ready)]
    [InlineData(true, 1, SafeMigrationReportStatus.Ready)]
    [InlineData(true, 2, SafeMigrationReportStatus.Blocked)]
    public async Task ProjectedNullableUniqueKey_UsesTheBoundedOriginalRowCount(
        bool index,
        int rowCount,
        SafeMigrationReportStatus expected
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        var inserts = rowCount == 0 ? string.Empty
            : "INSERT dbo.projected_null_keys (Id) VALUES "
                + string.Join(", ", Enumerable.Range(1, rowCount).Select(static id => $"({id})")) + ";";

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.projected_null_keys (Id int NOT NULL); " + inserts);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("Code", "projected_null_keys", type: "int", nullable: true);
        if (index)
        {
            builder.CreateIndexIfNotExists("IX_projected_null_keys_Code", "projected_null_keys", ["Code"],
                unique: true);
        }
        else
        {
            builder.AddUniqueConstraintIfNotExists("UQ_projected_null_keys_Code", "projected_null_keys", ["Code"]);
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-projected-null-key"));

        var remainingColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.projected_null_keys') "
            + "AND name = N'Code';");

        // Assert
        Assert.Equal(expected, report.Status);
        Assert.Equal(rowCount <= 1 ? SafeMigrationAction.Apply : SafeMigrationAction.RejectDataBlocked,
            report.Assessments[1].Action);
        Assert.Equal(0, remainingColumns);
    }

    /// <summary>Rechecks an index's declared key width after an accepted projected column expansion.</summary>
    [SqlServerLiveTheory]
    [InlineData(850, SafeMigrationReportStatus.Ready)]
    [InlineData(851, SafeMigrationReportStatus.Blocked)]
    public async Task ProjectedExpandedColumn_IndexUsesTargetByteWidth(
        int length,
        SafeMigrationReportStatus expected
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.projected_width (Code nvarchar(10) NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferentFromModel(
            operation => operation.AlterColumn<string>("Code", "projected_width",
                type: $"nvarchar({length})", maxLength: length, nullable: false,
                oldClrType: typeof(string), oldType: "nvarchar(10)", oldMaxLength: 10),
            SafeMigrationPolicy.RepairIfSafe);
        builder.CreateIndexIfNotExists("IX_projected_width_Code", "projected_width", ["Code"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-projected-width"));

        // Assert
        Assert.Equal(expected, report.Status);
        Assert.Equal(length <= 850 ? SafeMigrationAction.Apply : SafeMigrationAction.RejectUnsupported,
            report.Assessments[1].Action);
    }

    /// <summary>Refuses a new primary key whose newly added target column is physically nullable.</summary>
    [SqlServerLiveFact]
    public async Task ProjectedNullablePrimaryKey_IsRejectedBeforeAnyMutation()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.projected_pk (Id int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("Code", "projected_pk", type: "int", nullable: true);
        builder.AddPrimaryKeyIfNotExists("PK_projected_pk", "projected_pk", ["Code"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-projected-nullable-pk"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[1].Action);
        Assert.Equal("primary_key_nullable_column", report.Assessments[1].AnalysisCode);
    }

    /// <summary>Rechecks target-row uniqueness for a same-name replacement across all key families.</summary>
    [SqlServerLiveTheory]
    [InlineData(0, false, SafeMigrationReportStatus.Ready)]
    [InlineData(0, true, SafeMigrationReportStatus.Blocked)]
    [InlineData(1, false, SafeMigrationReportStatus.Ready)]
    [InlineData(1, true, SafeMigrationReportStatus.Blocked)]
    [InlineData(2, false, SafeMigrationReportStatus.Ready)]
    [InlineData(2, true, SafeMigrationReportStatus.Blocked)]
    public async Task ProjectedSameNameReplacement_UsesTheTargetKeyDataProof(
        int family,
        bool duplicates,
        SafeMigrationReportStatus expected
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        var original = family switch
        {
            0 => "CREATE UNIQUE INDEX K_projected_replace ON dbo.projected_replace (Id);",
            1 => "ALTER TABLE dbo.projected_replace ADD CONSTRAINT K_projected_replace UNIQUE (Id);",
            2 => "ALTER TABLE dbo.projected_replace ADD CONSTRAINT K_projected_replace PRIMARY KEY (Id);",
            _ => throw new UnreachableException(),
        };

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.projected_replace (Id int NOT NULL, Code int NOT NULL); "
            + "INSERT dbo.projected_replace (Id, Code) VALUES (1, 10), (2, "
            + (duplicates ? "10" : "20") + "); " + original);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        switch (family)
        {
            case 0:
                builder.DropIndexIfExists("K_projected_replace", "projected_replace");
                builder.CreateIndexIfNotExists("K_projected_replace", "projected_replace", ["Code"], unique: true);
                break;
            case 1:
                builder.DropUniqueConstraintIfExists("K_projected_replace", "projected_replace");
                builder.AddUniqueConstraintIfNotExists("K_projected_replace", "projected_replace", ["Code"]);
                break;
            case 2:
                builder.DropPrimaryKeyIfExists("K_projected_replace", "projected_replace");
                builder.AddPrimaryKeyIfNotExists("K_projected_replace", "projected_replace", ["Code"]);
                break;
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-projected-replacement"));

        var preservedOldKey = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.indexes i JOIN sys.index_columns ic "
            + "ON ic.object_id = i.object_id AND ic.index_id = i.index_id "
            + "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id "
            + "WHERE i.object_id = OBJECT_ID(N'dbo.projected_replace') AND i.name = N'K_projected_replace' "
            + "AND ic.key_ordinal = 1 AND c.name = N'Id';");

        // Assert
        Assert.Equal(expected, report.Status);
        Assert.Equal(duplicates ? SafeMigrationAction.RejectDataBlocked : SafeMigrationAction.Apply,
            report.Assessments[1].Action);
        Assert.Equal(1, preservedOldKey);
    }

    /// <summary>Ordinary index analysis requires catalog visibility but no target-table SELECT permission.</summary>
    [SqlServerLiveFact]
    public async Task NonUniqueIndexProjection_DoesNotRequestUnnecessaryRowAccess()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.metadata_only_index (Code int NOT NULL); "
            + "CREATE USER metadata_index_user WITHOUT LOGIN; "
            + "GRANT CONNECT TO metadata_index_user; "
            + "GRANT VIEW DEFINITION TO metadata_index_user; "
            + "GRANT ALTER ON OBJECT::dbo.metadata_only_index TO metadata_index_user; "
            + "DENY SELECT ON OBJECT::dbo.metadata_only_index TO metadata_index_user;");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER = N'metadata_index_user';");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("IX_metadata_only_index_Code", "metadata_only_index", ["Code"]);

        // Act
        SafeMigrationRunReport report;
        try
        {
            report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
                context, builder.Operations, new SafeMigrationRunOptions("sqlserver-metadata-only-key"));
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("REVERT;");
        }

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(report.Assessments).Action);
    }
}
