namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents a unary operation.</summary>
[PublicAPI]
public sealed class SafeMigrationSqlUnaryExpression : SafeMigrationSqlExpression
{
    /// <summary>Initializes a unary operation.</summary>
    /// <param name="operator">The unary operator.</param>
    /// <param name="operand">The operand.</param>
    public SafeMigrationSqlUnaryExpression(
        SafeMigrationSqlUnaryOperator @operator,
        SafeMigrationSqlExpression operand
    )
    {
        if (!Enum.IsDefined(@operator))
        {
            throw new ArgumentOutOfRangeException(nameof(@operator));
        }

        ArgumentNullException.ThrowIfNull(operand);

        Operator = @operator;
        Operand = operand;
    }

    /// <summary>Gets the unary operator.</summary>
    public SafeMigrationSqlUnaryOperator Operator { get; }

    /// <summary>Gets the operand.</summary>
    public SafeMigrationSqlExpression Operand { get; }
}
