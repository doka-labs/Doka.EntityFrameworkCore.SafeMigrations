namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Preserves deep public-tree contracts without imposing the parser's syntax-depth limit.</summary>
/// <param name="output">The informational allocation-evidence sink.</param>
public sealed class SafeMigrationSqlExpressionStackTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void MixedShallowContractPreservesNullCountsCaseOrderAndCapturedNumericTokens()
    {
        var expression = ShallowCorpus();

        var canonical = Hash(expression);

        // WHY: This golden is captured independently with the pre-stack-safe writer, not the new traversal itself.
        Assert.Equal("6d458823e246184137a030f2b7a06219b42bf7c31c6a6bfd2a97da9a0e381b45", canonical);
    }

    /// <summary>Exercises every child position beyond the parser's deliberately separate syntax-depth limit.</summary>
    /// <param name="shape">The public expression child position repeated through the deep tree.</param>
    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("unary")]
    [InlineData("null_test")]
    [InlineData("between_operand")]
    [InlineData("between_lower")]
    [InlineData("between_upper")]
    [InlineData("in_operand")]
    [InlineData("in_value")]
    [InlineData("function")]
    [InlineData("case_condition")]
    [InlineData("case_result")]
    [InlineData("case_else")]
    [InlineData("cast")]
    [InlineData("collate")]
    public void DeepTreesPreserveEqualityCanonicalEncodingAndSemanticHash(string shape)
    {
        var left = Deep(shape, 10_000, SafeMigrationSql.Literal(new byte[] { 0, 1, 255 }));
        var right = Deep(shape, 10_000, SafeMigrationSql.Literal(new byte[] { 0, 1, 255 }));
        var different = Deep(shape, 10_000, SafeMigrationSql.Literal(new byte[] { 0, 1, 254 }));
        var comparer = SafeMigrationSemanticDefinitionComparers.CheckConstraint;
        var first = ExpectedCheckConstraintDefinition.FromExpression("ck_one", "items", left);
        var second = ExpectedCheckConstraintDefinition.FromExpression("ck_two", "items", right);

        var equal = SafeMigrationSqlExpressionContract.Equivalent(left, right);
        var unequal = SafeMigrationSqlExpressionContract.Equivalent(left, different);
        var canonical = Hash(left);
        var repeatedCanonical = Hash(right);
        var differentCanonical = Hash(different);
        var semantic = comparer.GetHashCode(first);
        var repeatedSemantic = comparer.GetHashCode(second);

        Assert.True(equal);
        Assert.False(unequal);
        Assert.Equal(canonical, repeatedCanonical);
        Assert.NotEqual(canonical, differentCanonical);
        Assert.Equal(semantic, repeatedSemantic);
        Assert.True(comparer.Equals(first, second));
    }

    [Fact]
    public void WideListsAndCaseClausesPreserveTheirLastElementAndFallback()
    {
        var values = Enumerable.Range(0, 10_000)
            .Select(static value => SafeMigrationSql.Literal(value))
            .ToArray();

        var changed = values.ToArray();
        var clauses = values.Select(static value => new SafeMigrationSqlCaseWhenClause(value, value)).ToArray();
        var first = SafeMigrationSql.Function("probe", values);
        var equivalent = SafeMigrationSql.Function("probe", values.ToArray());
        var caseWithoutElse = SafeMigrationSql.Case(clauses);
        var caseWithElse = SafeMigrationSql.Case(clauses, SafeMigrationSql.Literal(null));

        changed[^1] = SafeMigrationSql.Literal(-1);

        var equal = SafeMigrationSqlExpressionContract.Equivalent(first, equivalent);
        var unequal = SafeMigrationSqlExpressionContract.Equivalent(first, SafeMigrationSql.Function("probe", changed));
        var withoutElse = Hash(caseWithoutElse);
        var withElse = Hash(caseWithElse);
        var firstCanonical = Hash(first);
        var equivalentCanonical = Hash(equivalent);

        Assert.True(equal);
        Assert.False(unequal);
        Assert.NotEqual(withoutElse, withElse);
        Assert.Equal(firstCanonical, equivalentCanonical);
    }

    [Fact]
    public void RootAndChildReferenceFastPathsDoNotInspectUnknownSubclasses()
    {
        var unknown = new UnknownExpression();
        var left = SafeMigrationSql.Binary(unknown, SafeMigrationSqlBinaryOperator.Add, SafeMigrationSql.Literal(1));
        var right = SafeMigrationSql.Binary(unknown, SafeMigrationSqlBinaryOperator.Add, SafeMigrationSql.Literal(1));

        var rootEqual = SafeMigrationSqlExpressionContract.Equivalent(unknown, unknown);
        var childEqual = SafeMigrationSqlExpressionContract.Equivalent(left, right);

        Assert.True(rootEqual);
        Assert.True(childEqual);
    }

    [Fact]
    public void EarlierMismatchShortCircuitsAnUnsupportedLaterChild()
    {
        var left = SafeMigrationSql.Binary(
            SafeMigrationSql.Literal(1), SafeMigrationSqlBinaryOperator.Add, new UnknownExpression());

        var right = SafeMigrationSql.Binary(
            SafeMigrationSql.Literal(2), SafeMigrationSqlBinaryOperator.Add, new UnknownExpression());

        var equal = SafeMigrationSqlExpressionContract.Equivalent(left, right);

        Assert.False(equal);
    }

    [Fact]
    public void InOperandStillPrecedesItsMismatchingCollectionCount()
    {
        var left = SafeMigrationSql.In(new UnknownExpression(), [SafeMigrationSql.Literal(1)]);
        var right = SafeMigrationSql.In(
            new UnknownExpression(), [SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2)]);

        var failure = Assert.Throws<UnreachableException>(
            () => SafeMigrationSqlExpressionContract.Equivalent(left, right));

        Assert.NotNull(failure);
    }

    [Fact]
    public void UnsupportedNodeWritingThrowsWithoutPreventingSubsequentTraversal()
    {
        var invalid = SafeMigrationSql.Binary(
            SafeMigrationSql.Literal(1), SafeMigrationSqlBinaryOperator.Add, new UnknownExpression());

        var valid = SafeMigrationSql.Binary(
            SafeMigrationSql.Literal(1), SafeMigrationSqlBinaryOperator.Add, SafeMigrationSql.Literal(2));

        var failure = Assert.Throws<UnreachableException>(() => Hash(invalid));
        var recovered = Hash(valid);
        var repeated = Hash(valid);

        Assert.NotNull(failure);
        Assert.Equal(repeated, recovered);
    }

    [Fact]
    public void WarmedEqualityAllocationIsReportedWithoutAHardwareGate()
    {
        var left = Deep("left", 1_024, SafeMigrationSql.Literal(1));
        var right = Deep("left", 1_024, SafeMigrationSql.Literal(1));

        for (var iteration = 0; iteration < 20; iteration++)
        {
            _ = SafeMigrationSqlExpressionContract.Equivalent(left, right);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var equal = true;

        for (var iteration = 0; iteration < 100; iteration++)
        {
            equal &= SafeMigrationSqlExpressionContract.Equivalent(left, right);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(equal);
        output.WriteLine($"Warmed equality: 100 traversals of 1,024 branches allocated {allocated} bytes.");
    }

    /// <summary>Builds a linear public tree without recursion or parser-dependent depth restrictions.</summary>
    private static SafeMigrationSqlExpression Deep(string shape, int depth, SafeMigrationSqlExpression leaf)
    {
        var expression = leaf;
        var scalar = SafeMigrationSql.Literal(0);

        for (var index = 0; index < depth; index++)
        {
            expression = shape switch
            {
                "left" => SafeMigrationSql.Binary(expression, SafeMigrationSqlBinaryOperator.Add, scalar),
                "right" => SafeMigrationSql.Binary(scalar, SafeMigrationSqlBinaryOperator.Add, expression),
                "unary" => SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, expression),
                "null_test" => SafeMigrationSql.IsNull(expression),
                "between_operand" => SafeMigrationSql.Between(expression, scalar, scalar),
                "between_lower" => SafeMigrationSql.Between(scalar, expression, scalar),
                "between_upper" => SafeMigrationSql.Between(scalar, scalar, expression),
                "in_operand" => SafeMigrationSql.In(expression, [scalar]),
                "in_value" => SafeMigrationSql.In(scalar, [expression]),
                "function" => SafeMigrationSql.Function("probe", expression),
                "case_condition" => SafeMigrationSql.Case([new(expression, scalar)]),
                "case_result" => SafeMigrationSql.Case([new(scalar, expression)]),
                "case_else" => SafeMigrationSql.Case([new(scalar, scalar)], expression),
                "cast" => SafeMigrationSql.Cast(expression, "integer"),
                "collate" => SafeMigrationSql.Collate(expression, "C", "pg_catalog"),
                _ => throw new ArgumentException("Unknown test shape.", nameof(shape)),
            };
        }

        return expression;
    }

    /// <summary>Combines every node family and representation-bearing facet in one shallow ordered contract.</summary>
    internal static SafeMigrationSqlExpression ShallowCorpus()
    {
        var identifier = SafeMigrationSql.Identifier("app", "value");
        var one = SafeMigrationSql.Literal(1, "integer");

        return SafeMigrationSql.Function(
            "outer_probe",
            identifier,
            one,
            SafeMigrationSql.Literal(new byte[] { 0, 1, 255 }, "varbinary(3)"),
            new SafeMigrationSqlLiteralExpression(1.0M, null, "01.0".AsMemory()),
            SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, one),
            SafeMigrationSql.Binary(one, SafeMigrationSqlBinaryOperator.Add, SafeMigrationSql.Literal(2)),
            SafeMigrationSql.IsNotNull(identifier),
            SafeMigrationSql.Between(identifier, one, SafeMigrationSql.Literal(9), negated: true),
            SafeMigrationSql.In(identifier, [one, SafeMigrationSql.Literal(2)], negated: true),
            SafeMigrationSql.Function("empty_probe"),
            SafeMigrationSql.Cast(identifier, "text"),
            new SafeMigrationSqlCastExpression(identifier, "integer", usesSqlServerConvert: true),
            SafeMigrationSql.Collate(identifier, "C", "pg_catalog"),
            SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp, 6),
            SafeMigrationSql.ProviderFragment("provider", "CURRENT_USER"),
            new SafeMigrationSqlOpaqueExpression("value + 1", followsIdentifierRename: true),
            SafeMigrationSql.Case([new(one, SafeMigrationSql.Literal(null)), new(identifier, one)]),
            SafeMigrationSql.Case([new(one, one)], SafeMigrationSql.Literal("fallback")));
    }

    /// <summary>Hashes the exact canonical bytes written by the production contract.</summary>
    private static string Hash(SafeMigrationSqlExpression? expression)
    {
        using var writer = new CanonicalHashWriter();

        SafeMigrationSqlExpressionContract.Write(writer, expression);

        return writer.GetHash();
    }

    /// <summary>Exercises the documented unknown-node fallback without adding a production node kind.</summary>
    private sealed class UnknownExpression : SafeMigrationSqlExpression;
}
