namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Guards deeply composed default proofs without relaxing provider-neutral backfill eligibility.</summary>
public sealed class SafeMigrationDefaultProofStackTests
{
    /// <summary>Deep conjunctions, disjunctions and transparent wrappers preserve the original proof.</summary>
    /// <param name="shape">The proof container repeated in the public tree.</param>
    /// <param name="nonNull">Whether the innermost literal is non-null.</param>
    [Theory]
    [InlineData("unary", true)]
    [InlineData("unary", false)]
    [InlineData("collate", true)]
    [InlineData("collate", false)]
    [InlineData("coalesce-first", true)]
    [InlineData("coalesce-first", false)]
    [InlineData("coalesce-last", true)]
    [InlineData("coalesce-last", false)]
    [InlineData("between", true)]
    [InlineData("between", false)]
    [InlineData("in", true)]
    [InlineData("in", false)]
    public void DeepStructuralProofRetainsItsResult(string shape, bool nonNull)
    {
        // Arrange
        var expression = SafeMigrationSql.Literal(nonNull ? 1 : null);
        for (var depth = 0; depth < 20_000; depth++)
        {
            expression = shape switch
            {
                "unary" => SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, expression),
                "collate" => SafeMigrationSql.Collate(expression, "binary"),
                "coalesce-first" => SafeMigrationSql.Function("COALESCE", expression, SafeMigrationSql.Literal(null)),
                "coalesce-last" => SafeMigrationSql.Function("COALESCE", SafeMigrationSql.Literal(null), expression),
                "between" => new SafeMigrationSqlBetweenExpression(SafeMigrationSql.Literal(1),
                    expression, SafeMigrationSql.Literal(2)),
                "in" => new SafeMigrationSqlInExpression(SafeMigrationSql.Literal(1),
                    [SafeMigrationSql.Literal(2), expression]),
                _ => throw new ArgumentOutOfRangeException(nameof(shape)),
            };
        }

        var candidate = SafeMigrationDefaultValue.Sql(expression);

        // Act
        var result = SafeMigrationColumnRepairHelper.HasProvablyNonNullDefault(candidate);

        // Assert
        Assert.Equal(nonNull, result);
    }

    /// <summary>A decisive proof does not inspect a later unknown expression subclass.</summary>
    /// <param name="any">Whether the container uses COALESCE's any-non-null rule.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DecisiveOperandPreservesShortCircuiting(bool any)
    {
        // Arrange
        var decisive = SafeMigrationSql.Literal(any ? 1 : null);
        var expression = any
            ? SafeMigrationSql.Function("COALESCE", decisive, new UnknownExpression())
            : (SafeMigrationSqlExpression)new SafeMigrationSqlInExpression(decisive, [new UnknownExpression()]);
        var candidate = SafeMigrationDefaultValue.Sql(expression);

        // Act
        var result = SafeMigrationColumnRepairHelper.HasProvablyNonNullDefault(candidate);

        // Assert
        Assert.Equal(any, result);
    }

    /// <summary>A reached unknown subclass retains the existing proof failure.</summary>
    [Fact]
    public void ReachedUnknownOperandPreservesFailure()
    {
        // Arrange
        var candidate = SafeMigrationDefaultValue.Sql(SafeMigrationSql.Function(
            "COALESCE", SafeMigrationSql.Literal(null), new UnknownExpression()));

        // Act
        var exception = Record.Exception(() => SafeMigrationColumnRepairHelper.HasProvablyNonNullDefault(candidate));

        // Assert
        Assert.IsType<System.Diagnostics.UnreachableException>(exception);
    }

    /// <summary>No recursion conversion may broaden a provider-dependent conversion/arithmetic/CASE proof.</summary>
    [Fact]
    public void ExistingConservativeProofBoundariesRemainUnchanged()
    {
        // Arrange
        var unknown = new UnknownExpression();
        SafeMigrationSqlExpression[] candidates =
        [
            SafeMigrationSql.Binary(unknown, SafeMigrationSqlBinaryOperator.Add, SafeMigrationSql.Literal(1)),
            SafeMigrationSql.Cast(unknown, "int"),
            new SafeMigrationSqlCaseExpression([new(unknown, SafeMigrationSql.Literal(1))]),
            SafeMigrationSql.Function("UNPROVEN", unknown),
            SafeMigrationSql.Function("COALESCE"),
        ];

        // Act
        var results = candidates.Select(static value => SafeMigrationColumnRepairHelper.HasProvablyNonNullDefault(
            SafeMigrationDefaultValue.Sql(value))).ToArray();

        // Assert
        Assert.All(results, static result => Assert.False(result));
    }

    private sealed class UnknownExpression : SafeMigrationSqlExpression;
}
