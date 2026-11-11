namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies retained transition certificates for dependency-free candidates.</summary>
public sealed class SqlServerTransitionDependencyAllocationTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=transition_allocations;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Shared empty dependencies preserve the former eager-set source-bound repair decisions.</summary>
    [Fact]
    public void DependencyFreeCandidates_MatchEagerEmptySetRepairDecisions()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        using var current = Analyzer(context);
        using var former = Analyzer(context);
        var operations = Enumerable
            .Range(0, 512)
            .Select(static index => new SafeMigrationOperation(
                new AlterColumnIntent(
                    "items_" + index,
                    new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
                    new ExpectedColumnDefinition("Value", typeof(int), false, "int")),
                SafeMigrationPolicy.RepairIfSafe))
            .ToArray();

        var analysis = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe,
            false,
            "candidate_safe");

        var blocked = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None,
            false,
            "candidate_blocked");

        var columns = new SafeMigrationPreflightProjection();
        var uncaptured = new SafeMigrationOperation(operations[0].Intent, operations[0].Policy);

        // Act
        Capture(current, operations, analysis, eagerSets: false);
        Capture(former, operations, analysis, eagerSets: true);
        var currentResults = operations
            .Select(operation => current.ValidateProjectedOperation(operation, blocked, columns))
            .ToArray();

        var formerResults = operations
            .Select(operation => former.ValidateProjectedOperation(operation, blocked, columns))
            .ToArray();

        var uncapturedResult = current.ValidateProjectedOperation(uncaptured, blocked, columns);

        // Assert
        Assert.All(currentResults.Concat(formerResults),
            result =>
            {
                Assert.Equal(SafeMigrationObservedState.Different, result.ObservedState);
                Assert.Equal(SafeMigrationRepairCapability.Safe, result.RepairCapability);
                Assert.Equal("column_integer_widening_safe", result.Code);
                Assert.False(result.PostconditionSatisfied);
                Assert.Equal(SafeMigrationOperationalImpact.TableRewritePossible, result.OperationalImpact);
                Assert.Empty(result.Differences);
                Assert.True(result.RepairPreservesLiveValueDomain);
                Assert.False(result.RequiresLiveDataProof);
            });

        Assert.Equal(SafeMigrationRepairCapability.None, uncapturedResult.RepairCapability);
    }

    /// <summary>Creates an analyzer using the registered SQL Server mapping and identifier services.</summary>
    private static SqlServerSafeMigrationProviderAnalyzer Analyzer(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    /// <summary>Publishes identical proofs with an immutable empty collection or the former eager set.</summary>
    private static void Capture(
        SqlServerSafeMigrationProviderAnalyzer analyzer,
        SafeMigrationOperation[] operations,
        SafeMigrationProviderAnalysis analysis,
        bool eagerSets
    )
    {
        foreach (var operation in operations)
        {
            IReadOnlyCollection<SqlServerSafeMigrationProviderAnalyzer.TransitionDependency> dependencies = eagerSets
                ? new HashSet<SqlServerSafeMigrationProviderAnalyzer.TransitionDependency>()
                : Array.Empty<SqlServerSafeMigrationProviderAnalyzer.TransitionDependency>();

            analyzer.CaptureProjectedIntegerWidening(operation, analysis, dependencies);
        }
    }
}
