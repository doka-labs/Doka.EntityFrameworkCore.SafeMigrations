namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies scalar domain boundaries and shared guarded conversion before any typed data relation.</summary>
public sealed class SqlServerManagedValueRepresentationTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=value_proofs;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Gets documented positive and negative temporal and numeric representability boundaries.</summary>
    public static IEnumerable<object[]> DomainCases()
    {
        yield return ["datetime", DateTime.MinValue, false];
        yield return ["datetime", new DateTime(1753, 1, 1), true];
        yield return ["datetime", new DateTime(9999, 12, 31, 23, 59, 59, 997), true];
        yield return ["datetime", DateTime.MaxValue, false];
        yield return ["smalldatetime", new DateTime(1899, 12, 31), false];
        yield return ["smalldatetime", new DateTime(1900, 1, 1), true];
        yield return ["smalldatetime", new DateTime(2079, 6, 6, 23, 59, 29, 998), true];
        yield return ["smalldatetime", new DateTime(2079, 6, 6, 23, 59, 30), false];
        yield return ["datetime2(7)", DateTime.MinValue, true];
        yield return ["datetime2(7)", DateTime.MaxValue, true];
        yield return ["datetime2(3)", DateTime.MaxValue, false];
        yield return ["date", DateOnly.MinValue, true];
        yield return ["time(7)", TimeSpan.FromHours(-1), false];
        yield return ["time(7)", TimeSpan.FromDays(1), false];
        yield return ["time(7)", TimeSpan.Zero, true];
        yield return ["tinyint", 255, true];
        yield return ["tinyint", 256, false];
        yield return ["decimal(3,2)", 9.994m, true];
        yield return ["decimal(3,2)", 9.995m, false];
        yield return ["money", 922337203685477.5807m, true];
        yield return ["money", 922337203685477.5808m, false];
        yield return ["real", double.MaxValue, false];
        yield return ["float", double.NaN, false];
        yield return ["float", double.PositiveInfinity, false];
        yield return ["float", 1.5d, true];
    }

    /// <summary>Rejects known scalar overflows while preserving valid minimum and rounded boundary values.</summary>
    [Theory]
    [MemberData(nameof(DomainCases))]
    public void ScalarDomain_UsesBuiltInSqlServerRanges(
        string storeType,
        object value,
        bool expected
    )
    {
        // Arrange
        var candidate = value;

        // Act
        var representable = SqlServerSafeMigrationCatalogSqlBuilder.IsManagedValueRepresentable(candidate, storeType);

        // Assert
        Assert.Equal(expected, representable);
    }

    /// <summary>
    /// Unrepresentable authored dates become invariant Unsupported before any CAST-bearing plan is generated.
    /// </summary>
    [Theory]
    [InlineData("datetime")]
    [InlineData("smalldatetime")]
    public void ManagedTemporalOverflow_IsUnsupportedBeforeRelations(string storeType)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = Catalog(context);
        var intent = new EnsureModelManagedDataIntent("dates", ["Id"], ["int"],
            ["Id", "Date"], ["int", storeType], new object?[,] { { 1, DateTime.MinValue } }, null, null);

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        Assert.True(plan.IsStaticallyUnsupported);
        Assert.Equal("model_managed_value_range", plan.UnsupportedCode);
        Assert.DoesNotContain("CAST(", plan.StateExpression);
    }

    /// <summary>
    /// Both live-plan and authored-row proofs guard valid typed values before their actual CAST evaluation.
    /// </summary>
    [Fact]
    public void ManagedTypedValue_UsesTheSameConversionGuardForLiveAndProjectedPlans()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = Catalog(context);
        var intent = new EnsureModelManagedDataIntent("dates", ["Id"], ["int"],
            ["Id", "Date"], ["int", "datetime2(7)"], new object?[,] { { 1, DateTime.MinValue } }, null, null);

        var columns = new[]
        {
            new ExpectedColumnDefinition("Id", typeof(int), false, "int"),
            new ExpectedColumnDefinition("Date", typeof(DateTime), false, "datetime2(7)"),
        };

        // Act
        var guard = catalog.ManagedValueRepresentationGuard(DateTime.MinValue, "datetime2(7)");
        var plan = catalog.Build(new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent));
        var projected = catalog.BuildProjectedSeedKeyProofSql([intent], columns, ["Id"]);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Contains("TRY_CAST(", guard);
        Assert.DoesNotContain("TRY_CAST(CAST(", guard);
        Assert.Contains(guard, plan.StateEvaluationGuardExpression);
        Assert.Contains(guard.Replace("'", "''", StringComparison.Ordinal), projected);
    }

    /// <summary>
    /// Large same-family LOB values remain supported instead of hitting TRY_CAST's input-length limitation.
    /// </summary>
    [Theory]
    [InlineData("nvarchar(max)")]
    [InlineData("varchar(max)")]
    public void ManagedLargeCharacterValue_RemainsSupported(string storeType)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = Catalog(context);
        var value = new string('a', 4_001);

        // Act
        var guard = catalog.ManagedValueRepresentationGuard(value, storeType);

        // Assert
        Assert.Equal("1 = 1", guard);
    }

    private static SqlServerSafeMigrationCatalogSqlBuilder Catalog(DbContext context)
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());
}
