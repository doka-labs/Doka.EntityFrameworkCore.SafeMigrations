namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Specifies a unary SQL operator.</summary>
[PublicAPI]
public enum SafeMigrationSqlUnaryOperator
{
    /// <summary>Logical negation.</summary>
    Not = 0,

    /// <summary>Arithmetic negation.</summary>
    Negate = 1,
}
