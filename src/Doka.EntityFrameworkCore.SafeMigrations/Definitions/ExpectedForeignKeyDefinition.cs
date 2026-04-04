namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Describes the expected shape of a foreign key for safe comparison.
/// </summary>
/// <param name="Name">The foreign-key name.</param>
/// <param name="Table">The dependent table name.</param>
/// <param name="Schema">The optional dependent schema name.</param>
/// <param name="Columns">The dependent column names.</param>
/// <param name="PrincipalTable">The principal table name.</param>
/// <param name="PrincipalSchema">The optional principal schema name.</param>
/// <param name="PrincipalColumns">The principal column names.</param>
/// <param name="OnUpdate">The expected update action.</param>
/// <param name="OnDelete">The expected delete action.</param>
public sealed record ExpectedForeignKeyDefinition(
    string Name,
    string Table,
    string? Schema,
    IReadOnlyList<string> Columns,
    string PrincipalTable,
    string? PrincipalSchema,
    IReadOnlyList<string> PrincipalColumns,
    ReferentialAction OnUpdate,
    ReferentialAction OnDelete
);
