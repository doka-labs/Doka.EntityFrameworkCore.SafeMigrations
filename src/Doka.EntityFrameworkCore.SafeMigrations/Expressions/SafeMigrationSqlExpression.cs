namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents a provider-neutral SQL expression with explicit token roles.</summary>
public abstract class SafeMigrationSqlExpression;

/// <summary>Specifies a unary SQL operator.</summary>
public enum SafeMigrationSqlUnaryOperator
{
    /// <summary>Logical negation.</summary>
    Not = 0,

    /// <summary>Arithmetic negation.</summary>
    Negate = 1,
}

/// <summary>Specifies a binary SQL operator.</summary>
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

/// <summary>Specifies a standard current date or time value.</summary>
public enum SafeMigrationSqlCurrentValue
{
    /// <summary>The current date.</summary>
    Date = 0,

    /// <summary>The provider's current time value.</summary>
    Time = 1,

    /// <summary>The provider's current timestamp value.</summary>
    Timestamp = 2,
}

/// <summary>Represents a structured identifier path.</summary>
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

/// <summary>Represents a typed literal value.</summary>
public sealed class SafeMigrationSqlLiteralExpression : SafeMigrationSqlExpression
{
    /// <summary>Initializes a typed literal.</summary>
    /// <param name="value">The CLR value, including null.</param>
    /// <param name="storeType">The optional provider store type used for an explicit cast.</param>
    public SafeMigrationSqlLiteralExpression(
        object? value,
        string? storeType = null
    )
    {
        Value = value;
        StoreType = SafeMigrationDefinitionValidator.Optional(storeType, nameof(storeType));
    }

    /// <summary>Gets the CLR value.</summary>
    public object? Value { get; }

    /// <summary>Gets the explicit provider store type when specified.</summary>
    public string? StoreType { get; }
}

/// <summary>Represents a unary operation.</summary>
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

/// <summary>Represents a binary operation.</summary>
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

/// <summary>Represents an IS NULL or IS NOT NULL predicate.</summary>
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

/// <summary>Represents a BETWEEN predicate.</summary>
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

/// <summary>Represents an IN predicate.</summary>
public sealed class SafeMigrationSqlInExpression : SafeMigrationSqlExpression
{
    /// <summary>Initializes an IN predicate.</summary>
    /// <param name="operand">The tested operand.</param>
    /// <param name="values">The non-empty set of candidate values.</param>
    /// <param name="negated">Whether the predicate is NOT IN.</param>
    public SafeMigrationSqlInExpression(
        SafeMigrationSqlExpression operand,
        IEnumerable<SafeMigrationSqlExpression> values,
        bool negated = false
    )
    {
        ArgumentNullException.ThrowIfNull(operand);
        ArgumentNullException.ThrowIfNull(values);

        var materialized = values.ToArray();
        if (materialized.Length == 0
            || materialized.Any(static value => value is null))
        {
            throw new ArgumentException("An IN predicate requires non-null values.", nameof(values));
        }

        Operand = operand;
        Values = Array.AsReadOnly(materialized);
        Negated = negated;
    }

    /// <summary>Gets the tested operand.</summary>
    public SafeMigrationSqlExpression Operand { get; }

    /// <summary>Gets the candidate values.</summary>
    public IReadOnlyList<SafeMigrationSqlExpression> Values { get; }

    /// <summary>Gets whether the predicate is negated.</summary>
    public bool Negated { get; }
}

/// <summary>Represents a function call.</summary>
public sealed class SafeMigrationSqlFunctionExpression : SafeMigrationSqlExpression
{
    /// <summary>Initializes a function call.</summary>
    /// <param name="name">The unqualified SQL function name.</param>
    /// <param name="arguments">The function arguments.</param>
    public SafeMigrationSqlFunctionExpression(
        string name,
        IEnumerable<SafeMigrationSqlExpression>? arguments = null
    )
    {
        Name = SafeMigrationDefinitionValidator.Required(name, nameof(name));
        if (!(Name[0] == '_' || char.IsAsciiLetter(Name[0]))
            || !Name.All(static character => character == '_' || char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException(
                "A SQL function name must start with an ASCII letter or underscore and contain only ASCII letters, digits, or underscores.",
                nameof(name));
        }

        var materialized = (arguments ?? []).ToArray();
        if (materialized.Any(static value => value is null))
        {
            throw new ArgumentException("Function arguments cannot contain null entries.", nameof(arguments));
        }

        Arguments = Array.AsReadOnly(materialized);
    }

    /// <summary>Gets the function name.</summary>
    public string Name { get; }

    /// <summary>Gets the function arguments.</summary>
    public IReadOnlyList<SafeMigrationSqlExpression> Arguments { get; }
}

/// <summary>Represents an explicit provider store-type cast.</summary>
public sealed class SafeMigrationSqlCastExpression : SafeMigrationSqlExpression
{
    /// <summary>Initializes a cast.</summary>
    /// <param name="operand">The cast operand.</param>
    /// <param name="storeType">The provider store type.</param>
    public SafeMigrationSqlCastExpression(
        SafeMigrationSqlExpression operand,
        string storeType
    )
    {
        ArgumentNullException.ThrowIfNull(operand);

        Operand = operand;
        StoreType = SafeMigrationDefinitionValidator.Required(storeType, nameof(storeType));
    }

