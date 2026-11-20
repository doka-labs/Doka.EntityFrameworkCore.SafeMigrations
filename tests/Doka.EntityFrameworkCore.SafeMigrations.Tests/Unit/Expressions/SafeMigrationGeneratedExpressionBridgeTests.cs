namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Protects the EF metadata boundary for generated expressions and CHECK predicates.</summary>
public sealed class SafeMigrationGeneratedExpressionBridgeTests
{
    /// <summary>Generated CASE and CONVERT syntax must retain explicit expression and type roles.</summary>
    /// <param name="sql">The complete provider-generated expression.</param>
    [Theory]
    [InlineData("CASE WHEN value > 0 THEN 1 ELSE 0 END")]
    [InlineData("CASE WHEN value > 1 THEN 2 WHEN value = 1 THEN 1 END")]
    [InlineData("CASE WHEN value > 0 THEN CASE WHEN value > 1 THEN 2 ELSE 1 END ELSE 0 END")]
    [InlineData("CONVERT(bit, CASE WHEN [right_bound] > [left_bound] THEN 1 ELSE 0 END)")]
    [InlineData("CONVERT(decimal(18, 2), value)")]
    public void ParserAcceptsGeneratedSearchedCaseAndTypedConvert(
        string sql
    )
    {
        // Arrange
        // The theory case supplies the complete provider-generated expression.

        // Act
        var parsed = SafeMigrationSqlExpressionParser.TryParse(sql, out var expression, out var failureCode);

        // Assert
        Assert.True(parsed, failureCode);
        Assert.NotNull(expression);
        Assert.True(SafeMigrationSqlExpressionInspector.IsStructurallyComparable(expression));
    }

    /// <summary>Generated metadata must retain the overflow-safe CASE instead of opaque SQL.</summary>
    [Fact]
    public void ComputedMetadataPreservesStructuredSearchedCase()
    {
        // Arrange
        var operation = new AddColumnOperation
        {
            Name = "has_children",
            Table = "nodes",
            ClrType = typeof(bool),
            ColumnType = "boolean",
            ComputedColumnSql = "\"right_bound\" > CASE WHEN \"left_bound\" = 9223372036854775807 "
                + "THEN \"left_bound\" ELSE \"left_bound\" + 1 END",
            IsStored = true,
        };

        // Act
        var definition = SafeMigrationExpectedDefinitionFactory.From(operation);

        // Assert
        Assert.NotNull(definition.ComputedExpression);
        Assert.Null(definition.ComputedColumnSql);
        Assert.True(SafeMigrationSqlExpressionInspector.IsStructurallyComparable(definition.ComputedExpression));
    }

    /// <summary>
    /// The helper emitted by scaffolding must preserve the predicate validated before source emission.
    /// </summary>
    [Fact]
    public void ScaffoldedCheckHelperPreservesStructuredPredicate()
    {
        // Arrange
        var builder = new MigrationBuilder("test");

        // Act
        builder.AddCheckConstraintIfNotExists("ck_nodes_bounds", "nodes", "`right_bound` > `left_bound`");

        // Assert
        var operation = Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
        var definition = Assert.IsType<EnsureCheckConstraintIntent>(operation.Intent)
            .Definition;

        Assert.NotNull(definition.Expression);
        Assert.Null(definition.Sql);
    }

    /// <summary>Metadata capture must not reinterpret an explicitly authored opaque CHECK contract.</summary>
    [Fact]
    public void ExplicitRawCheckRemainsDistinctFromCapturedStructuredCheck()
    {
        // Arrange
        const string sql = "`right_bound` > `left_bound`";
        var raw = new ExpectedCheckConstraintDefinition("ck_nodes_bounds", "nodes", sql);
        var captured = SafeMigrationExpectedDefinitionFactory.CheckConstraint(
            "ck_nodes_bounds",
            "nodes",
            sql,
            schema: null);

        // Act
        var equivalent = SafeMigrationSemanticDefinitionComparers.CheckConstraint.Equals(raw, captured);

        // Assert
        Assert.False(equivalent);
        Assert.Null(raw.Expression);
        Assert.Equal(sql, raw.Sql);
        Assert.NotNull(captured.Expression);
    }

    /// <summary>Unsupported generated syntax remains opaque instead of acquiring a partial structural proof.</summary>
    [Fact]
    public void UnknownGeneratedSyntaxRemainsOpaque()
    {
        // Arrange
        const string sql = "value + INTERVAL 1 DAY";
        var operation = new AddColumnOperation
        {
            Name = "expires_at",
            Table = "items",
            ClrType = typeof(DateTime),
            ColumnType = "datetime",
            ComputedColumnSql = sql,
        };

        // Act
        var captured = SafeMigrationExpectedDefinitionFactory.From(operation);

        // Assert
        Assert.Null(captured.ComputedExpression);
        Assert.Equal(sql, captured.ComputedColumnSql);
    }
}
