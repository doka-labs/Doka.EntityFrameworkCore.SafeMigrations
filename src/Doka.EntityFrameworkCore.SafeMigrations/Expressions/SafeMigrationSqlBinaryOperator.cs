namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Specifies a binary SQL operator.</summary>
[PublicAPI]
public enum SafeMigrationSqlBinaryOperator
{
    /// <summary>Logical conjunction.</summary>
    And = 0,

    /// <summary>Logical disjunction.</summary>
    Or = 1,

    /// <summary>Equality comparison.</summary>
    Equal = 2,

    /// <summary>Inequality comparison.</summary>
    NotEqual = 3,

    /// <summary>Less-than comparison.</summary>
    LessThan = 4,

    /// <summary>Less-than-or-equal comparison.</summary>
    LessThanOrEqual = 5,

    /// <summary>Greater-than comparison.</summary>
    GreaterThan = 6,

    /// <summary>Greater-than-or-equal comparison.</summary>
    GreaterThanOrEqual = 7,

    /// <summary>Addition.</summary>
    Add = 8,

    /// <summary>Subtraction.</summary>
    Subtract = 9,

    /// <summary>Multiplication.</summary>
    Multiply = 10,

    /// <summary>Division.</summary>
    Divide = 11,

    /// <summary>Remainder.</summary>
    Modulo = 12,
}
