namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents a BETWEEN predicate.</summary>
[PublicAPI]
public sealed class SafeMigrationSqlBetweenExpression : SafeMigrationSqlExpression
{
    /// <summary>Initializes a BETWEEN predicate.</summary>
    /// <param name="operand">The tested operand.</param>
    /// <param name="lower">The inclusive lower bound.</param>
    /// <param name="upper">The inclusive upper bound.</param>
    /// <param name="negated">Whether the predicate is NOT BETWEEN.</param>
    public SafeMigrationSqlBetweenExpression(
        SafeMigrationSqlExpression operand,
        SafeMigrationSqlExpression lower,
        SafeMigrationSqlExpression upper,
        bool negated = false
    )
    {
        ArgumentNullException.ThrowIfNull(operand);
        ArgumentNullException.ThrowIfNull(lower);
        ArgumentNullException.ThrowIfNull(upper);

        Operand = operand;
        Lower = lower;
        Upper = upper;
        Negated = negated;
    }

    /// <summary>Gets the tested operand.</summary>
    public SafeMigrationSqlExpression Operand { get; }

    /// <summary>Gets the inclusive lower bound.</summary>
    public SafeMigrationSqlExpression Lower { get; }

    /// <summary>Gets the inclusive upper bound.</summary>
    public SafeMigrationSqlExpression Upper { get; }

    /// <summary>Gets whether the predicate is negated.</summary>
    public bool Negated { get; }
}
