namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Creates structured SafeMigrations SQL expressions.</summary>
[PublicAPI]
public static class SafeMigrationSql
{
    /// <summary>Creates an identifier expression.</summary>
    /// <param name="parts">The identifier parts in qualification order.</param>
    /// <returns>The identifier expression.</returns>
    public static SafeMigrationSqlExpression Identifier(
        params ReadOnlySpan<string> parts
    ) => new SafeMigrationSqlIdentifierExpression(

        // WHY: The span is borrowed; retain the explicit array snapshot passed to the immutable constructor.
        // ReSharper disable once UseCollectionExpression
        parts.ToArray());

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

    /// <summary>Creates a searched CASE without changing branch order or its conditional evaluation.</summary>
    /// <param name="whenClauses">The non-empty ordered WHEN/THEN clauses.</param>
    /// <param name="elseResult">The fallback expression, or null for an omitted ELSE.</param>
    /// <returns>The immutable searched CASE expression.</returns>
    public static SafeMigrationSqlExpression Case(
        IEnumerable<SafeMigrationSqlCaseWhenClause> whenClauses,
        SafeMigrationSqlExpression? elseResult = null
    ) => new SafeMigrationSqlCaseExpression(whenClauses, elseResult);

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
    ) => new SafeMigrationSqlFunctionExpression(
        name,

        // WHY: Materialize the borrowed argument span using the existing exact array representation.
        // ReSharper disable once UseCollectionExpression
        arguments.ToArray());

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
