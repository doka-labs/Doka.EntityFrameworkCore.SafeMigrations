namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents an IS NULL or IS NOT NULL predicate.</summary>
[PublicAPI]
public sealed class SafeMigrationSqlNullTestExpression : SafeMigrationSqlExpression
{
    /// <summary>Initializes a null test.</summary>
    /// <param name="operand">The tested operand.</param>
    /// <param name="negated">Whether the predicate is IS NOT NULL.</param>
    public SafeMigrationSqlNullTestExpression(
        SafeMigrationSqlExpression operand,
        bool negated = false
    )
    {
        ArgumentNullException.ThrowIfNull(operand);

        Operand = operand;
        Negated = negated;
    }

    /// <summary>Gets the tested operand.</summary>
    public SafeMigrationSqlExpression Operand { get; }

    /// <summary>Gets whether the predicate is negated.</summary>
    public bool Negated { get; }
}
