namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Validates populated integer predicates without rejecting NULL's UNKNOWN truth value.</summary>
    [SqlServerLiveTheory]
    [InlineData("[Value] >= 0", "NULL,0,2147483647", null)]
    [InlineData("[Value] >= -9223372036854775808 AND [Value] <= 9223372036854775807",
        "NULL,-9223372036854775808,9223372036854775807", "dbo")]
    [InlineData("[Value] IS NULL OR NOT ([Value] < 0)", "NULL,0,1", "dbo")]
    public async Task PopulatedCheck_AdmitsTrueAndUnknownRowsAndReplays(
        string predicate,
        string values,
        string? schema
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var rows = string.Join(",", values.Split(',').Select(static value => "(" + value + ")"));
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.checked_values(Value bigint NULL); INSERT dbo.checked_values VALUES " + rows + ";");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("CK_checked_values", "checked_values", predicate, schema);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-populated-check"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var trusted = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.checked_values') "
            + "AND name=N'CK_checked_values' AND is_disabled=0 AND is_not_trusted=0;");

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(report.Assessments).Action);
        Assert.Equal(1, trusted);
    }

    /// <summary>A FALSE row blocks the entire CHECK addition without changing the table.</summary>
    [SqlServerLiveFact]
    public async Task PopulatedCheck_FalseRowIsDataBlocked()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.invalid_check_values(Value int NULL); "
            + "INSERT dbo.invalid_check_values VALUES(NULL),(-1);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("CK_invalid_values", "invalid_check_values", "[Value]>=0");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-false-check"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var checks = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.check_constraints "
            + "WHERE parent_object_id=OBJECT_ID(N'dbo.invalid_check_values');");

        // Assert
        Assert.Equal(SafeMigrationObservedState.DataBlocked, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(51003, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(0, checks);
    }

    /// <summary>Noninteger columns cannot reuse parsed integer syntax to trigger conversion failures.</summary>
    [SqlServerLiveTheory]
    [InlineData("varchar(20)", "'not a number'")]
    [InlineData("float", "1.5")]
    public async Task PopulatedCheck_UnsupportedPhysicalTypeFailsBeforeRowBinding(
        string storeType,
        string value
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.typed_check_values(Value " + storeType + " NULL); "
            + "INSERT dbo.typed_check_values VALUES(" + value + ");");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("CK_typed_values", "typed_check_values", "[Value]>=0");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-check-physical-type"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number);
    }

    /// <summary>Existing name branches cannot bypass SELECT and accidentally compile an unauthorized row arm.</summary>
    [SqlServerLiveTheory]
    [InlineData("matching")]
    [InlineData("occupied")]
    public async Task PopulatedCheck_DeniedSelectIsStructuredUnsupported(
        string existing
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.permission_check_values(Value int NOT NULL); "
            + "INSERT dbo.permission_check_values VALUES(1); "
            + "CREATE USER check_metadata_reader WITHOUT LOGIN WITH DEFAULT_SCHEMA=dbo; "
            + "GRANT VIEW DEFINITION TO check_metadata_reader; "
            + "GRANT ALTER ON OBJECT::dbo.permission_check_values TO check_metadata_reader; "
            + "DENY SELECT ON OBJECT::dbo.permission_check_values TO check_metadata_reader;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("CK_permission_values", "permission_check_values", "[Value]>=0");
        if (existing == "matching")
        {
            await ExecuteOperationsAsync(context, builder.Operations);
        }
        else
        {
            await ExecuteSqlAsync(connectionString,
                "ALTER TABLE dbo.permission_check_values ADD CONSTRAINT CK_permission_values UNIQUE(Value);");
        }

        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER=N'check_metadata_reader';");
        IReadOnlyList<SafeMigrationProviderAnalysis> analyses;
        Exception? failure;

        // Act
        try
        {
            analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context,
                builder.Operations.Cast<SafeMigrationOperation>().ToArray());
            failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("REVERT;");
        }

        // Assert
        Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(analyses).ObservedState);
        Assert.Equal("check_row_data_unproven", Assert.Single(analyses).Code);
        Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number);
    }

    /// <summary>Legacy stamped noninteger CHECKs replay without borrowing a new integer row certificate.</summary>
    [SqlServerLiveTheory]
    [InlineData("float")]
    [InlineData("varchar(20)")]
    public async Task PopulatedCheck_ExistingStampedNonintegerContractStillMatches(
        string storeType
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.legacy_check_values(Value " + storeType + " NULL);");
        await using var context = CreateContext(connectionString);
        var definition = new ExpectedCheckConstraintDefinition("CK_legacy_values", "legacy_check_values", "[Value]>=0");
        var operation = new SafeMigrationOperation(new EnsureCheckConstraintIntent(definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        await ExecuteSqlAsync(connectionString,
            "ALTER TABLE dbo.legacy_check_values ADD CONSTRAINT CK_legacy_values CHECK([Value]>=0); "
            + catalog.Build(operation).PostApplySql + "; INSERT dbo.legacy_check_values VALUES(1);");

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        await ExecuteOperationsAsync(context, [operation]);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(analyses).ObservedState);
        Assert.False(Assert.Single(analyses).RequiresLiveDataProof);
    }

    /// <summary>Raw foreign/session-dependent quoting never receives a populated integer proof.</summary>
    [SqlServerLiveTheory]
    [InlineData("`Value`>=0")]
    [InlineData("\"Value\">=0")]
    public async Task PopulatedCheck_RawDialectAndQuotedIdentifierOffStayEmptyOnly(
        string predicate
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.dialect_check_values(Value int NOT NULL); INSERT dbo.dialect_check_values VALUES(1);");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("SET QUOTED_IDENTIFIER OFF;");
        var operation = new SafeMigrationOperation(new EnsureCheckConstraintIntent(
            new ExpectedCheckConstraintDefinition("CK_dialect_values", "dialect_check_values", predicate)),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));

        // Assert
        Assert.Equal(SafeMigrationObservedState.DataBlocked, Assert.Single(analyses).ObservedState);
        Assert.Equal(51003, Assert.IsType<SqlException>(failure).Number);
    }

    /// <summary>A same-name local CHECK replacement retains new-predicate truth across an accepted widening.</summary>
    [SqlServerLiveTheory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task PopulatedCheck_SameNameReplacementPreflightsAndExecutesInOrder(
        bool widening,
        bool valid
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.replacement_check_values(Value int NOT NULL); "
            + "INSERT dbo.replacement_check_values VALUES(" + (valid ? "1" : "0") + "),(2147483647); "
            + "ALTER TABLE dbo.replacement_check_values ADD CONSTRAINT CK_replacement_values CHECK(Value>=0);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropCheckConstraintIfExists("CK_replacement_values", "replacement_check_values");
        if (widening)
        {
            builder.AlterColumnIfDifferent("replacement_check_values",
                new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
                new ExpectedColumnDefinition("Value", typeof(int), false, "int"), SafeMigrationPolicy.RepairIfSafe);
        }

        builder.AddCheckConstraintIfNotExists("CK_replacement_values", "replacement_check_values", "[Value]>=1");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-same-name-check-replacement"));

        if (valid)
        {
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        var trusted = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.check_constraints "
            + "WHERE parent_object_id=OBJECT_ID(N'dbo.replacement_check_values') "
            + "AND name=N'CK_replacement_values' AND is_disabled=0 AND is_not_trusted=0;");

        // Assert
        Assert.Equal(valid ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(valid ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.DataBlocked,
            report.Assessments[^1].ObservedState);
        Assert.Equal(1, trusted);
    }

    /// <summary>Opaque replacement stays empty-only even when a prior exact CHECK drop projects Missing.</summary>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PopulatedCheck_OpaqueSameNameReplacementRetainsEmptyOnlyBoundary(
        bool populated
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.opaque_replacement_values(Value int NOT NULL); "
            + "ALTER TABLE dbo.opaque_replacement_values ADD CONSTRAINT CK_opaque_values CHECK(Value>=0); "
            + (populated ? "INSERT dbo.opaque_replacement_values VALUES(1);" : string.Empty));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropCheckConstraintIfExists("CK_opaque_values", "opaque_replacement_values");
        builder.AddCheckConstraintIfNotExists("CK_opaque_values", "opaque_replacement_values", "[Value]/0>0");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-opaque-check-replacement"));

        if (!populated)
        {
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(populated ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(populated ? SafeMigrationObservedState.DataBlocked : SafeMigrationObservedState.Missing,
            report.Assessments[^1].ObservedState);
    }

    /// <summary>CHECK replacements consume resolved drop identity without removing a wrong-case CS target.</summary>
    /// <param name="caseSensitive">Whether a wrong-case drop is a different catalog object.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PopulatedCheck_AliasedReplacementUsesResolvedPhysicalDropIdentity(
        bool caseSensitive
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "ALTER DATABASE CURRENT COLLATE Latin1_General_100_" + (caseSensitive ? "CS" : "CI") + "_AS;");
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.alias_check_values(Value int NOT NULL); INSERT dbo.alias_check_values VALUES(1); "
            + "ALTER TABLE dbo.alias_check_values ADD CONSTRAINT CK_alias_check_values CHECK([Value]>=0);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropCheckConstraintIfExists("ck_alias_check_values",
            caseSensitive ? "alias_check_values" : "ALIAS_CHECK_VALUES", caseSensitive ? "dbo" : "DBO");
        builder.AddCheckConstraintIfNotExists(caseSensitive ? "CK_alias_check_values" : "ck_alias_check_values",
            caseSensitive ? "alias_check_values" : "ALIAS_CHECK_VALUES", "[Value]>=1",
            caseSensitive ? "dbo" : "DBO");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-catalog-check-drop-identity"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        if (!caseSensitive)
        {
            await ExecuteOperationsAsync(context, [builder.Operations[1]]);
        }

        var checks = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.alias_check_values') "
            + "AND is_disabled=0 AND is_not_trusted=0 AND CHARINDEX(N'"
            + (caseSensitive ? ">=(0)" : ">=(1)") + "',definition)>0;");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.alias_check_values WHERE Value=1;");

        // Assert
        Assert.Equal(caseSensitive ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready,
            report.Status);
        Assert.Equal(caseSensitive ? SafeMigrationAction.NoOp : SafeMigrationAction.Apply,
            report.Assessments[0].Action);
        Assert.Equal(caseSensitive ? SafeMigrationAction.RejectDifferent : SafeMigrationAction.Apply,
            report.Assessments[1].Action);
        if (caseSensitive)
        {
            Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.Equal(1, checks);
        Assert.Equal(1, rows);
    }
}
