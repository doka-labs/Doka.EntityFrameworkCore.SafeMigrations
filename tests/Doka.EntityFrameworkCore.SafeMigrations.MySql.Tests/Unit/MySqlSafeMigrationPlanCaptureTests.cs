namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlSafeMigrationPlanCaptureTests
{
    [Fact]
    public void Capture_PreservesOrdinalAndOperationIdentity()
    {
        var capture = new MySqlSafeMigrationPlanCapture();
        var first = Operation("first");
        var second = Operation("second");
        var firstPlan = Plan("first");
        var secondPlan = Plan("second");

        using var lease = capture.Begin([first, second]);
        capture.Record(1, second, secondPlan);
        capture.Record(0, first, firstPlan);
        var plans = lease.Complete();

        Assert.Same(firstPlan, plans[0]);
        Assert.Same(secondPlan, plans[1]);
        Assert.False(capture.IsActive);
    }

    [Fact]
    public void Capture_RejectsMissingDuplicateForeignAndOutOfRangeRecords()
    {
        var capture = new MySqlSafeMigrationPlanCapture();
        var operation = Operation("expected");
        var plan = Plan("expected");

        using var lease = capture.Begin([operation]);

        Assert.Throws<InvalidOperationException>(() => lease.Complete());
        Assert.Throws<InvalidOperationException>(() => capture.Record(-1, operation, plan));
        Assert.Throws<InvalidOperationException>(() => capture.Record(1, operation, plan));
        Assert.Throws<InvalidOperationException>(() => capture.Record(0, Operation("foreign"), plan));

        capture.Record(0, operation, plan);

        Assert.Throws<InvalidOperationException>(() => capture.Record(0, operation, plan));
    }

    [Fact]
    public void Capture_DisposeClearsFailedLeaseAndAllowsRecovery()
    {
        var capture = new MySqlSafeMigrationPlanCapture();
        var operation = Operation("expected");

        using (capture.Begin([operation]))
        {
            Assert.True(capture.IsActive);
            Assert.Throws<InvalidOperationException>(() => capture.Begin([operation]));
        }

        using var recovered = capture.Begin([operation]);
        capture.Record(0, operation, Plan("recovered"));

        Assert.Single(recovered.Complete());
    }

    [Fact]
    public void Capture_ProjectsOnlyExpectedUniqueIndexesByTable()
    {
        var capture = new MySqlSafeMigrationPlanCapture();
        var table = new SafeMigrationOperation(
            new EnsureTableIntent(
                new ExpectedTableDefinition(
                    "users",
                    [new ExpectedColumnDefinition("email", typeof(string), isNullable: true)]),
                SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        var unique = new SafeMigrationOperation(
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ux_users_email",
                    "users",
                    [new ExpectedIndexKeyDefinition(column: "email")],
                    unique: true)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var nonUnique = new SafeMigrationOperation(
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ix_users_name",
                    "users",
                    [new ExpectedIndexKeyDefinition(column: "name")])),
            SafeMigrationPolicy.ThrowIfDifferent);

        using (capture.Begin([table, unique, nonUnique]))
        {
            Assert.Equal(
                ["ux_users_email"],
                capture
                    .GetExpectedUniqueIndexes("users")
                    .Select(static index => index.Name));
            Assert.Empty(capture.GetExpectedUniqueIndexes("other"));
        }

        Assert.Throws<InvalidOperationException>(() => capture.GetExpectedUniqueIndexes("users"));
    }

    [Fact]
    public void BoundedCapture_UsesTheCompleteMigrationIndexCatalog()
    {
        var capture = new MySqlSafeMigrationPlanCapture();
        var table = new SafeMigrationOperation(
            new EnsureTableIntent(
                new ExpectedTableDefinition(
                    "users",
                    [new ExpectedColumnDefinition("email", typeof(string), isNullable: true)]),
                SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        var unique = new SafeMigrationOperation(
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ux_users_email",
                    "users",
                    [new ExpectedIndexKeyDefinition(column: "email")],
                    unique: true)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var completeCatalog = MySqlSafeMigrationPlanCapture.CreateExpectedUniqueIndexes([table, unique]);
        using var lease = capture.Begin([table], completeCatalog);

        Assert.Equal(
            ["ux_users_email"],
            capture
                .GetExpectedUniqueIndexes("users")
                .Select(static index => index.Name));

        capture.Record(0, table, Plan("table"));
        Assert.Single(lease.Complete());
    }

    [Fact]
    public void GenerationScope_ExposesCompleteTransitionCatalogAndRejectsNestedOwnership()
    {
        var capture = new MySqlSafeMigrationPlanCapture();
        var builder = new MigrationBuilder("Provider");
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "users",
                [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int")]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.AddForeignKeyIfNotExists(
            "fk_users_parent",
            "users",
            ["id"],
            "users",
            ["id"]);
        builder.CreateIndexIfNotExists("ux_users_id", "users", ["id"], unique: true);
        var tableIntent = Assert.IsType<EnsureTableIntent>(
            Assert.IsType<SafeMigrationOperation>(builder.Operations[0]).Intent);

        using (capture.BeginGeneration(builder.Operations))
        {
            Assert.True(capture.HasGenerationContract);
            Assert.Equal(
                "fk_users_parent",
                Assert.Single(capture.GetGenerationTableConstraints(tableIntent)!.AllowedForeignKeys).Name);
            Assert.Equal(
                "ux_users_id",
                Assert.Single(capture.GetGenerationUniqueIndexes("users")).Name);
            Assert.Throws<InvalidOperationException>(() => capture.BeginGeneration(builder.Operations));
            Assert.Throws<InvalidOperationException>(() => capture.Begin(
                builder.Operations.Cast<SafeMigrationOperation>().ToArray()));
        }

        Assert.False(capture.HasGenerationContract);
        Assert.Throws<InvalidOperationException>(() => capture.GetGenerationTableConstraints(tableIntent));
        Assert.Throws<InvalidOperationException>(() => capture.GetGenerationUniqueIndexes("users"));
    }

    [Fact]
    public void GenerationScopeWithoutEnsureTableUsesAnEmptyUniqueIndexCatalog()
    {
        // Arrange
        var capture = new MySqlSafeMigrationPlanCapture();
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent(
                "records",
                new ExpectedColumnDefinition(
                    "caption",
                    typeof(string),
                    isNullable: true,
                    storeType: "longtext")),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        using var lease = capture.BeginGeneration([operation]);
        var indexes = capture.GetGenerationUniqueIndexes("records");

        // Assert
        Assert.True(capture.HasGenerationContract);
        Assert.Empty(indexes);
    }

    [Fact]
    public void GeneratorDecorator_ClearsGenerationScopeWhenTheProviderFails()
    {
        var capture = new MySqlSafeMigrationPlanCapture();
        var generator = new MySqlSafeMigrationsSqlGenerator(new ThrowingSqlGenerator(), capture);

        var exception = Record.Exception(() => generator.Generate(
            [Operation("schema")],
            model: null));

        Assert.IsType<InvalidOperationException>(exception);
        Assert.False(capture.HasGenerationContract);
    }

    [Fact]
    public void CurrentDatabaseQualificationSharesOneExpectedIndexIdentity()
    {
        var capture = new MySqlSafeMigrationPlanCapture();
        var table = new SafeMigrationOperation(
            new EnsureTableIntent(
                new ExpectedTableDefinition(
                    "users",
                    [new ExpectedColumnDefinition("email", typeof(string), isNullable: true)],
                    schema: "application"),
                SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        var unique = new SafeMigrationOperation(
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ux_users_email",
                    "users",
                    [new ExpectedIndexKeyDefinition(column: "email")],
                    unique: true)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var catalog = MySqlSafeMigrationPlanCapture.CreateExpectedUniqueIndexes(
            [table, unique],
            currentDatabase: "application");

        using var lease = capture.Begin([table], catalog);

        Assert.Equal(
            ["ux_users_email"],
            capture
                .GetExpectedUniqueIndexes("users")
                .Select(static index => index.Name));
        Assert.Equal(
            ["ux_users_email"],
            capture
                .GetExpectedUniqueIndexes("users", "application")
                .Select(static index => index.Name));
        Assert.Empty(capture.GetExpectedUniqueIndexes("users", "foreign"));

        capture.Record(0, table, Plan("table"));
        Assert.Single(lease.Complete());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GenerationScope_NormalizesQualifiedAndUnqualifiedTransitionCatalogs(
        bool tableIsQualified
    )
    {
        // Arrange
        const string database = "application";
        var capture = new MySqlSafeMigrationPlanCapture();
        var builder = new MigrationBuilder("Provider");
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "users",
                [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int")],
                schema: tableIsQualified ? database : null),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.AddForeignKeyIfNotExists(
            "fk_users_parent",
            "users",
            ["id"],
            "users",
            ["id"],
            schema: tableIsQualified ? null : database);
        builder.CreateIndexIfNotExists(
            "ux_users_id",
            "users",
            ["id"],
            schema: tableIsQualified ? null : database,
            unique: true);
        var tableIntent = Assert.IsType<EnsureTableIntent>(
            Assert.IsType<SafeMigrationOperation>(builder.Operations[0]).Intent);

        // Act
        using var generation = capture.BeginGeneration(builder.Operations);

        // Assert
        Assert.Equal([database], capture.GenerationDatabaseQualifiers);
        Assert.Equal(
            "fk_users_parent",
            Assert.Single(capture.GetGenerationTableConstraints(tableIntent)!.AllowedForeignKeys).Name);
        Assert.Equal(
            "ux_users_id",
            Assert.Single(capture.GetGenerationUniqueIndexes("users", tableIntent.Definition.Schema)).Name);
    }

    [Fact]
    public void GenerationScope_DoesNotMergeDistinctExplicitDatabaseQualifiers()
    {
        // Arrange
        var capture = new MySqlSafeMigrationPlanCapture();
        var builder = new MigrationBuilder("Provider");
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "users",
                [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int")],
                schema: "application"),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.CreateIndexIfNotExists(
            "ux_users_id",
            "users",
            ["id"],
            schema: "foreign",
            unique: true);

        // Act
        using var generation = capture.BeginGeneration(builder.Operations);

        // Assert
        Assert.Equal(
            ["application", "foreign"],
            capture.GenerationDatabaseQualifiers);
        Assert.Empty(capture.GetGenerationUniqueIndexes("users", "application"));
        Assert.Empty(capture.GetGenerationUniqueIndexes("users", "foreign"));
    }

    [Fact]
    public void DirectCaptureDoesNotInferCurrentDatabaseFromAnExplicitQualifier()
    {
        // Arrange
        var capture = new MySqlSafeMigrationPlanCapture();
        var table = new SafeMigrationOperation(
            new EnsureTableIntent(
                new ExpectedTableDefinition(
                    "users",
                    [new ExpectedColumnDefinition("email", typeof(string), isNullable: true)],
                    schema: "foreign"),
                SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);
        var unique = new SafeMigrationOperation(
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ux_users_email",
                    "users",
                    [new ExpectedIndexKeyDefinition(column: "email")],
                    unique: true)),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        using var captureLease = capture.Begin([table, unique]);

        // Assert
        Assert.Empty(capture.GetExpectedUniqueIndexes("users", "foreign"));
        Assert.Empty(capture.GetExpectedUniqueIndexes("users"));
    }

    /// <summary>Offline generation builds an alternative only when strict owner spellings actually differ.</summary>
    /// <param name="mixedOwner">Whether the standalone index uses a native-equivalent alternate owner name.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerationNativeAlternativesAreDemandDriven(bool mixedOwner)
    {
        // Arrange
        var capture = new MySqlSafeMigrationPlanCapture();
        var builder = new MigrationBuilder("Provider");
        builder.EnsureTable(new ExpectedTableDefinition("rows",
                [new ExpectedColumnDefinition("id", typeof(int), false, "int")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        builder.CreateIndexIfNotExists("ux_id", mixedOwner ? "ROWS" : "rows", ["id"], unique: true);
        var intent = (EnsureTableIntent)((SafeMigrationOperation)builder.Operations[0]).Intent;

        // Act
        using var lease = capture.BeginGeneration(builder.Operations);
        var alternative = capture.GetGenerationTableAlternative(intent);
        var ordinal = capture.GetGenerationUniqueIndexes("rows");

        // Assert
        if (mixedOwner)
        {
            Assert.Empty(ordinal);
            var folded = Assert.IsType<MySqlTableContractAlternative>(alternative);
            Assert.True(folded.IsValid);
            Assert.Equal("ux_id", Assert.Single(folded.UniqueIndexes).Name);
        }
        else
        {
            Assert.Null(alternative);
            Assert.Equal("ux_id", Assert.Single(ordinal).Name);
        }

    }

    /// <summary>A live capture uses the explicitly qualified owner comparer consistently for both catalogs.</summary>
    [Fact]
    public void QualifiedCaptureSharesNativeOwnerContractsWithoutForeignSchemaBorrowing()
    {
        // Arrange
        var capture = new MySqlSafeMigrationPlanCapture();
        var builder = new MigrationBuilder("Provider");
        builder.EnsureTable(new ExpectedTableDefinition("rows",
                [new ExpectedColumnDefinition("id", typeof(int), false, "int")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        builder.CreateIndexIfNotExists("ux_id", "ROWS", ["id"], schema: "selected", unique: true);
        builder.AddUniqueConstraintIfNotExists("uq_id", "RoWs", ["id"], schema: "selected");
        var operations = builder.Operations.Cast<SafeMigrationOperation>().ToArray();
        var intent = (EnsureTableIntent)operations[0].Intent;
        var catalog = MySqlSafeMigrationPlanCapture.CreateExpectedUniqueIndexes(operations,
            "selected", StringComparer.OrdinalIgnoreCase);

        // Act
        using var lease = capture.Begin(operations, catalog);
        var indexes = capture.GetExpectedUniqueIndexes("rOwS");
        var constraints = capture.GetExpectedTableConstraints(intent);
        var foreignIndexes = capture.GetExpectedUniqueIndexes("rows", "foreign");

        // Assert
        Assert.Equal("ux_id", Assert.Single(indexes).Name);
        Assert.Equal("uq_id", Assert.Single(Assert.IsType<SafeMigrationExpectedTableConstraints>(constraints)
            .AllowedUniqueConstraints).Name);
        Assert.Empty(foreignIndexes);
    }

    /// <summary>An invalid folded dependency cannot prevent generation for independent mode-zero owners.</summary>
    [Fact]
    public void InvalidFoldedOwnerContractDoesNotAbortOrdinalGeneration()
    {
        // Arrange
        var capture = new MySqlSafeMigrationPlanCapture();
        var builder = new MigrationBuilder("Provider");
        foreach (var name in new[] { "rows", "ROWS" })
        {
            builder.EnsureTable(new ExpectedTableDefinition(name,
                    [new ExpectedColumnDefinition("id", typeof(int), false, "int"),
                        new ExpectedColumnDefinition("value", typeof(int), true, "int")]),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }

        builder.CreateIndexIfNotExists("ix_value", "ROWS", ["value"]);
        builder.DropColumnIfExists("value", "rows");
        var intent = (EnsureTableIntent)((SafeMigrationOperation)builder.Operations[0]).Intent;

        // Act
        using var lease = capture.BeginGeneration(builder.Operations);
        var alternative = capture.GetGenerationTableAlternative(intent);

        // Assert
        Assert.False(Assert.IsType<MySqlTableContractAlternative>(alternative).IsValid);
        Assert.True(capture.HasGenerationContract);
    }

    /// <summary>
    /// Folded alternatives and invalid-branch evidence cannot leak into another generation invocation.
    /// </summary>
    [Fact]
    public void CompletedGenerationDoesNotReuseFoldedContract()
    {
        // Arrange
        var capture = new MySqlSafeMigrationPlanCapture();
        var previous = new MigrationBuilder("Provider");
        foreach (var table in new[] { "rows", "ROWS" })
        {
            previous.EnsureTable(new ExpectedTableDefinition(table,
                    [new ExpectedColumnDefinition("id", typeof(int), false, "int"),
                        new ExpectedColumnDefinition("value", typeof(int), true, "int")]),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }

        previous.CreateIndexIfNotExists("ix_value", "ROWS", ["value"]);
        previous.DropColumnIfExists("value", "rows");
        capture.BeginGeneration(previous.Operations).Dispose();
        var next = new MigrationBuilder("Provider");
        next.EnsureTable(new ExpectedTableDefinition("next_rows",
                [new ExpectedColumnDefinition("id", typeof(int), false, "int")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        var intent = (EnsureTableIntent)((SafeMigrationOperation)next.Operations[0]).Intent;

        // Act
        using var lease = capture.BeginGeneration(next.Operations);
        var alternative = capture.GetGenerationTableAlternative(intent);

        // Assert
        Assert.Null(alternative);
        Assert.True(capture.HasGenerationContract);
    }

    /// <summary>Only the native folded branch inherits a differently spelled future backfill hazard.</summary>
    /// <param name="mixedOwner">Whether the future unique declaration uses a native-equivalent owner alias.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerationBackfillHazardRequiresNativeModeWhenOwnerSpellingDiffers(bool mixedOwner)
    {
        // Arrange
        var capture = new MySqlSafeMigrationPlanCapture();
        var operation = new SafeMigrationOperation(new AlterColumnIntent("rows",
                new ExpectedColumnDefinition("value", typeof(string), false, "varchar(20)",
                    defaultValue: SafeMigrationDefaultValue.Literal("x")),
                new ExpectedColumnDefinition("value", typeof(string), true, "varchar(10)")),
            SafeMigrationPolicy.RepairIfSafe);

        var builder = new MigrationBuilder("Provider");
        builder.Operations.Add(operation);
        builder.CreateIndexIfNotExists("ux_value", mixedOwner ? "ROWS" : "rows", ["value"], unique: true);

        // Act
        using var lease = capture.BeginGeneration(builder.Operations);
        var ordinal = capture.RequiresNullFreeBackfill(operation);
        var condition = capture.GetGenerationNullFreeBackfillCondition(operation);

        // Assert
        Assert.Equal(!mixedOwner, ordinal);
        Assert.Equal(mixedOwner ? "@@lower_case_table_names IN (1, 2)" : null, condition);
    }

    private static SafeMigrationOperation Operation(
        string name
    ) => new(new EnsureSchemaIntent(name), SafeMigrationPolicy.ThrowIfDifferent);

    private static MySqlSafeMigrationRuntimePlan Plan(
        string value
    ) => new(value, value, SafeMigrationRepairCapability.None, value);

    private sealed class ThrowingSqlGenerator : IMigrationsSqlGenerator
    {
        public IReadOnlyList<MigrationCommand> Generate(
            IReadOnlyList<MigrationOperation> operations,
            IModel? model = null,
            MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default
        ) => throw new InvalidOperationException("Provider generation failed.");
    }
}
