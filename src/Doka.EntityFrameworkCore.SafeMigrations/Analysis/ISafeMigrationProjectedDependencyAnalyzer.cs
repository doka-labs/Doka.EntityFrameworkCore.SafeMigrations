namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Revalidates provider dependencies against the accepted ordered migration state.</summary>
internal interface ISafeMigrationProjectedDependencyAnalyzer
{
    /// <summary>Binds subsequent ordered observations to their original migration-stream position.</summary>
    /// <param name="operationOrdinal">The zero-based ordinal in the complete, not safe-only, stream.</param>
    void SetCurrentOperationOrdinal(
        int operationOrdinal
    ) { }

    /// <summary>Qualifies captured facts before neutral shortcuts reuse them after provider side effects.</summary>
    /// <param name="operation">The next immutable operation in the ordered stream.</param>
    /// <param name="liveAnalysis">The assessment captured before preceding operations execute.</param>
    /// <param name="columns">The accepted ordered column definitions.</param>
    /// <returns>A provider-qualified assessment, or null when neutral projection remains authoritative.</returns>
    SafeMigrationProviderAnalysis? ValidateCapturedProjection(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis,
        ISafeMigrationProjectedColumnSource columns
    ) => null;

    /// <summary>Proves an operation after a provider transition opaque to neutral projection.</summary>
    /// <param name="operation">The operation being assessed.</param>
    /// <param name="liveAnalysis">The immutable catalog assessment.</param>
    /// <param name="columns">The accepted ordered column definitions.</param>
    /// <returns>A provider-proven ordered assessment, or null when the transition remains unproven.</returns>
    SafeMigrationProviderAnalysis? ValidateOpaqueProviderPostcondition(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis,
        ISafeMigrationProjectedColumnSource columns
    ) => null;

    /// <summary>Qualifies provider-owned table facets against the accepted physical table lifetime.</summary>
    /// <param name="intent">The target table contract.</param>
    /// <param name="acceptedAnnotations">The immutable facets of the accepted table definition.</param>
    /// <param name="newlyCreated">Whether that accepted definition created a new physical table.</param>
    /// <param name="liveAnalysis">The original catalog analysis of the target.</param>
    /// <returns>A mandatory refusal, or null when provider facets do not prevent intrinsic shape projection.</returns>
    SafeMigrationProviderAnalysis? ValidateProjectedTableProviderMetadata(
        EnsureTableIntent intent,
        IReadOnlyList<SafeMigrationProviderAnnotation> acceptedAnnotations,
        bool newlyCreated,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => intent.Definition.ProviderAnnotations.Count == 0
        || SafeMigrationDefinitionEquivalence.ProviderAnnotations(
            acceptedAnnotations,
            intent.Definition.ProviderAnnotations)
            ? null
            : new SafeMigrationProviderAnalysis(SafeMigrationObservedState.PrerequisiteMissing,
                SafeMigrationRepairCapability.None,
                false,
                "projected_table_provider_metadata_unproven");

    /// <summary>Validates physical dependencies after provider-neutral projection.</summary>
    /// <param name="operation">The operation being assessed.</param>
    /// <param name="projectedAnalysis">The provider-neutral projected assessment.</param>
    /// <param name="columns">The accepted ordered column definitions.</param>
    /// <returns>The assessment qualified by physical dependency rules.</returns>
    SafeMigrationProviderAnalysis ValidateProjectedOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis projectedAnalysis,
        ISafeMigrationProjectedColumnSource columns
    );

    /// <summary>Advances dependencies only after the decision accepts an operation.</summary>
    /// <param name="operation">The operation that was assessed.</param>
    /// <param name="liveAnalysis">The immutable catalog assessment.</param>
    /// <param name="analysis">The final projected assessment.</param>
    /// <param name="decision">The decision authorizing an ordered effect.</param>
    void ObserveAcceptedOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision
    );

    /// <summary>Invalidates or advances dependencies affected by ordinary provider operations.</summary>
    /// <param name="operation">The provider operation encountered in the stream.</param>
    void ObserveProviderOperation(
        MigrationOperation operation
    );
}
