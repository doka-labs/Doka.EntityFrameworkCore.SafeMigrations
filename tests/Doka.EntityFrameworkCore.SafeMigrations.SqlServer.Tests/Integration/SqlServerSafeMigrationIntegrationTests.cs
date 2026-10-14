namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Provides helpers shared by independently scheduled SQL Server feature suites.</summary>
/// <remarks>WHY: The abstract base owns no test cases, so leaf classes cannot inherit duplicate discoveries.</remarks>
public abstract partial class SqlServerSafeMigrationIntegrationTests : SqlServerIntegrationTestBase
{
    /// <summary>Bounds SQL Server fixture setup and large-operation command execution.</summary>
    protected const int PerformanceFixtureCommandTimeoutSeconds = 180;

    /// <summary>Creates shared helper state with the assembly-owned SQL Server fixture.</summary>
    /// <param name="fixture">The shared fixture providing a database per test.</param>
    protected SqlServerSafeMigrationIntegrationTests(
        SqlServerContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>
    /// Creates a disabled DDL audit so read-only layout analysis keeps its original proof environment.
    /// </summary>
    /// <param name="connectionString">The isolated test database that owns the audit table and trigger.</param>
    protected static async Task CreateColumnLayoutAuditAsync(
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
    protected static Task EnableColumnLayoutAuditAsync(
        string connectionString
    ) => ExecuteSqlAsync(connectionString, "ENABLE TRIGGER layout_ddl_audit ON DATABASE;");

    /// <summary>Builds the shared requested-column contract for layout and audit probes.</summary>
    /// <param name="name">The requested column's physical name.</param>
    /// <param name="storeType">The requested SQL Server store type.</param>
    /// <param name="clrType">The CLR type, or string when the probe requests text.</param>
    /// <returns>The guarded operation targeting the layout probe table.</returns>
    protected static SafeMigrationOperation LayoutColumn(
        string name,
        string storeType,
        Type? clrType = null
    ) => new(new EnsureColumnIntent("layout_probe", new ExpectedColumnDefinition(
        name, clrType ?? typeof(string), true, storeType)), SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Checks the complete deferred-origin contract rather than only its aggregate report status.</summary>
    /// <param name="assessment">The deferred operation assessment.</param>
    /// <param name="analysisCode">The exact cause reported by provider analysis.</param>
    /// <param name="operationOrdinal">The authored stream ordinal.</param>
    /// <param name="operationType">The concrete authored operation type.</param>
    /// <param name="migrationId">The EF migration identifier, or null for direct operation streams.</param>
    protected static void AssertDdlDeferredOrigin(
        SafeMigrationAssessment assessment,
        string analysisCode,
        int operationOrdinal,
        Type operationType,
        string? migrationId
    )
    {
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, assessment.Action);
        Assert.Equal("runtime_validation_required", assessment.Code);
        Assert.Equal(analysisCode, assessment.AnalysisCode);
        Assert.Null(assessment.ObservedState);
        Assert.Null(assessment.PostconditionSatisfied);
        var origin = Assert.IsType<SafeMigrationDeferredOrigin>(assessment.DeferredOrigin);
        Assert.Equal(operationOrdinal, origin.OperationOrdinal);
        Assert.Equal(operationType.FullName, origin.OperationType);
        Assert.Equal(migrationId, origin.MigrationId);
    }

    /// <summary>Builds a temporal-default operation shared by literal and filter probes.</summary>
    /// <param name="definition">The requested column including its temporal default.</param>
    /// <param name="inlineTable">Whether the column is inline in a new table.</param>
    /// <returns>The guarded inline table or standalone column operation.</returns>
    protected static SafeMigrationOperation TemporalDefaultOperation(
        ExpectedColumnDefinition definition,
        bool inlineTable
    )
    {
        SafeMigrationIntent intent = inlineTable
            ? new EnsureTableIntent(new ExpectedTableDefinition("new_default_items",
                [new ExpectedColumnDefinition("Id", typeof(int), false, "int"), definition]),
                SafeMigrationTableMode.StrictDefinition)
            : new EnsureColumnIntent("default_items", definition);

        return new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
    }

}
