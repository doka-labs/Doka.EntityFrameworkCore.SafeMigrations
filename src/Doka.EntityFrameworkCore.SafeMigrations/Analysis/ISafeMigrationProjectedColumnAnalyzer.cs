namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Qualifies exact projected column transitions with provider physical rules.</summary>
internal interface ISafeMigrationProjectedColumnAnalyzer
{
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
