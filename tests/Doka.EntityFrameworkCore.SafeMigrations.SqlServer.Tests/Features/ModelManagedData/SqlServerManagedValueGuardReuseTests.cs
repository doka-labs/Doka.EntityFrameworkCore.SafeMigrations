namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies rendering reuse does not change provider-owned scalar or row equality proofs.</summary>
public sealed class SqlServerManagedValueGuardReuseTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=guard_reuse;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Repeated constants emit one scalar predicate but retain every authored row.</summary>
    [Theory]
    [InlineData("same", "same", 1)]
    [InlineData("Same", "same", 2)]
    public void TypedConstantReuse_PreservesExactValuesAndCompleteRows(
        string first,
        string second,
        int expectedPredicates
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var intent = new EnsureModelManagedDataIntent("items", ["Id"], ["int"],
            ["Id", "Caption"], ["int", "nvarchar(20)"],
            new object?[,] { { 1, first }, { 2, second } }, null, null);

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent));
        var guard = plan.StateEvaluationGuardExpression;
        var predicateCount = guard.Split("AS nvarchar(20)) IS NOT NULL", StringSplitOptions.None).Length - 1;
        var firstGuard = catalog.ManagedValueRepresentationGuard(first, "nvarchar(20)");

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Equal(expectedPredicates, predicateCount);
        Assert.Equal(2, plan.ModelManagedRowCount);
        Assert.Contains("CAST(1 AS int)", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("CAST(2 AS int)", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains(firstGuard, guard, StringComparison.Ordinal);
        Assert.Contains(firstGuard, plan.StateEvaluationGuardFailureExpression, StringComparison.Ordinal);
    }
}
