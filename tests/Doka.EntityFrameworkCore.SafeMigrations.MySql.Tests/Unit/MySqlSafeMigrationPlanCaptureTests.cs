namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Protects bounded capture ownership and operation-local complete migration contracts.</summary>
public sealed class MySqlSafeMigrationPlanCaptureTests
{
    /// <summary>
    /// Returns captured plans in original operation order while retaining each operation's reference identity.
    /// </summary>
    [Fact]
    public void Capture_PreservesOrdinalAndOperationIdentity()
    {
        // Arrange
        var capture = new MySqlSafeMigrationPlanCapture();
        var first = Operation("first");
        var second = Operation("second");
        var firstPlan = Plan("first");
        var secondPlan = Plan("second");

        // Act
        using var lease = capture.Begin([first, second]);
        capture.Record(1, second, secondPlan);
        capture.Record(0, first, firstPlan);
        var plans = lease.Complete();

        // Assert
        Assert.Same(firstPlan, plans[0]);
        Assert.Same(secondPlan, plans[1]);
        Assert.False(capture.IsActive);
    }

    /// <summary>Rejects each invalid capture transition independently with the original identity intact.</summary>
    /// <param name="violation">The missing, duplicate, foreign, or out-of-range capture transition.</param>
    [Theory]
    [InlineData("missing")]
    [InlineData("negative-ordinal")]
    [InlineData("past-end-ordinal")]
    [InlineData("foreign-operation")]
    [InlineData("duplicate")]
    public void Capture_RejectsMissingDuplicateForeignAndOutOfRangeRecords(
        string violation
    )
    {
        // Arrange
        var capture = new MySqlSafeMigrationPlanCapture();
        var operation = Operation("expected");
        var plan = Plan("expected");
        using var lease = capture.Begin([operation]);
        if (violation == "duplicate")
        {
            capture.Record(0, operation, plan);
        }

        Action action = violation switch
        {
            "missing" => () => lease.Complete(),
            "negative-ordinal" => () => capture.Record(-1, operation, plan),
            "past-end-ordinal" => () => capture.Record(1, operation, plan),
            "foreign-operation" => () => capture.Record(0, Operation("foreign"), plan),
            "duplicate" => () => capture.Record(0, operation, plan),
            _ => throw new ArgumentOutOfRangeException(nameof(violation)),
        };

        // Act
        var exception = Record.Exception(action);

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Releases failed capture ownership and permits a later independent capture to complete.</summary>
    [Fact]
    public void Capture_DisposeClearsFailedLeaseAndAllowsRecovery()
    {
        // Arrange
        var capture = new MySqlSafeMigrationPlanCapture();
        var operation = Operation("expected");
        bool active;
        Exception? nestedFailure;

        // Act
        using (capture.Begin([operation]))
        {
            active = capture.IsActive;
            nestedFailure = Record.Exception(() => capture.Begin([operation]));
        }

        using var recovered = capture.Begin([operation]);
        capture.Record(0, operation, Plan("recovered"));
        var plans = recovered.Complete();

        // Assert
        Assert.True(active);
        Assert.IsType<InvalidOperationException>(nestedFailure);
        Assert.Single(plans);
    }

    /// <summary>Scopes expected unique-index projections to the requested owner and active lease.</summary>
    [Fact]
    public void Capture_ProjectsOnlyExpectedUniqueIndexesByTable()
    {
        // Arrange
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

        IReadOnlyList<ExpectedIndexDefinition> ownerIndexes;
        IReadOnlyList<ExpectedIndexDefinition> otherIndexes;

        // Act
        using (capture.Begin([table, unique, nonUnique]))
        {
            ownerIndexes = capture.GetExpectedUniqueIndexes("users");
            otherIndexes = capture.GetExpectedUniqueIndexes("other");
        }

        var exception = Record.Exception(() => capture.GetExpectedUniqueIndexes("users"));

        // Assert
        Assert.Equal(["ux_users_email"], ownerIndexes.Select(static index => index.Name));
        Assert.Empty(otherIndexes);
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>
    /// Uses the complete migration index catalog even when only one bounded operation chunk is captured.
    /// </summary>
    [Fact]
    public void BoundedCapture_UsesTheCompleteMigrationIndexCatalog()
    {
        // Arrange
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

        // Act
        using var lease = capture.Begin([table], completeCatalog);
        var indexes = capture.GetExpectedUniqueIndexes("users");
        capture.Record(0, table, Plan("table"));
        var plans = lease.Complete();

        // Assert
        Assert.Equal(["ux_users_email"], indexes.Select(static index => index.Name));
        Assert.Single(plans);
    }

    /// <summary>Exposes the full foreign-key and unique-index catalog until generation ownership is released.</summary>
    [Fact]
    public void GenerationScope_ExposesCompleteTransitionCatalogAndReleasesOwnership()
    {
        // Arrange
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
        var tableIntent = (EnsureTableIntent)((SafeMigrationOperation)builder.Operations[0]).Intent;

        bool active;
        SafeMigrationExpectedTableConstraints? constraints;
        IReadOnlyList<ExpectedIndexDefinition> indexes;

        // Act
        using (capture.BeginGeneration(builder.Operations))
        {
            active = capture.HasGenerationContract;
            constraints = capture.GetGenerationTableConstraints(tableIntent);
            indexes = capture.GetGenerationUniqueIndexes("users");
        }

        // Assert
        Assert.True(active);
        Assert.NotNull(constraints);
        Assert.Equal("fk_users_parent",
            Assert.Single(constraints.AllowedForeignKeys)
                .Name);

        Assert.Equal("ux_users_id",
            Assert.Single(indexes)
                .Name);

        Assert.False(capture.HasGenerationContract);
    }

    /// <summary>
    /// Rejects nested ownership and reads after disposal as independently discoverable contract cases.
    /// </summary>
    /// <param name="violation">The illegal nested lease or expired generation-catalog access.</param>
    [Theory]
    [InlineData("nested-generation")]
    [InlineData("nested-capture")]
    [InlineData("expired-constraints")]
    [InlineData("expired-indexes")]
    public void GenerationScope_RejectsInvalidOwnership(
        string violation
    )
    {
        // Arrange
        var capture = new MySqlSafeMigrationPlanCapture();
        var operation = Operation("expected");
        using var lease = capture.BeginGeneration([operation]);
        if (violation.StartsWith("expired", StringComparison.Ordinal))
        {
            lease.Dispose();
        }

        var table = new EnsureTableIntent(
            new ExpectedTableDefinition("users", [new ExpectedColumnDefinition("id", typeof(int), false)]),
            SafeMigrationTableMode.StrictDefinition);

        Action action = violation switch
        {
            "nested-generation" => () => capture.BeginGeneration([operation]),
            "nested-capture" => () => capture.Begin([operation]),
            "expired-constraints" => () => capture.GetGenerationTableConstraints(table),
            "expired-indexes" => () => capture.GetGenerationUniqueIndexes("users"),
            _ => throw new ArgumentOutOfRangeException(nameof(violation)),
        };

        // Act
        var exception = Record.Exception(action);

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Provides an empty unique-index catalog when generation contains no complete table contract.</summary>
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

    /// <summary>Releases generation ownership when the wrapped provider generator throws.</summary>
    [Fact]
    public void GeneratorDecorator_ClearsGenerationScopeWhenTheProviderFails()
    {
        // Arrange
        var capture = new MySqlSafeMigrationPlanCapture();
        var generator = new MySqlSafeMigrationsSqlGenerator(new ThrowingSqlGenerator(), capture);

        // Act
        var exception = Record.Exception(() => generator.Generate(
            [Operation("schema")],
            model: null));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.False(capture.HasGenerationContract);
    }

    /// <summary>Normalizes an explicit current-database qualifier into the same expected index identity.</summary>
    [Fact]
    public void CurrentDatabaseQualificationSharesOneExpectedIndexIdentity()
    {
        // Arrange
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

        // Act
        using var lease = capture.Begin([table], catalog);
        var unqualified = capture.GetExpectedUniqueIndexes("users");
        var qualified = capture.GetExpectedUniqueIndexes("users", "application");
        var foreign = capture.GetExpectedUniqueIndexes("users", "foreign");
        capture.Record(0, table, Plan("table"));
        var plans = lease.Complete();

        // Assert
        Assert.Equal(["ux_users_email"], unqualified.Select(static index => index.Name));
        Assert.Equal(["ux_users_email"], qualified.Select(static index => index.Name));
        Assert.Empty(foreign);
        Assert.Single(plans);
    }

    /// <summary>
    /// Shares qualified and unqualified transition catalogs only under the verified current-database identity.
    /// </summary>
    /// <param name="tableIsQualified">Whether the owner explicitly names the current database.</param>
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
                schema: tableIsQualified
                    ? database
                    : null),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.AddForeignKeyIfNotExists(
            "fk_users_parent",
            "users",
            ["id"],
            "users",
            ["id"],
            schema: tableIsQualified
                ? null
                : database);

        builder.CreateIndexIfNotExists(
            "ux_users_id",
            "users",
            ["id"],
            schema: tableIsQualified
                ? null
                : database,
            unique: true);

        var tableIntent = Assert.IsType<EnsureTableIntent>(
            Assert.IsType<SafeMigrationOperation>(builder.Operations[0])
                .Intent);

        // Act
        using var generation = capture.BeginGeneration(builder.Operations);

        // Assert
        Assert.Equal([database], capture.GenerationDatabaseQualifiers);
        Assert.Equal(
            "fk_users_parent",
            Assert.Single(capture.GetGenerationTableConstraints(tableIntent)!.AllowedForeignKeys)
                .Name);

        Assert.Equal(
            "ux_users_id",
            Assert.Single(capture.GetGenerationUniqueIndexes("users", tableIntent.Definition.Schema))
                .Name);
    }

    /// <summary>Does not merge two distinct explicitly qualified database owners.</summary>
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

    /// <summary>
    /// Does not infer an unqualified current database from a single explicit qualifier during direct capture.
    /// </summary>
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
    public void GenerationNativeAlternativesAreDemandDriven(
        bool mixedOwner
    )
    {
        // Arrange
        var capture = new MySqlSafeMigrationPlanCapture();
        var builder = new MigrationBuilder("Provider");
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "rows",
                [new ExpectedColumnDefinition("id", typeof(int), false, "int")]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.CreateIndexIfNotExists(
            "ux_id",
            mixedOwner
                ? "ROWS"
                : "rows",
            ["id"],
            unique: true);

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
            Assert.Equal("ux_id",
                Assert.Single(folded.UniqueIndexes)
                    .Name);
        }
        else
        {
            Assert.Null(alternative);
            Assert.Equal("ux_id",
                Assert.Single(ordinal)
                    .Name);
        }
    }

