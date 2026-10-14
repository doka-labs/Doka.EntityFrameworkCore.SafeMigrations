namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies member rename evidence SQL Server contracts against isolated databases.</summary>
public sealed class SqlServerSafeMigrationMemberRenameEvidenceIntegrationTests : SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Creates the feature suite with the assembly-owned SQL Server fixture.</summary>
    /// <param name="fixture">The shared fixture providing a database per test.</param>
    public SqlServerSafeMigrationMemberRenameEvidenceIntegrationTests(
        SqlServerContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Mixed member probes retain bit metadata and nullable source values through native UNION ALL.</summary>
    [SqlServerLiveFact]
    public async Task MemberRename_MixedProbeWireTypesRemainBoolean()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.member_rows (id int NOT NULL); "
            + "CREATE INDEX source_index ON dbo.member_rows (id);");

        await using var context = CreateContext(connectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var column = new RenameColumnIntent("id", "member_rows", "missing", "dbo");
        var index = new RenameIndexIntent("source_index", "member_rows", "missing_index", "dbo");
        var replay = new RenameColumnIntent("missing", "member_rows", "id", "dbo");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 0, " + catalog.BuildMemberRenameDestinationExpression(column)
            + ", CAST(NULL AS bit) UNION ALL SELECT 1, " + catalog.BuildMemberRenameDestinationExpression(index)
            + ", " + catalog.BuildIndexRenameSourceSupportedExpression(index)
            + " UNION ALL SELECT 2, " + catalog.BuildMemberRenameDestinationExpression(replay)
            + ", CAST(NULL AS bit) ORDER BY 1;";

        // Act
        await using var reader = await command.ExecuteReaderAsync();
        var targetType = reader.GetFieldType(1);
        var sourceType = reader.GetFieldType(2);
        var rows = new List<(int Ordinal, object Target, object Source)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt32(0), reader.GetValue(1), reader.GetValue(2)));
        }

        // Assert
        Assert.Equal(typeof(bool), targetType);
        Assert.Equal(typeof(bool), sourceType);
        Assert.Equal(3, rows.Count);
        Assert.Equal((0, (object)false, DBNull.Value), rows[0]);
        Assert.Equal((1, (object)false, (object)true), rows[1]);
        Assert.Equal((2, (object)true, DBNull.Value), rows[2]);
    }

    /// <summary>A source created on a brownfield table cannot overwrite an occupied owner-local destination.</summary>
    /// <param name="index">Whether an index rather than a column is renamed.</param>
    /// <param name="sourceExists">Whether the source exists in the immutable catalog.</param>
    /// <param name="targetExists">Whether the destination exists on the same physical table.</param>
    [SqlServerLiveTheory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task MemberRename_BrownfieldCreationUsesIndependentDestination(
        bool index,
        bool sourceExists,
        bool targetExists
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var source = index ? "source_index" : "source_value";
        var target = index ? "target_index" : "target_value";
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.member_rows (id int NOT NULL, decoy int NULL"
            + (!index && sourceExists ? ", source_value int NULL" : string.Empty)
            + (!index && targetExists ? ", target_value int NULL" : string.Empty) + "); "
            + "INSERT INTO dbo.member_rows (id) VALUES (7); "
            + (index && sourceExists ? "CREATE INDEX source_index ON dbo.member_rows (id); " : string.Empty)
            + (index && targetExists ? "CREATE INDEX target_index ON dbo.member_rows (decoy); " : string.Empty));

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (index)
        {
            builder.EnsureIndex(new ExpectedIndexDefinition(source, "member_rows",
                [new ExpectedIndexKeyDefinition("id")]), SafeMigrationPolicy.ThrowIfDifferent);
            builder.RenameIndexIfExists(source, "member_rows", target);
        }
        else
        {
            builder.EnsureColumn("member_rows", new ExpectedColumnDefinition(source, typeof(int), true, "int"),
                policy: SafeMigrationPolicy.ThrowIfDifferent);
            builder.RenameColumnIfExists(source, "member_rows", target);
        }

        var rename = (SafeMigrationOperation)builder.Operations[^1];

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [rename]);
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("member-rename-brownfield"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var targetCount = await ScalarIntAsync(connectionString, index
            ? "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.member_rows', N'U') "
                + "AND name = N'target_index';"
            : "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.member_rows', N'U') "
                + "AND name = N'target_value';");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.member_rows WHERE id = 7;");

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(targetExists, analysis.RenameTargetExists);
        Assert.Equal(index ? sourceExists : (bool?)null, analysis.RenameSourceSupported);
        Assert.Equal(!sourceExists ? SafeMigrationObservedState.Missing
            : targetExists ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching,
            analysis.ObservedState);
        Assert.Equal(targetExists ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(targetExists ? SafeMigrationAction.RejectDifferent : SafeMigrationAction.Apply,
            report.Assessments[^1].Action);
        if (targetExists)
        {
            Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.Equal(1, targetCount);
        Assert.Equal(1, rows);
    }

    /// <summary>An identically named index on another owner is not a SQL Server rename collision.</summary>
    [SqlServerLiveFact]
    public async Task MemberRename_IndexDestinationRemainsOwnerLocal()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.member_rows (id int NOT NULL); CREATE TABLE dbo.other_rows (id int NOT NULL); "
            + "CREATE INDEX target_index ON dbo.other_rows (id);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(new ExpectedIndexDefinition("source_index", "member_rows",
            [new ExpectedIndexKeyDefinition("id")]), SafeMigrationPolicy.ThrowIfDifferent);
        builder.RenameIndexIfExists("source_index", "member_rows", "target_index");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("member-rename-owner-local"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var targets = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE name = N'target_index';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^1].Action);
        Assert.Equal(2, targets);
    }

    /// <summary>Absent-source replay remains NoOp even when the live destination is present.</summary>
    /// <param name="index">Whether an index rather than a column is renamed.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemberRename_AbsentSourceReplayPreservesDestination(bool index)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.member_rows (id int NOT NULL, target_value int NULL); "
            + "INSERT INTO dbo.member_rows VALUES (7, 29); CREATE INDEX target_index ON dbo.member_rows (id);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (index)
        {
            builder.RenameIndexIfExists("source_index", "member_rows", "target_index");
        }
        else
        {
            builder.RenameColumnIfExists("source_value", "member_rows", "target_value");
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("member-rename-replay"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.member_rows WHERE target_value = 29;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(report.Assessments).Action);
        Assert.Equal(1, rows);
    }

    /// <summary>Freeing a destination never converts a constraint's backing index into an independent source.</summary>
    /// <param name="constraint">Whether a primary-key constraint owns the source index.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemberRename_TargetDropRequiresIndependentlySupportedSource(bool constraint)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.member_rows (id int NOT NULL, decoy int NULL"
            + (constraint ? ", CONSTRAINT source_index PRIMARY KEY (id)" : string.Empty)
            + "); INSERT INTO dbo.member_rows (id) VALUES (7); "
            + (constraint ? string.Empty : "CREATE INDEX source_index ON dbo.member_rows (id); ")
            + "CREATE INDEX target_index ON dbo.member_rows (decoy);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("target_index", "member_rows");
        builder.RenameIndexIfExists("source_index", "member_rows", "target_index");
        var rename = (SafeMigrationOperation)builder.Operations[^1];

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [rename]);
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("member-rename-source-kind"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var sourceCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.member_rows', N'U') "
            + "AND name = N'source_index';");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.member_rows WHERE id = 7;");

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
        Assert.True(analysis.RenameTargetExists);
        Assert.Equal(!constraint, analysis.RenameSourceSupported);
        Assert.Equal(constraint ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(constraint ? SafeMigrationAction.RejectDifferent : SafeMigrationAction.Apply,
            report.Assessments[^1].Action);
        if (constraint)
        {
            Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.Equal(constraint ? 1 : 0, sourceCount);
        Assert.Equal(1, rows);
    }
}
