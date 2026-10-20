namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Preserves public expression-tree behavior independently of CLR call-stack depth.</summary>
public sealed class SqlServerStackSafeExpressionTests
{
    private const int DeepCount = 12_000;
    private const string ConnectionString = "Server=127.0.0.1;Database=stack_safe;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    private static readonly string[] s_filterColumns = ["a", "b", "b", "c"];
    private static readonly string?[] s_literalStoreTypes = ["varchar(max)", "nvarchar(max)", "varchar(max)", null];
    private static readonly object?[] s_literalValues = ["first", "second", "third", null];

    /// <summary>Deep binary spines retain exact parenthesization and both operand orders.</summary>
    /// <param name="leftDeep">Whether each new ancestor wraps the previous tree on its left.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeepBinaryRenderingPreservesExactSql(bool leftDeep)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var renderer = Renderer(context);
        var expression = SafeMigrationSql.Identifier("x");
        for (var index = 0; index < DeepCount; index++)
        {
            expression = leftDeep
                ? SafeMigrationSql.Binary(expression, SafeMigrationSqlBinaryOperator.Add, SafeMigrationSql.Literal(1))
                : SafeMigrationSql.Binary(SafeMigrationSql.Literal(1), SafeMigrationSqlBinaryOperator.Add, expression);
        }

        var expected = leftDeep ? new string('(', DeepCount) + "[x]" + Repeat(" + 1)", DeepCount)
            : Repeat("(1 + ", DeepCount) + "[x]" + new string(')', DeepCount);

        // Act
        var feature = renderer.GetUnsupportedFeature(expression);
        var sql = renderer.Render(expression);