    /// <summary>A live capture uses the explicitly qualified owner comparer consistently for both catalogs.</summary>
    [Fact]
    public void QualifiedCaptureSharesNativeOwnerContractsWithoutForeignSchemaBorrowing()
    {
        // Arrange
        var capture = new MySqlSafeMigrationPlanCapture();
        var builder = new MigrationBuilder("Provider");
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "rows",
                [new ExpectedColumnDefinition("id", typeof(int), false, "int")]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.CreateIndexIfNotExists("ux_id", "ROWS", ["id"], schema: "selected", unique: true);
        builder.AddUniqueConstraintIfNotExists("uq_id", "RoWs", ["id"], schema: "selected");
        var operations = builder
            .Operations
            .Cast<SafeMigrationOperation>()

            // WHY: Retain this explicit materialization/copy path and its collection lowering.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        var intent = (EnsureTableIntent)operations[0].Intent;
        var catalog = MySqlSafeMigrationPlanCapture.CreateExpectedUniqueIndexes(
            operations,
            "selected",
            StringComparer.OrdinalIgnoreCase);

        // Act
        using var lease = capture.Begin(operations, catalog);
        var indexes = capture.GetExpectedUniqueIndexes("rOwS");
        var constraints = capture.GetExpectedTableConstraints(intent);
        var foreignIndexes = capture.GetExpectedUniqueIndexes("rows", "foreign");

        // Assert
        Assert.Equal("ux_id",
            Assert.Single(indexes)
                .Name);

        Assert.Equal(
            "uq_id",
            Assert.Single(
                    Assert.IsType<SafeMigrationExpectedTableConstraints>(constraints)
                        .AllowedUniqueConstraints)
                .Name);

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
            builder.EnsureTable(
                new ExpectedTableDefinition(
                    name,
                    [
                        new ExpectedColumnDefinition("id", typeof(int), false, "int"),
                        new ExpectedColumnDefinition("value", typeof(int), true, "int"),
                    ]),
                SafeMigrationTableMode.StrictDefinition,
                SafeMigrationPolicy.ThrowIfDifferent);
        }

