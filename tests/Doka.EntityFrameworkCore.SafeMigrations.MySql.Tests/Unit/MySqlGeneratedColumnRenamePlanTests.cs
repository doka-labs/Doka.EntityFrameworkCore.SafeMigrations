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
        Assert.Equal(!isMariaDb, plan.StateExpression.Contains(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS c WHERE c.TABLE_SCHEMA = DATABASE() "
            + "AND c.TABLE_NAME =", StringComparison.Ordinal));
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
        Assert.Contains("CAST(LOWER(c.COLUMN_NAME) AS BINARY) <>", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("generated_column_drop_unproven", plan.ClassificationCodeExpression,
            StringComparison.Ordinal);
        Assert.NotNull(plan.DropLeavesNoGeneratedColumnsExpression);
        Assert.Contains("SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS c WHERE c.TABLE_SCHEMA = DATABASE() "
            + "AND c.TABLE_NAME =", plan.DropLeavesNoGeneratedColumnsExpression, StringComparison.Ordinal);
        Assert.Equal(isMariaDb, plan.Postcondition.Contains("CAST(LOWER(c.COLUMN_NAME) AS BINARY) =",
            StringComparison.Ordinal));
        Assert.DoesNotContain("FROM `items`", plan.StateExpression, StringComparison.Ordinal);
    }

    /// <summary>Only MariaDB DROP can use native identifier absence without borrowing empty-set evidence.</summary>
    /// <param name="isMariaDb">Whether the catalog prints freshly bound physical names.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeSourceAbsenceIsDropOnlyAndDoesNotCertifyAnEmptyGeneratedSet(bool isMariaDb)
    {
        // Arrange
        using var context = new DbContext(new DbContextOptionsBuilder()
            .UseMySql("Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test",
                isMariaDb ? MySqlServerVersion.MariaDb(new Version(11, 8, 8))
                    : MySqlServerVersion.MySql(new Version(8, 4, 11)))
            .UseMySqlSafeMigrations().Options);

        var drop = new SafeMigrationOperation(new DropColumnIntent("source", "items"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var rename = new SafeMigrationOperation(new RenameColumnIntent("source", "items", "target"),
            SafeMigrationPolicy.ThrowIfDifferent);
        using var capture = context.GetService<MySqlSafeMigrationPlanCapture>().Begin([drop, rename]);

        // Act
        _ = context.GetService<IMigrationsSqlGenerator>().Generate([drop, rename], context.Model);
        var plans = capture.Complete();

        // Assert
        Assert.Equal(2, plans.Length);
        Assert.Equal(isMariaDb, plans[0].StateExpression.Contains("native_source", StringComparison.Ordinal));
        Assert.DoesNotContain("native_source", plans[1].StateExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("native_source", plans[0].DropLeavesNoGeneratedColumnsExpression!,
            StringComparison.Ordinal);
        Assert.DoesNotContain("LOCATE", plans[0].DropLeavesNoGeneratedColumnsExpression!, StringComparison.Ordinal);
        if (isMariaDb)
        {
            Assert.Contains("REPLACE(CAST(native_source.COLUMN_NAME AS BINARY), 0x60, 0x6060)",
                plans[0].StateExpression, StringComparison.Ordinal);
            Assert.Contains("REPLACE(CAST(native_source.COLUMN_NAME AS BINARY), 0x22, 0x2222)",
                plans[0].StateExpression, StringComparison.Ordinal);
            Assert.Contains("AND NOT COALESCE(", plans[0].StateExpression, StringComparison.Ordinal);
            Assert.Contains("CAST(c.COLUMN_NAME AS BINARY) <> CAST(native_source.COLUMN_NAME AS BINARY)",
                plans[0].StateExpression, StringComparison.Ordinal);
        }

        Assert.Equal("TRUE", plans[0].StateEvaluationGuardExpression);
        Assert.False(plans[0].RequiresLazyStateEvaluation);
        Assert.DoesNotContain("FROM `items`", plans[0].StateExpression, StringComparison.Ordinal);
    }
}
