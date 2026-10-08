namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Fixes the bounded integer literal domain independently of rendering or live table data.</summary>
public sealed class SqlServerIntegerCheckLiteralTests
{
    /// <summary>
    /// Only uncast integral values within the signed bigint domain authorize populated CHECK validation.
    /// </summary>
    /// <param name="kind">The literal's CLR form or exact boundary case.</param>
    /// <param name="negated">Whether the authored scalar applies unary negation.</param>
    /// <param name="safe">Whether the complete scalar remains in the admitted domain.</param>
    [Theory]
    [InlineData("byte", false, true)]
    [InlineData("sbyte", false, true)]
    [InlineData("short", false, true)]
    [InlineData("ushort", false, true)]
    [InlineData("int", false, true)]
    [InlineData("uint", false, true)]
    [InlineData("long-min", false, true)]
    [InlineData("long-max", false, true)]
    [InlineData("ulong-signed-max", false, true)]
    [InlineData("ulong-signed-min-magnitude", false, false)]
    [InlineData("ulong-signed-min-magnitude", true, true)]
    [InlineData("ulong-max", true, false)]
    [InlineData("long-min", true, false)]
    [InlineData("decimal-min", false, true)]
    [InlineData("decimal-max", false, true)]
    [InlineData("decimal-positive-overflow", false, false)]
    [InlineData("decimal-positive-overflow", true, true)]
    [InlineData("decimal-negative-overflow", false, false)]
    [InlineData("decimal-fraction", false, false)]
    [InlineData("decimal-type-max", false, false)]
    [InlineData("decimal-type-min", false, false)]
    [InlineData("double", false, false)]
    [InlineData("float", false, false)]
    [InlineData("bool", false, false)]
    [InlineData("string", false, false)]
    [InlineData("null", false, false)]
    [InlineData("cast", false, false)]
    [InlineData("cast", true, false)]
    public void IntegerLiteral_AdmitsExactSignedDomainOnly(
        string kind,
        bool negated,
        bool safe
    )
    {
        // Arrange
        SafeMigrationSqlExpression scalar = new SafeMigrationSqlLiteralExpression(Value(kind),
            kind == "cast" ? "bigint" : null);

        if (negated)
        {
            scalar = new SafeMigrationSqlUnaryExpression(SafeMigrationSqlUnaryOperator.Negate, scalar);
        }

        var predicate = new SafeMigrationSqlBinaryExpression(new SafeMigrationSqlIdentifierExpression(["Value"]),
            SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, scalar);

        // Act
        var actual = SqlServerSafeMigrationCatalogSqlBuilder.IsSafeIntegerCheckPredicate(predicate);

        // Assert
        Assert.Equal(safe, actual);
    }

    /// <summary>Produces each exact CLR scalar without a lossy intermediate conversion.</summary>
    private static object? Value(
        string kind
    )
        => kind switch
        {
            "byte" => byte.MaxValue,
            "sbyte" => sbyte.MinValue,
            "short" => short.MinValue,
            "ushort" => ushort.MaxValue,
            "int" => int.MinValue,
            "uint" => uint.MaxValue,
            "long-min" => long.MinValue,
            "long-max" => long.MaxValue,
            "ulong-signed-max" => (ulong)long.MaxValue,
            "ulong-signed-min-magnitude" => (ulong)long.MaxValue + 1,
            "ulong-max" => ulong.MaxValue,
            "decimal-min" => (decimal)long.MinValue,
            "decimal-max" => (decimal)long.MaxValue,
            "decimal-positive-overflow" => (decimal)long.MaxValue + 1,
            "decimal-negative-overflow" => (decimal)long.MinValue - 1,
            "decimal-fraction" => 0.5m,
            "decimal-type-max" => decimal.MaxValue,
            "decimal-type-min" => decimal.MinValue,
            "double" => 0d,
            "float" => 0f,
            "bool" => false,
            "string" => "0",
            "null" => null,
            "cast" => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}
