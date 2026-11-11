namespace Doka.EntityFrameworkCore.SafeMigrations.Benchmarks;

/// <summary>Shares the exact prepared projection workload between functional tests and BenchmarkDotNet.</summary>
internal sealed class PreparedColumnProjectionWorkload
{
    private readonly SafeMigrationDecision _decision;
    private readonly SafeMigrationProviderAnalysis _live;
    private readonly SafeMigrationOperation[] _operations;

    /// <summary>Prepares immutable inputs without charging their construction to projection measurement.</summary>
    /// <param name="count">The nonnegative number of accepted columns in one invocation.</param>
    public PreparedColumnProjectionWorkload(
        int count
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _operations = Enumerable
            .Range(0, count)
            .Select(index => new SafeMigrationOperation(
                new EnsureColumnIntent("items",
                    new ExpectedColumnDefinition($"value_{index}", typeof(int), true, "int")),
                SafeMigrationPolicy.ThrowIfDifferent))
            .ToArray();

        _live = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None,
            false,
            "live_missing");

        _decision = SafeMigrationDecisionPlanner.Plan(
            SafeMigrationOperationKind.EnsureColumn,
            _live.ObservedState,
            SafeMigrationPolicy.ThrowIfDifferent);
    }

    /// <summary>Projects a fresh invocation while reusing only its immutable authored inputs.</summary>
    /// <returns>The number of accepted definitions independently classified as matching.</returns>
    public int ProjectAcceptedColumns()
    {
        // WHY: A projection is invocation-local mutable evidence, not a warmed cross-run cache.
        var projection = new SafeMigrationPreflightProjection();
        var matching = 0;

        foreach (var operation in _operations)
        {
            projection.Observe(operation, _live, _live, _decision);
            matching += projection.Project(operation, _live)
                    .ObservedState
                == SafeMigrationObservedState.Matching
                    ? 1
                    : 0;
        }

        return matching;
    }
}
