namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies metadata-only column capacity rejection precedes runtime binding and mutation.</summary>
public sealed class SqlServerColumnLayoutGuardSqlTests
{
    private const string ConnectionString =
        "Server=127.0.0.1,1433;Database=column_layout;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Rejects layout failures before row probes, state, policy, and baseline DDL can execute.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnsureColumn_LayoutGuardPrecedesRowsStatePolicyAndDdl(
        bool nullable
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("layout_items",
            new ExpectedColumnDefinition("Value", typeof(int), nullable, "int"), "dbo"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(operation);
        var runtime = GenerateGuardedCommand(context, operation);
        var guard = "IF (" + plan.ColumnLayoutFailureExpression + ") IS NOT NULL\nBEGIN\n"
            + "    THROW 51002, N'doka_sm_unsupported', 1;\nEND;\n";

        var guardOffset = runtime.IndexOf(guard, StringComparison.Ordinal);
        var dynamicOffset = runtime.IndexOf("EXEC sys.sp_executesql", StringComparison.Ordinal);
        var rowsOffset = runtime.IndexOf("SELECT TOP (1) 1", StringComparison.Ordinal);
        var stateOffset = runtime.IndexOf("DECLARE @doka_state", StringComparison.Ordinal);
        var policyOffset = runtime.IndexOf("SET @doka_action", StringComparison.Ordinal);
        var ddlOffset = runtime.IndexOf("ALTER TABLE", StringComparison.Ordinal);

        // Assert
        var failureExpression = Assert.IsType<string>(plan.ColumnLayoutFailureExpression);
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.True(plan.RequiresDelayedBinding);
        Assert.Contains("column_limit", failureExpression, StringComparison.Ordinal);
        Assert.Contains("column_fixed_row_limit", failureExpression, StringComparison.Ordinal);
        Assert.Contains("column_layout_unproven", failureExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT TOP (1)", failureExpression, StringComparison.Ordinal);
        Assert.True(guardOffset >= 0);
        Assert.True(dynamicOffset > guardOffset);
        Assert.True(stateOffset > guardOffset);
        Assert.True(policyOffset > guardOffset);
        Assert.True(ddlOffset > guardOffset);
        Assert.True(rowsOffset > guardOffset);
    }

    /// <summary>Checks capacity even before a collated typed default needs its delayed SQL binding.</summary>
    [Fact]
    public void EnsureColumn_LayoutGuardPrecedesTypedDefaultBinding()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var definition = new ExpectedColumnDefinition("Caption", typeof(string), false, "varchar(80)",
            defaultValue: SafeMigrationDefaultValue.Literal("seed"),
            collation: new SafeMigrationCollationIdentifier("Latin1_General_100_CI_AS"));

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("layout_items", definition, "dbo"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(operation);
        var runtime = GenerateGuardedCommand(context, operation);
        var guardOffset = runtime.IndexOf("IF (" + plan.ColumnLayoutFailureExpression + ") IS NOT NULL",
            StringComparison.Ordinal);

        var defaultOffset = runtime.IndexOf("DECLARE @doka_default_supported", StringComparison.Ordinal);
        var dynamicOffset = runtime.IndexOf("EXEC sys.sp_executesql", StringComparison.Ordinal);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.NotNull(plan.ColumnLayoutFailureExpression);
        Assert.True(plan.DefaultValueSupportRequiresDelayedBinding);
        Assert.True(guardOffset >= 0);
        Assert.True(defaultOffset > guardOffset);
        Assert.True(dynamicOffset > guardOffset);
    }

    /// <summary>
    /// Existing named columns bypass capacity checks instead of consuming another slot or fixed byte.
    /// </summary>
    [Fact]
    public void EnsureColumn_ExistingTargetSelectsNullBeforeNewLayoutFailure()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("layout_items",
            new ExpectedColumnDefinition("Value", typeof(int), true, "int"), "dbo"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(operation);
        var runtime = GenerateGuardedCommand(context, operation);

        // Assert
        var failureExpression = Assert.IsType<string>(plan.ColumnLayoutFailureExpression);
        Assert.StartsWith("CASE WHEN EXISTS (SELECT 1 FROM sys.columns c", failureExpression,
            StringComparison.Ordinal);
        Assert.Contains("c.name = N'Value') THEN CONVERT(nvarchar(128), NULL) ELSE (", failureExpression,
            StringComparison.Ordinal);
        Assert.Contains("IF (" + failureExpression + ") IS NOT NULL", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("IF COALESCE((" + failureExpression, runtime, StringComparison.Ordinal);
        Assert.False(plan.IsStaticallyUnsupported);
    }

    /// <summary>Leaves unrelated supported plans free of a column-addition guard.</summary>
    [Fact]
    public void OtherPlans_HaveNoColumnLayoutGuard()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var operation = new SafeMigrationOperation(new DropTableIntent("layout_items", "dbo"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(operation);
        var runtime = GenerateGuardedCommand(context, operation);

        // Assert
        Assert.Null(plan.ColumnLayoutFailureExpression);
        Assert.DoesNotContain(") IS NOT NULL\nBEGIN\n    THROW 51002", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("column_limit", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("column_fixed_row_limit", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("column_layout_unproven", runtime, StringComparison.Ordinal);
        Assert.Contains("DROP TABLE", runtime, StringComparison.Ordinal);
    }

    private static SqlServerSafeMigrationCatalogSqlBuilder CreateCatalog(
        DbContext context
    )
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    /// <summary>Selects the guarded operation batch rather than its shared identifier preamble.</summary>
    private static string GenerateGuardedCommand(
        DbContext context,
        SafeMigrationOperation operation
    )
        => context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model)
            .Single(static command => command.CommandText.Contains("DECLARE @doka_state", StringComparison.Ordinal))
            .CommandText;
}
