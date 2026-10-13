namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Revalidates provider-specific physical key feasibility against ordered
/// projected column definitions.
/// </summary>
internal interface ISafeMigrationProjectedKeyAnalyzer
{
    /// <summary>
    /// Gets whether unique constraints and ordinary unique indexes identify
    /// the same physical provider object.
    /// </summary>
    bool SharesUniqueConstraintAndIndexIdentity { get; }

    /// <summary>Gets whether PK identity is a provider-owned singleton independent of the authored name.</summary>
    /// <remarks>Providers with named primary-key constraints retain exact physical-name identity by default.</remarks>
    bool PrimaryKeyUsesNameIndependentIdentity => false;

    /// <summary>Applies provider physical limits to a projected index.</summary>
    /// <param name="intent">The index contract being projected.</param>
    /// <param name="columns">The ordered projected-column source.</param>
    /// <param name="liveAnalysis">The immutable live-catalog analysis.</param>
    /// <param name="projectedAnalysis">The provider-neutral projected state.</param>
    /// <returns>The provider-qualified projected state.</returns>
    SafeMigrationProviderAnalysis ValidateProjectedIndex(
        EnsureIndexIntent intent,
        ISafeMigrationProjectedColumnSource columns,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis
    );

    /// <summary>Applies provider physical limits to a projected primary key.</summary>
    /// <param name="intent">The primary-key contract being projected.</param>
    /// <param name="columns">The ordered projected-column source.</param>
    /// <param name="liveAnalysis">The immutable live-catalog analysis.</param>
    /// <param name="projectedAnalysis">The provider-neutral projected state.</param>
    /// <returns>The provider-qualified projected state.</returns>
    SafeMigrationProviderAnalysis ValidateProjectedPrimaryKey(
        EnsurePrimaryKeyIntent intent,
        ISafeMigrationProjectedColumnSource columns,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis
    );

    /// <summary>Applies provider physical limits to a projected unique constraint.</summary>
    /// <param name="intent">The unique-constraint contract being projected.</param>
    /// <param name="columns">The ordered projected-column source.</param>
    /// <param name="liveAnalysis">The immutable live-catalog analysis.</param>
    /// <param name="projectedAnalysis">The provider-neutral projected state.</param>
    /// <returns>The provider-qualified projected state.</returns>
    SafeMigrationProviderAnalysis ValidateProjectedUniqueConstraint(
        EnsureUniqueConstraintIntent intent,
        ISafeMigrationProjectedColumnSource columns,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis
    );
}

/// <summary>Provides exact ordered column definitions without allocating snapshots.</summary>
internal interface ISafeMigrationProjectedColumnSource
{
    /// <summary>Checks whether the current accepted primary key contains a projected column.</summary>
    /// <param name="table">The projected table identity.</param>
    /// <param name="schema">The optional provider-normalized owner schema.</param>
    /// <param name="column">The column member being changed.</param>
    /// <param name="columnComparer">The provider's column-only identity comparison rule.</param>
    /// <returns>Current accepted membership, or null when accepted key proof is unavailable.</returns>
    bool? IsProjectedPrimaryKeyColumn(
        string table, string? schema, string column, IEqualityComparer<string> columnComparer) => null;

    /// <summary>Checks accepted outgoing SET NULL actions before a required-column transition.</summary>
    /// <param name="table">The projected dependent table identity.</param>
    /// <param name="schema">The optional provider-normalized owner schema.</param>
    /// <param name="column">The dependent column that must become required.</param>
    /// <param name="columnComparer">The provider's comparison rule for column identifiers, not table names.</param>
    /// <returns>Whether a current accepted FK requires this column to permit NULL.</returns>
    bool HasProjectedSetNullForeignKey(
        string table, string? schema, string column, IEqualityComparer<string> columnComparer) => false;

    /// <summary>Attempts to read one exact projected column definition.</summary>
    /// <param name="table">The table name.</param>
    /// <param name="schema">The optional schema name.</param>
    /// <param name="column">The column name.</param>
    /// <param name="definition">The projected definition when known.</param>
    /// <returns><see langword="true" /> when the definition is known exactly.</returns>
    bool TryGetProjectedColumn(
        string table,
        string? schema,
        string column,
        [NotNullWhen(true)] out ExpectedColumnDefinition? definition
    );
}
