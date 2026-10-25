namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Protects accepted public expression trees independently of parser nesting and server limits.</summary>
public sealed class MySqlDeepExpressionTraversalTests
{
    private const int Depth = 12000;
    private static readonly string[] s_caseOrder = ["operand", "first", "shared", "shared", "second", "else"];
    private static readonly string[] s_elseFirstOrder = ["else", "operand", "first", "shared", "shared", "second"];

    /// <summary>Both rendering contexts retain every ordered child without using the process call stack.</summary>
    /// <param name="shape">The position and kind of the recursively nested child.</param>
    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("unary")]
    [InlineData("null-test")]
    [InlineData("function")]
    [InlineData("case")]
    [InlineData("in")]
    [InlineData("between")]
    [InlineData("cast")]
    [InlineData("collate")]
    public void DeepTreesPreserveExactOrdinaryAndDefaultSql(string shape)
    {
        // Arrange
        using var context = CreateContext();
        var renderer = Renderer(context);
        var expression = Build(shape, SafeMigrationSql.Literal(1));
        var ordinary = Expected(shape, defaultContext: false);
        var defaults = Expected(shape, defaultContext: true);

        // Act
        var actual = renderer.Render(expression);
        var actualDefault = renderer.RenderDefault(expression);
        var unsupported = renderer.GetUnsupportedFeature(expression);

        // Assert
        Assert.Equal(ordinary, actual);
        Assert.Equal(defaults, actualDefault);
        Assert.Null(unsupported);
    }

    /// <summary>Generated-source validators preserve eligibility across deep binary and ordered-list paths.</summary>
    /// <param name="shape">The position and kind of the recursively nested child.</param>
    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("unary")]
    [InlineData("null-test")]
    [InlineData("function")]
    [InlineData("case")]
    [InlineData("in")]
    [InlineData("between")]
    [InlineData("cast")]
    [InlineData("collate")]
    public void DeepGeneratedTreesPreserveLocalSourceAndCapabilityChecks(string shape)
    {
        // Arrange
        var expression = Build(shape, SafeMigrationSql.Identifier("source"));

        // Act
        var local = MySqlGeneratedColumnDependencies.ReferencesOnlyLocalColumns(expression);
        var capability = MySqlGeneratedColumnExpressionCapability.GetUnsupportedFeature(expression);
        var captured = MySqlCapturedExpressionContract.GetUnsupportedFeature("`source`", expression, isMariaDb: false);
        var stableStrings = MySqlCapturedExpressionContract.RequiresStableStrings(expression);

        // Assert
        Assert.True(local);
        Assert.Null(capability);
        Assert.Null(captured);
        Assert.False(stableStrings);
    }

    /// <summary>Deep late failures remain visible when pending traversal work is resumed.</summary>
    /// <param name="shape">The position and kind of the recursively nested child.</param>
    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("function")]
    [InlineData("case")]
    [InlineData("in")]
    public void DeepTreesRetainNegativeBindingAndCapabilityResults(string shape)
    {
        // Arrange
        var nonLocal = Build(shape, SafeMigrationSql.Identifier("other", "source"));
        var nondeterministic = Build(shape, SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp));
        var invalidCast = Build(shape, SafeMigrationSql.Cast(SafeMigrationSql.Identifier("source"), "bigint"));

        // Act
        var local = MySqlGeneratedColumnDependencies.ReferencesOnlyLocalColumns(nonLocal);
        var capability = MySqlGeneratedColumnExpressionCapability.GetUnsupportedFeature(nondeterministic);
        var captured = MySqlCapturedExpressionContract.GetUnsupportedFeature("`source`", invalidCast, isMariaDb: false);

        // Assert
        Assert.False(local);
        Assert.Equal("generated_column_nondeterministic_expression", capability);
        Assert.Equal("captured_expression_cast_target_unverified", captured);
    }

    /// <summary>Ordered validation returns the first failure and does not inspect an unknown later node.</summary>
    [Fact]
    public void RendererValidationPreservesFirstFailureShortCircuit()
    {
        // Arrange
        using var context = CreateContext();
        var expression = SafeMigrationSql.Binary(
            SafeMigrationSql.ProviderFragment("other", "ignored"), SafeMigrationSqlBinaryOperator.Add,
            new UnknownExpression());

        // Act
        var result = Renderer(context).GetUnsupportedFeature(expression);

        // Assert
        Assert.Equal("provider_fragment_mismatch", result);
    }

    /// <summary>Renderer CAST validation follows its operand rather than moving ahead of child rendering.</summary>
    [Fact]
    public void CastRenderingPreservesOperandFailurePriority()
    {
        // Arrange
        using var context = CreateContext();
        var expression = SafeMigrationSql.Cast(SafeMigrationSql.ProviderFragment("other", "ignored"), "invalid");

        // Act
        var error = Assert.Throws<NotSupportedException>(() => Renderer(context).Render(expression));

        // Assert
        Assert.Contains("SQL fragment provider 'other'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>All ordered CASE branches contribute text flags, including a deeply nested ELSE result.</summary>
    [Fact]
    public void DeepCaseStringsRetainStableAndSupplementaryFlags()
    {
        // Arrange
        var expression = Build("case", SafeMigrationSql.Literal("quote'\\\ud83d\ude00"));

        // Act
        var stable = MySqlCapturedExpressionContract.RequiresStableStrings(expression);
        var maria = MySqlCapturedExpressionContract.GetUnsupportedFeature("'value'", expression, isMariaDb: true);
        var mysql = MySqlCapturedExpressionContract.GetUnsupportedFeature("'value'", expression, isMariaDb: false);

        // Assert
        Assert.True(stable);
        Assert.Equal("captured_expression_literal_catalog_unverifiable", maria);
        Assert.Null(mysql);
    }

    /// <summary>Native display preserves deep NOT folding without normalizing arbitrary SQL text.</summary>
    /// <param name="isMariaDb">Whether native comparison operands retain MariaDB ordering.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepNegationPreservesNativeDisplay(bool isMariaDb)
    {
        // Arrange
        using var context = CreateContext();
        var expression = (SafeMigrationSqlExpression)SafeMigrationSql.Identifier("source");
        for (var index = 0; index < Depth; index++)
        {
            expression = SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, expression);
        }

        // Act
        var result = MySqlGeneratedExpressionDisplay.Render(expression, Renderer(context), isMariaDb);

        // Assert
        Assert.Equal(isMariaDb ? "`source` <> 0" : "(0 <> `source`)", result);
    }

    /// <summary>Deep right grouping and CASE display retain independently constructed native spellings.</summary>
    /// <param name="shape">The nested native-expression shape.</param>
    /// <param name="isMariaDb">Whether minimally grouped MariaDB output is expected.</param>
    [Theory]
    [InlineData("left", true)]
    [InlineData("right", true)]
    [InlineData("case", true)]
    [InlineData("left", false)]
    [InlineData("right", false)]
    [InlineData("case", false)]
    public void DeepNativeDisplayPreservesGrouping(string shape, bool isMariaDb)
    {
        // Arrange
        using var context = CreateContext();
        var expression = Build(shape, SafeMigrationSql.Literal(1));
        var prefix = shape == "case" ? "case when 1 then " : shape == "right" ? "1 + " : "";
        var suffix = shape == "case" ? " else 1 end" : shape == "left" ? " + 1" : "";
        var grouping = !isMariaDb || shape == "right";
        var expected = new StringBuilder();
        for (var index = 0; index < Depth; index++)
        {
            expected.Append(grouping && (index > 0 || !isMariaDb) ? "(" : "").Append(prefix);
        }

        expected.Append('1');
        for (var index = Depth - 1; index >= 0; index--)
        {
            expected.Append(suffix).Append(grouping && (index > 0 || !isMariaDb) ? ")" : "");
        }

        // Act
        var result = MySqlGeneratedExpressionDisplay.Render(expression, Renderer(context), isMariaDb);

        // Assert
        Assert.Equal(expected.ToString(), result);
    }

    /// <summary>Cursor traversal retains operand, list and CASE ordering, including repeated shared nodes.</summary>
    /// <param name="caseElseFirst">Whether the metadata scan must visit ELSE before the WHEN pairs.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TraversalPreservesEveryOrderedOccurrence(bool caseElseFirst)
    {
        // Arrange
        var shared = SafeMigrationSql.Identifier("shared");
        var expression = SafeMigrationSql.Case(
        [
            new SafeMigrationSqlCaseWhenClause(SafeMigrationSql.In(SafeMigrationSql.Identifier("operand"),
                [SafeMigrationSql.Identifier("first"), shared]), SafeMigrationSql.Function("COALESCE", shared,
                SafeMigrationSql.Identifier("second"))),
        ], SafeMigrationSql.Identifier("else"));
        var traversal = new MySqlExpressionTraversal(expression, caseElseFirst);
        var identifiers = new List<string>();

        // Act
        try
        {
            while (traversal.TryMoveNext(out var current))
            {
                if (current is SafeMigrationSqlIdentifierExpression identifier)
                {
                    identifiers.Add(identifier.Parts[0]);
                }
            }
        }
        finally
        {
            traversal.Dispose();
        }

        // Assert
        Assert.Equal(caseElseFirst ? s_elseFirstOrder : s_caseOrder, identifiers);
    }

    /// <summary>Unknown subclasses keep each validator's existing policy without a new global rejection.</summary>
    [Fact]
    public void UnknownLeafRetainsConsumerSpecificBehavior()
    {
        // Arrange
        using var context = CreateContext();
        var expression = new UnknownExpression();

        // Act
        var local = MySqlGeneratedColumnDependencies.ReferencesOnlyLocalColumns(expression);
        var capability = MySqlGeneratedColumnExpressionCapability.GetUnsupportedFeature(expression);
        var captured = MySqlCapturedExpressionContract.GetUnsupportedFeature("1", expression, isMariaDb: true);
        var stable = MySqlCapturedExpressionContract.RequiresStableStrings(expression);
        var rendererError = Record.Exception(() => Renderer(context).GetUnsupportedFeature(expression));

        // Assert
        Assert.True(local);
        Assert.Null(capability);
        Assert.Null(captured);
        Assert.False(stable);
        Assert.IsType<UnreachableException>(rendererError);
    }

    /// <summary>A wide list does not lose late failures or the order of repeated literal occurrences.</summary>
    [Fact]
    public void WideListsRetainEveryOccurrenceAndLateFailure()
    {
        // Arrange
        using var context = CreateContext();
        var values = new SafeMigrationSqlExpression[Depth];
        Array.Fill(values, SafeMigrationSql.Literal(1));
        var expression = SafeMigrationSql.Function("COALESCE", values);
        values[^1] = SafeMigrationSql.Identifier("other", "source");
        var nonLocal = SafeMigrationSql.Function("COALESCE", values);
        var expected = "coalesce(" + string.Join(", ", Enumerable.Repeat("1", Depth)) + ")";

        // Act
        var sql = Renderer(context).Render(expression);
        var local = MySqlGeneratedColumnDependencies.ReferencesOnlyLocalColumns(nonLocal);

        // Assert
        Assert.Equal(expected, sql);
        Assert.False(local);
    }

    /// <summary>The existing MariaDB non-null proof bound remains an eligibility rule, not AST admission.</summary>
    /// <param name="depth">The number of unary wrappers around a known non-null integral literal.</param>
    /// <param name="expected">Whether the already qualified bounded proof accepts that exact depth.</param>
    [Theory]
    [InlineData(0, true)]
    [InlineData(63, true)]
    [InlineData(64, false)]
    [InlineData(Depth, false)]
    public void ExistingBoundedMariaProofRetainsItsEligibilityBoundary(int depth, bool expected)
    {
        // Arrange
        var expression = (SafeMigrationSqlExpression)SafeMigrationSql.Literal(1);
        for (var index = 0; index < depth; index++)
        {
            expression = SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, expression);
        }

        var definition = new ExpectedColumnDefinition("generated", typeof(long), false, "bigint",
            computedExpression: expression);

        // Act
        var proven = MariaDbGeneratedColumnNullability.TryGetRequiredIntegerSources(definition, out var sources);
        var unsupported = MySqlGeneratedColumnExpressionCapability.GetUnsupportedFeature(expression);

        // Assert
        Assert.Equal(expected, proven);
        Assert.Empty(sources);
        Assert.Null(unsupported);
    }

    /// <summary>Parallel readers retain independent buffers across early returns and rendering failures.</summary>
    [Fact]
    public void ParallelSessionsRetainIndependentTraversalAndRenderingState()
    {
        // Arrange
        var expression = Build("left", SafeMigrationSql.Identifier("source"));
        var expected = Expected("left", defaultContext: false);
        expected = expected[..Depth] + "`source`" + expected[(Depth + 1)..];
        var rejected = SafeMigrationSql.Binary(SafeMigrationSql.ProviderFragment("other", "ignored"),
            SafeMigrationSqlBinaryOperator.Add, expression);
        var outputs = new (string Sql, bool Local, Exception? Error)[16];

        // Act
        Parallel.For(0, outputs.Length, index =>
        {
            using var context = CreateContext();
            var renderer = Renderer(context);
            var error = Record.Exception(() => renderer.Render(rejected));
            outputs[index] = (renderer.Render(expression),
                MySqlGeneratedColumnDependencies.ReferencesOnlyLocalColumns(expression), error);
        });

        // Assert
        Assert.All(outputs, output =>
        {
            Assert.Equal(expected, output.Sql);
            Assert.True(output.Local);
            Assert.IsType<NotSupportedException>(output.Error);
        });
    }

    /// <summary>Native-candidate discovery proves unchanged deep spelling without rendering extra candidates.</summary>
    /// <param name="shape">The unchanged MySQL expression whose full tree must be examined.</param>
    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("unary")]
    [InlineData("cast")]
    [InlineData("collate")]
    [InlineData("function")]
    public void DeepNativeDisplayDiscoveryRetainsEmptyFastPath(string shape)
    {
        // Arrange
        using var context = CreateContext();
        var expression = Build(shape, SafeMigrationSql.Literal(1));

        // Act
        var candidates = MySqlGeneratedExpressionDisplay.AdditionalNativeCandidates(expression, Renderer(context),
            isMariaDb: false);

        // Assert
        Assert.Empty(candidates);
    }

    /// <summary>Repeated native NOT folding retains three-valued coercion as well as De Morgan branch order.</summary>
    /// <param name="count">The authored NOT count, including a positive control without negation.</param>
    /// <param name="isMariaDb">Whether native display uses minimal grouping.</param>
    /// <param name="expected">The independent native expression spelling.</param>
    [Theory]
    [InlineData(0, true, "`a` = 1 and `b` is null")]
    [InlineData(1, true, "`a` <> 1 or `b` is not null")]
    [InlineData(2, true, "`a` = 1 and `b` is null")]
    [InlineData(3, true, "`a` <> 1 or `b` is not null")]
    [InlineData(0, false, "((`a` = 1) and (`b` is null))")]
    [InlineData(1, false, "((`a` <> 1) or (`b` is not null))")]
    [InlineData(2, false, "((`a` = 1) and (`b` is null))")]
    [InlineData(3, false, "((`a` <> 1) or (`b` is not null))")]
    public void NativeNegationPreservesShallowBranchSemantics(int count, bool isMariaDb, string expected)
    {
        // Arrange
        using var context = CreateContext();
        var expression = (SafeMigrationSqlExpression)SafeMigrationSql.Binary(
            SafeMigrationSql.Binary(SafeMigrationSql.Identifier("a"), SafeMigrationSqlBinaryOperator.Equal,
                SafeMigrationSql.Literal(1)), SafeMigrationSqlBinaryOperator.And,
            SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("b")));
        for (var index = 0; index < count; index++)
        {
            expression = SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, expression);
        }

        // Act
        var sql = MySqlGeneratedExpressionDisplay.Render(expression, Renderer(context), isMariaDb);

        // Assert
        Assert.Equal(expected, sql);
    }

    /// <summary>Constructs public expression nodes without invoking a parser or adding an admission limit.</summary>
    private static SafeMigrationSqlExpression Build(string shape, SafeMigrationSqlExpression leaf)
    {
        var one = SafeMigrationSql.Literal(1);
        var expression = leaf;
        for (var index = 0; index < Depth; index++)
        {
            expression = shape switch
            {
                "left" => SafeMigrationSql.Binary(expression, SafeMigrationSqlBinaryOperator.Add, one),
                "right" => SafeMigrationSql.Binary(one, SafeMigrationSqlBinaryOperator.Add, expression),
                "unary" => SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, expression),
                "null-test" => SafeMigrationSql.IsNull(expression),
                "function" => SafeMigrationSql.Function("COALESCE", expression, one),
                "case" => SafeMigrationSql.Case([new SafeMigrationSqlCaseWhenClause(one, expression)], one),
                "in" => SafeMigrationSql.In(one, [expression, one]),
                "between" => SafeMigrationSql.Between(one, expression, one),
                "cast" => SafeMigrationSql.Cast(expression, "signed"),
                "collate" => SafeMigrationSql.Collate(expression, "utf8mb4_bin"),
                _ => throw new ArgumentOutOfRangeException(nameof(shape)),
            };
        }

        return expression;
    }

    /// <summary>Builds the expected output independently from the production traversal.</summary>
    private static string Expected(string shape, bool defaultContext)
    {
        var (prefix, suffix) = shape switch
        {
            "left" => ("(", " + 1)"),
            "right" => ("(1 + ", ")"),
            "unary" => ("(-", ")"),
            "null-test" => ("(", " IS NULL)"),
            "function" => ("coalesce(", defaultContext ? ",1)" : ", 1)"),
            "case" => ("CASE WHEN 1 THEN ", " ELSE 1 END"),
            "in" => ("(1 IN (", defaultContext ? ",1))" : ", 1))"),
            "between" => ("(1 BETWEEN ", " AND 1)"),
            "cast" => ("CAST(", " AS SIGNED)"),
            "collate" => ("(", " COLLATE `utf8mb4_bin`)"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        var builder = new StringBuilder();
        for (var index = 0; index < Depth; index++)
        {
            builder.Append(prefix);
        }

        builder.Append('1');
        for (var index = 0; index < Depth; index++)
        {
            builder.Append(suffix);
        }

        return builder.ToString();
    }

    /// <summary>Uses the real native literal mappings without a database connection.</summary>
    private static MySqlSafeMigrationSqlExpressionRenderer Renderer(DbContext context) => new(
        context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    /// <summary>Creates a managed-only renderer scope.</summary>
    private static DbContext CreateContext() => new(new DbContextOptionsBuilder()
        .UseMySql("Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test",
            MySqlServerVersion.MySql(new Version(8, 4, 11))).UseMySqlSafeMigrations().Options);

    /// <summary>Proves that validation still short-circuits before an unreachable unknown node.</summary>
    private sealed class UnknownExpression : SafeMigrationSqlExpression;
}
