namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies searched CASE token roles, immutability, resource bounds, and contract identity.</summary>
public sealed class SafeMigrationSqlCaseExpressionTests
{
    /// <summary>Dialect-dependent CONVERT roles cannot be confused with a portable CAST contract.</summary>
    [Fact]
    public void ConvertDialectProvenanceSurvivesRenameAndContractHashing()
    {
        Assert.True(SafeMigrationSqlExpressionParser.TryParse("CONVERT(signed, value)", out var converted));
        var portable = SafeMigrationSql.Cast(SafeMigrationSql.Identifier("value"), "signed");
        using var portableWriter = new CanonicalHashWriter();
        using var convertedWriter = new CanonicalHashWriter();

        var renamed = SafeMigrationSqlExpressionInspector.RenameIdentifier(converted!, "value", "renamed");
        SafeMigrationSqlExpressionContract.Write(portableWriter, portable);
        SafeMigrationSqlExpressionContract.Write(convertedWriter, converted);

        Assert.True(Assert.IsType<SafeMigrationSqlCastExpression>(renamed).UsesSqlServerConvert);
        Assert.False(SafeMigrationSqlExpressionContract.Equivalent(portable, converted));
        Assert.NotEqual(portableWriter.GetHash(), convertedWriter.GetHash());
    }

    /// <summary>The expression snapshots caller-owned storage without changing branch order.</summary>
    [Fact]
    public void ConstructionSnapshotsOrderedClauses()
    {
        var first = new SafeMigrationSqlCaseWhenClause(SafeMigrationSql.Literal(true), SafeMigrationSql.Literal(1));
        var second = new SafeMigrationSqlCaseWhenClause(SafeMigrationSql.Literal(false), SafeMigrationSql.Literal(2));
        var clauses = new[] { first, second };

        var expression = Assert.IsType<SafeMigrationSqlCaseExpression>(SafeMigrationSql.Case(clauses));
        clauses[0] = second;

        Assert.Same(first, expression.WhenClauses[0]);
        Assert.Same(second, expression.WhenClauses[1]);
        Assert.Null(expression.ElseResult);
    }

    /// <summary>The published ordered clause view rejects caller mutation.</summary>
    [Fact]
    public void ClauseViewRejectsMutation()
    {
        // Arrange
        var clause = new SafeMigrationSqlCaseWhenClause(SafeMigrationSql.Literal(true), SafeMigrationSql.Literal(1));
        var expression = new SafeMigrationSqlCaseExpression([clause]);
        var clauses = Assert.IsAssignableFrom<IList<SafeMigrationSqlCaseWhenClause>>(expression.WhenClauses);

        // Act
        var exception = Record.Exception(() => clauses[0] = clause);

        // Assert
        Assert.IsType<NotSupportedException>(exception);
    }

    /// <summary>Invalid AST boundaries cannot create partial or empty CASE contracts.</summary>
    /// <param name="boundary">The independently invalid construction boundary.</param>
    /// <param name="exceptionType">The exact public argument exception contract.</param>
    /// <param name="parameter">The invalid public parameter.</param>
    [Theory]
    [InlineData(0, typeof(ArgumentNullException), "whenClauses")]
    [InlineData(1, typeof(ArgumentException), "whenClauses")]
    [InlineData(2, typeof(ArgumentException), "whenClauses")]
    [InlineData(3, typeof(ArgumentNullException), "condition")]
    [InlineData(4, typeof(ArgumentNullException), "result")]
    public void ConstructionRejectsNullOrEmptyClauses(int boundary, Type exceptionType, string parameter)
    {
        // Arrange
        Func<object> construct = boundary switch
        {
            0 => static () => SafeMigrationSql.Case(null!),
            1 => static () => SafeMigrationSql.Case([]),
            2 => static () => SafeMigrationSql.Case([null!]),
            3 => static () => new SafeMigrationSqlCaseWhenClause(null!, SafeMigrationSql.Literal(1)),
            4 => static () => new SafeMigrationSqlCaseWhenClause(SafeMigrationSql.Literal(true), null!),
            _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
        };

        // Act
        var exception = Record.Exception(() => { _ = construct(); });

        // Assert
        Assert.IsType(exceptionType, exception);
        Assert.Equal(parameter, Assert.IsAssignableFrom<ArgumentException>(exception).ParamName);
    }

    /// <summary>Every branch is traversed, while type tokens and literal text remain independent roles.</summary>
    [Fact]
    public void IdentifierInspectionAndRenameVisitAllBranches()
    {
        Assert.True(SafeMigrationSqlExpressionParser.TryParse(
            "CONVERT(bit, CASE WHEN source > other THEN source WHEN other > 0 THEN 'source' ELSE fallback END)",
            out var expression));

        var renamed = SafeMigrationSqlExpressionInspector.RenameColumnIdentifier(
            expression!, "source", "target", StringComparer.Ordinal);

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        SafeMigrationSqlExpressionInspector.CollectIdentifiers(renamed, identifiers);
        var cast = Assert.IsType<SafeMigrationSqlCastExpression>(renamed);
        var conditional = Assert.IsType<SafeMigrationSqlCaseExpression>(cast.Operand);
        Assert.Equal("bit", cast.StoreType);
        Assert.Equal(["fallback", "other", "target"], identifiers.Order(StringComparer.Ordinal));
        var literal = Assert.IsType<SafeMigrationSqlLiteralExpression>(conditional.WhenClauses[1].Result);
        Assert.Equal("source", literal.Value);
    }

