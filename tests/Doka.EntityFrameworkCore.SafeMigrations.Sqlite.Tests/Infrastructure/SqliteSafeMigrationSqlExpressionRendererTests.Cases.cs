namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>
/// Provides isolated SQLite coverage for safe migration SQL expression renderer.
/// </summary>
public sealed partial class SqliteSafeMigrationSqlExpressionRendererTests
{
    /// <summary>Gets serializable operator cases shared by rendering and completeness tests.</summary>
    public static TheoryData<SafeMigrationSqlBinaryOperator, string> BinaryOperatorCases() => new()
    {
        { SafeMigrationSqlBinaryOperator.And, "(1 AND 2)" },
        { SafeMigrationSqlBinaryOperator.Or, "(1 OR 2)" },
        { SafeMigrationSqlBinaryOperator.Equal, "(1 = 2)" },
        { SafeMigrationSqlBinaryOperator.NotEqual, "(1 <> 2)" },
        { SafeMigrationSqlBinaryOperator.LessThan, "(1 < 2)" },
        { SafeMigrationSqlBinaryOperator.LessThanOrEqual, "(1 <= 2)" },
        { SafeMigrationSqlBinaryOperator.GreaterThan, "(1 > 2)" },
        { SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, "(1 >= 2)" },
        { SafeMigrationSqlBinaryOperator.Add, "(1 + 2)" },
        { SafeMigrationSqlBinaryOperator.Subtract, "(1 - 2)" },
        { SafeMigrationSqlBinaryOperator.Multiply, "(1 * 2)" },
        { SafeMigrationSqlBinaryOperator.Divide, "(1 / 2)" },
        { SafeMigrationSqlBinaryOperator.Modulo, "(1 % 2)" },
    };

    /// <summary>Restores each original heterogeneous input from a discoverable stable identifier.</summary>
    private static SafeMigrationSqlExpression CreateStructuredExpression(
        string caseId
    )
    {
        var value = SafeMigrationSql.Identifier("app", "Value");

        // WHY: Only serializable case IDs enter xUnit discovery; expression
        // objects belong to their invocation rather than shared theory data.
        return caseId switch
        {
            "identifier" => value,
            "null" => SafeMigrationSql.Literal(null),
            "typed-null" => SafeMigrationSql.Literal(null, "INTEGER"),
            "literal" => SafeMigrationSql.Literal(42),
            "typed-literal" => SafeMigrationSql.Literal(42, "INTEGER"),
            "not" => SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, value),
            "negate" => SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, value),
            "is-null" => SafeMigrationSql.IsNull(value),
            "is-not-null" => SafeMigrationSql.IsNotNull(value),
            "between" => SafeMigrationSql.Between(value, SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(10)),
            "not-between" => SafeMigrationSql.Between(
                value,
                SafeMigrationSql.Literal(1),
                SafeMigrationSql.Literal(10),
                negated: true),
            "in" => SafeMigrationSql.In(value, [SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2)]),
            "not-in" => SafeMigrationSql.In(value, [SafeMigrationSql.Literal(1)], negated: true),
            "function" => SafeMigrationSql.Function("LOWER", value),
            "cast" => SafeMigrationSql.Cast(value, "TEXT"),
            "collate" => SafeMigrationSql.Collate(value, "NOCASE"),
            "current-date" => SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Date),
            "current-time" => SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Time),
            "current-timestamp" => SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp),
            "provider-fragment" => SafeMigrationSql.ProviderFragment("microsoft_sqlite", "CURRENT_USER"),
            "opaque" => SafeMigrationSql.Opaque("value + 1"),
            _ => throw new ArgumentOutOfRangeException(nameof(caseId), caseId, "Unknown structured expression case."),
        };
    }

    /// <summary>Creates unsupported inputs without arbitrary CLR objects in theory discovery.</summary>
    private static SafeMigrationSqlExpression CreateUnsupportedExpression(
        string caseId
    )
    {
        return caseId switch
        {
            "unknown-literal" => SafeMigrationSql.Literal(new object()),
            "nested-unknown-literal" => SafeMigrationSql.Function(
                "COALESCE",
                SafeMigrationSql.Literal(0),
                SafeMigrationSql.Literal(new object())),
            "unsafe-literal-type" => SafeMigrationSql.Literal(1, "INTEGER); DROP TABLE items; --"),
            "unsafe-cast" => SafeMigrationSql.Cast(
                SafeMigrationSql.Identifier("value"),
                "INTEGER); DROP TABLE items; --"),
            "unsafe-cast-literal" => SafeMigrationSql.Cast(
                SafeMigrationSql.Literal(1),
                "INTEGER); DROP TABLE items; --"),
            "qualified-collation" => SafeMigrationSql.Collate(SafeMigrationSql.Identifier("value"), "NOCASE", "main"),
            "precise-current-value" => SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp, precision: 6),
            "foreign-fragment" => SafeMigrationSql.ProviderFragment("foreign_provider", "CURRENT_USER"),
            _ => throw new ArgumentOutOfRangeException(nameof(caseId), caseId, "Unknown unsupported expression case."),
        };
    }
}
