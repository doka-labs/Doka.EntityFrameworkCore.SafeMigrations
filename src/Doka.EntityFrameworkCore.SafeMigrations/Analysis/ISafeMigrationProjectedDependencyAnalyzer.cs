namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Revalidates provider dependencies against the accepted ordered migration state.</summary>
internal interface ISafeMigrationProjectedDependencyAnalyzer
{
    /// <summary>Validates physical dependencies after provider-neutral projection.</summary>
    /// <param name="operation">The operation being assessed.</param>
    /// <param name="projectedAnalysis">The provider-neutral projected assessment.</param>
    /// <param name="columns">The accepted ordered column definitions.</param>
    /// <returns>The assessment qualified by physical dependency rules.</returns>
    SafeMigrationProviderAnalysis ValidateProjectedOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis projectedAnalysis,
        ISafeMigrationProjectedColumnSource columns);

    /// <summary>Advances dependencies only after the decision accepts an operation.</summary>
    /// <param name="operation">The operation that was assessed.</param>
    /// <param name="liveAnalysis">The immutable catalog assessment.</param>
    /// <param name="analysis">The final projected assessment.</param>
    /// <param name="decision">The decision authorizing an ordered effect.</param>
    void ObserveAcceptedOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision);

    /// <summary>Invalidates or advances dependencies affected by ordinary provider operations.</summary>
    /// <param name="operation">The provider operation encountered in the stream.</param>
    void ObserveProviderOperation(MigrationOperation operation);
}
