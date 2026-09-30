namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies physical filtered-index qualification before row predicates are bound.</summary>
public sealed class SqlServerPhysicalFilterGuardTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=physical_filters;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Preserves raw ANSI and Unicode literal types despite provider-neutral parsing.</summary>
    [Fact]
    public void RawFilter_PreservesLiteralEncodingAndEscapedQuotes()
    {
        // Arrange
        var definition = new ExpectedIndexDefinition("IX_items_Code", "items",
            [new ExpectedIndexKeyDefinition("Code")], filter: "[odd'column] = 'a''b' AND [Caption] IN (N'c', 'd')");

        // Act
        var filter = SqlServerSafeMigrationCatalogSqlBuilder.GetStructuredIndexFilter(definition);
        var comparisons = filter is null ? Array.Empty<(string Column, SafeMigrationSqlLiteralExpression? Literal)>()
            : SqlServerSafeMigrationCatalogSqlBuilder.GetIndexFilterComparisons(filter).ToArray();

        // Assert
        Assert.NotNull(filter);
        Assert.Equal(3, comparisons.Length);
        Assert.Equal("a'b", comparisons[0].Literal?.Value);
        Assert.Equal("varchar(max)", comparisons[0].Literal?.StoreType);
        Assert.Equal("nvarchar(max)", comparisons[1].Literal?.StoreType);
        Assert.Equal("varchar(max)", comparisons[2].Literal?.StoreType);
        Assert.Equal("[odd'column] = 'a''b' AND [Caption] IN (N'c', 'd')", definition.Filter);
    }

    /// <summary>Shares one physical constant proof across pure, delayed, and runtime classification.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PhysicalFilterGate_PrecedesPrerequisitesAndData(
        bool unique
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var definition = new ExpectedIndexDefinition("IX_items_Code", "items",
            [new ExpectedIndexKeyDefinition("Code")], unique: unique, filter: "[Flag] = N'abc'");

        var operation = new SafeMigrationOperation(new EnsureIndexIntent(definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(operation);
        var selection = unique ? SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogSelection(257, plan)
            : SqlServerSafeMigrationProviderAnalyzer.BuildCatalogSelection(257, plan);

        var runtime = string.Join("\n", context.GetService<IMigrationsSqlGenerator>()
            .Generate([operation], context.Model).Select(static command => command.CommandText));

        // Assert
        var support = Assert.IsType<string>(plan.IndexFilterSupportExpression);
        Assert.Contains("c.is_computed = 0", support, StringComparison.Ordinal);
        Assert.Contains("ty.is_user_defined = 0", support, StringComparison.Ordinal);
        Assert.Contains("ty.is_assembly_type = 0", support, StringComparison.Ordinal);
        Assert.Contains("TRY_CAST", support, StringComparison.Ordinal);
        Assert.Contains("NOT EXISTS", support, StringComparison.Ordinal);
        Assert.Contains(support, selection, StringComparison.Ordinal);
        Assert.Contains("index_filter_unproven", selection, StringComparison.Ordinal);
        Assert.Contains("N'Flag'", plan.PrerequisiteExpression, StringComparison.Ordinal);
        var supportOffset = runtime.IndexOf(support, StringComparison.Ordinal);
        var stateOffset = runtime.IndexOf("DECLARE @doka_state", StringComparison.Ordinal);
        Assert.True(supportOffset >= 0 && stateOffset > supportOffset);
        if (unique)
        {
            Assert.True(selection.IndexOf(support, StringComparison.Ordinal)
                < selection.IndexOf("INSERT INTO @doka_analysis EXEC", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains("doka_filter.supported", selection, StringComparison.Ordinal);
        }
    }

    /// <summary>Never emits SQL Server's prohibited numeric-to-datetime2 TRY_CAST in a skipped branch.</summary>
    [Fact]
    public void TypedNumericFilter_DoesNotBindProhibitedTemporalConversions()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var definition = new ExpectedIndexDefinition("IX_items_Code", "items",
            [new ExpectedIndexKeyDefinition("Code")], structuredFilter: SafeMigrationSql.Binary(
                SafeMigrationSql.Identifier("Flag"), SafeMigrationSqlBinaryOperator.Equal,
                SafeMigrationSql.Literal("1", "int")));

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(new EnsureIndexIntent(definition),
            SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        var support = Assert.IsType<string>(plan.IndexFilterSupportExpression);
        Assert.DoesNotContain("AS datetime2", support, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AS datetimeoffset", support, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AS time(", support, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AS int", support, StringComparison.OrdinalIgnoreCase);
    }
}
