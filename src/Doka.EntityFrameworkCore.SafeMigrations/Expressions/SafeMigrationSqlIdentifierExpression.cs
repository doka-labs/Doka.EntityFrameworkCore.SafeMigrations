namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents a structured identifier path.</summary>
[PublicAPI]
public sealed class SafeMigrationSqlIdentifierExpression : SafeMigrationSqlExpression
{
    /// <summary>Initializes an identifier path.</summary>
    /// <param name="parts">The identifier parts in qualification order.</param>
    public SafeMigrationSqlIdentifierExpression(
        IEnumerable<string> parts
    )
    {
        Parts = SafeMigrationDefinitionValidator.Identifiers(parts, nameof(parts), allowEmpty: false);
    }

    /// <summary>Gets the identifier parts.</summary>
    public IReadOnlyList<string> Parts { get; }
}
