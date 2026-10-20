namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Preserves inspection and rename semantics independently of public expression-tree depth.</summary>
public sealed class SafeMigrationExpressionInspectorStackTests
{
    /// <summary>Inspects every accepted child position without recursive traversal.</summary>
    /// <param name="shape">The repeated public expression shape.</param>
    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("unary")]
    [InlineData("between")]
    [InlineData("in")]
    [InlineData("function")]
    [InlineData("case")]
    [InlineData("cast")]
    [InlineData("collate")]
    [InlineData("null-test")]
    public void DeepTreesRetainIdentifiersAndStructuralEligibility(string shape)
    {
        // Arrange
        var expression = Nest(shape, SafeMigrationSql.Identifier("owner", "source"));
        var identifiers = new HashSet<string>(StringComparer.Ordinal);

        // Act
        SafeMigrationSqlExpressionInspector.CollectIdentifiers(expression, identifiers);
        var referenced = SafeMigrationSqlExpressionInspector.ReferencesIdentifier(expression, "source");
        var unrelated = SafeMigrationSqlExpressionInspector.ReferencesIdentifier(expression, "absent");
        var comparable = SafeMigrationSqlExpressionInspector.IsStructurallyComparable(expression);

        // Assert
        Assert.Equal(["source"], identifiers);
        Assert.True(referenced);
        Assert.False(unrelated);
        Assert.True(comparable);
    }

    /// <summary>Rebuilds deeply nested references without changing owner qualifiers or opaque leaf rules.</summary>
    /// <param name="shape">The repeated public expression shape.</param>
    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("unary")]
    [InlineData("between")]
    [InlineData("in")]
    [InlineData("function")]
    [InlineData("case")]
    [InlineData("cast")]
    [InlineData("collate")]
    [InlineData("null-test")]
    public void DeepRenamesPreserveQualifiedMembers(string shape)
    {
        // Arrange
        var expression = Nest(shape, SafeMigrationSql.Identifier("Source", "source"));

        // Act
        var renamed = SafeMigrationSqlExpressionInspector.RenameColumnIdentifier(
            expression, "SOURCE", "target", StringComparer.OrdinalIgnoreCase);

        // Assert
        Assert.True(SafeMigrationSqlExpressionInspector.ReferencesIdentifier(renamed, "target"));
        Assert.False(SafeMigrationSqlExpressionInspector.ReferencesIdentifier(renamed, "source"));
        Assert.True(SafeMigrationSqlExpressionInspector.ReferencesIdentifier(expression, "source"));
        var leaf = Terminal(shape, renamed);
        Assert.Equal(["Source", "target"], Assert.IsType<SafeMigrationSqlIdentifierExpression>(leaf).Parts);
    }

    /// <summary>Opaque leaves remain a refusal, including when the leaf is beyond normal call-stack depth.</summary>
    /// <param name="shape">The repeated public expression shape.</param>
    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("unary")]
    [InlineData("between")]
    [InlineData("in")]
    [InlineData("function")]
    [InlineData("case")]
    public void DeepOpaqueLeavesRemainUnproven(string shape)
    {
        // Arrange
        var expression = Nest(shape, SafeMigrationSql.Opaque("private_payload"));

        // Act
        var renamed = SafeMigrationSqlExpressionInspector.RenameIdentifier(expression, "source", "target");
        var comparable = SafeMigrationSqlExpressionInspector.IsStructurallyComparable(renamed);

        // Assert
        Assert.False(comparable);
        Assert.IsType<SafeMigrationSqlOpaqueExpression>(Terminal(shape, renamed));
    }

    /// <summary>Collection order and reference-search order remain intentionally different for IN.</summary>
    [Fact]
    public void InCollectionAndReferenceSearchPreserveTheirOriginalOrder()
    {
        // Arrange
        var expression = SafeMigrationSql.In(SafeMigrationSql.Identifier("operand"),
            [SafeMigrationSql.Identifier("first"), SafeMigrationSql.Identifier("second")]);
        var collected = new RecordingSet();
        var comparer = new RecordingComparer();

        // Act
        SafeMigrationSqlExpressionInspector.CollectIdentifiers(expression, collected);
        var found = SafeMigrationSqlExpressionInspector.ReferencesColumnIdentifier(expression, "absent", comparer);

        // Assert
        Assert.False(found);
        Assert.Equal(["operand", "first", "second"], collected.Seen);
        Assert.Equal(["first", "second", "operand"], comparer.Seen);
    }

    /// <summary>CASE rename retains the old constructor's ELSE-before-lazy-clauses evaluation order.</summary>
    [Fact]
    public void CaseRenamePreservesElseFirstComparerOrder()
    {
        // Arrange
        var expression = new SafeMigrationSqlCaseExpression(
            [new(SafeMigrationSql.Identifier("condition"), SafeMigrationSql.Identifier("result"))],
            SafeMigrationSql.Identifier("fallback"));
        var comparer = new RecordingComparer();

        // Act
        var renamed = SafeMigrationSqlExpressionInspector.RenameColumnIdentifier(
            expression, "absent", "target", comparer);

        // Assert
        Assert.Equal(["fallback", "condition", "result"], comparer.Seen);
        Assert.True(SafeMigrationSqlExpressionContract.Equivalent(expression, renamed));
    }

    /// <summary>Wide lists preserve every child and its order without a pending work frame per sibling.</summary>
    /// <param name="shape">The variable-width child container.</param>
    [Theory]
    [InlineData("function")]
    [InlineData("in")]
    [InlineData("case")]
    public void WideRenamesRetainEveryOrderedChild(string shape)
    {
        // Arrange
        var names = Enumerable.Range(0, 9_999).Select(static value => $"member{value}").Append("source").ToArray();
        var children = names.Select(static value => SafeMigrationSql.Identifier(value)).ToArray();
        SafeMigrationSqlExpression expression = shape switch
        {
            "function" => new SafeMigrationSqlFunctionExpression("COALESCE", children),
            "in" => new SafeMigrationSqlInExpression(SafeMigrationSql.Identifier("operand"), children),
            "case" => new SafeMigrationSqlCaseExpression(children.Select(static value =>
                new SafeMigrationSqlCaseWhenClause(SafeMigrationSql.Literal(true), value)),
                SafeMigrationSql.Identifier("fallback")),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        var expected = names[..^1].Append("target");
        expected = shape == "in" ? expected.Prepend("operand")
            : shape == "case" ? expected.Append("fallback") : expected;
        var collected = new RecordingSet();

        // Act
        var renamed = SafeMigrationSqlExpressionInspector.RenameIdentifier(expression, "source", "target");
        SafeMigrationSqlExpressionInspector.CollectIdentifiers(renamed, collected);
        var sourceRemains = SafeMigrationSqlExpressionInspector.ReferencesIdentifier(renamed, "source");

        // Assert
        Assert.Equal(expected, collected.Seen);
        Assert.Equal(shape == "function" ? 10_000 : 10_001, collected.Count);
        Assert.False(sourceRemains);
        Assert.True(SafeMigrationSqlExpressionInspector.IsStructurallyComparable(renamed));
    }

    /// <summary>Decisive inspection results do not visit an irrelevant unknown subclass.</summary>
    [Fact]
    public void DecisiveEarlierChildrenPreserveShortCircuiting()
    {
        // Arrange
        var search = SafeMigrationSql.In(new UnknownExpression(), [SafeMigrationSql.Identifier("match")]);
        var comparison = SafeMigrationSql.Binary(
            SafeMigrationSql.Opaque("opaque"), SafeMigrationSqlBinaryOperator.Add, new UnknownExpression());

        // Act
        var found = SafeMigrationSqlExpressionInspector.ReferencesIdentifier(search, "match");
        var comparable = SafeMigrationSqlExpressionInspector.IsStructurallyComparable(comparison);

        // Assert
        Assert.True(found);
        Assert.False(comparable);
    }

    /// <summary>Unknown subclasses are not silently admitted by the iterative implementation.</summary>
    [Fact]
    public void ReachedUnknownSubclassesRetainExistingFailures()
    {
        // Arrange
        var expression = Nest("left", new UnknownExpression());

        // Act
        var collected = Record.Exception(() => SafeMigrationSqlExpressionInspector.CollectIdentifiers(
            expression, new HashSet<string>()));
        var searched = Record.Exception(() => SafeMigrationSqlExpressionInspector.ReferencesIdentifier(
            expression, "absent"));
        var compared = Record.Exception(() => SafeMigrationSqlExpressionInspector.IsStructurallyComparable(expression));
        var renamed = Record.Exception(() => SafeMigrationSqlExpressionInspector.RenameIdentifier(
            expression, "source", "target"));

        // Assert
        Assert.IsType<System.Diagnostics.UnreachableException>(collected);
        Assert.IsType<System.Diagnostics.UnreachableException>(searched);
        Assert.IsType<System.Diagnostics.UnreachableException>(compared);
        Assert.IsType<System.Diagnostics.UnreachableException>(renamed);
    }

    /// <summary>Preserves a non-recursive legacy leaf fast path for immutable literal objects.</summary>
    [Fact]
    public void RenameReusesExistingLiteralLeaf()
    {
        // Arrange
        var literal = SafeMigrationSql.Literal(42);

        // Act
        var renamed = SafeMigrationSqlExpressionInspector.RenameIdentifier(literal, "source", "target");

        // Assert
        Assert.Same(literal, renamed);
    }

    /// <summary>Flat syntax keeps its deeper tree while the existing syntactic nesting limit still applies.</summary>
    [Fact]
    public void ParserLimitsRemainSyntacticRatherThanNewTreeAdmissionCaps()
    {
        // Arrange
        var flat = string.Join(" + ", Enumerable.Repeat("value", 300));
        var nested = new string('(', SafeMigrationSqlExpressionParser.MaximumDepth + 1)
            + "value" + new string(')', SafeMigrationSqlExpressionParser.MaximumDepth + 1);

        // Act
        var accepted = SafeMigrationSqlExpressionParser.TryParse(flat, out var expression, out var failure);
        var refused = SafeMigrationSqlExpressionParser.TryParse(nested, out _, out var nestedFailure);
        var comparable = expression is not null
            && SafeMigrationSqlExpressionInspector.IsStructurallyComparable(expression);

        // Assert
        Assert.True(accepted);
        Assert.Empty(failure);
        Assert.True(comparable);
        Assert.False(refused);
        Assert.Equal("expression_too_deep", nestedFailure);
    }

    /// <summary>Builds a public tree without recursive fixture construction.</summary>
    private static SafeMigrationSqlExpression Nest(string shape, SafeMigrationSqlExpression expression)
    {
        var literal = SafeMigrationSql.Literal(1);
        for (var depth = 0; depth < 20_000; depth++)
        {
            expression = shape switch
            {
                "left" => SafeMigrationSql.Binary(expression, SafeMigrationSqlBinaryOperator.Add, literal),
                "right" => SafeMigrationSql.Binary(literal, SafeMigrationSqlBinaryOperator.Add, expression),
                "unary" => SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, expression),
                "between" => new SafeMigrationSqlBetweenExpression(literal, expression, literal),
                "in" => new SafeMigrationSqlInExpression(literal, [expression, literal]),
                "function" => SafeMigrationSql.Function("ABS", expression),
                "case" => new SafeMigrationSqlCaseExpression([new(literal, expression)], literal),
                "cast" => SafeMigrationSql.Cast(expression, "int"),
                "collate" => SafeMigrationSql.Collate(expression, "binary"),
                "null-test" => SafeMigrationSql.IsNull(expression),
                _ => throw new ArgumentOutOfRangeException(nameof(shape)),
            };
        }

        return expression;
    }

    /// <summary>Checks the reconstructed terminal path without using recursive assertions.</summary>
    private static SafeMigrationSqlExpression Terminal(string shape, SafeMigrationSqlExpression expression)
    {
        for (var depth = 0; depth < 20_000; depth++)
        {
            expression = shape switch
            {
                "left" => Assert.IsType<SafeMigrationSqlBinaryExpression>(expression).Left,
                "right" => Assert.IsType<SafeMigrationSqlBinaryExpression>(expression).Right,
                "unary" => Assert.IsType<SafeMigrationSqlUnaryExpression>(expression).Operand,
                "between" => Assert.IsType<SafeMigrationSqlBetweenExpression>(expression).Lower,
                "in" => Assert.IsType<SafeMigrationSqlInExpression>(expression).Values[0],
                "function" => Assert.IsType<SafeMigrationSqlFunctionExpression>(expression).Arguments[0],
                "case" => Assert.IsType<SafeMigrationSqlCaseExpression>(expression).WhenClauses[0].Result,
                "cast" => Assert.IsType<SafeMigrationSqlCastExpression>(expression).Operand,
                "collate" => Assert.IsType<SafeMigrationSqlCollateExpression>(expression).Operand,
                "null-test" => Assert.IsType<SafeMigrationSqlNullTestExpression>(expression).Operand,
                _ => throw new ArgumentOutOfRangeException(nameof(shape)),
            };
        }

        return expression;
    }

    private sealed class UnknownExpression : SafeMigrationSqlExpression;

    private sealed class RecordingComparer : IEqualityComparer<string>
    {
        public List<string> Seen { get; } = [];

        public bool Equals(string? left, string? right)
        {
            Seen.Add(left!);

            return StringComparer.Ordinal.Equals(left, right);
        }

        public int GetHashCode(string value) => StringComparer.Ordinal.GetHashCode(value);
    }

    private sealed class RecordingSet : HashSet<string>, ISet<string>
    {
        public List<string> Seen { get; } = [];

        bool ISet<string>.Add(string item)
        {
            Seen.Add(item);

            return Add(item);
        }
    }
}
