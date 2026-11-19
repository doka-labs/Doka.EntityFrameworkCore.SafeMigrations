namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents an explicit collation clause.</summary>
[PublicAPI]
public sealed class SafeMigrationSqlCollateExpression : SafeMigrationSqlExpression
{
    /// <summary>Initializes a collation clause.</summary>
    /// <param name="operand">The collated operand.</param>
    /// <param name="name">The collation name.</param>
    /// <param name="schema">The optional collation schema.</param>
    public SafeMigrationSqlCollateExpression(
        SafeMigrationSqlExpression operand,
        string name,
        string? schema = null
    )
    {
        ArgumentNullException.ThrowIfNull(operand);

        Operand = operand;
        Name = SafeMigrationDefinitionValidator.Required(name, nameof(name));
        Schema = SafeMigrationDefinitionValidator.Optional(schema, nameof(schema));
    }

    /// <summary>Gets the collated operand.</summary>
    public SafeMigrationSqlExpression Operand { get; }

    /// <summary>Gets the collation name.</summary>
    public string Name { get; }

    /// <summary>Gets the collation schema when specified.</summary>
    public string? Schema { get; }
}
