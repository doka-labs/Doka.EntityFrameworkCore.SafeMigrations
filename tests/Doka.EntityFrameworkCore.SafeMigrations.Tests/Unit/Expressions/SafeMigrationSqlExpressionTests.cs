namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies structured SQL factories, role-preserving renames and canonical contract identity.</summary>
public sealed class SafeMigrationSqlExpressionTests
{
    /// <summary>Composition retains identifier, literal and predicate-node roles.</summary>
    [Fact]
    public void Factories_CreateEveryStructuredNodeWithoutLosingRoles()
    {
        // Arrange: The factory graph contains every predicate child position.

        // Act
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

        // Assert
        var identifierValue = Assert.IsType<SafeMigrationSqlIdentifierExpression>(identifier);
        var literalValue = Assert.IsType<SafeMigrationSqlLiteralExpression>(literal);
        var binaryValue = Assert.IsType<SafeMigrationSqlBinaryExpression>(binary);

        Assert.Equal(["app", "value"], identifierValue.Parts);
        Assert.Equal(42, literalValue.Value);
        Assert.Equal("integer", literalValue.StoreType);
        Assert.Equal(SafeMigrationSqlBinaryOperator.GreaterThan, binaryValue.Operator);
        Assert.True(SafeMigrationSqlExpressionInspector.IsStructurallyComparable(expression));
    }

    /// <summary>Specialized factories retain native type, collation, precision and opaque provenance.</summary>
    [Fact]
    public void SpecializedFactories_PreserveProviderRelevantFacets()
    {
        // Arrange
        var identifier = SafeMigrationSql.Identifier("value");

        // Act
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

        // Assert
        Assert.Equal("lower", function.Name);
        Assert.Equal("text", cast.StoreType);
        Assert.Equal("C", collate.Name);
        Assert.Equal("pg_catalog", collate.Schema);
        Assert.Equal(6, current.Precision);
        Assert.Equal("provider", fragment.ProviderId);
        Assert.False(SafeMigrationSqlExpressionInspector.IsStructurallyComparable(fragment));
        Assert.False(SafeMigrationSqlExpressionInspector.IsStructurallyComparable(opaque));
    }

