namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Describes the expected shape of a table for safe comparison.
/// </summary>
/// <param name="Table">The table name.</param>
/// <param name="Schema">The optional schema name.</param>
/// <param name="Columns">The expected table columns.</param>
/// <param name="PrimaryKey">The expected primary key, if any.</param>
public sealed record ExpectedTableDefinition(
    string Table,
    string? Schema,
    IReadOnlyList<ExpectedColumnDefinition> Columns,
    ExpectedPrimaryKeyDefinition? PrimaryKey = null
);
