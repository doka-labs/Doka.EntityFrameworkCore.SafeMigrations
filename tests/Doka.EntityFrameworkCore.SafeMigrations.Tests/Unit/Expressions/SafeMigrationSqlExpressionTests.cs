namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationSqlExpressionTests
{
    [Fact]
    public void Factories_CreateEveryStructuredNodeWithoutLosingRoles()
    {
        var identifier = SafeMigrationSql.Identifier("app", "value");
        var literal = SafeMigrationSql.Literal(42, "integer");
        var binary = SafeMigrationSql.Binary(identifier, SafeMigrationSqlBinaryOperator.GreaterThan, literal);
        var expression = SafeMigrationSql.Binary(
            SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, SafeMigrationSql.IsNull(identifier)),
            SafeMigrationSqlBinaryOperator.And,
            SafeMigrationSql.Binary(
                SafeMigrationSql.Between(identifier, SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(100)),
                SafeMigrationSqlBinaryOperator.Or,
                SafeMigrationSql.In(
                    identifier,
                    [SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2)],
                    negated: true)));

        var identifierValue = Assert.IsType<SafeMigrationSqlIdentifierExpression>(identifier);
        var literalValue = Assert.IsType<SafeMigrationSqlLiteralExpression>(literal);
        var binaryValue = Assert.IsType<SafeMigrationSqlBinaryExpression>(binary);

        Assert.Equal(["app", "value"], identifierValue.Parts);
        Assert.Equal(42, literalValue.Value);
        Assert.Equal("integer", literalValue.StoreType);
        Assert.Equal(SafeMigrationSqlBinaryOperator.GreaterThan, binaryValue.Operator);
        Assert.True(SafeMigrationSqlExpressionInspector.IsStructurallyComparable(expression));
    }

    [Fact]
    public void SpecializedFactories_PreserveProviderRelevantFacets()
    {
        var identifier = SafeMigrationSql.Identifier("value");
        var function = Assert.IsType<SafeMigrationSqlFunctionExpression>(
            SafeMigrationSql.Function("lower", identifier));

        var cast = Assert.IsType<SafeMigrationSqlCastExpression>(SafeMigrationSql.Cast(function, "text"));
        var collate = Assert.IsType<SafeMigrationSqlCollateExpression>(
            SafeMigrationSql.Collate(cast, "C", "pg_catalog"));

        var current = Assert.IsType<SafeMigrationSqlCurrentValueExpression>(
            SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp, precision: 6));

        var fragment = Assert.IsType<SafeMigrationSqlProviderFragmentExpression>(
            SafeMigrationSql.ProviderFragment("provider", "provider_expression"));

        var opaque = Assert.IsType<SafeMigrationSqlOpaqueExpression>(SafeMigrationSql.Opaque("value + 1"));

        Assert.Equal("lower", function.Name);
        Assert.Equal("text", cast.StoreType);
        Assert.Equal("C", collate.Name);
        Assert.Equal("pg_catalog", collate.Schema);
        Assert.Equal(6, current.Precision);
        Assert.Equal("provider", fragment.ProviderId);
        Assert.False(SafeMigrationSqlExpressionInspector.IsStructurallyComparable(fragment));
        Assert.False(SafeMigrationSqlExpressionInspector.IsStructurallyComparable(opaque));
    }

    [Fact]
    public void Constructors_RejectInvalidOrAmbiguousInputs()
    {
        Assert.Throws<ArgumentException>(() => SafeMigrationSql.Identifier());
        Assert.Throws<ArgumentOutOfRangeException>(() => SafeMigrationSql.Unary(
            (SafeMigrationSqlUnaryOperator)99,
            SafeMigrationSql.Literal(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SafeMigrationSql.Binary(
            SafeMigrationSql.Literal(1),
            (SafeMigrationSqlBinaryOperator)99,
            SafeMigrationSql.Literal(2)));
        Assert.Throws<ArgumentException>(() => SafeMigrationSql.In(SafeMigrationSql.Identifier("value"), []));
        Assert.Throws<ArgumentException>(() => SafeMigrationSql.Function("bad-name"));
        Assert.Throws<ArgumentOutOfRangeException>(() => SafeMigrationSql.Current((SafeMigrationSqlCurrentValue)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => SafeMigrationSql.Current(
            SafeMigrationSqlCurrentValue.Timestamp,
            precision: 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => SafeMigrationSql.Current(
            SafeMigrationSqlCurrentValue.Date,
            precision: 0));
    }

    [Fact]
    public void RenameIdentifier_ChangesOnlyTypedIdentifierNodes()
    {
        var expression = SafeMigrationSql.Binary(
            SafeMigrationSql.Identifier("source"),
            SafeMigrationSqlBinaryOperator.Equal,
            SafeMigrationSql.Literal("source"));

        var renamed = Assert.IsType<SafeMigrationSqlBinaryExpression>(
            SafeMigrationSqlExpressionInspector.RenameIdentifier(expression, "source", "target"));

        var identifier = Assert.IsType<SafeMigrationSqlIdentifierExpression>(renamed.Left);
        var literal = Assert.IsType<SafeMigrationSqlLiteralExpression>(renamed.Right);

        Assert.Equal(["target"], identifier.Parts);
        Assert.Equal("source", literal.Value);
    }

    [Fact]
    public void RenameIdentifier_MarksOpaqueAndProviderFragmentsAsUnproven()
    {
        var opaque = Assert.IsType<SafeMigrationSqlOpaqueExpression>(
            SafeMigrationSqlExpressionInspector.RenameIdentifier(
                SafeMigrationSql.Opaque("source + 1"),
                "source",
                "target"));

        var fragment = Assert.IsType<SafeMigrationSqlOpaqueExpression>(
            SafeMigrationSqlExpressionInspector.RenameIdentifier(
                SafeMigrationSql.ProviderFragment("provider", "source + 1"),
                "source",
                "target"));

        Assert.True(opaque.FollowsIdentifierRename);
        Assert.True(fragment.FollowsIdentifierRename);
        Assert.Equal("source + 1", opaque.Sql);
        Assert.Equal("source + 1", fragment.Sql);
    }

    [Fact]
    public void ExpressionContract_BindsEveryNodeFacetAndBinaryLiteralContent()
    {
        var baseline = SafeMigrationSql.Binary(
            SafeMigrationSql.Identifier("value"),
            SafeMigrationSqlBinaryOperator.Equal,
            SafeMigrationSql.Literal(new byte[] { 1, 2, 3 }, "bytea"));

        var equivalent = SafeMigrationSql.Binary(
            SafeMigrationSql.Identifier("value"),
            SafeMigrationSqlBinaryOperator.Equal,
            SafeMigrationSql.Literal(new byte[] { 1, 2, 3 }, "bytea"));

        var changed = SafeMigrationSql.Binary(
            SafeMigrationSql.Identifier("value"),
            SafeMigrationSqlBinaryOperator.NotEqual,
            SafeMigrationSql.Literal(new byte[] { 1, 2, 4 }, "bytea"));

        Assert.True(SafeMigrationSqlExpressionContract.Equivalent(baseline, equivalent));
        Assert.False(SafeMigrationSqlExpressionContract.Equivalent(baseline, changed));

        using var firstWriter = new CanonicalHashWriter();
        using var secondWriter = new CanonicalHashWriter();
        SafeMigrationSqlExpressionContract.Write(firstWriter, baseline);
        SafeMigrationSqlExpressionContract.Write(secondWriter, changed);

        Assert.NotEqual(firstWriter.GetHash(), secondWriter.GetHash());
    }

    [Fact]
    public void ExpressionContract_HandlesEveryStructuredNodeAndLiteralKind()
    {
        var pairs = new (SafeMigrationSqlExpression Left, SafeMigrationSqlExpression Right)[]
        {
            (SafeMigrationSql.Identifier("app", "value"), SafeMigrationSql.Identifier("app", "value")),
            (SafeMigrationSql.Literal(null), SafeMigrationSql.Literal(null)),
            (SafeMigrationSql.Literal(1.25F), SafeMigrationSql.Literal(1.25F)),
            (SafeMigrationSql.Literal(2.5D), SafeMigrationSql.Literal(2.5D)),
            (SafeMigrationSql.Literal(3.75M), SafeMigrationSql.Literal(3.75M)),
            (SafeMigrationSql.Literal(new DateOnly(2026, 8, 20)),
                SafeMigrationSql.Literal(new DateOnly(2026, 8, 20))),
            (SafeMigrationSql.Literal(new TimeOnly(12, 34, 56)),
                SafeMigrationSql.Literal(new TimeOnly(12, 34, 56))),
            (SafeMigrationSql.Literal(
                new DateTime(
                    2026,
                    8,
                    20,
                    12,
                    34,
                    56,
                    DateTimeKind.Utc)), SafeMigrationSql.Literal(
                new DateTime(
                    2026,
                    8,
                    20,
                    12,
                    34,
                    56,
                    DateTimeKind.Utc))),
            (SafeMigrationSql.Literal(
                new DateTimeOffset(
                    2026,
                    8,
                    20,
                    12,
                    34,
                    56,
                    TimeSpan.FromHours(2))), SafeMigrationSql.Literal(
                new DateTimeOffset(
                    2026,
                    8,
                    20,
                    12,
                    34,
                    56,
                    TimeSpan.FromHours(2)))),
            (SafeMigrationSql.Literal(TimeSpan.FromMinutes(5)), SafeMigrationSql.Literal(TimeSpan.FromMinutes(5))),
            (SafeMigrationSql.Literal(Guid.Parse("12345678-1234-1234-1234-123456789abc")),
                SafeMigrationSql.Literal(Guid.Parse("12345678-1234-1234-1234-123456789abc"))),
            (SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, SafeMigrationSql.Literal(1)),
                SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, SafeMigrationSql.Literal(1))),
            (SafeMigrationSql.IsNotNull(SafeMigrationSql.Identifier("value")),
                SafeMigrationSql.IsNotNull(SafeMigrationSql.Identifier("value"))),
            (SafeMigrationSql.Between(SafeMigrationSql.Identifier("value"), SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(10), negated: true),
                SafeMigrationSql.Between(
                    SafeMigrationSql.Identifier("value"),
                    SafeMigrationSql.Literal(1),
                    SafeMigrationSql.Literal(10),
                    negated: true)),
            (SafeMigrationSql.In(SafeMigrationSql.Identifier("value"), [SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2)]),
                SafeMigrationSql.In(
                    SafeMigrationSql.Identifier("value"),
                    [SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2)])),
            (SafeMigrationSql.Function("lower", SafeMigrationSql.Identifier("value")),
                SafeMigrationSql.Function("lower", SafeMigrationSql.Identifier("value"))),
            (SafeMigrationSql.Cast(SafeMigrationSql.Identifier("value"), "text"),
                SafeMigrationSql.Cast(SafeMigrationSql.Identifier("value"), "text")),
            (SafeMigrationSql.Collate(SafeMigrationSql.Identifier("value"), "C", "pg_catalog"),
                SafeMigrationSql.Collate(SafeMigrationSql.Identifier("value"), "C", "pg_catalog")),
            (SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Time, precision: 3),
                SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Time, precision: 3)),
            (SafeMigrationSql.ProviderFragment("provider", "CURRENT_USER"),
                SafeMigrationSql.ProviderFragment("provider", "CURRENT_USER")),
            (SafeMigrationSql.Opaque("value + 1"), SafeMigrationSql.Opaque("value + 1")),
        };

        using var writer = new CanonicalHashWriter();
        foreach (var pair in pairs)
        {
            Assert.True(SafeMigrationSqlExpressionContract.Equivalent(pair.Left, pair.Right));
            SafeMigrationSqlExpressionContract.Write(writer, pair.Left);
        }

        Assert.Equal(
            64,
            writer.GetHash()
                .Length);
    }

    [Fact]
    public void ExpressionContract_RejectsEveryChangedNodeFacet()
    {
        var identifier = SafeMigrationSql.Identifier("value");
        var changes = new (SafeMigrationSqlExpression Left, SafeMigrationSqlExpression Right)[]
        {
            (identifier, SafeMigrationSql.Identifier("other")), (identifier, SafeMigrationSql.Literal("value")),
            (SafeMigrationSql.Literal(1, "integer"), SafeMigrationSql.Literal(1, "bigint")),
            (SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, identifier),
                SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, identifier)),
            (SafeMigrationSql.Binary(identifier, SafeMigrationSqlBinaryOperator.Equal, SafeMigrationSql.Literal(1)),
                SafeMigrationSql.Binary(
                    identifier,
                    SafeMigrationSqlBinaryOperator.NotEqual,
                    SafeMigrationSql.Literal(1))),
            (SafeMigrationSql.IsNull(identifier), SafeMigrationSql.IsNotNull(identifier)),
            (SafeMigrationSql.Between(identifier, SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2)),
                SafeMigrationSql.Between(identifier, SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(3))),
            (SafeMigrationSql.In(identifier, [SafeMigrationSql.Literal(1)]),
                SafeMigrationSql.In(identifier, [SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2)])),
            (SafeMigrationSql.Function("lower", identifier), SafeMigrationSql.Function("upper", identifier)),
            (SafeMigrationSql.Cast(identifier, "text"), SafeMigrationSql.Cast(identifier, "varchar")),
            (SafeMigrationSql.Collate(identifier, "C", "pg_catalog"),
                SafeMigrationSql.Collate(identifier, "C", "app")),
            (SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Time, precision: 3),
                SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Time, precision: 6)),
            (SafeMigrationSql.ProviderFragment("provider", "CURRENT_USER"),
                SafeMigrationSql.ProviderFragment("other", "CURRENT_USER")),
            (SafeMigrationSql.Opaque("value + 1"), SafeMigrationSql.Opaque("value + 2")),
        };

        Assert.False(SafeMigrationSqlExpressionContract.Equivalent(null, identifier));
        Assert.All(changes, pair => Assert.False(SafeMigrationSqlExpressionContract.Equivalent(pair.Left, pair.Right)));
    }

    [Fact]
    public void Inspector_TraversesAndRenamesEveryStructuredNode()
    {
        var source = SafeMigrationSql.Identifier("source");
        var expressions = new SafeMigrationSqlExpression[]
        {
            source, SafeMigrationSql.Literal("source"),
            SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, source),
            SafeMigrationSql.Binary(source, SafeMigrationSqlBinaryOperator.Equal, SafeMigrationSql.Literal(1)),
            SafeMigrationSql.IsNotNull(source),
            SafeMigrationSql.Between(source, SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2)),
            SafeMigrationSql.In(source, [SafeMigrationSql.Literal(1)]), SafeMigrationSql.Function("lower", source),
            SafeMigrationSql.Cast(source, "text"), SafeMigrationSql.Collate(source, "C"),
            SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp),
        };

        foreach (var expression in expressions)
        {
            Assert.True(SafeMigrationSqlExpressionInspector.IsStructurallyComparable(expression));
            Assert.NotNull(SafeMigrationSqlExpressionInspector.RenameIdentifier(expression, "source", "target"));
        }
    }

    [Fact]
    public void CanonicalHashWriter_EnforcesLifecycleAndHandlesLargeValues()
    {
        var writer = new CanonicalHashWriter();
        writer.Add(new string('a', 1024));
        writer.Add((string?)null);
        writer.Add((int?)42);
        writer.Add((long?)42L);
        writer.Add((bool?)true);
        writer.Add(typeof(string));
        var hash = writer.GetHash();

        Assert.Equal(64, hash.Length);
        Assert.Throws<InvalidOperationException>(() => writer.GetHash());

        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => writer.GetHash());
    }
}