        // Assert
        Assert.Null(feature);
        Assert.Equal(expected, sql);
    }

    /// <summary>Unary paths remain admissible without imposing a new global depth limit.</summary>
    /// <param name="predicateRole">Whether the deep unary tree is used as a CASE search condition.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepUnaryRenderingPreservesNativeRoles(bool predicateRole)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var renderer = Renderer(context);
        var expression = SafeMigrationSql.Identifier("enabled");
        for (var index = 0; index < DeepCount; index++)
        {
            expression = SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, expression);
        }

        var expected = Repeat("(NOT ", DeepCount)
            + (predicateRole ? "([enabled] = CAST(1 AS bit))" : "[enabled]") + new string(')', DeepCount);

        if (predicateRole)
        {
            expression = SafeMigrationSql.Case([new(expression, SafeMigrationSql.Literal(1))]);
            expected = "CASE WHEN " + expected + " THEN 1 END";
        }

        // Act
        var sql = renderer.Render(expression);

        // Assert
        Assert.Equal(expected, sql);
    }

    /// <summary>Mixed list, range, cast and scalar CASE roles keep their exact established SQL spelling.</summary>
    [Fact]
    public void MixedShallowRenderingPreservesGoldenSql()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var renderer = Renderer(context);
        var condition = SafeMigrationSql.In(SafeMigrationSql.Identifier("x"),
            [SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2)]);

        var predicate = SafeMigrationSql.Between(SafeMigrationSql.Identifier("x"),
            SafeMigrationSql.Literal(0), SafeMigrationSql.Literal(3));

        var expression = SafeMigrationSql.Case([new(condition, predicate)],
            SafeMigrationSql.Cast(SafeMigrationSql.Function("COALESCE",
                [SafeMigrationSql.Literal(null), SafeMigrationSql.Literal(0)]), "bit"));

        // Act
        var sql = renderer.Render(expression);

        // Assert
        Assert.Equal("CASE WHEN ([x] IN (1, 2)) THEN CASE WHEN ([x] BETWEEN 0 AND 3) "
            + "THEN CAST(1 AS bit) WHEN NOT ([x] BETWEEN 0 AND 3) THEN CAST(0 AS bit) ELSE NULL END "
            + "ELSE CAST(COALESCE(NULL, 0) AS bit) END", sql);
    }

    /// <summary>Wide argument lists use a resumable cursor without reversing their literal order.</summary>
    [Fact]
    public void WideInRenderingPreservesEveryArgument()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var values = Enumerable.Range(0, DeepCount).Select(static value => SafeMigrationSql.Literal(value)).ToArray();
        var expression = SafeMigrationSql.In(SafeMigrationSql.Identifier("x"), values);
        var expected = "([x] IN (" + string.Join(", ", Enumerable.Range(0, DeepCount)) + "))";

        // Act
        var sql = Renderer(context).Render(expression);

        // Assert
        Assert.Equal(expected, sql);
    }

    /// <summary>Validation still reports the first native role failure instead of exploring later siblings.</summary>
    /// <param name="firstOpaque">Whether the opaque node precedes the invalid cast.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeepValidationPreservesFailurePriority(bool firstOpaque)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var renderer = Renderer(context);
        var first = firstOpaque ? SafeMigrationSql.Opaque("untrusted")
            : SafeMigrationSql.Cast(SafeMigrationSql.Literal(1), "not_a_native_type");

        for (var index = 0; index < DeepCount; index++)
        {
            first = SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, first);
        }

        var second = firstOpaque ? SafeMigrationSql.Cast(SafeMigrationSql.Literal(1), "not_a_native_type")
            : SafeMigrationSql.Opaque("untrusted");

        var expression = SafeMigrationSql.Binary(first, SafeMigrationSqlBinaryOperator.Add, second);

        // Act
        var feature = renderer.GetUnsupportedFeature(expression);

        // Assert
        Assert.Equal(firstOpaque ? "opaque_sql_expression" : "structured_cast_type", feature);
    }

    /// <summary>Rejected left siblings still prevent evaluation of a later unknown public node kind.</summary>
    [Fact]
    public void ValidationRetainsShortCircuitAgainstUnknownSibling()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var expression = SafeMigrationSql.Binary(SafeMigrationSql.Opaque("unproven"),
            SafeMigrationSqlBinaryOperator.Add, new UnknownExpression());

        // Act
        var feature = Renderer(context).GetUnsupportedFeature(expression);

        // Assert
        Assert.Equal("opaque_sql_expression", feature);
    }

    /// <summary>Finite Boolean proofs survive deep integral casts while preserving negative terminal values.</summary>
    /// <param name="terminal">The final untyped literal value.</param>
    /// <param name="expected">Whether the established finite proof must accept that value.</param>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void DeepFiniteBooleanProofPreservesItsDomain(int terminal, bool expected)
    {
        // Arrange
        var expression = SafeMigrationSql.Literal(terminal);
        for (var index = 0; index < DeepCount; index++)
        {
            expression = SafeMigrationSql.Cast(expression, "int");
        }

        // Act
        var nonNull = SqlServerComputedColumnContract.IsNonNull(expression);
        var deterministic = SqlServerComputedColumnContract.IsDeterministic(expression);

        // Assert
        Assert.Equal(expected, nonNull);
        Assert.False(deterministic);
    }

    /// <summary>Pure collation wrappers retain the terminal non-NULL proof without renting a work buffer.</summary>
    /// <param name="hasValue">Whether the final untyped literal is non-NULL.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepCollationProofPreservesTerminalNullability(bool hasValue)
    {
        // Arrange
        var expression = SafeMigrationSql.Literal(hasValue ? 1 : null);
        for (var index = 0; index < DeepCount; index++)
        {
            expression = SafeMigrationSql.Collate(expression, "Latin1_General_100_CI_AS");
        }

        // Act
        var nonNull = SqlServerComputedColumnContract.IsNonNull(expression);

        // Assert
        Assert.Equal(hasValue, nonNull);
    }

    /// <summary>CASE proof traversal retains ELSE-first checks and the existing native CASE limit.</summary>
    /// <param name="nullableElse">Whether the innermost ELSE is NULL.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepCaseProofDoesNotBroadenNativeEligibility(bool nullableElse)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var expression = SafeMigrationSql.Literal(nullableElse ? null : 1);
        for (var index = 0; index < DeepCount; index++)
        {
            expression = SafeMigrationSql.Case([new(SafeMigrationSql.Literal(true), SafeMigrationSql.Literal(0))],
                expression);
        }

        // Act
        var nonNull = SqlServerComputedColumnContract.IsNonNull(expression);
        var feature = Renderer(context).GetUnsupportedFeature(expression);

        // Assert
        Assert.Equal(!nullableElse, nonNull);
        Assert.Equal("structured_case_nesting", feature);
    }

    /// <summary>COALESCE nullability proof keeps the all-NULL negative case at any public-tree depth.</summary>
    /// <param name="hasValue">Whether a non-NULL finite value terminates the spine.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepCoalesceProofRetainsAllNullRejection(bool hasValue)
    {
        // Arrange
        var expression = SafeMigrationSql.Literal(hasValue ? 1 : null);
        for (var index = 0; index < DeepCount; index++)
        {
            expression = SafeMigrationSql.Function("COALESCE", [SafeMigrationSql.Literal(null), expression]);
        }

        // Act
        var nonNull = SqlServerComputedColumnContract.IsNonNull(expression);

        // Assert
        Assert.Equal(hasValue, nonNull);
    }

    /// <summary>Deep conjunctions retain source-order comparison enumeration and invalid-leaf rejection.</summary>
    /// <param name="supported">Whether the final branch belongs to the native filtered-index grammar.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepFilterValidationAndEnumerationPreserveOrder(bool supported)
    {
        // Arrange
        var expression = supported
            ? SafeMigrationSql.IsNotNull(SafeMigrationSql.Identifier("last"))
            : SafeMigrationSql.Opaque("not_a_filter");

        for (var index = DeepCount - 1; index >= 0; index--)
        {
            var comparison = SafeMigrationSql.Binary(SafeMigrationSql.Identifier("c" + index),
                SafeMigrationSqlBinaryOperator.Equal, SafeMigrationSql.Literal(index));

            expression = SafeMigrationSql.Binary(comparison, SafeMigrationSqlBinaryOperator.And, expression);
        }

        // Act
        var valid = SqlServerSafeMigrationCatalogSqlBuilder.IsSupportedIndexFilter(expression);
        var enumerator = SqlServerSafeMigrationCatalogSqlBuilder.GetIndexFilterComparisons(expression).GetEnumerator();
        using (enumerator)
        {
            // Assert
            Assert.Equal(supported, valid);
            for (var index = 0; index < DeepCount; index++)
            {
                Assert.True(enumerator.MoveNext());
                Assert.Equal("c" + index, enumerator.Current.Column);
                Assert.Equal(index, enumerator.Current.Literal?.Value);
            }

            if (supported)
            {
                Assert.True(enumerator.MoveNext());
                Assert.Equal("last", enumerator.Current.Column);
                Assert.False(enumerator.MoveNext());
            }
            else
            {
                Assert.Throws<ArgumentException>(() => enumerator.MoveNext());
            }
        }
    }

    /// <summary>A bounded parsed filter keeps source ANSI/Unicode queues and unmodified identifier leaves.</summary>
    [Fact]
    public void ParsedFilterRewriteRetainsLiteralEncodingAndOperandIdentity()
    {
        // Arrange
        var definition = new ExpectedIndexDefinition("IX_items_code", "items", [new("code")],
            filter: "[a] = 'first' AND [b] IN (N'second', 'third') AND [c] IS NOT NULL");

        // Act
        var rewritten = SqlServerSafeMigrationCatalogSqlBuilder.GetStructuredIndexFilter(definition);
        var comparisons = SqlServerSafeMigrationCatalogSqlBuilder.GetIndexFilterComparisons(rewritten!).ToArray();

        // Assert
        Assert.Equal(s_filterColumns, comparisons.Select(static item => item.Column));
        Assert.Equal(s_literalStoreTypes, comparisons.Select(static item => item.Literal?.StoreType));
        Assert.Equal(s_literalValues, comparisons.Select(static item => item.Literal?.Value));
    }

    /// <summary>Resumed seed binding visits both sides even when the left branch cannot bind.</summary>
    [Fact]
    public void SeedBindingPreservesAliasOrderAndNegativeSiblingEffects()
    {
        // Arrange
        var columns = new Dictionary<string, ExpectedColumnDefinition>(StringComparer.Ordinal)
        {
            ["right"] = new("right", typeof(int), true, "int"),
        };

        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var expression = SafeMigrationSql.Binary(SafeMigrationSql.Identifier("missing"),
            SafeMigrationSqlBinaryOperator.Equal, SafeMigrationSql.Identifier("right"));

        // Act
        var rewritten = InvokeStatic<SafeMigrationSqlExpression?>("BindProjectedSeedFilter",
            expression, columns, aliases);

        // Assert
        Assert.Null(rewritten);
        Assert.Equal("f0", Assert.Single(aliases).Value);
    }

    /// <summary>Deep seed predicates rebuild in alias order while retaining immutable shared literals.</summary>
    [Fact]
    public void DeepSeedBindingPreservesSharedLiteralIdentity()
    {
        // Arrange
        var literal = SafeMigrationSql.Literal(1);
        var columns = new Dictionary<string, ExpectedColumnDefinition>(StringComparer.Ordinal)
        {
            ["source"] = new("source", typeof(int), true, "int"),
        };

        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var expression = SafeMigrationSql.Binary(SafeMigrationSql.Identifier("source"),
            SafeMigrationSqlBinaryOperator.Equal, literal);

        for (var index = 0; index < DeepCount; index++)
        {
            expression = SafeMigrationSql.Binary(expression, SafeMigrationSqlBinaryOperator.And,
                SafeMigrationSql.IsNotNull(SafeMigrationSql.Identifier("source")));
        }

        // Act
        var rewritten = InvokeStatic<SafeMigrationSqlExpression?>("BindProjectedSeedFilter",
            expression, columns, aliases);

        var comparisons = SqlServerSafeMigrationCatalogSqlBuilder.GetIndexFilterComparisons(rewritten!).ToArray();

        // Assert
        Assert.Equal(DeepCount + 1, comparisons.Length);
        Assert.All(comparisons, static item => Assert.Equal("f0", item.Column));
        Assert.Same(literal, comparisons[0].Literal);
        Assert.Equal("f0", Assert.Single(aliases).Value);
        Assert.NotSame(expression, rewritten);
    }

    /// <summary>Added-NULL uniqueness proof keeps its narrow AND/IS NOT NULL contract without recursion.</summary>
    /// <param name="negated">Whether the terminal predicate rejects NULL.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepAddedNullProofPreservesItsNegativeCase(bool negated)
    {
        // Arrange
        var expression = negated ? SafeMigrationSql.IsNotNull(SafeMigrationSql.Identifier("new"))
            : SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("new"));
        for (var index = 0; index < DeepCount; index++)
        {
            expression = SafeMigrationSql.Binary(SafeMigrationSql.Literal(true),
                SafeMigrationSqlBinaryOperator.And, expression);
        }

        var method = typeof(SqlServerProjectedKeyShape).GetMethod("RejectsAddedNull",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;

        // Act
        var rejected = method.Invoke(null, [expression, new HashSet<string>(StringComparer.Ordinal) { "new" }]);

        // Assert
        Assert.Equal(negated, rejected);
    }

    /// <summary>Deep default admissibility retains both the non-NULL requirement and scalar-family failures.</summary>
    /// <param name="valid">Whether the final branch is representable as an integer.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepDefaultValidationPreservesBranchFailure(bool valid)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = Catalog(context);
        var expression = SafeMigrationSql.Literal(valid ? 1 : "invalid integer");
        for (var index = 0; index < DeepCount; index++)
        {
            expression = SafeMigrationSql.Function("COALESCE", [SafeMigrationSql.Literal(1), expression]);
        }

        // Act
        var feature = InvokeInstance<string?>(catalog, "GetDefaultExpressionFeature", expression, "int", false);

        // Assert
        Assert.Equal(valid ? null : SqlServerSafeMigrationRuntimePlan.DefaultValueUnsupportedCode, feature);
    }

    /// <summary>Deep omitted-column default proofs keep the all-NULL negative without recursive calls.</summary>
    /// <param name="hasValue">Whether the final leaf proves a non-NULL default.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepDefaultNullabilityProofRetainsNegative(bool hasValue)
    {
        // Arrange
        var expression = SafeMigrationSql.Literal(hasValue ? 1 : null);
        for (var index = 0; index < DeepCount; index++)
        {
            expression = SafeMigrationSql.Function("COALESCE", [SafeMigrationSql.Literal(null), expression]);
        }

        // Act
        var nonNull = InvokeStatic<bool>("IsNonNullDefaultExpression", expression);

        // Assert
        Assert.Equal(hasValue, nonNull);
    }

    /// <summary>COALESCE support SQL is composed once while preserving every leaf predicate and separator.</summary>
    [Fact]
    public void DeepDefaultSupportSqlPreservesExactComposition()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = Catalog(context);
        var literal = SafeMigrationSql.Literal(1);
        var leaf = InvokeInstance<string>(catalog, "BuildDefaultExpressionSupportPredicate",
            literal, "int", true, null);

        var expression = literal;
        const int depth = 2_048;
        for (var index = 0; index < depth; index++)
        {
            expression = SafeMigrationSql.Function("COALESCE", [SafeMigrationSql.Literal(null), expression]);
        }

        var expected = Repeat("(1 = 1 AND ", depth) + leaf + new string(')', depth);

        // Act
        var proof = InvokeInstance<string>(catalog, "BuildDefaultExpressionSupportPredicate",
            expression, "int", false, null);

        // Assert
        Assert.Equal(expected, proof);
    }

    private static SqlServerSafeMigrationSqlExpressionRenderer Renderer(DbContext context)
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    private static SqlServerSafeMigrationCatalogSqlBuilder Catalog(DbContext context)
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    private static string Repeat(string text, int count)
    {
        var builder = new StringBuilder(text.Length * count);
        for (var index = 0; index < count; index++)
        {
            builder.Append(text);
        }

        return builder.ToString();
    }

    private static TResult InvokeStatic<TResult>(string name, params object?[] arguments)
    {
        var method = typeof(SqlServerSafeMigrationCatalogSqlBuilder).GetMethod(name,
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;

        return (TResult)method.Invoke(null, arguments)!;
    }

    private static TResult InvokeInstance<TResult>(
        SqlServerSafeMigrationCatalogSqlBuilder instance,
        string name,
        params object?[] arguments
    )
    {
        var method = typeof(SqlServerSafeMigrationCatalogSqlBuilder).GetMethod(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        return (TResult)method.Invoke(instance, arguments)!;
    }

    /// <summary>Probes traversal order without supplying another admitted expression-tree kind.</summary>
    private sealed class UnknownExpression : SafeMigrationSqlExpression;
}
