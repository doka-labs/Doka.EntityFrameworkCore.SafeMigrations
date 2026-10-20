namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Exposes compact accepted table proofs to provider-specific validators.</summary>
internal interface ISafeMigrationProjectedTableSource
{
    /// <summary>Checks current accepted generated dependencies; nondependency requires a complete owner.</summary>
    /// <param name="table">The physical owner name.</param>
    /// <param name="schema">The provider-qualified owner schema.</param>
    /// <param name="column">The source member whose generated dependents must be checked.</param>
    /// <param name="columnComparer">The provider's column-member equality, never its table-name policy.</param>
    /// <returns>True for a dependency, false for complete comparable nondependency, or null if unproven.</returns>
    bool? HasProjectedGeneratedColumnDependency(
        string table,
        string? schema,
        string column,
        IEqualityComparer<string> columnComparer
    ) => null;

    /// <summary>Attempts to read the ordered proof associated with a known table.</summary>
    /// <param name="table">The table name.</param>
    /// <param name="schema">The optional provider schema.</param>
    /// <param name="state">The accepted proof when the table is known.</param>
    /// <returns>Whether earlier accepted operations established the table state.</returns>
    bool TryGetProjectedTableState(
        string table,
        string? schema,
        out SafeMigrationProjectedTableState state);
}

/// <summary>Contains only the table proofs consumed by physical provider validation.</summary>
/// <param name="IsNewlyCreated">Whether the ordered stream created an initially empty table.</param>
/// <param name="HasDataMutation">Whether earlier unanalysed data changes invalidate row proofs.</param>
/// <param name="PrimaryKeyWasDropped">Whether an accepted drop removed the table's primary key.</param>
/// <param name="HasUnknownStructure">Whether earlier operations invalidated structural proof.</param>
internal readonly record struct SafeMigrationProjectedTableState(
    bool IsNewlyCreated,
    bool HasDataMutation,
    bool PrimaryKeyWasDropped,
    bool HasUnknownStructure);
