namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Separates SQL Server 2019 metadata compatibility from actual SQL Server 2022+ ledger rejection.</summary>
[Collection(SqlServerSharedContainer.Name)]
public sealed class SqlServerLedgerPhysicalBoundaryTests : SqlServerIntegrationTestBase
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    /// <summary>Creates the version-capability contract with explicit branch reporting.</summary>
    /// <param name="fixture">The isolated SQL Server database fixture.</param>
    /// <param name="output">The test output retaining which capability branch was exercised.</param>
    public SqlServerLedgerPhysicalBoundaryTests(
        SqlServerContainerFixture fixture,
        Xunit.Abstractions.ITestOutputHelper output
    ) : base(fixture)
    {
        _output = output;
    }

    /// <summary>Executes the ordinary metadata and managed-row positive on every qualified engine version.</summary>
    [SqlServerLiveFact]
    public async Task OrdinaryPhysicalBoundary_ExecutesVersionCompatibleMetadataAndManagedRows()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var major = await ScalarIntAsync(connectionString,
            "SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion'));");

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.ordinary_control (Id int NOT NULL PRIMARY KEY, Value int NOT NULL);");

        await using var context = CreateContext(connectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var ordinary = new SafeMigrationOperation(new EnsureModelManagedDataIntent("ordinary_control", ["Id"], ["int"],
            ["Id", "Value"], ["int", "int"], new object?[,] { { 1, 7 } }, null, null),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var ordinaryGate = await ScalarIntAsync(connectionString,
            "SELECT (" + catalog.BuildPhysicalTableSupportExpression("ordinary_control", null) + ");");

        await ExecuteOperationsAsync(context, [ordinary]);
        var ordinaryAnalyses = await analyzer.AnalyzeAsync(context, [ordinary]);
        await ExecuteOperationsAsync(context, [ordinary]);

        // Assert
        var ordinaryAnalysis = Assert.Single(ordinaryAnalyses);
        Assert.True(major >= 15, "The qualification contract requires SQL Server 2019 or later.");
        Assert.Equal(1, ordinaryGate);
        Assert.Equal(SafeMigrationObservedState.Matching, ordinaryAnalysis.ObservedState);
        _output.WriteLine("SQL Server {0}: ordinary 2019-compatible metadata and managed-row path executed.", major);
    }

    /// <summary>
    /// Reports SQL Server 2019 metadata evidence separately from actual ledger negatives on newer engines.
    /// </summary>
    [SqlServerLiveFact]
    public async Task LedgerPhysicalBoundary_ExecutesTheExplicitEngineCapabilityBranch()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var major = await ScalarIntAsync(connectionString,
            "SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion'));");

        var ledgerAvailable = major >= 16;
        var table = ledgerAvailable ? "ledger_items" : "ordinary_control";
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo." + table + " (Id int NOT NULL PRIMARY KEY, Value int NOT NULL) "
            + (ledgerAvailable ? "WITH (LEDGER = ON (APPEND_ONLY = ON))" : string.Empty)
            + "; INSERT dbo." + table + " (Id, Value) VALUES (1, 7);");

        await using var context = CreateContext(connectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        SafeMigrationOperation[] ledgerOperations = ledgerAvailable
            ?
            [
                new(new UpdateModelManagedDataIntent("ledger_items", ["Id"], ["int"], new object?[,] { { 1 } },
                    ["Value"], ["int"], new object?[,] { { 7 } }, new object?[,] { { 8 } }, null, null),
                    SafeMigrationPolicy.ThrowIfDifferent),
                new(new DeleteModelManagedDataIntent("ledger_items", ["Id"], ["int"], new object?[,] { { 1 } },
                    ["Id", "Value"], ["int", "int"], new object?[,] { { 1, 7 } }, null, []),
                    SafeMigrationPolicy.ThrowIfDifferent),
            ]
            : [];

        // Act
        var physicalGate = await ScalarIntAsync(connectionString,
            "SELECT (" + catalog.BuildPhysicalTableSupportExpression(table, null) + ");");

        IReadOnlyList<SafeMigrationProviderAnalysis> ledgerAnalyses;
        await using (await analyzer.AcquireAnalysisScopeAsync(context))
        {
            ledgerAnalyses = await analyzer.AnalyzeAsync(context, ledgerOperations);
        }

        var failures = new List<Exception?>();
        foreach (var operation in ledgerOperations)
        {
            failures.Add(await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation])));
        }

        var rowCount = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo." + table + ";");
        var unchangedValue = await ScalarIntAsync(connectionString,
            "SELECT Value FROM dbo." + table + " WHERE Id = 1;");

        // Assert
        Assert.True(major >= 15, "The qualification contract requires SQL Server 2019 or later.");
        Assert.Equal(ledgerAvailable ? 0 : 1, physicalGate);
        Assert.Equal(1, rowCount);
        Assert.Equal(7, unchangedValue);
        if (!ledgerAvailable)
        {
            Assert.Empty(ledgerAnalyses);
            Assert.Empty(failures);
            _output.WriteLine("SQL Server {0}: ordinary 2019-compatible metadata predicate executed. "
                + "Ledger is unavailable; this branch does not qualify ledger rejection.", major);

            return;
        }

        Assert.All(ledgerAnalyses, static analysis =>
        {
            Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState);
            Assert.Equal("physical_table_unproven", analysis.Code);
            Assert.True(analysis.IsInvariantUnsupported);
            Assert.Null(analysis.ModelManagedDataEvidence);
        });
        Assert.All(failures, static failure => Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number));
        _output.WriteLine("SQL Server {0}: actual append-only ledger rejection executed.", major);
    }
}