    /// <summary>Each invalid factory input retains its exact argument-exception family.</summary>
    /// <param name="boundary">The independently invalid factory boundary.</param>
    /// <param name="exceptionType">The expected argument-exception type.</param>
    [Theory]
    [InlineData(0, typeof(ArgumentException))]
    [InlineData(1, typeof(ArgumentOutOfRangeException))]
    [InlineData(2, typeof(ArgumentOutOfRangeException))]
    [InlineData(3, typeof(ArgumentException))]
    [InlineData(4, typeof(ArgumentException))]
    [InlineData(5, typeof(ArgumentOutOfRangeException))]
    [InlineData(6, typeof(ArgumentOutOfRangeException))]
    [InlineData(7, typeof(ArgumentOutOfRangeException))]
    public void Constructors_RejectInvalidOrAmbiguousInputs(
        int boundary,
        Type exceptionType
    )
    {
        // Arrange
        Func<SafeMigrationSqlExpression> construct = boundary switch
        {
            0 => static () => SafeMigrationSql.Identifier(),
            1 => static () => SafeMigrationSql.Unary((SafeMigrationSqlUnaryOperator)99, SafeMigrationSql.Literal(1)),
            2 => static () => SafeMigrationSql.Binary(SafeMigrationSql.Literal(1),
                (SafeMigrationSqlBinaryOperator)99,
                SafeMigrationSql.Literal(2)),
            3 => static () => SafeMigrationSql.In(SafeMigrationSql.Identifier("value"), []),
            4 => static () => SafeMigrationSql.Function("bad-name"),
            5 => static () => SafeMigrationSql.Current((SafeMigrationSqlCurrentValue)99),
            6 => static () => SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp, precision: 7),
            7 => static () => SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Date, precision: 0),
            _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
        };

        // Act
        var exception = Record.Exception(() => { _ = construct(); });

        // Assert
        Assert.IsType(exceptionType, exception);
    }

    /// <summary>A rename changes typed identifiers without rewriting equal literal text.</summary>
    [Fact]
    public void RenameIdentifier_ChangesOnlyTypedIdentifierNodes()
    {
        // Arrange
        var expression = SafeMigrationSql.Binary(
            SafeMigrationSql.Identifier("source"),
            SafeMigrationSqlBinaryOperator.Equal,
            SafeMigrationSql.Literal("source"));

        // Act
        var result = SafeMigrationSqlExpressionInspector.RenameIdentifier(expression, "source", "target");

        // Assert
        var renamed = Assert.IsType<SafeMigrationSqlBinaryExpression>(result);
        var identifier = Assert.IsType<SafeMigrationSqlIdentifierExpression>(renamed.Left);
        var literal = Assert.IsType<SafeMigrationSqlLiteralExpression>(renamed.Right);

        Assert.Equal(["target"], identifier.Parts);
        Assert.Equal("source", literal.Value);
    }

    /// <summary>A rename cannot certify the rewritten meaning of opaque or provider-authored SQL.</summary>
    /// <param name="providerFragment">Whether the unproven source has a provider identity.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenameIdentifier_MarksOpaqueAndProviderFragmentsAsUnproven(
        bool providerFragment
    )
    {
        // Arrange
        var expression = providerFragment
            ? SafeMigrationSql.ProviderFragment("provider", "source + 1")
            : SafeMigrationSql.Opaque("source + 1");

        // Act
        var result = SafeMigrationSqlExpressionInspector.RenameIdentifier(expression, "source", "target");

        // Assert
        var opaque = Assert.IsType<SafeMigrationSqlOpaqueExpression>(result);
        Assert.True(opaque.FollowsIdentifierRename);
        Assert.Equal("source + 1", opaque.Sql);
    }

    /// <summary>Binary content and operator differences affect equality and canonical fingerprints together.</summary>
    [Fact]
    public void ExpressionContract_BindsEveryNodeFacetAndBinaryLiteralContent()
    {
        // Arrange
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

        using var firstWriter = new CanonicalHashWriter();
        using var secondWriter = new CanonicalHashWriter();

        // Act
        var equal = SafeMigrationSqlExpressionContract.Equivalent(baseline, equivalent);
        var unequal = SafeMigrationSqlExpressionContract.Equivalent(baseline, changed);
        SafeMigrationSqlExpressionContract.Write(firstWriter, baseline);
        SafeMigrationSqlExpressionContract.Write(secondWriter, changed);
        var firstHash = firstWriter.GetHash();
        var secondHash = secondWriter.GetHash();

        // Assert
        Assert.True(equal);
        Assert.False(unequal);
        Assert.NotEqual(firstHash, secondHash);
    }

    /// <summary>Each structured node and literal domain retains equality and a complete canonical hash.</summary>
    /// <param name="caseIndex">The independently reported node or literal pair.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    public void ExpressionContract_HandlesEveryStructuredNodeAndLiteralKind(
        int caseIndex
    )
    {
        // Arrange
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
            (SafeMigrationSql.Between(
                    SafeMigrationSql.Identifier("value"),
                    SafeMigrationSql.Literal(1),
                    SafeMigrationSql.Literal(10),
                    negated: true),
                SafeMigrationSql.Between(
                    SafeMigrationSql.Identifier("value"),
                    SafeMigrationSql.Literal(1),
                    SafeMigrationSql.Literal(10),
                    negated: true)),
            (SafeMigrationSql.In(
                    SafeMigrationSql.Identifier("value"),
                    [SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2)]),
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

        var pair = pairs[caseIndex];
        using var writer = new CanonicalHashWriter();

        // Act
        var equal = SafeMigrationSqlExpressionContract.Equivalent(pair.Left, pair.Right);
        SafeMigrationSqlExpressionContract.Write(writer, pair.Left);
        var hash = writer.GetHash();

        // Assert
        Assert.True(equal);
        Assert.Equal(64, hash.Length);
    }

    /// <summary>Each changed structural facet prevents equality independently of the remaining corpus.</summary>
    /// <param name="caseIndex">The independently reported changed facet.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void ExpressionContract_RejectsEveryChangedNodeFacet(
        int caseIndex
    )
    {
        // Arrange
        var identifier = SafeMigrationSql.Identifier("value");
        var changes = new (SafeMigrationSqlExpression Left, SafeMigrationSqlExpression Right)[]
        {
            (identifier, SafeMigrationSql.Identifier("other")),
            (identifier, SafeMigrationSql.Literal("value")),
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

        var pair = changes[caseIndex];

        // Act
        var equivalent = SafeMigrationSqlExpressionContract.Equivalent(pair.Left, pair.Right);

        // Assert
        Assert.False(equivalent);
    }

    /// <summary>An absent expression cannot equal a structured identifier.</summary>
    [Fact]
    public void ExpressionContract_RejectsMissingExpression()
    {
        // Arrange
        var identifier = SafeMigrationSql.Identifier("value");

        // Act
        var equivalent = SafeMigrationSqlExpressionContract.Equivalent(null, identifier);

        // Assert
        Assert.False(equivalent);
    }

    /// <summary>Every supported node remains inspectable and traversable through a typed rename.</summary>
    /// <param name="caseIndex">The independently reported supported node.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void Inspector_TraversesAndRenamesEveryStructuredNode(
        int caseIndex
    )
    {
        // Arrange
        var source = SafeMigrationSql.Identifier("source");
        var expressions = new[]
        {
            source,
            SafeMigrationSql.Literal("source"),
            SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, source),
            SafeMigrationSql.Binary(source, SafeMigrationSqlBinaryOperator.Equal, SafeMigrationSql.Literal(1)),
            SafeMigrationSql.IsNotNull(source),
            SafeMigrationSql.Between(source, SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2)),
            SafeMigrationSql.In(source, [SafeMigrationSql.Literal(1)]),
            SafeMigrationSql.Function("lower", source),
            SafeMigrationSql.Cast(source, "text"),
            SafeMigrationSql.Collate(source, "C"),
            SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp),
        };

        var expression = expressions[caseIndex];

        // Act
        var comparable = SafeMigrationSqlExpressionInspector.IsStructurallyComparable(expression);
        var renamed = SafeMigrationSqlExpressionInspector.RenameIdentifier(expression, "source", "target");

        // Assert
        Assert.True(comparable);
        Assert.NotNull(renamed);
    }

    /// <summary>Hash finalization and disposal preserve their distinct lifecycle rejection contracts.</summary>
    [Fact]
    public void CanonicalHashWriter_EnforcesLifecycleAndHandlesLargeValues()
    {
        // Arrange
        var writer = new CanonicalHashWriter();

        // Act
        writer.Add(new string('a', 1024));
        writer.Add((string?)null);
        writer.Add((int?)42);
        writer.Add((long?)42L);
        writer.Add((bool?)true);
        writer.Add(typeof(string));
        var hash = writer.GetHash();

        // WHY: Record.Exception invokes this probe synchronously before the explicit disposal below.
        // ReSharper disable once AccessToDisposedClosure
        var finalizedFailure = Record.Exception(() => writer.GetHash());
        writer.Dispose();
        var disposedFailure = Record.Exception(() => writer.GetHash());

        // Assert
        Assert.Equal(64, hash.Length);
        Assert.IsType<InvalidOperationException>(finalizedFailure);
        Assert.IsType<ObjectDisposedException>(disposedFailure);
    }
}
