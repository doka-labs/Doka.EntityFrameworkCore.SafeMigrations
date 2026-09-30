namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Rejects unsupported engines before row probes, prerequisite failures, projection, or DDL.</summary>
    [SqlServerLiveTheory]
    [InlineData("memory")]
    [InlineData("history")]
    public async Task PhysicalTableEngine_AllTableIntentsAreInvariantUnsupportedWithoutMutation(string engine)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, PhysicalTableSetupSql(engine));
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.ordinary_child (Id int NOT NULL); "
            + "CREATE TABLE dbo.engine_ddl_events (Id int NOT NULL);");
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER doka_engine_ddl_audit ON DATABASE FOR DDL_TABLE_EVENTS, DDL_INDEX_EVENTS "
            + "AS INSERT dbo.engine_ddl_events VALUES (1);");
        var originalId = await ScalarIntAsync(connectionString, "SELECT OBJECT_ID(N'dbo.physical_items', N'U');");
        var originalColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.physical_items', N'U');");

        var originalRows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.physical_items;");
        var operations = PhysicalTableOperations();
        await using var context = CreateContext(connectionString);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        IReadOnlyList<SafeMigrationProviderAnalysis> analyses;
        await using (await analyzer.AcquireAnalysisScopeAsync(context))
        {
            // WHY: Memory-optimized row reads under the owned ReadCommitted
            // transaction would fail before classification without this gate.
            analyses = await analyzer.AnalyzeAsync(context, operations);
        }

        var failures = new List<Exception?>();
        foreach (var operation in operations)
        {
            failures.Add(await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation])));
        }

        var remainingId = await ScalarIntAsync(connectionString, "SELECT OBJECT_ID(N'dbo.physical_items', N'U');");
        var remainingColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.physical_items', N'U');");

        var remainingRows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.physical_items;");
        var ddlEvents = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.engine_ddl_events;");

        // Assert
        Assert.Equal(operations.Length, analyses.Count);
        Assert.All(analyses, static analysis =>
        {
            Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState);
            Assert.Equal("physical_table_unproven", analysis.Code);
            Assert.True(analysis.IsInvariantUnsupported);
            Assert.False(analysis.PostconditionSatisfied);
            Assert.Equal(SafeMigrationRepairCapability.None, analysis.RepairCapability);
            Assert.Null(analysis.ModelManagedDataEvidence);
        });
        Assert.All(failures, static failure => Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number));
        Assert.Equal(originalId, remainingId);
        Assert.Equal(originalColumns, remainingColumns);
        Assert.Equal(originalRows, remainingRows);
        Assert.Equal(0, ddlEvents);
    }

    /// <summary>Still matches ordinary tables and applies then replays an absent ordinary table.</summary>
    [SqlServerLiveFact]
    public async Task OrdinaryAndAbsentTables_PhysicalBoundaryAllowsApplyAndReplay()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.ordinary_items (Id int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("ordinary_items",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureTable(new ExpectedTableDefinition("absent_items",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var operations = builder.Operations.Cast<SafeMigrationOperation>().ToArray();

        // Act
        var before = await analyzer.AnalyzeAsync(context, operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var after = await analyzer.AnalyzeAsync(context, operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var ordinaryEngines = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE name IN (N'ordinary_items', N'absent_items') "
            + "AND is_memory_optimized = 0 AND is_filetable = 0 AND temporal_type = 0;");

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, before[0].ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, before[1].ObservedState);
        Assert.All(after, static analysis => Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState));
        Assert.Equal(2, ordinaryEngines);
    }

    private static SafeMigrationOperation[] PhysicalTableOperations()
    {
        var intents = Enum.GetValues<SafeMigrationOperationKind>()
            .Where(static kind => kind is not SafeMigrationOperationKind.EnsureSchema
                and not SafeMigrationOperationKind.DropSchema)
            .Select(SqlServerPhysicalTableSqlTests.CreateIntent)
            .ToList();

        intents.Add(new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition("FK_engine_parent", "ordinary_child",
            ["Id"], "physical_items", ["Id"])));
        var inlineForeignKey = new ExpectedForeignKeyDefinition("FK_inline_engine", "new_child", ["Id"],
            "physical_items", ["Id"]);

        intents.Add(new EnsureTableIntent(new ExpectedTableDefinition("new_child",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int")], foreignKeys: [inlineForeignKey]),
            SafeMigrationTableMode.StrictDefinition));
        intents.Add(new RenameTableIntent("absent_source", "physical_items"));

        return intents
            .Select(static intent => new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent))
            .ToArray();
    }

    private static string PhysicalTableSetupSql(string engine) => engine switch
    {
        "memory" => "DECLARE @database sysname = DB_NAME(); "
            + "DECLARE @quoted_database nvarchar(258) = QUOTENAME(@database); "
            + "DECLARE @data nvarchar(4000) = CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultDataPath')); "
            + "DECLARE @file nvarchar(4000) = @data + @database + N'_memory'; "
            + "DECLARE @sql nvarchar(max) = N'ALTER DATABASE ' + @quoted_database "
            + "+ N' ADD FILEGROUP doka_memory CONTAINS MEMORY_OPTIMIZED_DATA'; EXEC sys.sp_executesql @sql; "
            + "SET @sql = N'ALTER DATABASE ' + @quoted_database "
            + "+ N' ADD FILE (NAME = N''doka_memory_file'', FILENAME = N''' + REPLACE(@file, N'''', N'''''') "
            + "+ N''') TO FILEGROUP doka_memory'; EXEC sys.sp_executesql @sql; "
            + "CREATE TABLE dbo.physical_items (Id int NOT NULL PRIMARY KEY NONCLUSTERED, Value int NULL) "
            + "WITH (MEMORY_OPTIMIZED = ON, DURABILITY = SCHEMA_ONLY); "
            + "INSERT dbo.physical_items VALUES (1, 7);",
        "history" => "CREATE TABLE dbo.temporal_current (Id int NOT NULL PRIMARY KEY, Value int NULL, "
            + "ValidFrom datetime2 GENERATED ALWAYS AS ROW START NOT NULL, "
            + "ValidTo datetime2 GENERATED ALWAYS AS ROW END NOT NULL, PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)) "
            + "WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.physical_items)); "
            + "INSERT dbo.temporal_current (Id, Value) VALUES (1, 7); "
            + "UPDATE dbo.temporal_current SET Value = 8 WHERE Id = 1;",
        _ => throw new ArgumentOutOfRangeException(nameof(engine)),
    };
}
