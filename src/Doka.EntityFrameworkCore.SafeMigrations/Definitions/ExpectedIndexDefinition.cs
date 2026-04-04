namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Describes the expected shape of an index for safe comparison.
/// </summary>
/// <param name="Name">The index name.</param>
/// <param name="Table">The table name.</param>
/// <param name="Schema">The optional schema name.</param>
/// <param name="Columns">The indexed column names.</param>
/// <param name="Unique">Whether the index is expected to be unique.</param>
/// <param name="Filter">The expected filter expression, if any.</param>
/// <param name="Descending">The expected per-column descending flags, if any.</param>
public sealed record ExpectedIndexDefinition(
    string Name,
    string Table,
    string? Schema,
    IReadOnlyList<string> Columns,
    bool Unique,
    string? Filter = null,
    IReadOnlyList<bool>? Descending = null
);
