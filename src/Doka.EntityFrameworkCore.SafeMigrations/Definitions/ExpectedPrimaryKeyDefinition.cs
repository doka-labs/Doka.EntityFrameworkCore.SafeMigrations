namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Describes the expected shape of a primary key for safe comparison.
/// </summary>
/// <param name="Name">The primary-key name.</param>
/// <param name="Table">The table name.</param>
/// <param name="Schema">The optional schema name.</param>
/// <param name="Columns">The primary-key column names.</param>
public sealed record ExpectedPrimaryKeyDefinition(
    string Name,
    string Table,
    string? Schema,
    IReadOnlyList<string> Columns
);
