namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Checks candidates against independently captured native MySQL clauses.</summary>
public sealed class MySqlNestedBooleanCatalogTests
{
    /// <summary>Both source associations must recognize the actual nested native AND chains.</summary>
    /// <param name="target">Whether to check the target rather than active tuple.</param>
    /// <param name="rightAssociated">Whether source chains associate to the right.</param>
    /// <param name="captured">Whether the caller supplies a captured ordinary-EF expression.</param>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void CapturedNativeClauseIsAnExpectedCandidate(
        bool target,
        bool rightAssociated,
        bool captured
    )
    {
        // Arrange
        using var context = new DbContext(new DbContextOptionsBuilder()
            .UseMySql("Server=localhost;Database=offline;User ID=root;Password=unused;",
                MySqlServerVersion.MySql(new Version(8, 4, 13)))
            .Options);

        var renderer = new MySqlSafeMigrationSqlExpressionRenderer(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var expression = MySqlNestedBooleanCatalogCases.Create(target, rightAssociated);
        var native = target
            ? MySqlNestedBooleanCatalogCases.TargetTuple
            : MySqlNestedBooleanCatalogCases.ActiveTuple;

        // Act
        var candidates = MySqlGeneratedExpressionDisplay.AdditionalNativeCandidates(expression,
            renderer,
            isMariaDb: false,
            capturedSource: captured);

        // Assert
        Assert.Contains(candidates, candidate => candidate.Text.Equals(native, StringComparison.Ordinal));
    }
}
