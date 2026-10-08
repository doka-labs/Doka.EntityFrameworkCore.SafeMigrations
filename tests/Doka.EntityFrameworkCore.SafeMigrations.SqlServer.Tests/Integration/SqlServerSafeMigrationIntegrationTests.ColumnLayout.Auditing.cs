namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Proves the layout audit records real table DDL only while its trigger is enabled.</summary>
    /// <param name="disabled">Whether the audit remains disabled for the known ALTER statement.</param>
    [SqlServerLiveTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ColumnLayoutAudit_RecordsKnownDdlOnlyWhenEnabled(
        bool disabled
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.layout_probe (Id int NOT NULL); INSERT dbo.layout_probe VALUES(7);");
        await CreateColumnLayoutAuditAsync(connectionString);
        if (!disabled)
        {
            await EnableColumnLayoutAuditAsync(connectionString);
        }

        // Act
        await ExecuteSqlAsync(connectionString, "ALTER TABLE dbo.layout_probe ADD Added int NULL;");
        var triggerDisabled = await ScalarIntAsync(connectionString,
            "SELECT CONVERT(int,is_disabled) FROM sys.triggers "
            + "WHERE parent_class=0 AND name=N'layout_ddl_audit';");

        var events = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.layout_ddl_events;");
        var added = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.layout_probe',N'U') "
            + "AND name=N'Added' AND column_id=2 AND system_type_id=TYPE_ID(N'int') "
            + "AND user_type_id=TYPE_ID(N'int') AND max_length=4 AND is_nullable=1;");

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.layout_probe WHERE Id=7 AND Added IS NULL;");

        // Assert
        Assert.Equal(disabled ? 1 : 0, triggerDisabled);
        Assert.Equal(disabled ? 0 : 1, events);
        Assert.Equal(1, added);
        Assert.Equal(1, rows);
    }

    /// <summary>
    /// Enabled DDL auditing defers ordered layout proofs without bypassing the runtime capacity guard.
    /// </summary>
    /// <param name="freshTable">Whether accepted table creation precedes both column additions.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrderedColumnAdditions_EnabledAuditDefersAndRejectsAtRuntime(
        bool freshTable
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        if (!freshTable)
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE TABLE dbo.layout_probe (Existing char(4000) NULL); "
                + "INSERT dbo.layout_probe VALUES('sentinel');");
        }

        await CreateColumnLayoutAuditAsync(connectionString);
        await EnableColumnLayoutAuditAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var operations = new List<MigrationOperation>();
        if (freshTable)
        {
            operations.Add(new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition(
                "layout_probe", [new ExpectedColumnDefinition("Existing", typeof(string), true, "char(4000)")]),
                SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent));
        }

        operations.Add(LayoutColumn("First", "char(3000)"));
        operations.Add(LayoutColumn("Second", "char(2000)"));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("sqlserver-ordered-layout-enabled-audit"));

        var preflightFailure = Record.Exception(report.ThrowIfBlocked);
        var preflightTables = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'dbo.layout_probe',N'U');");

        var preflightColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.layout_probe',N'U');");

        var preflightEvents = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.layout_ddl_events;");
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, operations));
        var events = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.layout_ddl_events;");
        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.layout_probe',N'U');");

        var acceptedBindings = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.layout_probe',N'U') "
            + "AND system_type_id=TYPE_ID(N'char') AND user_type_id=TYPE_ID(N'char') AND is_nullable=1 "
            + "AND ((name=N'Existing' AND column_id=1 AND max_length=4000) "
            + "OR (name=N'First' AND column_id=2 AND max_length=3000));");

        var rejected = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.layout_probe',N'U') "
            + "AND name=N'Second';");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.layout_probe;");
        var sentinelRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.layout_probe WHERE RTRIM(Existing)='sentinel' AND First IS NULL;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        if (freshTable)
        {
            AssertDdlDeferredOrigin(report.Assessments[1], "projected_ddl_trigger_data_unknown", 0,
                typeof(SafeMigrationOperation), migrationId: null);
            AssertDdlDeferredOrigin(report.Assessments[2], "projected_provider_postcondition_unknown", 1,
                typeof(SafeMigrationOperation), migrationId: null);
        }
        else
        {
            AssertDdlDeferredOrigin(report.Assessments[1], "projected_ddl_trigger_data_unknown", 0,
                typeof(SafeMigrationOperation), migrationId: null);
        }

        Assert.Null(preflightFailure);
        Assert.Equal(freshTable ? 0 : 1, preflightTables);
        Assert.Equal(freshTable ? 0 : 1, preflightColumns);
        Assert.Equal(0, preflightEvents);
        Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number);

        // WHY: ExecuteOperationsAsync has no outer transaction; accepted commands
        // and their audit inserts remain committed when a later guard rejects.
        Assert.Equal(freshTable ? 2 : 1, events);
        Assert.Equal(2, columns);
        Assert.Equal(2, acceptedBindings);
        Assert.Equal(0, rejected);
        Assert.Equal(freshTable ? 0 : 1, rows);
        Assert.Equal(freshTable ? 0 : 1, sentinelRows);
    }

    /// <summary>
    /// Creates a disabled DDL audit so read-only layout analysis keeps its original proof environment.
    /// </summary>
    /// <param name="connectionString">The isolated test database that owns the audit table and trigger.</param>
    private static async Task CreateColumnLayoutAuditAsync(
        string connectionString
    )
    {
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.layout_ddl_events (Id int NOT NULL);");
        await ExecuteSqlAsync(connectionString, "CREATE TRIGGER layout_ddl_audit ON DATABASE FOR DDL_TABLE_EVENTS "
            + "AS INSERT dbo.layout_ddl_events VALUES(1);");

        // WHY: Even an insert-only observer is an enabled DDL trigger. Keeping
        // it active during analysis invalidates subsequent ordered layout proofs.
        await ExecuteSqlAsync(connectionString, "DISABLE TRIGGER layout_ddl_audit ON DATABASE;");
    }

    /// <summary>
    /// Arms the observer immediately before runtime DDL whose accepted or rejected events are asserted.
    /// </summary>
    /// <param name="connectionString">The isolated test database containing the disabled layout audit.</param>
    private static Task EnableColumnLayoutAuditAsync(
        string connectionString
    ) => ExecuteSqlAsync(connectionString, "ENABLE TRIGGER layout_ddl_audit ON DATABASE;");
}
