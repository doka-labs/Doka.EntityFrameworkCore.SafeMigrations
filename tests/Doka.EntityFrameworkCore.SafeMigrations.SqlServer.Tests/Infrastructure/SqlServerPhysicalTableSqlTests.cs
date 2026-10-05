namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies a shared physical-engine boundary before classifier probes and runtime DDL.</summary>
public sealed class SqlServerPhysicalTableSqlTests
{
    private const string ConnectionString =
        "Server=127.0.0.1,1433;Database=physical_tables;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Gates every table-scoped intent with the exact same catalog predicate at runtime.</summary>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureTable)]
    [InlineData(SafeMigrationOperationKind.DropTable)]
    [InlineData(SafeMigrationOperationKind.RenameTable)]
    [InlineData(SafeMigrationOperationKind.EnsureColumn)]
    [InlineData(SafeMigrationOperationKind.DropColumn)]
    [InlineData(SafeMigrationOperationKind.RenameColumn)]
    [InlineData(SafeMigrationOperationKind.AlterColumn)]
    [InlineData(SafeMigrationOperationKind.EnsureIndex)]
    [InlineData(SafeMigrationOperationKind.DropIndex)]
    [InlineData(SafeMigrationOperationKind.RenameIndex)]
    [InlineData(SafeMigrationOperationKind.EnsurePrimaryKey)]
    [InlineData(SafeMigrationOperationKind.DropPrimaryKey)]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint)]
    [InlineData(SafeMigrationOperationKind.DropUniqueConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint)]
    [InlineData(SafeMigrationOperationKind.DropCheckConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureForeignKey)]
    [InlineData(SafeMigrationOperationKind.DropForeignKey)]
    [InlineData(SafeMigrationOperationKind.EnsureModelManagedData)]
    [InlineData(SafeMigrationOperationKind.UpdateModelManagedData)]
    [InlineData(SafeMigrationOperationKind.DeleteModelManagedData)]
    public void TableIntent_PhysicalGatePrecedesClassificationAndRuntime(
        SafeMigrationOperationKind kind
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var operation = new SafeMigrationOperation(CreateIntent(kind), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(operation);
        var classifier = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogTemplate(plan);
        var runtime = SqlServerGuardedSqlTestContract.GenerateBody(context, operation);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        var gate = Assert.IsType<string>(plan.PhysicalTableSupportExpression);
        Assert.Contains("is_memory_optimized = 1", gate, StringComparison.Ordinal);
        Assert.Contains("is_filetable = 1", gate, StringComparison.Ordinal);
        Assert.Contains("temporal_type <> 0", gate, StringComparison.Ordinal);
        Assert.Contains("N'[dbo].[physical_items]'", gate, StringComparison.Ordinal);
        Assert.StartsWith("DECLARE @doka_physical int; EXEC sys.sp_executesql N'SELECT @doka_proof = ("
            + gate.Replace("'", "''", StringComparison.Ordinal) + ");'", classifier, StringComparison.Ordinal);
        Assert.Contains("@doka_proof = @doka_physical OUTPUT; IF COALESCE(@doka_physical, 0) <> 1 ",
            classifier, StringComparison.Ordinal);
        Assert.Contains("@doka_ordinal, N'unsupported', 0, 0, N'physical_table_unproven'",
            classifier, StringComparison.Ordinal);
        var guard = "IF COALESCE((" + gate + "), 0) <> 1\nBEGIN\n    THROW 51002";
        var engineGuard = runtime.IndexOf(guard, StringComparison.Ordinal);
        var stateEvaluation = runtime.IndexOf("DECLARE @doka_state", StringComparison.Ordinal);
        Assert.True(engineGuard >= 0);
        Assert.True(stateEvaluation > engineGuard);
    }

    /// <summary>Does not turn an absent table into an unsupported engine or a false existence match.</summary>
    [Fact]
    public void PhysicalGate_AllowsAbsenceAndLeavesExistenceAsASeparateContract()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);

        // Act
        var gate = catalog.BuildPhysicalTableSupportExpression("absent_items", null);
        var plan = catalog.Build(new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition(
            "absent_items", [new ExpectedColumnDefinition("Id", typeof(int), false, "int")]),
            SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        Assert.StartsWith("CASE WHEN NOT EXISTS (SELECT 1 FROM sys.tables", gate, StringComparison.Ordinal);
        Assert.Contains("THEN N'missing'", plan.StateExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("is_memory_optimized", plan.StateExpression, StringComparison.Ordinal);
    }

    /// <summary>Detects ledger storage without binding catalog fields introduced after SQL Server 2019.</summary>
    [Fact]
    public void PhysicalGate_LedgerDetectionUsesVersionCompatibleColumnMetadata()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);

        // Act
        var gate = CreateCatalog(context).BuildPhysicalTableSupportExpression("physical_items", null);

        // Assert
        Assert.Contains("FROM sys.columns ledger_column", gate, StringComparison.Ordinal);
        Assert.Contains("ledger_column.generated_always_type IN (5, 6, 7, 8)", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("ledger_type", gate, StringComparison.Ordinal);
    }

    /// <summary>Keeps pure catalog classifiers in bounded UNION ALL form with one shared engine predicate.</summary>
    [Fact]
    public void MetadataOnlyClassification_DoesNotRequirePerOperationDynamicSql()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var plan = CreateCatalog(context).Build(new SafeMigrationOperation(new DropTableIntent("physical_items"),
            SafeMigrationPolicy.ThrowIfDifferent));

        // Act
        var selection = SqlServerSafeMigrationProviderAnalyzer.BuildCatalogSelection(257, plan);

        // Assert
        Assert.False(plan.RequiresDelayedBinding);
        Assert.Null(plan.CatalogPreambleSql);
        Assert.StartsWith("SELECT 257,", selection, StringComparison.Ordinal);
        Assert.DoesNotContain("sp_executesql", selection, StringComparison.Ordinal);
        Assert.Equal(2, selection.Split(plan.PhysicalTableSupportExpression!, StringSplitOptions.None).Length);
        Assert.Contains("FROM (SELECT COALESCE((", selection, StringComparison.Ordinal);
        Assert.Contains("doka_physical.supported = 1", selection, StringComparison.Ordinal);
        Assert.Contains("ELSE N'physical_table_unproven' END", selection, StringComparison.Ordinal);
    }

    /// <summary>Covers both rename identities, inline FK principals, and managed-delete dependent probes.</summary>
    [Fact]
    public void PhysicalGate_IncludesEveryAdditionalPhysicalIdentity()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var foreignKey = new ExpectedForeignKeyDefinition("FK_physical", "physical_items", ["Id"],
            "physical_parent", ["Id"], principalSchema: "parent_schema");

        SafeMigrationIntent[] intents =
        [
            new RenameTableIntent("physical_items", "renamed_items", "source_schema", "target_schema"),
            new EnsureForeignKeyIntent(foreignKey),
            new EnsureTableIntent(new ExpectedTableDefinition("physical_items",
                [new ExpectedColumnDefinition("Id", typeof(int), false, "int")], foreignKeys: [foreignKey]),
                SafeMigrationTableMode.StrictDefinition),
            new DeleteModelManagedDataIntent("physical_items", ["Id"], ["int"], new object?[,] { { 1 } },
                ["Id"], ["int"], new object?[,] { { 1 } }, null,
                [new ExpectedModelManagedDataForeignKeyDefinition(
                    "physical_child", ["ParentId"], ["Id"], "child_schema")]),
        ];

        // Act
        var gates = intents.Select(intent => catalog.Build(new SafeMigrationOperation(
            intent, SafeMigrationPolicy.ThrowIfDifferent)).PhysicalTableSupportExpression!).ToArray();

        // Assert
        Assert.Contains("N'[source_schema].[physical_items]'", gates[0], StringComparison.Ordinal);
        Assert.Contains("N'[target_schema].[renamed_items]'", gates[0], StringComparison.Ordinal);
        Assert.Contains("N'[parent_schema].[physical_parent]'", gates[1], StringComparison.Ordinal);
        Assert.Contains("N'[parent_schema].[physical_parent]'", gates[2], StringComparison.Ordinal);
        Assert.Contains("N'[child_schema].[physical_child]'", gates[3], StringComparison.Ordinal);
    }

    /// <summary>Does not introduce a table-engine dependency for a schema-only operation.</summary>
    [Fact]
    public void SchemaIntents_HaveNoPhysicalTableGate()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);

        // Act
        var plans = new SafeMigrationIntent[] { new EnsureSchemaIntent("ordinary"), new DropSchemaIntent("ordinary") }
            .Select(intent => catalog.Build(new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent)));

        // Assert
        Assert.All(plans, static plan => Assert.Null(plan.PhysicalTableSupportExpression));
    }

    /// <summary>Declares inline FK topology proof inside the dynamic scalar that consumes it.</summary>
    [Fact]
    public void InlineForeignKey_PreambleSharesTheRuntimeStateEvaluationScope()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var foreignKey = new ExpectedForeignKeyDefinition("FK_inline", "physical_items", ["Id"],
            "physical_parent", ["Id"], onDelete: ReferentialAction.Cascade);

        var operation = new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition("physical_items",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int")], foreignKeys: [foreignKey]),
            SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateCatalog(context).Build(operation);
        var runtime = SqlServerGuardedSqlTestContract.GenerateBody(context, operation);

        // Assert
        Assert.NotNull(plan.CatalogPreambleSql);
        var declaration = runtime.IndexOf(
            "EXEC sys.sp_executesql N'DECLARE @doka_fk_graph_safe", StringComparison.Ordinal);

        Assert.True(declaration >= 0);

        var stateUse = runtime.IndexOf("WHEN NOT", declaration, StringComparison.Ordinal);
        Assert.True(stateUse > declaration);
        Assert.Contains("@doka_fk_graph_safe", runtime[stateUse..], StringComparison.Ordinal);
        Assert.Contains("SET @doka_value = (" + plan.StateExpression.Replace("'", "''", StringComparison.Ordinal),
            runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("SET @doka_state = (" + plan.StateExpression, runtime, StringComparison.Ordinal);
    }

    /// <summary>Leaves schema-only scalar evaluation free of unrelated FK setup and dynamic probes.</summary>
    [Fact]
    public void SchemaOnlyPlan_HasNoUnnecessaryPreamble()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var operation = new SafeMigrationOperation(
            new EnsureSchemaIntent("ordinary"), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateCatalog(context).Build(operation);
        var runtime = SqlServerGuardedSqlTestContract.GenerateBody(context, operation);

        // Assert
        Assert.Null(plan.CatalogPreambleSql);
        Assert.Contains("SET @doka_state = (", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_fk_graph_safe", runtime, StringComparison.Ordinal);
    }

    private static SqlServerSafeMigrationCatalogSqlBuilder CreateCatalog(
        DbContext context
    )
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    internal static SafeMigrationIntent CreateIntent(
        SafeMigrationOperationKind kind
    ) => kind switch
    {
        SafeMigrationOperationKind.EnsureTable => new EnsureTableIntent(new ExpectedTableDefinition("physical_items",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int")]), SafeMigrationTableMode.StrictDefinition),
        SafeMigrationOperationKind.DropTable => new DropTableIntent("physical_items"),
        SafeMigrationOperationKind.RenameTable => new RenameTableIntent("physical_items", "renamed_items"),
        SafeMigrationOperationKind.EnsureColumn => new EnsureColumnIntent("physical_items", Column()),
        SafeMigrationOperationKind.DropColumn => new DropColumnIntent("Value", "physical_items"),
        SafeMigrationOperationKind.RenameColumn => new RenameColumnIntent("Value", "physical_items", "NewValue"),
        // WHY: EF omits identical old/new alterations, so widen nullability to exercise real DDL.
        SafeMigrationOperationKind.AlterColumn =>
            new AlterColumnIntent("physical_items", Column(),
                oldDefinition: new ExpectedColumnDefinition("Value", typeof(int), false, "int")),
        SafeMigrationOperationKind.EnsureIndex => new EnsureIndexIntent(new ExpectedIndexDefinition(
            "IX_physical", "physical_items",
            [new ExpectedIndexKeyDefinition("Id")])),
        SafeMigrationOperationKind.DropIndex => new DropIndexIntent("IX_physical", "physical_items"),
        SafeMigrationOperationKind.RenameIndex => new RenameIndexIntent("IX_physical", "physical_items", "IX_renamed"),
        SafeMigrationOperationKind.EnsurePrimaryKey => new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition(
            "PK_physical", "physical_items", ["Id"])),
        SafeMigrationOperationKind.DropPrimaryKey => new DropPrimaryKeyIntent("PK_physical", "physical_items"),
        SafeMigrationOperationKind.EnsureUniqueConstraint =>
            new EnsureUniqueConstraintIntent(new ExpectedUniqueConstraintDefinition(
            "UQ_physical", "physical_items", ["Id"])),
        SafeMigrationOperationKind.DropUniqueConstraint =>
            new DropUniqueConstraintIntent("UQ_physical", "physical_items"),
        SafeMigrationOperationKind.EnsureCheckConstraint =>
            new EnsureCheckConstraintIntent(new ExpectedCheckConstraintDefinition(
            "CK_physical", "physical_items", "[Id] > 0")),
        SafeMigrationOperationKind.DropCheckConstraint =>
            new DropCheckConstraintIntent("CK_physical", "physical_items"),
        SafeMigrationOperationKind.EnsureForeignKey => new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            "FK_physical", "physical_items", ["Id"], "physical_parent", ["Id"])),
        SafeMigrationOperationKind.DropForeignKey => new DropForeignKeyIntent("FK_physical", "physical_items"),
        SafeMigrationOperationKind.EnsureModelManagedData => new EnsureModelManagedDataIntent(
            "physical_items", ["Id"], ["int"],
            ["Id"], ["int"], new object?[,] { { 1 } }, null, null),
        SafeMigrationOperationKind.UpdateModelManagedData => new UpdateModelManagedDataIntent(
            "physical_items", ["Id"], ["int"], new object?[,] { { 1 } }, ["Value"], ["int"],
            new object?[,] { { 1 } }, new object?[,] { { 2 } }, null, null),
        SafeMigrationOperationKind.DeleteModelManagedData => new DeleteModelManagedDataIntent(
            "physical_items", ["Id"], ["int"],
            new object?[,] { { 1 } }, ["Id"], ["int"], new object?[,] { { 1 } }, null, []),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static ExpectedColumnDefinition Column() => new("Value", typeof(int), true, "int");
}
