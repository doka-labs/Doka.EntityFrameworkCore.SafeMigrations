namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies safe integer CHECK row validation and metadata binding gates.</summary>
public sealed class SqlServerCheckValidationSqlTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=check_validation_sql;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Parsed comparisons reject FALSE rows while allowing SQL UNKNOWN results.</summary>
    [Theory]
    [InlineData("[Left] >= 1")]
    [InlineData("[Right] > [Left]")]
    [InlineData("[Depth] >= 0 AND ([Position] IS NULL OR [Position] >= 0)")]
    [InlineData("NOT ([Value] < -2147483648 OR [Value] > 2147483647)")]
    public void SafeIntegerPredicate_UsesFalseOnlyViolationAndPhysicalColumnGuards(
        string sql
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("CK_items", "items", sql);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        // Act
        var plan = catalog.Build((SafeMigrationOperation)builder.Operations[0]);

        // Assert
        Assert.True(plan.RequiresDelayedBinding);
        Assert.Contains("WHERE NOT (", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("ty.is_user_defined = 0", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
        Assert.Contains("N'tinyint', N'smallint', N'int', N'bigint'", plan.StateEvaluationGuardExpression,
            StringComparison.Ordinal);
        Assert.Contains("HAS_PERMS_BY_NAME", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
        Assert.Equal("N'unsupported'", plan.StateEvaluationGuardFailureExpression);
        Assert.Contains("check_row_data_unproven", plan.PrerequisiteFailureCodeExpression, StringComparison.Ordinal);
        Assert.Contains("check_row_data_unproven",
            SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogTemplate(plan),
            StringComparison.Ordinal);
        Assert.Contains("cc.is_not_trusted = 0", plan.Postcondition, StringComparison.Ordinal);
    }

    /// <summary>Renderability alone does not authorize arithmetic, casts, functions, or qualified references.</summary>
    [Theory]
    [InlineData("[Value] / 0 > 1")]
    [InlineData("[Value] + 1 > 0")]
    [InlineData("CAST([Value] AS int) >= 0")]
    [InlineData("ABS([Value]) >= 0")]
    [InlineData("[items].[Value] >= 0")]
    [InlineData("[Value] >= 0.5")]
    [InlineData("[Value] >= '0'")]
    [InlineData("`Value` >= 0")]
    [InlineData("\"Value\" >= 0")]
    public void UnsafePredicate_RemainsEmptyOnly(
        string sql
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("CK_items", "items", sql);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        // Act
        var plan = catalog.Build((SafeMigrationOperation)builder.Operations[0]);

        // Assert
        Assert.DoesNotContain("WHERE NOT (", plan.StateExpression, StringComparison.Ordinal);
    }

    /// <summary>Bracketed names retain their authored quote characters without inheriting another dialect.</summary>
    [Theory]
    [InlineData("[Value`quoted] >= 0")]
    [InlineData("[Value\"quoted] >= 0")]
    public void BracketedIdentifier_WithDialectCharactersRemainsSafe(
        string sql
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var operation = new SafeMigrationOperation(new EnsureCheckConstraintIntent(
            new ExpectedCheckConstraintDefinition("CK_items", "items", sql)), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(operation);

        // Assert
        Assert.Contains("WHERE NOT (" + sql + ")", plan.StateExpression, StringComparison.Ordinal);
    }

    /// <summary>Replacement classification ignores only a local physical CHECK, never schema-wide occupancy.</summary>
    [Fact]
    public void ReplacementCapture_RequiresPhysicalSupportAndLocalCheckIdentity()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var intent = new EnsureCheckConstraintIntent(
            new ExpectedCheckConstraintDefinition("CK_items", "items", "[Value]>=1"));

        // Act
        var plan = catalog.BuildCheckPredicateCapturePlan(intent);

        // Assert
        Assert.Contains("AND NOT (EXISTS (SELECT 1 FROM sys.check_constraints", plan.StateExpression,
            StringComparison.Ordinal);
        Assert.Contains("cc.parent_object_id", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("WHERE NOT ([Value]>=1)", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("ty.is_user_defined = 0", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
        Assert.DoesNotContain(" OR ", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
    }
}
