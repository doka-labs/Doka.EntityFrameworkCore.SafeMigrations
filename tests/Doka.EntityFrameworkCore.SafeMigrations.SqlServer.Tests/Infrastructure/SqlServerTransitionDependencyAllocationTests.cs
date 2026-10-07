namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Measures retained transition certificates without allocating sets for dependency-free candidates.</summary>
public sealed class SqlServerTransitionDependencyAllocationTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=transition_allocations;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Shared immutable empty dependencies remove the former per-candidate empty HashSet cost.</summary>
    [Fact]
    public void DependencyFreeCandidates_AllocateLessThanEagerEmptySets()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var current = Analyzer(context);
        var former = Analyzer(context);
        var operations = Enumerable.Range(0, 512).Select(static index => new SafeMigrationOperation(
            new AlterColumnIntent("items_" + index,
                new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
                new ExpectedColumnDefinition("Value", typeof(int), false, "int")),
            SafeMigrationPolicy.RepairIfSafe)).ToArray();

        var analysis = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe, false, "candidate_safe");

        // WHY: Warm both dictionaries and capture paths with identical owned
        // candidates. The reference recreates only the removed eager-set
        // allocation; SQL generation, input construction, and timing are excluded.
        Capture(current, operations, analysis, eagerSets: false);
        Capture(former, operations, analysis, eagerSets: true);

        // Act
        var currentBytes = Measure(current, operations, analysis, eagerSets: false);
        var formerBytes = Measure(former, operations, analysis, eagerSets: true);

        // Assert
        Assert.InRange(currentBytes, 1, formerBytes - 1);
    }

    /// <summary>Creates an analyzer using the registered SQL Server mapping and identifier services.</summary>
    private static SqlServerSafeMigrationProviderAnalyzer Analyzer(
        DbContext context
    )
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    /// <summary>Counts only synchronous certificate capture after preparation and dictionary growth.</summary>
    private static long Measure(
        SqlServerSafeMigrationProviderAnalyzer analyzer,
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis analysis,
        bool eagerSets
    )
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 32; iteration++)
        {
            Capture(analyzer, operations, analysis, eagerSets);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Publishes identical proofs with an immutable empty collection or the former eager set.</summary>
    private static void Capture(
        SqlServerSafeMigrationProviderAnalyzer analyzer,
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis analysis,
        bool eagerSets
    )
    {
        for (var index = 0; index < operations.Count; index++)
        {
            IReadOnlyCollection<SqlServerSafeMigrationProviderAnalyzer.TransitionDependency> dependencies = eagerSets
                ? new HashSet<SqlServerSafeMigrationProviderAnalyzer.TransitionDependency>()
                : Array.Empty<SqlServerSafeMigrationProviderAnalyzer.TransitionDependency>();

            analyzer.CaptureProjectedIntegerWidening(operations[index], analysis, dependencies);
        }
    }
}