        builder.CreateIndexIfNotExists("ix_value", "ROWS", ["value"]);
        builder.DropColumnIfExists("value", "rows");
        var intent = (EnsureTableIntent)((SafeMigrationOperation)builder.Operations[0]).Intent;

        // Act
        using var lease = capture.BeginGeneration(builder.Operations);
        var alternative = capture.GetGenerationTableAlternative(intent);

        // Assert
        Assert.False(Assert.IsType<MySqlTableContractAlternative>(alternative)
            .IsValid);

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
            previous.EnsureTable(
                new ExpectedTableDefinition(
                    table,
                    [
                        new ExpectedColumnDefinition("id", typeof(int), false, "int"),
                        new ExpectedColumnDefinition("value", typeof(int), true, "int"),
                    ]),
                SafeMigrationTableMode.StrictDefinition,
                SafeMigrationPolicy.ThrowIfDifferent);
        }

        previous.CreateIndexIfNotExists("ix_value", "ROWS", ["value"]);
        previous.DropColumnIfExists("value", "rows");
        capture
            .BeginGeneration(previous.Operations)
            .Dispose();

        var next = new MigrationBuilder("Provider");
        next.EnsureTable(
            new ExpectedTableDefinition(
                "next_rows",
                [new ExpectedColumnDefinition("id", typeof(int), false, "int")]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

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
    public void GenerationBackfillHazardRequiresNativeModeWhenOwnerSpellingDiffers(
        bool mixedOwner
    )
    {
        // Arrange
        var capture = new MySqlSafeMigrationPlanCapture();
        var operation = new SafeMigrationOperation(
            new AlterColumnIntent(
                "rows",
                new ExpectedColumnDefinition(
                    "value",
                    typeof(string),
                    false,
                    "varchar(20)",
                    defaultValue: SafeMigrationDefaultValue.Literal("x")),
                new ExpectedColumnDefinition("value", typeof(string), true, "varchar(10)")),
            SafeMigrationPolicy.RepairIfSafe);

        var builder = new MigrationBuilder("Provider");
        builder.Operations.Add(operation);
        builder.CreateIndexIfNotExists(
            "ux_value",
            mixedOwner
                ? "ROWS"
                : "rows",
            ["value"],
            unique: true);

        // Act
        using var lease = capture.BeginGeneration(builder.Operations);
        var ordinal = capture.RequiresNullFreeBackfill(operation);
        var condition = capture.GetGenerationNullFreeBackfillCondition(operation);

        // Assert
        Assert.Equal(!mixedOwner, ordinal);
        Assert.Equal(
            mixedOwner
                ? "@@lower_case_table_names IN (1, 2)"
                : null,
            condition);
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
