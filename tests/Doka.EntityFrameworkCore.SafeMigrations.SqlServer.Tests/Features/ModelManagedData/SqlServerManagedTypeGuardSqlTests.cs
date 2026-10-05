namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies set-based managed-data metadata proofs retain every captured type contract.</summary>
public sealed class SqlServerManagedTypeGuardSqlTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=managed_type_guard;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Key and value columns share one catalog join without losing builtin provenance.</summary>
    [Fact]
    public void TableTypeGuard_UsesOneCatalogJoinForKeysAndValues()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var intent = new EnsureModelManagedDataIntent("items", ["Id"], ["int"],
            ["Id", "Caption", "Amount"], ["int", "nvarchar(40)", "decimal(9,3)"],
            new object?[,] { { 1, null, null } }, null, null);

        // Act
        var guard = catalog.BuildModelManagedDataAnalysisGuard(intent).Guard;

        // Assert
        Assert.Equal(1, Occurrences(guard, "LEFT JOIN sys.columns c"));
        Assert.Equal(1, Occurrences(guard, "LEFT JOIN sys.types ty"));
        Assert.Contains("ty.is_user_defined = 0 AND ty.is_assembly_type = 0", guard, StringComparison.Ordinal);
        Assert.Contains("WHERE CASE WHEN c.column_id IS NOT NULL", guard, StringComparison.Ordinal);
        Assert.Contains("THEN 0 ELSE 1 END = 1)", guard, StringComparison.Ordinal);
        Assert.Contains("(N'Id', N'int', CAST(NULL AS int), CAST(NULL AS int), CAST(NULL AS int))",
            guard, StringComparison.Ordinal);
        Assert.Contains("(N'Caption', N'nvarchar', 80, CAST(NULL AS int), CAST(NULL AS int))",
            guard, StringComparison.Ordinal);
        Assert.Contains("(N'Amount', N'decimal', CAST(NULL AS int), 9, 3)", guard, StringComparison.Ordinal);
    }

    /// <summary>The shared parser retains byte lengths, MAX, precision, scale, and absent facets.</summary>
    /// <param name="storeType">The authored store type.</param>
    /// <param name="expectedRow">The catalog-contract VALUES row.</param>
    [Theory]
    [InlineData("nvarchar(40)", "(N'Value', N'nvarchar', 80, CAST(NULL AS int), CAST(NULL AS int))")]
    [InlineData("nvarchar(max)", "(N'Value', N'nvarchar', -1, CAST(NULL AS int), CAST(NULL AS int))")]
    [InlineData("varbinary(16)", "(N'Value', N'varbinary', 16, CAST(NULL AS int), CAST(NULL AS int))")]
    [InlineData("decimal(9,3)", "(N'Value', N'decimal', CAST(NULL AS int), 9, 3)")]
    [InlineData("datetime2(4)", "(N'Value', N'datetime2', CAST(NULL AS int), CAST(NULL AS int), 4)")]
    [InlineData("int", "(N'Value', N'int', CAST(NULL AS int), CAST(NULL AS int), CAST(NULL AS int))")]
    public void TableTypeGuard_RetainsOptionalPhysicalFacets(
        string storeType,
        string expectedRow
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var intent = new EnsureModelManagedDataIntent("items", ["Id"], ["int"],
            ["Id", "Value"], ["int", storeType], new object?[,] { { 1, null } }, null, null);

        // Act
        var guard = catalog.BuildModelManagedDataAnalysisGuard(intent).Guard;

        // Assert
        Assert.Contains(expectedRow, guard, StringComparison.Ordinal);
        Assert.Contains("expected.max_length IS NULL OR c.max_length = expected.max_length",
            guard, StringComparison.Ordinal);
        Assert.Contains("expected.precision_value IS NULL OR c.precision = expected.precision_value",
            guard, StringComparison.Ordinal);
        Assert.Contains("expected.scale_value IS NULL OR c.scale = expected.scale_value",
            guard, StringComparison.Ordinal);
    }

    /// <summary>Malformed physical contracts cannot become an empty successful catalog proof.</summary>
    /// <param name="storeType">The unsupported or malformed type text.</param>
    [Theory]
    [InlineData("nvarchar")]
    [InlineData("nvarchar(0)")]
    [InlineData("decimal(9,10)")]
    [InlineData("opaque")]
    public void TableTypeGuard_InvalidParseFailsClosed(
        string storeType
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var intent = new EnsureModelManagedDataIntent("items", ["Id"], ["int"],
            ["Id", "Value"], ["int", storeType], new object?[,] { { 1, null } }, null, null);

        // Act
        var guard = catalog.BuildModelManagedDataAnalysisGuard(intent).Guard;

        // Assert
        Assert.Contains("AND (1 = 0)", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("LEFT JOIN sys.types ty", guard, StringComparison.Ordinal);
    }

    /// <summary>A column captured as both key and value retains its first key contract exactly once.</summary>
    [Fact]
    public void TableTypeGuard_OverlappingKeyAndValueAppearsOnce()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var intent = new EnsureModelManagedDataIntent("items", ["Id"], ["INT"],
            ["Id"], ["int"], new object?[,] { { 1 } }, null, null);

        // Act
        var guard = catalog.BuildModelManagedDataAnalysisGuard(intent).Guard;

        // Assert
        Assert.Equal(1, Occurrences(guard,
            "(N'Id', N'int', CAST(NULL AS int), CAST(NULL AS int), CAST(NULL AS int))"));
        Assert.Equal(1, Occurrences(guard, "LEFT JOIN sys.types ty"));
    }

    /// <summary>Dependent-table grouping preserves every foreign-key type requirement and schema identity.</summary>
    [Fact]
    public void DeleteTypeGuard_GroupsPhysicalTablesWithoutDiscardingConflictingColumnContracts()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var intent = new DeleteModelManagedDataIntent("principals", ["Id"], ["int"],
            new object?[,] { { 1 } }, ["Id", "Code"], ["int", "bigint"],
            new object?[,] { { 1, 2L } }, null,
            [new ExpectedModelManagedDataForeignKeyDefinition("dependents", ["ReferenceId"], ["Id"]),
                new ExpectedModelManagedDataForeignKeyDefinition("dependents", ["ReferenceId"], ["Code"], "dbo"),
                new ExpectedModelManagedDataForeignKeyDefinition("dependents", ["ReferenceId"], ["Id"], "audit")]);

        // Act
        var guard = catalog.BuildModelManagedDataAnalysisGuard(intent).Guard;

        // Assert
        Assert.Equal(3, Occurrences(guard, "LEFT JOIN sys.types ty"));
        Assert.Equal(1, Occurrences(guard, "OBJECT_ID(N'[dbo].[dependents]', 'U')"));
        Assert.Equal(1, Occurrences(guard, "OBJECT_ID(N'[audit].[dependents]', 'U')"));
        Assert.Contains("(N'ReferenceId', N'int', CAST(NULL AS int), CAST(NULL AS int), CAST(NULL AS int)), "
            + "(N'ReferenceId', N'bigint', CAST(NULL AS int), CAST(NULL AS int), CAST(NULL AS int))",
            guard, StringComparison.Ordinal);
    }

    /// <summary>Analysis and execution keep identical proofs in their independent binding scopes.</summary>
    [Fact]
    public void TableTypeGuard_AnalysisAndExecutionRetainTheSameProof()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var intent = new EnsureModelManagedDataIntent("items", ["Id"], ["int"],
            ["Id", "Caption"], ["int", "nvarchar(40)"], new object?[,] { { 1, "target" } }, null, null);

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent));
        var analysis = catalog.BuildModelManagedDataAnalysisGuard(intent);

        // Assert
        Assert.Equal(analysis.Guard, plan.StateEvaluationGuardExpression);
        Assert.Equal(analysis.Failure, plan.StateEvaluationGuardFailureExpression);
        Assert.True(plan.RequiresDelayedBinding);
    }

    /// <summary>
    /// Identity INSERT remains supported, UPDATE is blocked, and DELETE does not require writable columns.
    /// </summary>
    /// <param name="kind">The managed-data operation.</param>
    /// <param name="rejectsIdentity">Whether the metadata proof must reject identity targets.</param>
    [Theory]
    [InlineData("ensure", false)]
    [InlineData("update", true)]
    [InlineData("delete", false)]
    public void WritableGuard_UsesOneColumnProbeAndPreservesIdentitySemantics(
        string kind,
        bool rejectsIdentity
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var intent = CreateIdentityIntent(kind);

        // Act
        var guard = catalog.BuildModelManagedDataAnalysisGuard(intent).Guard;

        // Assert
        Assert.DoesNotContain("sys.identity_columns", guard, StringComparison.Ordinal);
        Assert.Equal(rejectsIdentity, guard.Contains("OR c.is_identity = 1", StringComparison.Ordinal));
        Assert.Equal(kind != "delete", guard.Contains("c.is_computed = 1", StringComparison.Ordinal));
        Assert.Equal(kind != "delete", guard.Contains("c.generated_always_type <> 0", StringComparison.Ordinal));
    }

    /// <summary>Creates a catalog builder using the same provider mappings as runtime plans.</summary>
    private static SqlServerSafeMigrationCatalogSqlBuilder CreateCatalog(
        DbContext context
    )
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    /// <summary>Captures the same identity target for each supported managed-data operation.</summary>
    private static ModelManagedDataIntent CreateIdentityIntent(
        string kind
    )
        => kind switch
        {
            "ensure" => new EnsureModelManagedDataIntent("items", ["Id"], ["int"],
                ["Id"], ["int"], new object?[,] { { 1 } }, null, null),
            "update" => new UpdateModelManagedDataIntent("items", ["Id"], ["int"],
                new object?[,] { { 1 } }, ["Id"], ["int"],
                new object?[,] { { 1 } }, new object?[,] { { 1 } }, null, null),
            "delete" => new DeleteModelManagedDataIntent("items", ["Id"], ["int"],
                new object?[,] { { 1 } }, ["Id"], ["int"], new object?[,] { { 1 } }, null, []),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    /// <summary>Counts complete SQL tokens rather than unrelated identifier fragments.</summary>
    private static int Occurrences(
        string sql,
        string value
    )
        => sql.Split(value, StringSplitOptions.None).Length - 1;
}
