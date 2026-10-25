namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationSqlExpressionRendererTests
{
    /// <summary>Restores the original inputs without serializing expression objects through xUnit v2.</summary>
    private static (SafeMigrationSqlExpression Expression, string Sql) StructuredNodeCase(string caseId)
    {
        var value = SafeMigrationSql.Identifier("app", "Value");

        return caseId switch
        {
            "identifier" => (value, "app.\"Value\""),
            "null" => (SafeMigrationSql.Literal(null), "NULL"),
            "typed-null" => (SafeMigrationSql.Literal(null, "integer"), "NULL::integer"),
            "alias-null" => (SafeMigrationSql.Literal(null, "int4"), "NULL::integer"),
            "array-null" => (SafeMigrationSql.Literal(null, "int4[]"), "NULL::integer[]"),
            "integer" => (SafeMigrationSql.Literal(42), "42"),
            "typed-integer" => (SafeMigrationSql.Literal(42, "bigint"), "42::bigint"),
            "not" => (SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, value), "(NOT app.\"Value\")"),
            "negate" => (SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, value), "(-app.\"Value\")"),
            "is-null" => (SafeMigrationSql.IsNull(value), "(app.\"Value\" IS NULL)"),
            "is-not-null" => (SafeMigrationSql.IsNotNull(value), "(app.\"Value\" IS NOT NULL)"),
            "between" => (SafeMigrationSql.Between(value, SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(10)),
                "(app.\"Value\" BETWEEN 1 AND 10)"),
            "in" => (SafeMigrationSql.In(value, [SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2)]),
                "(app.\"Value\" IN (1, 2))"),
            "function" => (SafeMigrationSql.Function("LOWER", value), "lower(app.\"Value\")"),
            "cast" => (SafeMigrationSql.Cast(value, "text"), "CAST(app.\"Value\" AS text)"),
            "alias-cast" => (SafeMigrationSql.Cast(value, "int4"), "CAST(app.\"Value\" AS integer)"),
            "collate" => (SafeMigrationSql.Collate(value, "C", "pg_catalog"),
                "(app.\"Value\" COLLATE pg_catalog.\"C\")"),
            "current-date" => (SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Date), "CURRENT_DATE"),
            "current-time" => (SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Time, precision: 3),
                "CURRENT_TIME(3)"),
            "current-timestamp" => (SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp),
                "CURRENT_TIMESTAMP"),
            "provider-fragment" => (SafeMigrationSql.ProviderFragment("npgsql_postgresql", "CURRENT_USER"),
                "CURRENT_USER"),
            "opaque" => (SafeMigrationSql.Opaque("value + 1"), "value + 1"),
            _ => throw new ArgumentOutOfRangeException(nameof(caseId), caseId, "Unknown structured node case."),
        };
    }

    /// <summary>Keeps the original unsupported inputs independently discoverable with stable IDs.</summary>
    private static SafeMigrationSqlExpression UnsupportedExpressionCase(string caseId) => caseId switch
    {
        "unmapped-literal" => SafeMigrationSql.Literal(new object()),
        "unsafe-cast-literal" => SafeMigrationSql.Cast(SafeMigrationSql.Literal(1),
            "integer); DROP TABLE items; --"),
        "unsafe-cast-identifier" => SafeMigrationSql.Cast(SafeMigrationSql.Identifier("value"),
            "integer); DROP TABLE items; --"),
        "foreign-fragment" => SafeMigrationSql.ProviderFragment("other", "CURRENT_USER"),
        _ => throw new ArgumentOutOfRangeException(nameof(caseId), caseId, "Unknown unsupported-input case."),
    };
}