    /// <summary>Gets the cast operand.</summary>
    public SafeMigrationSqlExpression Operand { get; }

    /// <summary>Gets the provider store type.</summary>
    public string StoreType { get; }
}

/// <summary>Represents an explicit collation clause.</summary>
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

/// <summary>Represents a standard current date or time value.</summary>
public sealed class SafeMigrationSqlCurrentValueExpression : SafeMigrationSqlExpression
{
    /// <summary>Initializes a current-value expression.</summary>
    /// <param name="value">The current value to read.</param>
    /// <param name="precision">The optional fractional-second precision.</param>
    public SafeMigrationSqlCurrentValueExpression(
        SafeMigrationSqlCurrentValue value,
        int? precision = null
    )
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        if (precision is < 0 or > 6
            || (value == SafeMigrationSqlCurrentValue.Date && precision is not null))
        {
            throw new ArgumentOutOfRangeException(nameof(precision));
        }

        Value = value;
        Precision = precision;
    }

    /// <summary>Gets the current value to read.</summary>
    public SafeMigrationSqlCurrentValue Value { get; }

    /// <summary>Gets the fractional-second precision when specified.</summary>
    public int? Precision { get; }
}

/// <summary>Represents provider-owned SQL whose structure cannot be compared by Core.</summary>
public sealed class SafeMigrationSqlProviderFragmentExpression : SafeMigrationSqlExpression
{
    /// <summary>Initializes a provider fragment.</summary>
    /// <param name="providerId">The provider contract identifier.</param>
    /// <param name="sql">The provider-owned SQL.</param>
    public SafeMigrationSqlProviderFragmentExpression(
        string providerId,
        string sql
    )
    {
        ProviderId = SafeMigrationDefinitionValidator.Required(providerId, nameof(providerId));
        Sql = SafeMigrationDefinitionValidator.Required(sql, nameof(sql));
    }

    /// <summary>Gets the provider contract identifier.</summary>
    public string ProviderId { get; }

    /// <summary>Gets the provider-owned SQL.</summary>
    public string Sql { get; }
}

/// <summary>Represents opaque SQL that cannot prove catalog equivalence.</summary>
public sealed class SafeMigrationSqlOpaqueExpression : SafeMigrationSqlExpression
{
    /// <summary>Initializes opaque SQL.</summary>
    /// <param name="sql">The opaque SQL text.</param>
    public SafeMigrationSqlOpaqueExpression(
        string sql
    ) : this(sql, followsIdentifierRename: false) { }

    internal SafeMigrationSqlOpaqueExpression(
        string sql,
        bool followsIdentifierRename
    )
    {
        Sql = SafeMigrationDefinitionValidator.Required(sql, nameof(sql));
        FollowsIdentifierRename = followsIdentifierRename;
    }

    /// <summary>Gets the opaque SQL.</summary>
    public string Sql { get; }

    /// <summary>Gets whether an identifier rename made this expression unproven.</summary>
    public bool FollowsIdentifierRename { get; }
}

/// <summary>Creates structured SafeMigrations SQL expressions.</summary>
public static class SafeMigrationSql
{
    /// <summary>Creates an identifier expression.</summary>
    /// <param name="parts">The identifier parts in qualification order.</param>
    /// <returns>The identifier expression.</returns>
    public static SafeMigrationSqlExpression Identifier(
        params ReadOnlySpan<string> parts
    ) => new SafeMigrationSqlIdentifierExpression(parts.ToArray());

    /// <summary>Creates a typed literal expression.</summary>
    /// <param name="value">The CLR value, including null.</param>
    /// <param name="storeType">The optional provider store type used for an explicit cast.</param>
    /// <returns>The literal expression.</returns>
    public static SafeMigrationSqlExpression Literal(
        object? value,
        string? storeType = null
    ) => new SafeMigrationSqlLiteralExpression(value, storeType);

    /// <summary>Creates a binary expression.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="operator">The binary operator.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The binary expression.</returns>
    public static SafeMigrationSqlExpression Binary(
        SafeMigrationSqlExpression left,
        SafeMigrationSqlBinaryOperator @operator,
        SafeMigrationSqlExpression right
    ) => new SafeMigrationSqlBinaryExpression(left, @operator, right);

