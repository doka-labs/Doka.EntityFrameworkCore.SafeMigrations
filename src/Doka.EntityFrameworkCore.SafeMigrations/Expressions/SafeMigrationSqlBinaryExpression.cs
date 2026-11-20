namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents a binary operation.</summary>
[PublicAPI]
public sealed class SafeMigrationSqlBinaryExpression : SafeMigrationSqlExpression
{
    /// <summary>Initializes a binary operation.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="operator">The binary operator.</param>
    /// <param name="right">The right operand.</param>
    public SafeMigrationSqlBinaryExpression(
        SafeMigrationSqlExpression left,
        SafeMigrationSqlBinaryOperator @operator,
        SafeMigrationSqlExpression right
    )
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (!Enum.IsDefined(@operator))
        {
            throw new ArgumentOutOfRangeException(nameof(@operator));
        }

        Left = left;
        Operator = @operator;
        Right = right;
    }

    /// <summary>Gets the left operand.</summary>
    public SafeMigrationSqlExpression Left { get; }

    /// <summary>Gets the binary operator.</summary>
    public SafeMigrationSqlBinaryOperator Operator { get; }

    /// <summary>Gets the right operand.</summary>
    public SafeMigrationSqlExpression Right { get; }
}
