namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Invalid filter grammar is rejected before uniqueness probes or any earlier migration DDL.</summary>
    /// <param name="filter">The syntactically representable but unsupported filtered-index predicate.</param>
    [SqlServerLiveTheory]
    [InlineData("[Flag] = 1 OR [Flag] = 2")]
    [InlineData("[Flag] = [Id]")]
    [InlineData("[Flag] = NULL")]
    [InlineData("[Flag] NOT IN (1, 2)")]
    [InlineData("ABS([Flag]) = 1")]
    [InlineData("[Flag] + 1 = 2")]
    public async Task UnsupportedFilterGrammarRejectsBeforeDataAndDdl(
        string filter
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.filter_items (Id int NOT NULL, Code int NULL, Flag int NULL); "
            + "INSERT dbo.filter_items (Id, Code, Flag) VALUES (1, 7, 1), (2, 7, 1); "
            + "CREATE TABLE dbo.filter_ddl_events (Id int NOT NULL);");
        await CreateFilterPredicateAuditAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var definition = new ExpectedIndexDefinition(
            "IX_filter_items_Code",
            "filter_items",
            [new ExpectedIndexKeyDefinition("Code")],
            unique: true,
            filter: filter);

        var operation = new SafeMigrationOperation(
            new EnsureIndexIntent(definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        var ordinaryPrefix = new SqlOperation { Sql = "CREATE TABLE dbo.before_filter_rejection (Id int NOT NULL);" };

        // Act
        var originalTableId = await ScalarIntAsync(connectionString, "SELECT OBJECT_ID(N'dbo.filter_items', N'U');");
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [ordinaryPrefix, operation]));

        var remainingTableId = await ScalarIntAsync(connectionString, "SELECT OBJECT_ID(N'dbo.filter_items', N'U');");
        var indexCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.filter_items', N'U') "
            + "AND name = N'IX_filter_items_Code';");

        var rowCount = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.filter_items WHERE Code = 7;");
        var ddlEvents = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.filter_ddl_events;");
        var ordinaryTables = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE name = N'before_filter_rejection';");

        // Assert
        var analysis = Assert.Single(analyses);

        Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState);
        Assert.Equal("index_filter_unproven", analysis.Code);
        Assert.True(analysis.IsInvariantUnsupported);
        Assert.IsType<NotSupportedException>(failure);
        Assert.Equal(originalTableId, remainingTableId);
        Assert.Equal(0, indexCount);
        Assert.Equal(2, rowCount);
        Assert.Equal(0, ddlEvents);
        Assert.Equal(0, ordinaryTables);
    }

    /// <summary>Forbidden physical filter columns are rejected before a duplicate scan or index mutation.</summary>
    /// <param name="kind">The unsupported filter-column storage or missing-column case.</param>
    [SqlServerLiveTheory]
    [InlineData("computed")]
    [InlineData("alias")]
    [InlineData("spatial")]
    [InlineData("hierarchy")]
    [InlineData("missing")]
    public async Task UnsupportedFilterColumnRejectsWithoutDdlOrSchemaChanges(
        string kind
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        if (kind == "alias")
        {
            await ExecuteSqlAsync(connectionString, "CREATE TYPE dbo.FilterFlag FROM int;");
        }

        var column = kind switch
        {
            "computed" => ", Special AS (Id + 1) PERSISTED",
            "alias" => ", Special dbo.FilterFlag NULL",
            "spatial" => ", Special geometry NULL",
            "hierarchy" => ", Special hierarchyid NULL",
            "missing" => string.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.filter_items (Id int NOT NULL, Code int NULL" + column + "); "
            + "INSERT dbo.filter_items (Id, Code) VALUES (1, 7), (2, 7); "
            + "CREATE TABLE dbo.filter_ddl_events (Id int NOT NULL);");
        await CreateFilterPredicateAuditAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var operation = new SafeMigrationOperation(
            new EnsureIndexIntent(new ExpectedIndexDefinition(
                "IX_filter_items_Code",
                "filter_items",
                [new ExpectedIndexKeyDefinition("Code")],
                unique: true,
                filter: "[Special] IS NOT NULL")),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var originalTableId = await ScalarIntAsync(connectionString, "SELECT OBJECT_ID(N'dbo.filter_items', N'U');");
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var remainingTableId = await ScalarIntAsync(connectionString, "SELECT OBJECT_ID(N'dbo.filter_items', N'U');");
        var indexCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.filter_items', N'U') "
            + "AND name = N'IX_filter_items_Code';");

        var rowCount = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.filter_items WHERE Code = 7;");
        var ddlEvents = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.filter_ddl_events;");

        // Assert
        Assert.Equal(
            kind == "missing" ? SafeMigrationObservedState.PrerequisiteMissing : SafeMigrationObservedState.Unsupported,
            Assert.Single(analyses).ObservedState);
        Assert.Equal(kind == "missing" ? 51004 : 51002, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(originalTableId, remainingTableId);
        Assert.Equal(0, indexCount);
        Assert.Equal(2, rowCount);
        Assert.Equal(0, ddlEvents);
    }

    /// <summary>Documented IN, AND, and null tests apply, replay without extra DDL, and satisfy postflight.</summary>
    /// <param name="filter">The supported filtered-index predicate.</param>
    [SqlServerLiveTheory]
    [InlineData("[Flag] IN (1, 2)")]
    [InlineData("[Code] IS NOT NULL AND [Flag] IN (1, 2)")]
    [InlineData("[Code] IS NOT NULL")]
    [InlineData("[Flag] IS NULL")]
    public async Task SupportedFilterAppliesReplaysAndVerifies(
        string filter
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.filter_items (Id int NOT NULL, Code int NULL, Flag int NULL); "
            + "INSERT dbo.filter_items (Id, Code, Flag) VALUES "
            + "(1, 10, 1), (2, 20, 2), (3, NULL, 3), (4, NULL, NULL); "
            + "CREATE TABLE dbo.filter_ddl_events (Id int NOT NULL);");
        await CreateFilterPredicateAuditAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists(
            "IX_filter_items_Code",
            "filter_items",
            ["Code"],
            unique: true,
            filter: filter);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("supported-filter-before"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var ddlEvents = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.filter_ddl_events;");
        await ExecuteOperationsAsync(context, builder.Operations);
        var replayDdlEvents = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.filter_ddl_events;");
        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("supported-filter-after"));

        var indexCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.filter_items', N'U') "
            + "AND name = N'IX_filter_items_Code' AND is_unique = 1 AND has_filter = 1;");

        var rowCount = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.filter_items;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(preflight.Assessments).Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.True(Assert.Single(postflight.Assessments).PostconditionSatisfied);
        Assert.Equal(1, indexCount);
        Assert.True(ddlEvents > 0);
        Assert.Equal(ddlEvents, replayDdlEvents);
        Assert.Equal(4, rowCount);
    }

    /// <summary>Only null tests on the actual unique key exclude duplicate NULL keys from the index.</summary>
    /// <param name="filter">The supported filter with either relevant or unrelated null exclusion.</param>
    /// <param name="safe">Whether the selected rows have a unique key.</param>
    [SqlServerLiveTheory]
    [InlineData("[Code] IS NOT NULL", true)]
    [InlineData("[Code] IS NOT NULL AND [Flag] = 1", true)]
    [InlineData("[Flag] IS NOT NULL", false)]
    [InlineData("[Code] IS NULL", false)]
    public async Task NullExclusionMustApplyToTheIndexedColumn(
        string filter,
        bool safe
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.filter_items (Id int NOT NULL, Code int NULL, Flag int NULL); "
            + "INSERT dbo.filter_items (Id, Code, Flag) VALUES (1, NULL, 1), (2, NULL, 1), (3, 7, 1); "
            + "CREATE TABLE dbo.filter_ddl_events (Id int NOT NULL);");
        await CreateFilterPredicateAuditAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var operation = new SafeMigrationOperation(
            new EnsureIndexIntent(new ExpectedIndexDefinition(
                "IX_filter_items_Code",
                "filter_items",
                [new ExpectedIndexKeyDefinition("Code")],
                unique: true,
                filter: filter)),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var indexCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.filter_items', N'U') "
            + "AND name = N'IX_filter_items_Code';");

        var ddlEvents = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.filter_ddl_events;");

        // Assert
        Assert.Equal(safe ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.DataBlocked,
            Assert.Single(analyses).ObservedState);
        Assert.Equal(safe ? 1 : 0, indexCount);
        if (safe)
        {
            Assert.Null(failure);
            Assert.True(ddlEvents > 0);
        }
        else
        {
            Assert.Equal(51003, Assert.IsType<SqlException>(failure).Number);
            Assert.Equal(0, ddlEvents);
        }
    }

    /// <summary>
    /// Requires supported filters and existing references even for new empty tables and added nullable columns.
    /// </summary>
    /// <param name="filter">The unsupported expression shape or absent predicate-column case.</param>
    /// <param name="expectedAction">The expected filtered-index decision after ordered column projection.</param>
    [SqlServerLiveTheory]
    [InlineData("[Code] IS NOT NULL", SafeMigrationAction.Apply)]
    [InlineData("[Code] IS NOT NULL AND [Flag] IN (1, 2)", SafeMigrationAction.Apply)]
    [InlineData("[Code] IS NOT NULL OR [Flag] IS NOT NULL", SafeMigrationAction.RejectUnsupported)]
    [InlineData("[Code] = NULL", SafeMigrationAction.RejectUnsupported)]
    [InlineData("[MissingFlag] IS NOT NULL", SafeMigrationAction.RejectPrerequisiteMissing)]
    public async Task ProjectedEmptyRowsRequireValidatedFilterReferences(
        string filter,
        SafeMigrationAction expectedAction
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "projected_filter_items",
            table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                Code = table.Column<int>(type: "int", nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_projected_filter_items", row => row.Id));
        builder.AddColumnIfNotExists<int>("Flag", "projected_filter_items", type: "int", nullable: true);
        builder.CreateIndexIfNotExists(
            "IX_projected_filter_items_Code",
            "projected_filter_items",
            ["Code"],
            unique: true,
            filter: filter);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("projected-filter-validation"));

        var tableCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE name = N'projected_filter_items';");

        // Assert
        var expectedStatus = expectedAction == SafeMigrationAction.Apply
            ? SafeMigrationReportStatus.Ready
            : SafeMigrationReportStatus.Blocked;

        Assert.Equal(expectedStatus, report.Status);
        Assert.Equal(expectedAction, report.Assessments[^1].Action);
        if (expectedAction == SafeMigrationAction.RejectPrerequisiteMissing)
        {
            Assert.Equal("projected_index_filter_column_unknown", report.Assessments[^1].AnalysisCode);
        }
        else if (expectedAction == SafeMigrationAction.RejectUnsupported)
        {
            Assert.Equal("index_filter_unproven", report.Assessments[^1].AnalysisCode);
        }

        Assert.Equal(0, tableCount);
    }

    private static Task CreateFilterPredicateAuditAsync(
        string connectionString
    ) => ExecuteSqlAsync(
        connectionString,
        "CREATE TRIGGER doka_filter_ddl_audit ON DATABASE FOR DDL_TABLE_EVENTS, DDL_INDEX_EVENTS "
        + "AS INSERT dbo.filter_ddl_events VALUES (1);");
}
