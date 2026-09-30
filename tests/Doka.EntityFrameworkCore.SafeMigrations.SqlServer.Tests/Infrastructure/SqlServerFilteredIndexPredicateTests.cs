namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies SQL Server's restricted filtered-index grammar before row binding or baseline generation.
/// </summary>
public sealed class SqlServerFilteredIndexPredicateTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=filter_predicates;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>
    /// Accepts only documented column/constant comparisons, membership, null tests, and conjunctions.
    /// </summary>
    /// <param name="filter">The authored SQL filter.</param>
    /// <param name="supported">Whether SQL Server's restricted filter grammar supports the shape.</param>
    [Theory]
    [InlineData("[Flag] = 1", true)]
    [InlineData("[Flag] <> 1", true)]
    [InlineData("[Flag] != 1", true)]
    [InlineData("[Flag] > 1", true)]
    [InlineData("[Flag] >= 1", true)]
    [InlineData("[Flag] < 1", true)]
    [InlineData("[Flag] <= 1", true)]
    [InlineData("[Flag] IN (1, 2)", true)]
    [InlineData("[Flag] IS NULL", true)]
    [InlineData("[Flag] IS NOT NULL", true)]
    [InlineData("[Code] IS NOT NULL AND [Flag] IN (1, 2)", true)]
    [InlineData("[Flag] = 1 OR [Flag] = 2", false)]
    [InlineData("[Flag] = [Id]", false)]
    [InlineData("[Flag] = NULL", false)]
    [InlineData("[Flag] <> NULL", false)]
    [InlineData("[Flag] NOT IN (1, 2)", false)]
    [InlineData("ABS([Flag]) = 1", false)]
    [InlineData("[Flag] + 1 = 2", false)]
    [InlineData("[Flag] = 1 + 1", false)]
    [InlineData("[Flag] BETWEEN 1 AND 2", false)]
    [InlineData("NOT ([Flag] = 1)", false)]
    [InlineData("1 = 1", false)]
    [InlineData("[Flag] IN (1, NULL)", false)]
    public void RawFilterUsesRestrictedGrammar(
        string filter,
        bool supported
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateFilterCatalog(context);
        var definition = new ExpectedIndexDefinition(
            "IX_filter_items_Code",
            "filter_items",
            [new ExpectedIndexKeyDefinition("Code")],
            unique: true,
            filter: filter);

        var operation = new SafeMigrationOperation(
            new EnsureIndexIntent(definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(operation);
        var failure = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));

        // Assert
        Assert.Equal(!supported, plan.IsStaticallyUnsupported);
        if (supported)
        {
            Assert.Null(failure);
            Assert.Null(plan.UnsupportedCode);
            Assert.Contains("GROUP BY", plan.StateExpression, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal("index_filter_unproven", plan.UnsupportedCode);
            Assert.IsType<NotSupportedException>(failure);
            Assert.DoesNotContain("GROUP BY", plan.StateExpression, StringComparison.Ordinal);
            Assert.DoesNotContain("filter_items", plan.StateExpression, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Requires the same restricted grammar for structured expressions instead of a broader renderer contract.
    /// </summary>
    /// <param name="shape">The structured-expression case.</param>
    /// <param name="supported">Whether the shape is valid filtered-index grammar.</param>
    [Theory]
    [InlineData("and", true)]
    [InlineData("in", true)]
    [InlineData("null", true)]
    [InlineData("or", false)]
    [InlineData("column", false)]
    [InlineData("null-comparison", false)]
    [InlineData("not-in", false)]
    [InlineData("function", false)]
    [InlineData("arithmetic", false)]
    public void StructuredFilterUsesTheSameRestrictedGrammar(
        string shape,
        bool supported
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var definition = new ExpectedIndexDefinition(
            "IX_filter_items_Code",
            "filter_items",
            [new ExpectedIndexKeyDefinition("Code")],
            unique: true,
            structuredFilter: FilterExpression(shape));

        var operation = new SafeMigrationOperation(
            new EnsureIndexIntent(definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateFilterCatalog(context).Build(operation);
        var failure = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));

        // Assert
        Assert.Equal(!supported, plan.IsStaticallyUnsupported);
        if (supported)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal("index_filter_unproven", plan.UnsupportedCode);
            Assert.IsType<NotSupportedException>(failure);
            Assert.DoesNotContain("GROUP BY", plan.StateExpression, StringComparison.Ordinal);
        }
    }

    private static SqlServerSafeMigrationCatalogSqlBuilder CreateFilterCatalog(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    private static SafeMigrationSqlExpression FilterExpression(
        string shape
    )
    {
        var flag = SafeMigrationSql.Identifier("Flag");
        var one = SafeMigrationSql.Literal(1);
        var comparison = SafeMigrationSql.Binary(flag, SafeMigrationSqlBinaryOperator.Equal, one);

        return shape switch
        {
            "and" => SafeMigrationSql.Binary(comparison, SafeMigrationSqlBinaryOperator.And,
                SafeMigrationSql.IsNotNull(SafeMigrationSql.Identifier("Code"))),
            "in" => SafeMigrationSql.In(flag, [one, SafeMigrationSql.Literal(2)]),
            "null" => SafeMigrationSql.IsNull(flag),
            "or" => SafeMigrationSql.Binary(comparison, SafeMigrationSqlBinaryOperator.Or,
                SafeMigrationSql.IsNotNull(SafeMigrationSql.Identifier("Code"))),
            "column" => SafeMigrationSql.Binary(flag, SafeMigrationSqlBinaryOperator.Equal,
                SafeMigrationSql.Identifier("Id")),
            "null-comparison" => SafeMigrationSql.Binary(flag, SafeMigrationSqlBinaryOperator.Equal,
                SafeMigrationSql.Literal(null)),
            "not-in" => SafeMigrationSql.In(flag, [one], negated: true),
            "function" => SafeMigrationSql.Binary(SafeMigrationSql.Function("ABS", flag),
                SafeMigrationSqlBinaryOperator.Equal, one),
            "arithmetic" => SafeMigrationSql.Binary(
                SafeMigrationSql.Binary(flag, SafeMigrationSqlBinaryOperator.Add, one),
                SafeMigrationSqlBinaryOperator.Equal, one),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }
}
