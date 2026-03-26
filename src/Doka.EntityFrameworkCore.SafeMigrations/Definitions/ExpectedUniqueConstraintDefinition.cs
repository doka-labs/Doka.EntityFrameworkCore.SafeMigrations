namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Describes the expected shape of a unique constraint for safe comparison.
/// </summary>
/// <param name="Name">The unique-constraint name.</param>
/// <param name="Table">The table name.</param>
/// <param name="Schema">The optional schema name.</param>
/// <param name="Columns">The constrained column names.</param>
public sealed record ExpectedUniqueConstraintDefinition
(
    string Name,
    string Table,
    string? Schema,
    IReadOnlyList<string> Columns
);
