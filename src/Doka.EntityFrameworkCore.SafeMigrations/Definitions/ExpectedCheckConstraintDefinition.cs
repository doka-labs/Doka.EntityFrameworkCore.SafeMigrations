namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Describes the expected shape of a check constraint for safe comparison.
/// </summary>
/// <param name="Name">The check-constraint name.</param>
/// <param name="Table">The table name.</param>
/// <param name="Schema">The optional schema name.</param>
/// <param name="Sql">The expected check expression.</param>
public sealed record ExpectedCheckConstraintDefinition(
    string Name,
    string Table,
    string? Schema,
    string Sql
);
