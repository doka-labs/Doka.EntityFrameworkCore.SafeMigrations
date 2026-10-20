namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Protects generated dependency classification in ordinary runtime state, without a native engine.</summary>
public sealed class MySqlGeneratedColumnRenamePlanTests
{
    /// <summary>MySQL checks remain in eager state without adding a catalog-only analysis guard batch.</summary>
    /// <param name="isMariaDb">Whether native MariaDB already rewrites generated source references.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CatalogOnlyRenameQualificationIsPartOfOrdinaryState(bool isMariaDb)
    {
        // Arrange
        using var context = new DbContext(new DbContextOptionsBuilder()
            .UseMySql("Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test",
                isMariaDb ? MySqlServerVersion.MariaDb(new Version(11, 8, 8))
                    : MySqlServerVersion.MySql(new Version(8, 4, 11)))
            .UseMySqlSafeMigrations().Options);

        var operation = new SafeMigrationOperation(new RenameColumnIntent("source", "items", "target"),
            SafeMigrationPolicy.ThrowIfDifferent);

        using var capture = context.GetService<MySqlSafeMigrationPlanCapture>().Begin([operation]);

        // Act
        _ = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);
        var plan = Assert.Single(capture.Complete());

        // Assert
        Assert.Equal("TRUE", plan.StateEvaluationGuardExpression);
        Assert.False(plan.RequiresLazyStateEvaluation);
        Assert.Equal(!isMariaDb, plan.StateExpression.Contains("GENERATION_EXPRESSION", StringComparison.Ordinal));
        Assert.Equal(!isMariaDb, plan.StateExpression.Contains("THEN 'unsupported'", StringComparison.Ordinal));
    }

    /// <summary>Both engines guard source DROP against surviving native generated members without row SQL.</summary>
    /// <param name="isMariaDb">Whether the native engine is MariaDB.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DropQualificationExcludesOnlyItsOwnGeneratedMember(bool isMariaDb)
    {
        // Arrange
        using var context = new DbContext(new DbContextOptionsBuilder()
            .UseMySql("Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test",
                isMariaDb ? MySqlServerVersion.MariaDb(new Version(11, 8, 8))
                    : MySqlServerVersion.MySql(new Version(8, 4, 11)))
            .UseMySqlSafeMigrations().Options);

        var operation = new SafeMigrationOperation(new DropColumnIntent("source", "items"),
            SafeMigrationPolicy.ThrowIfDifferent);

        using var capture = context.GetService<MySqlSafeMigrationPlanCapture>().Begin([operation]);

        // Act
        _ = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);
        var plan = Assert.Single(capture.Complete());

        // Assert
        Assert.Equal("TRUE", plan.StateEvaluationGuardExpression);
        Assert.False(plan.RequiresLazyStateEvaluation);
        Assert.Contains("GENERATION_EXPRESSION", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("c.COLUMN_NAME <>", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("generated_column_drop_unproven", plan.ClassificationCodeExpression,
            StringComparison.Ordinal);
        Assert.NotNull(plan.DropLeavesNoGeneratedColumnsExpression);
        Assert.DoesNotContain("FROM `items`", plan.StateExpression, StringComparison.Ordinal);
    }
}
