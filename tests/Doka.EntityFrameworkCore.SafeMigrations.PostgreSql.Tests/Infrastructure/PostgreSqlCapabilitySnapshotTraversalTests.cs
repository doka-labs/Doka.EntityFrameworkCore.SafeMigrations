namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Guards ordered capability scans of immutable table definitions without a live server.</summary>
public sealed class PostgreSqlCapabilitySnapshotTraversalTests
{
    /// <summary>UUID admission examines the final generated column even after many supported ordinary columns.</summary>
    /// <param name="width">The complete immutable table-column count.</param>
    /// <param name="valid">Whether the last column's captured literal is a canonical UUID.</param>
    [Theory]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(16, true)]
    [InlineData(16, false)]
    [InlineData(128, true)]
    [InlineData(128, false)]
    public void LastGeneratedColumnRetainsUuidAssignmentValidation(int width, bool valid)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Host=127.0.0.1;Database=capability_snapshot;Username=unused;Password=unused");

        var catalog = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var literal = valid ? "00000000-0000-0000-0000-000000000001" : "not-a-uuid";
        var columns = Enumerable.Range(0, width - 1)
            .Select(index => new ExpectedColumnDefinition("c" + index.ToString(CultureInfo.InvariantCulture),
                typeof(int), false, "integer"))
            .Append(new ExpectedColumnDefinition("generated", typeof(Guid), true, "uuid", isStored: true,
                computedExpression: SafeMigrationSql.Literal(literal))
            {
                CapturedComputedSql = "'" + literal + "'",
            })
            .ToArray();

        var intent = new EnsureTableIntent(new ExpectedTableDefinition("items", columns),
            SafeMigrationTableMode.StrictDefinition);

        var classify = typeof(PostgreSqlSafeMigrationCatalogSqlBuilder)
            .GetMethod("GetUnsupportedColumnFeature", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate<Func<SafeMigrationIntent, string?>>(catalog);

        // Act
        var unsupported = classify(intent);

        // Assert
        Assert.Equal(valid ? null : "generated_expression_assignment", unsupported);
    }
}
