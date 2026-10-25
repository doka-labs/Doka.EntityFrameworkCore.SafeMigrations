namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies semantic generated nullability without inferring guarantees from current data.</summary>
public sealed class MariaDbGeneratedColumnNullabilityTests
{
    private static readonly string[] s_guardedValueSources = ["left_bound", "right_bound"];

    /// <summary>Unsigned literal values cannot escape the signed integral domain used by the semantic proof.</summary>
    /// <param name="outsideSignedDomain">Whether the literal exceeds the largest signed 64-bit value.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiteralProofRetainsSignedIntegralDomain(bool outsideSignedDomain)
    {
        // Arrange
        var literal = outsideSignedDomain ? ulong.MaxValue : (ulong)long.MaxValue;
        var definition = new ExpectedColumnDefinition("generated", typeof(long), false, "bigint",
            computedExpression: SafeMigrationSql.Literal(literal));

        // Act
        var proven = MariaDbGeneratedColumnNullability.TryGetRequiredIntegerSources(definition, out var sources);

        // Assert
        Assert.Equal(!outsideSignedDomain, proven);
        Assert.Empty(sources);
    }

    /// <summary>Non-integral target assignment cannot inherit an integral expression's non-null certificate.</summary>
    /// <param name="storeType">The target conversion domain that has not been qualified by this proof.</param>
    [Theory]
    [InlineData("date")]
    [InlineData("varchar(20)")]
    [InlineData("decimal(20,0)")]
    [InlineData("bigint unsigned")]
    public void NonIntegralTargetStorageCannotBorrowAnIntegerResultProof(string storeType)
    {
        // Arrange
        var definition = new ExpectedColumnDefinition("generated", typeof(long), false, storeType,
            computedExpression: SafeMigrationSql.Literal(1));

        // Act
        var proven = MariaDbGeneratedColumnNullability.TryGetRequiredIntegerSources(definition, out var sources);

        // Assert
        Assert.False(proven);
        Assert.Empty(sources);
    }

    /// <summary>Only required ordinary signed integer declarations can supply non-null arithmetic operands.</summary>
    /// <param name="storeType">The authored native type or its normal display-width variant.</param>
    /// <param name="nullable">Whether the source permits future NULL values.</param>
    /// <param name="computed">Whether the source has a separate generated dependency graph.</param>
    /// <param name="expected">Whether this exact source definition can certify non-null integral values.</param>
    [Theory]
    [InlineData("bigint", false, false, true)]
    [InlineData("bigint(20)", false, false, true)]
    [InlineData("int", false, false, true)]
    [InlineData("smallint", false, false, true)]
    [InlineData("tinyint(1)", false, false, true)]
    [InlineData("bigint", true, false, false)]
    [InlineData("bigint", false, true, false)]
    [InlineData("bigint unsigned", false, false, false)]
    [InlineData("bigint(20) unsigned", false, false, false)]
    [InlineData("decimal(20,0)", false, false, false)]
    [InlineData("double", false, false, false)]
    [InlineData("varchar(20)", false, false, false)]
    public void SourceProofRequiresAnExactRequiredSignedInteger(string storeType, bool nullable, bool computed,
        bool expected)
    {
        // Arrange
        var definition = new ExpectedColumnDefinition("source", typeof(long), nullable, storeType,
            computedExpression: computed ? SafeMigrationSql.Literal(1) : null);

        // Act
        var qualified = MariaDbGeneratedColumnNullability.IsRequiredOrdinaryInteger(definition);

        // Assert
        Assert.Equal(expected, qualified);
    }

    /// <summary>The proof rejects conversions and operators that can produce NULL despite non-null operands.</summary>
    /// <param name="sql">The independently parsed generated expression.</param>
    [Theory]
    [InlineData("value / 1")]
    [InlineData("value % 1")]
    [InlineData("CAST(value AS signed)")]
    [InlineData("COALESCE(value, 1)")]
    [InlineData("CASE WHEN value > 0 THEN value END")]
    [InlineData("CASE WHEN value > 0 THEN NULL ELSE 1 END")]
    [InlineData("CASE WHEN value > 0 THEN 1 ELSE NULL END")]
    [InlineData("NULL")]
    [InlineData("'1'")]
    [InlineData("CURRENT_TIMESTAMP")]
    public void UnknownOrPotentiallyNullResultsDoNotAcquireAProof(string sql)
    {
        // Arrange
        Assert.True(SafeMigrationSqlExpressionParser.TryParse(sql, out var expression, out _));
        var definition = new ExpectedColumnDefinition("generated", typeof(long), false, "bigint",
            computedExpression: expression);

        // Act
        var proven = MariaDbGeneratedColumnNullability.TryGetRequiredIntegerSources(definition, out var sources);

        // Assert
        Assert.False(proven);
        Assert.Empty(sources);
    }

    /// <summary>Every CASE result branch is proved without assuming a condition is always true or false.</summary>
    [Fact]
    public void GuardedComparisonCollectsBothRequiredValueSourcesExactlyOnce()
    {
        // Arrange
        const string sql = "right_bound > CASE WHEN left_bound = 9223372036854775807 "
            + "THEN left_bound ELSE left_bound + 1 END";
        Assert.True(SafeMigrationSqlExpressionParser.TryParse(sql, out var expression, out _));
        var definition = new ExpectedColumnDefinition("generated", typeof(bool), false, "tinyint(1)",
            computedExpression: expression);

        // Act
        var proven = MariaDbGeneratedColumnNullability.TryGetRequiredIntegerSources(definition, out var sources);

        // Assert
        Assert.True(proven);
        Assert.Equal(s_guardedValueSources, sources.Order(StringComparer.Ordinal));
    }

    /// <summary>A nullable condition does not make literal THEN and ELSE results nullable.</summary>
    [Fact]
    public void CaseProofDependsOnAllResultsRatherThanConditionNullability()
    {
        // Arrange
        var expression = SafeMigrationSql.Case(
        [
            new SafeMigrationSqlCaseWhenClause(SafeMigrationSql.Identifier("nullable_condition"),
                SafeMigrationSql.Literal(1)),
        ], SafeMigrationSql.Literal(0));
        var definition = new ExpectedColumnDefinition("generated", typeof(int), false, "int",
            computedExpression: expression);

        // Act
        var proven = MariaDbGeneratedColumnNullability.TryGetRequiredIntegerSources(definition, out var sources);

        // Assert
        Assert.True(proven);
        Assert.Empty(sources);
    }
}
