namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Qualifies exact projected column transitions with provider physical rules.</summary>
internal interface ISafeMigrationProjectedColumnAnalyzer
{
    /// <summary>Certifies an accepted structural mutation that preserves existing intrinsic column sources.</summary>
    /// <param name="intent">The unchanged, accepted mutation contract.</param>
    /// <returns>True only for provider-qualified metadata changes, never a complete key or row proof.</returns>
    /// <remarks>
    /// Core applies this result only to <see cref="EnsureIndexIntent"/>, <see cref="DropPrimaryKeyIntent"/>
    /// and <see cref="DropForeignKeyIntent"/>. Other intent kinds cannot opt into source preservation
    /// through this callback; their separate projection contracts remain authoritative.
    /// </remarks>
    bool PreservesIntrinsicColumnSources(SafeMigrationIntent intent) => false;

    /// <summary>Certifies an exact live Alter result after accepted column-preserving key changes.</summary>
    /// <param name="intent">The independently analyzed source and target contract.</param>
    /// <param name="liveAnalysis">The captured intrinsic source and session evidence.</param>
    /// <returns>True only when the transition is independent of the changed physical key layout.</returns>
    bool CanReuseLiveColumnProofAfterKeyChanges(
        AlterColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => false;

    /// <summary>Certifies an Ensure result without inventing old metadata after accepted key changes.</summary>
    /// <param name="intent">The independently analyzed immutable target.</param>
    /// <param name="liveAnalysis">The complete captured native facet and repair evidence.</param>
    /// <returns>True only for independently proven matching or intrinsic-layout-preserving results.</returns>
    bool CanReuseLiveColumnProofAfterKeyChanges(
        EnsureColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => false;

    /// <summary>Qualifies a projected Missing column add without changing existing target equality.</summary>
    /// <param name="intent">The unchanged immutable add contract.</param>
    /// <param name="context">The accepted owner lifetime and versioned row-proof boundaries.</param>
    /// <param name="liveAnalysis">The independently captured provider result and session identity.</param>
    /// <param name="projectedAnalysis">The resolved Missing result before provider qualification.</param>
    /// <returns>The qualified add result; providers that do not opt in retain existing behavior.</returns>
    /// <remarks>Core calls this only for Missing, never for known Matching or unresolved owner state.</remarks>
    SafeMigrationProviderAnalysis ValidateProjectedMissingColumnAdd(
        EnsureColumnIntent intent,
        SafeMigrationProjectedMissingColumnContext context,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis
    ) => projectedAnalysis;

    /// <summary>Certifies one exact live column result after accepted foreign-key-only removals.</summary>
    /// <param name="intent">The unchanged operation contract analyzed against the live source.</param>
    /// <param name="liveAnalysis">The provider's independently captured source-bound result.</param>
    /// <returns>True only when this provider's column proof is unaffected by those removals.</returns>
    /// <remarks>
    /// Core retains only removals and certified nullability changes, rejecting stale rows, keys and dependencies.
    /// This opt-in does not certify the table's complete physical shape or replace live source evidence.
    /// </remarks>
    bool CanReuseLiveColumnProofAfterForeignKeyDrops(
        AlterColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => false;

    /// <summary>Certifies a current source-bound Ensure result after bounded foreign-key removals.</summary>
    /// <param name="intent">The unchanged target independently analyzed against the live column.</param>
    /// <param name="liveAnalysis">The provider's captured source, row and repair evidence.</param>
    /// <returns>True only when the accepted removal history preserves this exact live result.</returns>
    bool CanReuseLiveColumnProofAfterForeignKeyDrops(
        EnsureColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => false;

    /// <summary>Certifies sibling-proof preservation for an independently safe Ensure repair.</summary>
    /// <param name="intent">The reviewed target backed by current provider evidence.</param>
    /// <param name="source">The unchanged source retained by an accepted earlier observation.</param>
    /// <param name="liveAnalysis">The independently captured source-bound repair result.</param>
    /// <returns>True only for a supported same-layout, non-data nullability change.</returns>
    bool PreservesSiblingColumnProofsAfterForeignKeyDrops(
        EnsureColumnIntent intent,
        ExpectedColumnDefinition source,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => false;

    /// <summary>Compares accepted metadata with a provider-qualified physical source.</summary>
    /// <param name="accepted">The immutable source retained by an accepted earlier observation.</param>
    /// <param name="source">The declaration independently qualified by the current live provider result.</param>
    /// <returns>True only when both declarations preserve the provider's exact physical source contract.</returns>
    /// <remarks>
    /// Core calls this only after the provider opted into bounded live-proof reuse and all lifetime guards passed.
    /// The default retains complete annotation equality; overrides must validate any nonphysical metadata difference.
    /// </remarks>
    bool AreProjectedColumnSourcesEquivalent(
        ExpectedColumnDefinition accepted,
        ExpectedColumnDefinition source
    ) => SafeMigrationDefinitionEquivalence.Column(accepted, source);

    /// <summary>Checks a transition after Core has verified its exact old definition.</summary>
    /// <param name="intent">The reviewed transition.</param>
    /// <param name="source">The exact accepted source column.</param>
    /// <param name="context">The accepted physical shape and versioned proof boundaries.</param>
    /// <param name="liveAnalysis">The source-bound immutable provider evidence.</param>
    /// <param name="projectedAnalysis">The conservative provider-neutral result.</param>
    /// <returns>The provider-qualified transition result.</returns>
    /// <remarks>
    /// Consume the borrowed table view synchronously within this callback. Do not retain it or
    /// its enumerables, or enumerate concurrently: Core observes the result only after return.
    /// </remarks>
    SafeMigrationProviderAnalysis ValidateProjectedAlterColumn(
        AlterColumnIntent intent,
        ExpectedColumnDefinition source,
        SafeMigrationProjectedAlterColumnContext context,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis
    );
}

/// <summary>Groups accepted owner and row facts for a Missing column add.</summary>
/// <param name="TableWasCreated">Whether an accepted operation created the current physical owner.</param>
/// <param name="ProvesEmpty">Whether a current versioned empty-table proof remains valid.</param>
/// <param name="HasDataMutation">Whether accepted operations invalidated the captured live row proof.</param>
/// <param name="ColumnWasDropped">Whether accepted absence invalidates an original owned-staging certificate.</param>
internal readonly record struct SafeMigrationProjectedMissingColumnContext(
    bool TableWasCreated,
    bool ProvesEmpty,
    bool HasDataMutation,
    bool ColumnWasDropped
);

/// <summary>Groups physical provenance and row-proof lifetime for one projected transition.</summary>
/// <param name="Table">The accepted table view, borrowed only for the synchronous validation callback.</param>
/// <param name="HasCompleteTable">Whether the view contains every physical column and key.</param>
/// <param name="ProvesEmpty">Whether a current versioned proof establishes an empty table.</param>
/// <param name="HasForeignKeyDependency">Whether an accepted foreign key references the changing column.</param>
/// <param name="CanReuseLiveProof">Whether the immutable complete physical proof is still current.</param>
/// <param name="HasDataMutation">Whether earlier operations invalidated live row evidence.</param>
/// <param name="PreservesLiveValueDomain">Whether certified column changes preserve the captured value domain.</param>
/// <param name="CanReuseCreationCharacterSet">
/// Whether the captured default charset applies to this created table.
/// </param>
internal readonly record struct SafeMigrationProjectedAlterColumnContext(
    ISafeMigrationProjectedAlterTable Table,
    bool HasCompleteTable,
    bool ProvesEmpty,
    bool HasForeignKeyDependency,
    bool CanReuseLiveProof,
    bool HasDataMutation,
    bool PreservesLiveValueDomain,
    bool CanReuseCreationCharacterSet
);

/// <summary>Reads accepted table facts without copying collections per operation.</summary>
/// <remarks>
/// This is a callback-scoped view, not an immutable snapshot. Repeated enumeration is stable
/// during validation because Core applies accepted mutations only after the callback returns.
/// </remarks>
internal interface ISafeMigrationProjectedAlterTable : ISafeMigrationProjectedColumnSource
{
    /// <summary>Gets the accepted columns.</summary>
    IEnumerable<ExpectedColumnDefinition> Columns { get; }

    /// <summary>Gets the accepted primary key.</summary>
    ExpectedPrimaryKeyDefinition? PrimaryKey { get; }

    /// <summary>Gets the accepted unique constraints.</summary>
    IEnumerable<ExpectedUniqueConstraintDefinition> UniqueConstraints { get; }

    /// <summary>Gets the accepted indexes.</summary>
    IEnumerable<ExpectedIndexDefinition> Indexes { get; }
}

/// <summary>Exposes immutable analyses captured against a candidate rename's physical source.</summary>
internal interface ISafeMigrationRenamedTableAnalyzer
{
    /// <summary>Looks up evidence only after Core accepted the corresponding physical rename.</summary>
    /// <param name="operation">The unchanged operation contract.</param>
    /// <param name="sourceTable">The accepted rename's original physical table.</param>
    /// <param name="sourceSchema">The original physical schema.</param>
    /// <param name="analysis">The source-bound evidence when available.</param>
    /// <returns>Whether evidence exists for this exact operation and source identity.</returns>
    bool TryGetRenamedTableAnalysis(
        SafeMigrationOperation operation,
        string sourceTable,
        string? sourceSchema,
        [NotNullWhen(true)] out SafeMigrationProviderAnalysis? analysis
    );
}
