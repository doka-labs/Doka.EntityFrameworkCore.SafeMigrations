namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies native source roles independently from the portable SQL Server Boolean renderer.</summary>
public sealed class SqlServerSourceExpressionRoleTests
{
    /// <summary>Explicit raw empty-only CHECKs do not reinterpret unknown function type arguments as columns.</summary>
    [Fact]
    public void ExplicitRawCheckKeepsUnknownTypeArgumentsOutsideColumnProofs()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Server=127.0.0.1;Database=roles;User ID=sa;Password=unused;TrustServerCertificate=True");

        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var definition = new ExpectedCheckConstraintDefinition("ck_roles", "items",
            "TRY_CONVERT(int, [value]) > 0");

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(
            new EnsureCheckConstraintIntent(definition), SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.DoesNotContain("N'int'", plan.PrerequisiteExpression, StringComparison.Ordinal);
    }

    /// <summary>Empty-only row proofs still retain every parsed native operand prerequisite.</summary>
    [Fact]
    public void ComplexCheckKeepsSourceColumnPrerequisites()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Server=127.0.0.1;Database=roles;User ID=sa;Password=unused;TrustServerCertificate=True");

        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var definition = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_roles", "items",
            "CASE WHEN [value] > 0 THEN 10 / [value] ELSE 0 END >= 0", null);

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(
            new EnsureCheckConstraintIntent(definition), SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Contains("N'value'", plan.PrerequisiteExpression, StringComparison.Ordinal);
    }

    /// <summary>Native CHECK and CASE predicates cannot inherit portable scalar truthiness conversions.</summary>
    /// <param name="sql">The validated source grammar.</param>
    /// <param name="scalar">Whether the source occupies a native scalar role.</param>
    /// <param name="predicate">Whether the source occupies a native predicate role.</param>
    [Theory]
    [InlineData("1", true, false)]
    [InlineData("NOT 1", false, false)]
    [InlineData("[enabled]", true, false)]
    [InlineData("TRUE", false, false)]
    [InlineData("FALSE", false, false)]
    [InlineData("[TRUE]", true, false)]
    [InlineData("[value] > 0", false, true)]
    [InlineData("NOT ([value] > 0)", false, true)]
    [InlineData("[value] IS NULL OR [value] BETWEEN 1 AND 2", false, true)]
    [InlineData("[value] IN (1, 2)", false, true)]
    [InlineData("CASE WHEN [value] > 0 THEN 1 ELSE 0 END", true, false)]
    [InlineData("CASE WHEN 1 THEN 1 ELSE 0 END = 1", false, false)]
    [InlineData("CASE WHEN [enabled] THEN 1 ELSE 0 END = 1", false, false)]
    [InlineData("CASE WHEN [value] > 0 THEN 1 ELSE 0 END = 1", false, true)]
    [InlineData("CASE WHEN [value] > 0 THEN [value] > 1 ELSE 0 END", false, false)]
    [InlineData("COALESCE([value] > 0, 0)", false, false)]
    [InlineData("CAST([value] > 0 AS bit)", false, false)]
    [InlineData("CONVERT(bit, CASE WHEN [value] > 0 THEN 1 ELSE 0 END)", true, false)]
    public void NativeRolesRejectScalarPredicateConfusion(string sql, bool scalar, bool predicate)
    {
        // Arrange
        Assert.True(SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var expression));

        // Act
        var actualScalar = SqlServerSourceExpressionRoles.IsNativeScalar(expression);
        var actualPredicate = SqlServerSourceExpressionRoles.IsNativePredicate(expression);

        // Assert
        Assert.Equal(scalar, actualScalar);
        Assert.Equal(predicate, actualPredicate);
    }

    /// <summary>Invalid native predicates are rejected before an empty table can authorize CHECK application.</summary>
    /// <param name="sql">The syntactically parsed but native-invalid source.</param>
    [Theory]
    [InlineData("1")]
    [InlineData("NOT 1")]
    [InlineData("CASE WHEN 1 THEN 1 ELSE 0 END = 1")]
    [InlineData("COALESCE([value] > 0, 0) = 0")]
    public void CheckPlanRejectsInvalidNativeSourceRoles(string sql)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Server=127.0.0.1;Database=roles;User ID=sa;Password=unused;TrustServerCertificate=True");

        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var definition = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_roles", "items", sql, null);

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(
            new EnsureCheckConstraintIntent(definition), SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        Assert.True(plan.IsStaticallyUnsupported);
        Assert.Equal("check_expression_unproven", plan.UnsupportedCode);
    }
}
