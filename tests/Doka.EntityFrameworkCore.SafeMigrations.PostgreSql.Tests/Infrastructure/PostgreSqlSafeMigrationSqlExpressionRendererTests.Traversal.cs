namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationSqlExpressionRendererTests
{
    /// <summary>Native Boolean chains share separators without changing either spine's flattened SQL.</summary>
    /// <param name="operation">The native Boolean operator being flattened.</param>
    /// <param name="rightSpine">Whether the repeated branch is on the right.</param>
    [Theory]
    [InlineData(SafeMigrationSqlBinaryOperator.And, false)]
    [InlineData(SafeMigrationSqlBinaryOperator.And, true)]
    [InlineData(SafeMigrationSqlBinaryOperator.Or, false)]
    [InlineData(SafeMigrationSqlBinaryOperator.Or, true)]
    public void DeepNativeBooleanSpinesPreserveExactSql(SafeMigrationSqlBinaryOperator operation, bool rightSpine)
    {
        // Arrange
        const int depth = 20_000;
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var literal = SafeMigrationSql.Literal(true);
        var expression = literal;
        for (var index = 0; index < depth; index++)
        {
            expression = rightSpine ? SafeMigrationSql.Binary(literal, operation, expression)
                : SafeMigrationSql.Binary(expression, operation, literal);
        }

        var separator = operation == SafeMigrationSqlBinaryOperator.And ? " AND " : " OR ";
        var expected = Literal("(" + string.Join(separator, Enumerable.Repeat("true", depth + 1)) + ")");

        // Act
        var sql = renderer.RenderCatalogCapturedNativeDeparsedCandidateSql(expression, Literal);

        // Assert
        Assert.Equal(expected, sql);
    }

    /// <summary>Unlike Boolean operators retain their own native grouping.</summary>
    /// <param name="operation">The enclosing Boolean operator.</param>
    /// <param name="expected">The independently authored native catalog form.</param>
    [Theory]
    [InlineData(SafeMigrationSqlBinaryOperator.And, "'((true OR false) AND true)'")]
    [InlineData(SafeMigrationSqlBinaryOperator.Or, "'((true AND false) OR true)'")]
    public void MixedNativeBooleanOperatorsRetainChildGrouping(
        SafeMigrationSqlBinaryOperator operation,
        string expected
    )
    {
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var other = operation == SafeMigrationSqlBinaryOperator.And
            ? SafeMigrationSqlBinaryOperator.Or : SafeMigrationSqlBinaryOperator.And;
        var child = SafeMigrationSql.Binary(SafeMigrationSql.Literal(true), other, SafeMigrationSql.Literal(false));
        var expression = SafeMigrationSql.Binary(child, operation, SafeMigrationSql.Literal(true));

        // Act
        var sql = renderer.RenderCatalogCapturedNativeDeparsedCandidateSql(expression, Literal);

        // Assert
        Assert.Equal(expected, sql);
    }

    /// <summary>Deep authored arithmetic keeps exact DDL and catalog parentheses without recursive calls.</summary>
    /// <param name="rightSpine">Whether the nested operand is on the right rather than the left.</param>
    /// <param name="catalog">Whether the catalog candidate is rendered instead of executable DDL.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DeepArithmeticPreservesExactOutput(bool rightSpine, bool catalog)
    {
        // Arrange
        const int depth = 20_000;
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = DeepArithmetic(depth, rightSpine);
        var expected = new StringBuilder();
        for (var index = 0; index < depth; index++)
        {
            expected.Append(rightSpine ? "(1 + " : "(");
        }

        expected.Append('0');
        for (var index = 0; index < depth; index++)
        {
            expected.Append(rightSpine ? ")" : " + 1)");
        }

        var expectedSql = catalog ? Literal(expected.ToString()) : expected.ToString();

        // Act
        var feature = renderer.GetUnsupportedFeature(expression);
        var sql = catalog ? renderer.RenderCatalogCandidateSql(expression, Literal) : renderer.Render(expression);

        // Assert
        Assert.Null(feature);
        Assert.Equal(expectedSql, sql);
    }

    /// <summary>Native pretty rendering retains left associativity and right-side grouping at depth.</summary>
    /// <param name="rightSpine">Whether right-side parentheses are semantically required.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepDeparsedArithmeticPreservesPrecedence(bool rightSpine)
    {
        // Arrange
        const int depth = 20_000;
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = DeepArithmetic(depth, rightSpine);
        var expected = new StringBuilder("(");
        for (var index = 0; index < depth; index++)
        {
            expected.Append(rightSpine ? index == 0 ? "1 + " : "(1 + " : string.Empty);
        }

        expected.Append('0');
        for (var index = 0; index < depth; index++)
        {
            expected.Append(rightSpine ? ")" : " + 1");
        }

        if (!rightSpine)
        {
            expected.Append(')');
        }

        // Act
        var sql = renderer.RenderCatalogDeparsedCandidateSql(expression, Literal);

        // Assert
        Assert.Equal(Literal(expected.ToString()), sql);
    }

    /// <summary>Native unary rendering neither rescans every suffix nor consumes call-stack depth.</summary>
    [Fact]
    public void DeepNativeNegationPreservesEveryAuthoredOperator()
    {
        // Arrange
        const int depth = 30_000;
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Literal(1);
        var expected = new StringBuilder();
        for (var index = 0; index < depth; index++)
        {
            expression = SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, expression);
            expected.Append("(- ");
        }

        expected.Append('1').Append(')', depth);

        // Act
        var sql = renderer.RenderCatalogNativeDeparsedCandidateSql(expression, Literal);

        // Assert
        Assert.Equal(Literal(expected.ToString()), sql);
    }

    /// <summary>CASE and COALESCE result folds retain their accepted depth without invoking conditions.</summary>
    /// <param name="shape">The repeated result-dependency shape.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DeepNativeResultTypesRemainProvable(int shape)
    {
        // Arrange
        var expression = SafeMigrationSql.Literal(1);
        for (var index = 0; index < 20_000; index++)
        {
            expression = shape switch
            {
                0 => SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, expression),
                1 => SafeMigrationSql.Function("COALESCE", expression, SafeMigrationSql.Literal(2)),
                2 => SafeMigrationSql.Case([new(SafeMigrationSql.Opaque("unresolved_condition"), expression)],
                    SafeMigrationSql.Literal(2)),
                _ => throw new ArgumentOutOfRangeException(nameof(shape)),
            };
        }

        // Act
        var type = PostgreSqlSafeMigrationSqlExpressionRenderer.RenderNativeResultTypeSql(expression, Literal);

        // Assert
        Assert.Equal("23", type);
    }

    /// <summary>Unknown operands keep each native fold's historical short-circuit and callback order.</summary>
    /// <param name="shape">The independent branch/fold shape.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NativeTypeFailureRetainsOriginalCallbackOrder(int shape)
    {
        // Arrange
        var calls = new List<string>();
        var unknown = SafeMigrationSql.Function("unresolved");
        var typed = SafeMigrationSql.Cast(SafeMigrationSql.Literal(1), "integer");
        var expression = shape switch
        {
            0 => SafeMigrationSql.Binary(unknown, SafeMigrationSqlBinaryOperator.Add, typed),
            1 => SafeMigrationSql.Case([new(SafeMigrationSql.Literal(true), typed)], unknown),
            2 => SafeMigrationSql.Function("COALESCE", unknown, typed),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        // Act
        var type = PostgreSqlSafeMigrationSqlExpressionRenderer.RenderNativeResultTypeSql(expression,
            text => { calls.Add(text); return Literal(text); });

        // Assert
        Assert.Null(type);
        Assert.Equal(shape == 0 ? ["integer"] : Array.Empty<string>(), calls);
    }

    /// <summary>Native common-type inference inspects ELSE before authored WHEN results.</summary>
    [Fact]
    public void NativeCaseTypePreservesElseFirstRegistration()
    {
        // Arrange
        var calls = new List<string>();
        var expression = SafeMigrationSql.Case(
            [new(SafeMigrationSql.Literal(true), SafeMigrationSql.Cast(SafeMigrationSql.Literal(1), "integer"))],
            SafeMigrationSql.Cast(SafeMigrationSql.Literal(2), "bigint"));

        // Act
        var type = PostgreSqlSafeMigrationSqlExpressionRenderer.RenderNativeResultTypeSql(expression,
            text => { calls.Add(text); return Literal(text); });

        // Assert
        Assert.NotNull(type);
        Assert.Equal(["bigint", "integer"], calls);
        Assert.Contains("(0, (pg_catalog.to_regtype('bigint')", type, StringComparison.Ordinal);
    }

    /// <summary>A left-hand incompatibility cannot be hidden by a deeply nested valid sibling.</summary>
    /// <param name="badLeft">Whether the unsupported provider fragment precedes or follows an unmapped literal.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepSupportValidationRetainsFirstFailure(bool badLeft)
    {
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var foreign = SafeMigrationSql.ProviderFragment("foreign", "private_payload");
        var unmapped = SafeMigrationSql.Literal(new object());
        var expression = SafeMigrationSql.Binary(badLeft ? foreign : unmapped,
            SafeMigrationSqlBinaryOperator.Equal, badLeft ? unmapped : foreign);
        for (var index = 0; index < 20_000; index++)
        {
            expression = SafeMigrationSql.IsNull(expression);
        }

        // Act
        var feature = renderer.GetUnsupportedFeature(expression);

        // Assert
        Assert.Equal(badLeft ? "provider_fragment_mismatch" : "structured_literal_mapping", feature);
    }

    /// <summary>Captured proofs accept deep finite arithmetic but still reject an unsupported leaf.</summary>
    /// <param name="finite">Whether the last authored operand is inside the existing finite family.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepCapturedOperandProofRetainsItsBoundary(bool finite)
    {
        // Arrange
        var expression = finite ? SafeMigrationSql.Identifier("value")
            : SafeMigrationSql.Function("unresolved");
        for (var index = 0; index < 20_000; index++)
        {
            expression = SafeMigrationSql.Binary(expression, SafeMigrationSqlBinaryOperator.Add,
                SafeMigrationSql.Literal(1));
        }

        var definition = new ExpectedColumnDefinition("computed", typeof(int), false,
            computedExpression: expression) { CapturedComputedSql = "captured_source" };

        // Act
        var required = PostgreSqlSafeMigrationCatalogSqlBuilder.RequiresCapturedOperandBinding(definition);

        // Assert
        Assert.Equal(finite, required);
    }

    /// <summary>Captured CAST collection keeps its historical IN values-before-operand registration order.</summary>
    [Fact]
    public void DeepCapturedCastCollectionPreservesNativeBindingOrder()
    {
        // Arrange
        var expression = SafeMigrationSql.In(
            SafeMigrationSql.Cast(SafeMigrationSql.Identifier("operand"), "bigint"),
            [SafeMigrationSql.Cast(SafeMigrationSql.Identifier("first"), "text"),
                SafeMigrationSql.Cast(SafeMigrationSql.Identifier("second"), "integer")]);
        for (var index = 0; index < 20_000; index++)
        {
            expression = SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, expression);
        }

        var method = typeof(PostgreSqlSafeMigrationCatalogSqlBuilder).GetMethod("CollectCapturedCastTypes",
            BindingFlags.NonPublic | BindingFlags.Static) ?? throw new InvalidOperationException("Missing collector.");
        object?[] arguments = [expression, null];

        // Act
        method.Invoke(null, arguments);

        // Assert
        var types = Assert.IsType<HashSet<string>>(arguments[1]);
        Assert.Equal(["text", "integer", "bigint"], types);
    }

    /// <summary>Implicit and explicit captured operator proofs keep their first-occurrence order at depth.</summary>
    [Fact]
    public void DeepCapturedOperatorCollectionPreservesFirstOccurrenceOrder()
    {
        // Arrange
        var expression = SafeMigrationSql.Between(
            SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, SafeMigrationSql.Identifier("value")),
            SafeMigrationSql.Literal(0),
            SafeMigrationSql.In(SafeMigrationSql.Literal(1), [SafeMigrationSql.Literal(2)]));
        for (var index = 0; index < 20_000; index++)
        {
            expression = SafeMigrationSql.IsNull(expression);
        }

        var method = typeof(PostgreSqlSafeMigrationCatalogSqlBuilder).GetMethod("CollectCapturedOperators",
            BindingFlags.NonPublic | BindingFlags.Static) ?? throw new InvalidOperationException("Missing collector.");
        object?[] arguments = [expression, null];

        // Act
        method.Invoke(null, arguments);

        // Assert
        var operators = Assert.IsType<HashSet<(string Name, bool Unary)>>(arguments[1]);
        Assert.Equal([(">=", false), ("<=", false), ("-", true), ("=", false)], operators);
    }

    /// <summary>Captured owner and stable-clock guards stay fail-closed throughout deep expressions.</summary>
    [Fact]
    public void DeepCapturedFunctionCollectionRetainsIndependentOwnerAndClockGuards()
    {
        // Arrange
        using var context = CreateContext();
        var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());
        var expression = SafeMigrationSql.Function("COALESCE",
            SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp),
            SafeMigrationSql.Identifier("unrelated", "value"));
        for (var index = 0; index < 20_000; index++)
        {
            expression = SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, expression);
        }

        var intent = new EnsureCheckConstraintIntent(ExpectedCheckConstraintDefinition.FromExpression(
            "ck_owner", "expected_owner", expression));
        var method = typeof(PostgreSqlSafeMigrationCatalogSqlBuilder).GetMethod("CollectCapturedFunctionProofs",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Missing collector.");
        object?[] arguments = [expression, intent, true, null, null];

        // Act
        method.Invoke(builder, arguments);

        // Assert
        Assert.Equal(["FALSE"], Assert.IsType<List<string>>(arguments[3]));
        Assert.Equal(["FALSE"], Assert.IsType<List<string>>(arguments[4]));
    }

    /// <summary>Repeated BETWEEN operands register parameters per occurrence, not per shared node.</summary>
    [Fact]
    public void CatalogRenderingPreservesExactOccurrenceRegistration()
    {
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var calls = new List<string>();
        var expression = SafeMigrationSql.Between(SafeMigrationSql.Identifier("value"),
            SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2));

        // Act
        var sql = renderer.RenderCatalogCandidateSql(expression,
            text => { calls.Add(text); return "@p" + calls.Count.ToString(CultureInfo.InvariantCulture); });

        // Assert
        Assert.Equal(["((", "value", " >= 1) AND (", "value", " <= 2))"], calls);
        Assert.Equal("(@p1 || pg_catalog.quote_ident(@p2) || @p3 || pg_catalog.quote_ident(@p4) || @p5)", sql);
    }

    /// <summary>Builds an adversarial authored spine independently of SQL parsing limits.</summary>
    private static SafeMigrationSqlExpression DeepArithmetic(int depth, bool rightSpine)
    {
        var expression = SafeMigrationSql.Literal(0);
        for (var index = 0; index < depth; index++)
        {
            expression = SafeMigrationSql.Binary(rightSpine ? SafeMigrationSql.Literal(1) : expression,
                SafeMigrationSqlBinaryOperator.Add, rightSpine ? expression : SafeMigrationSql.Literal(1));
        }

        return expression;
    }
}
