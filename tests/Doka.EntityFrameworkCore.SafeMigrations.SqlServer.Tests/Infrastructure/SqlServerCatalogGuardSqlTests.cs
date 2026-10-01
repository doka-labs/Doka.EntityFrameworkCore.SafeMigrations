namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies delayed local proof scope and builtin store-type provenance before database execution.
/// </summary>
public sealed class SqlServerCatalogGuardSqlTests
{
    private const string ConnectionString =
        "Server=127.0.0.1,1433;Database=catalog_guards;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>
    /// Declares local proof variables inside the same escaped dynamic command that consumes them.
    /// </summary>
    [Fact]
    public void DelayedPreamble_IsEscapedInsideTheStateEvaluationScope()
    {
        // Arrange
        var plan = new SqlServerSafeMigrationRuntimePlan(
            "CASE WHEN @proof = 1 THEN N'matching' ELSE N'different' END",
            "0", SafeMigrationRepairCapability.None, "0")
        {
            RequiresDelayedBinding = true,
            CatalogPreambleSql = "DECLARE @proof bit = 1; DECLARE @label nvarchar(20) = N'O''Brien';",
        };

        const string dynamicPrefix = "INSERT INTO @doka_analysis EXEC sys.sp_executesql N'";

        // Act
        var sql = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogSelection(257, plan);
        var dynamicStart = sql.IndexOf(dynamicPrefix, StringComparison.Ordinal);
        var fallbackStart = sql.IndexOf("', N'@doka_ordinal int'", StringComparison.Ordinal);

        // Assert
        Assert.True(dynamicStart >= 0);
        Assert.True(fallbackStart > dynamicStart);
        var dynamicSql = sql[(dynamicStart + dynamicPrefix.Length)..fallbackStart];
        Assert.StartsWith("DECLARE @proof bit = 1; DECLARE @label nvarchar(20) = N''O''''Brien'';\n"
            + "SELECT @doka_ordinal,",
            dynamicSql, StringComparison.Ordinal);
        Assert.Contains("CASE WHEN @proof = 1 THEN N''matching'' ELSE N''different'' END",
            dynamicSql, StringComparison.Ordinal);
        Assert.DoesNotContain("@proof", sql[..dynamicStart], StringComparison.Ordinal);
        Assert.DoesNotContain("@proof", sql[fallbackStart..], StringComparison.Ordinal);
    }

    /// <summary>
    /// Includes the escaped preamble itself in the delayed wire-payload bound.
    /// </summary>
    [Fact]
    public void DelayedPreamble_QuoteExpansionCannotEscapeThePayloadBound()
    {
        // Arrange
        var preamble = "DECLARE @proof nvarchar(max) = N'"
            + new string('\'', SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes / 2) + "';";

        var plan = new SqlServerSafeMigrationRuntimePlan("N'matching'", "0", SafeMigrationRepairCapability.None, "0")
        {
            RequiresDelayedBinding = true,
            CatalogPreambleSql = preamble,
        };

        // Act
        var observed = Record.Exception(() =>
            SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogSelection(379, plan));

        // Assert
        Assert.True(Encoding.UTF8.GetByteCount(preamble) < SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes);
        Assert.Contains("operation 379 exceeds a bounded query limit",
            Assert.IsType<InvalidOperationException>(observed).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Does not equate a same-named user-defined or assembly type with an ordinary expected builtin type.
    /// </summary>
    [Theory]
    [InlineData(typeof(int), "int")]
    [InlineData(typeof(Guid), "uniqueidentifier")]
    public void OrdinaryColumn_RequiresBuiltinTypeProvenance(
        Type clrType,
        string storeType
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("catalog_guard_items",
            new ExpectedColumnDefinition("Value", clrType, true, storeType)), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(operation);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Contains("JOIN sys.types ty ON ty.user_type_id = c.user_type_id",
            plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("ty.is_user_defined = 0", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("ty.is_assembly_type = 0", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("ty.is_user_defined = 0", plan.Postcondition, StringComparison.Ordinal);
        Assert.Contains("ty.is_assembly_type = 0", plan.Postcondition, StringComparison.Ordinal);
    }

    /// <summary>
    /// Proves builtin provenance for model-managed keys and values before reading or changing rows.
    /// </summary>
    [Theory]
    [InlineData("ensure")]
    [InlineData("update")]
    [InlineData("delete")]
    public void ModelManagedData_KeyAndValueGuardsRequireBuiltinTypeProvenance(
        string operationKind
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var intent = CreateModelManagedIntent(operationKind);
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(operation);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.True(plan.RequiresDelayedBinding);
        Assert.Contains("JOIN sys.types ty ON ty.user_type_id = c.user_type_id",
            plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
        Assert.Equal(2,
            plan.StateEvaluationGuardExpression.Split("ty.is_user_defined = 0", StringSplitOptions.None).Length - 1);
        Assert.Equal(2,
            plan.StateEvaluationGuardExpression.Split("ty.is_assembly_type = 0", StringSplitOptions.None).Length - 1);
        Assert.Contains("c.name = N'Id'", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
        Assert.Contains("c.name = N'Value'", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
    }

    private static SqlServerSafeMigrationCatalogSqlBuilder CreateCatalog(
        DbContext context
    )
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    private static ModelManagedDataIntent CreateModelManagedIntent(
        string operationKind
    )
        => operationKind switch
        {
            "ensure" => new EnsureModelManagedDataIntent("catalog_guard_items", ["Id"], ["int"],
                ["Id", "Value"], ["int", "nvarchar(80)"], new object?[,] { { 1, "target" } }, null, null),
            "update" => new UpdateModelManagedDataIntent("catalog_guard_items", ["Id"], ["int"],
                new object?[,] { { 1 } }, ["Value"], ["nvarchar(80)"],
                new object?[,] { { "source" } }, new object?[,] { { "target" } }, null, null),
            "delete" => new DeleteModelManagedDataIntent("catalog_guard_items", ["Id"], ["int"],
                new object?[,] { { 1 } }, ["Id", "Value"], ["int", "nvarchar(80)"],
                new object?[,] { { 1, "source" } }, null, []),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind)),
        };
}