    /// <summary>Order, predicate, result, and ELSE are independently bound into equality and fingerprints.</summary>
    /// <param name="changedSql">The changed contract compared with the baseline.</param>
    [Theory]
    [InlineData("CASE WHEN a = 1 THEN 2 WHEN a = 0 THEN 1 ELSE 3 END")]
    [InlineData("CASE WHEN a = 2 THEN 1 WHEN a = 1 THEN 2 ELSE 3 END")]
    [InlineData("CASE WHEN a = 0 THEN 4 WHEN a = 1 THEN 2 ELSE 3 END")]
    [InlineData("CASE WHEN a = 0 THEN 1 WHEN a = 1 THEN 2 ELSE 4 END")]
    [InlineData("CASE WHEN a = 0 THEN 1 WHEN a = 1 THEN 2 END")]
    public void ContractRetainsEveryOrderedBranchFacet(
        string changedSql
    )
    {
        Assert.True(SafeMigrationSqlExpressionParser.TryParse(
            "CASE WHEN a = 0 THEN 1 WHEN a = 1 THEN 2 ELSE 3 END", out var baseline));
        Assert.True(SafeMigrationSqlExpressionParser.TryParse(changedSql, out var changed));
        using var baselineWriter = new CanonicalHashWriter();
        using var changedWriter = new CanonicalHashWriter();

        var equivalent = SafeMigrationSqlExpressionContract.Equivalent(baseline, changed);
        SafeMigrationSqlExpressionContract.Write(baselineWriter, baseline);
        SafeMigrationSqlExpressionContract.Write(changedWriter, changed);

        Assert.False(equivalent);
        Assert.NotEqual(baselineWriter.GetHash(), changedWriter.GetHash());
    }

    /// <summary>Identical CASE definitions share structural equality and semantic hash identity.</summary>
    [Fact]
    public void EquivalentCaseHasConsistentSemanticHash()
    {
        const string sql = "CASE WHEN a IS NULL THEN 1 ELSE 0 END";
        Assert.True(SafeMigrationSqlExpressionParser.TryParse(sql, out var first));
        Assert.True(SafeMigrationSqlExpressionParser.TryParse(sql, out var second));
        var left = ExpectedCheckConstraintDefinition.FromExpression("ck_a", "items", first!);
        var right = ExpectedCheckConstraintDefinition.FromExpression("ck_b", "items", second!);

        var equivalent = SafeMigrationSemanticDefinitionComparers.CheckConstraint.Equals(left, right);

        Assert.True(equivalent);
        Assert.Equal(SafeMigrationSemanticDefinitionComparers.CheckConstraint.GetHashCode(left),
            SafeMigrationSemanticDefinitionComparers.CheckConstraint.GetHashCode(right));
    }

    /// <summary>Parsing alone never proves that a CASE default is a safe non-null backfill.</summary>
    /// <param name="sql">The syntactically supported default with provider-dependent semantics.</param>
    [Theory]
    [InlineData("CASE WHEN TRUE THEN 1 ELSE 2 END")]
    [InlineData("CASE WHEN TRUE THEN 1 END")]
    [InlineData("CASE WHEN 1 / 0 > 0 THEN 1 ELSE 2 END")]
    public void CaseDefaultDoesNotAuthorizeBackfill(
        string sql
    )
    {
        Assert.True(SafeMigrationSqlExpressionParser.TryParse(sql, out var expression));
        var candidate = SafeMigrationDefaultValue.Sql(expression!);

        var proven = SafeMigrationColumnRepairHelper.HasProvablyNonNullDefault(candidate);

        Assert.False(proven);
    }

    /// <summary>Malformed CASE, unsupported conversion styles, and statement syntax remain fail-closed.</summary>
    /// <param name="sql">The rejected SQL expression.</param>
    [Theory]
    [InlineData("CASE END")]
    [InlineData("CASE a WHEN 1 THEN 2 END")]
    [InlineData("CASE WHEN a > 0 1 ELSE 0 END")]
    [InlineData("CASE WHEN a > 0 THEN 1")]
    [InlineData("CASE WHEN a > 0 THEN END END")]
    [InlineData("CASE WHEN a > 0 THEN 1 ELSE END END")]
    [InlineData("CASE WHEN a > 0 THEN 1 END; SELECT 1")]
    [InlineData("CONVERT(bit, a, 1)")]
    [InlineData("CONVERT(bit)")]
    [InlineData("CONVERT(, a)")]
    public void ParserRejectsIncompleteOrAmbiguousForms(
        string sql
    )
    {
        var parsed = SafeMigrationSqlExpressionParser.TryParse(sql, out var expression, out var failureCode);

        Assert.False(parsed);
        Assert.Null(expression);
        Assert.NotEmpty(failureCode);
    }

    /// <summary>CASE nesting participates in the parser's bounded stack contract.</summary>
    [Fact]
    public void ParserRejectsExcessiveCaseNesting()
    {
        var sql = string.Concat(Enumerable.Repeat("CASE WHEN TRUE THEN ",
            SafeMigrationSqlExpressionParser.MaximumDepth + 1))
            + "1" + string.Concat(Enumerable.Repeat(" END", SafeMigrationSqlExpressionParser.MaximumDepth + 1));

        var parsed = SafeMigrationSqlExpressionParser.TryParse(sql, out var expression, out var failureCode);

        Assert.False(parsed);
        Assert.Null(expression);
        Assert.Equal("expression_too_deep", failureCode);
    }
}