    /// <summary>Creates a unary expression.</summary>
    /// <param name="operator">The unary operator.</param>
    /// <param name="operand">The operand.</param>
    /// <returns>The unary expression.</returns>
    public static SafeMigrationSqlExpression Unary(
        SafeMigrationSqlUnaryOperator @operator,
        SafeMigrationSqlExpression operand
    ) => new SafeMigrationSqlUnaryExpression(@operator, operand);

    /// <summary>Creates an IS NULL predicate.</summary>
    /// <param name="operand">The tested operand.</param>
    /// <returns>The null-test expression.</returns>
    public static SafeMigrationSqlExpression IsNull(
        SafeMigrationSqlExpression operand
    ) => new SafeMigrationSqlNullTestExpression(operand);

    /// <summary>Creates an IS NOT NULL predicate.</summary>
    /// <param name="operand">The tested operand.</param>
    /// <returns>The negated null-test expression.</returns>
    public static SafeMigrationSqlExpression IsNotNull(
        SafeMigrationSqlExpression operand
    ) => new SafeMigrationSqlNullTestExpression(operand, negated: true);

    /// <summary>Creates a BETWEEN predicate.</summary>
    /// <param name="operand">The tested operand.</param>
    /// <param name="lower">The inclusive lower bound.</param>
    /// <param name="upper">The inclusive upper bound.</param>
    /// <param name="negated">Whether the predicate is NOT BETWEEN.</param>
    /// <returns>The BETWEEN expression.</returns>
    public static SafeMigrationSqlExpression Between(
        SafeMigrationSqlExpression operand,
        SafeMigrationSqlExpression lower,
        SafeMigrationSqlExpression upper,
        bool negated = false
    ) => new SafeMigrationSqlBetweenExpression(operand, lower, upper, negated);

    /// <summary>Creates an IN predicate.</summary>
    /// <param name="operand">The tested operand.</param>
    /// <param name="values">The non-empty set of candidate values.</param>
    /// <param name="negated">Whether the predicate is NOT IN.</param>
    /// <returns>The IN expression.</returns>
    public static SafeMigrationSqlExpression In(
        SafeMigrationSqlExpression operand,
        IEnumerable<SafeMigrationSqlExpression> values,
        bool negated = false
    ) => new SafeMigrationSqlInExpression(operand, values, negated);

    /// <summary>Creates a function call.</summary>
    /// <param name="name">The unqualified SQL function name.</param>
    /// <param name="arguments">The function arguments.</param>
    /// <returns>The function-call expression.</returns>
    public static SafeMigrationSqlExpression Function(
        string name,
        params ReadOnlySpan<SafeMigrationSqlExpression> arguments
    ) => new SafeMigrationSqlFunctionExpression(name, arguments.ToArray());

    /// <summary>Creates an explicit provider store-type cast.</summary>
    /// <param name="operand">The cast operand.</param>
    /// <param name="storeType">The provider store type.</param>
    /// <returns>The cast expression.</returns>
    public static SafeMigrationSqlExpression Cast(
        SafeMigrationSqlExpression operand,
        string storeType
    ) => new SafeMigrationSqlCastExpression(operand, storeType);

    /// <summary>Creates an explicit collation clause.</summary>
    /// <param name="operand">The collated operand.</param>
    /// <param name="name">The collation name.</param>
    /// <param name="schema">The optional collation schema.</param>
    /// <returns>The collation expression.</returns>
    public static SafeMigrationSqlExpression Collate(
        SafeMigrationSqlExpression operand,
        string name,
        string? schema = null
    ) => new SafeMigrationSqlCollateExpression(operand, name, schema);

    /// <summary>Creates a standard current date or time value.</summary>
    /// <param name="value">The current value to read.</param>
    /// <param name="precision">The optional fractional-second precision.</param>
    /// <returns>The current-value expression.</returns>
    public static SafeMigrationSqlExpression Current(
        SafeMigrationSqlCurrentValue value,
        int? precision = null
    ) => new SafeMigrationSqlCurrentValueExpression(value, precision);

    /// <summary>Creates provider-owned SQL that Core cannot compare structurally.</summary>
    /// <param name="providerId">The provider contract identifier.</param>
    /// <param name="sql">The provider-owned SQL.</param>
    /// <returns>The provider-fragment expression.</returns>
    public static SafeMigrationSqlExpression ProviderFragment(
        string providerId,
        string sql
    ) => new SafeMigrationSqlProviderFragmentExpression(providerId, sql);

    /// <summary>Creates an opaque expression that cannot authorize a catalog match.</summary>
    /// <param name="sql">The opaque SQL text.</param>
    /// <returns>The opaque expression.</returns>
    public static SafeMigrationSqlExpression Opaque(
        string sql
    ) => new SafeMigrationSqlOpaqueExpression(sql);

    internal static SafeMigrationSqlExpression OpaqueAfterRename(
        string sql
    ) => new SafeMigrationSqlOpaqueExpression(sql, followsIdentifierRename: true);
}
