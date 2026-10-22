namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Guards immutable model sharing without reusing operation-bound rebuild evidence.</summary>
public sealed class SqliteRebuildModelCaptureTests : SqliteIntegrationTestBase
{
    /// <summary>Only the captured model table dictionary can be shared between independent window contracts.</summary>
    [Fact]
    public void WithOperationHistorySharesOnlyCapturedModelTables()
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = CreateContext(connection);
        var capture = SqliteRebuildArtifactContract.FromModel(context.GetService<IDesignTimeModel>().Model);
        var builder = CandidateHistory();

        // Act
        var first = capture.WithOperationHistory(builder.Operations);
        var second = capture.WithOperationHistory(builder.Operations);

        // Assert
        Assert.Same(Field(capture, "_tables"), Field(first, "_tables"));
        Assert.Same(Field(first, "_tables"), Field(second, "_tables"));
        foreach (var state in new[]
                 {
                     "_operations", "_addedArtifacts", "_removedArtifacts", "_removedTables",
                     "_tableConstraintTransitions", "_potentialColumnRebuilds",
                 })
        {
            Assert.NotNull(Field(first, state));
            Assert.NotSame(Field(first, state), Field(second, state));
        }
    }

    /// <summary>Different initialized model identities must never share captured model tables.</summary>
    [Fact]
    public void DistinctModelsRetainIndependentModelTables()
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var firstContext = new WindowModelContext<BasicModel>(connection);
        using var secondContext = new WindowModelContext<AdditionalColumnModel>(connection);
        var firstModel = firstContext.GetService<IDesignTimeModel>().Model;
        var secondModel = secondContext.GetService<IDesignTimeModel>().Model;

        // Act
        var first = SqliteRebuildArtifactContract.FromModel(firstModel).WithOperationHistory([]);
        var second = SqliteRebuildArtifactContract.FromModel(secondModel).WithOperationHistory([]);

        // Assert
        Assert.NotSame(firstModel, secondModel);
        Assert.NotSame(Field(first, "_tables"), Field(second, "_tables"));
    }

    /// <summary>An immutable model can be recaptured without a static or cross-invocation cache.</summary>
    [Fact]
    public void SeparateCapturesOfSameModelRemainIndependent()
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = CreateContext(connection);
        var model = context.GetService<IDesignTimeModel>().Model;

        // Act
        var first = SqliteRebuildArtifactContract.FromModel(model);
        var second = SqliteRebuildArtifactContract.FromModel(model);

        // Assert
        Assert.NotSame(Field(first, "_tables"), Field(second, "_tables"));
    }

    /// <summary>Sharing model artifacts cannot change opaque barriers or typed-DML candidate semantics.</summary>
    /// <param name="boundary">The original boundary between source addition and exact DROP.</param>
    /// <param name="expected">Whether that exact DROP may request a conditional row proof.</param>
    [Theory]
    [InlineData("opaque", false)]
    [InlineData("insert", true)]
    [InlineData("update", true)]
    [InlineData("delete", true)]
    public void OperationHistoryRetainsBoundarySemantics(string boundary, bool expected)
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = CreateContext(connection);
        var model = context.GetService<IDesignTimeModel>().Model;
        var builder = CandidateHistory(CreateBoundary(boundary));
        var drop = (SafeMigrationOperation)builder.Operations[^1];
        var capture = SqliteRebuildArtifactContract.FromModel(model);
        var reference = SqliteRebuildArtifactContract.FromModel(model, builder.Operations);

        // Act
        var derived = capture.WithOperationHistory(builder.Operations);
        var candidate = derived.MayRebuildMissingColumn(drop);

        // Assert
        Assert.Equal(expected, candidate);
        Assert.Equal(reference.MayRebuildMissingColumn(drop), candidate);
    }

    /// <summary>Each window snapshots its own caller-owned list and cannot inherit earlier candidate sets.</summary>
    [Fact]
    public void CallerHistoryMutationCannotReplaceCapturedWindowEvidence()
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = CreateContext(connection);
        var capture = SqliteRebuildArtifactContract.FromModel(context.GetService<IDesignTimeModel>().Model);
        var builder = CandidateHistory();
        var drop = (SafeMigrationOperation)builder.Operations[^1];
        var history = new List<MigrationOperation> { builder.Operations[0] };

        // Act
        var beforeDrop = capture.WithOperationHistory(history);
        history.Add(drop);
        var withDrop = capture.WithOperationHistory(history);
        history.Clear();
        var afterClear = capture.WithOperationHistory(history);

        // Assert
        Assert.False(beforeDrop.MayRebuildMissingColumn(drop));
        Assert.True(withDrop.MayRebuildMissingColumn(drop));
        Assert.False(afterClear.MayRebuildMissingColumn(drop));
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<MigrationOperation>>(Field(beforeDrop, "_operations")));
        Assert.Equal(2,
            Assert.IsAssignableFrom<IReadOnlyList<MigrationOperation>>(Field(withDrop, "_operations")).Count);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<MigrationOperation>>(Field(afterClear, "_operations")));
        Assert.Same(Field(beforeDrop, "_tables"), Field(afterClear, "_tables"));
    }

    /// <summary>Equal DROP values cannot borrow candidate ownership from a different operation reference.</summary>
    [Fact]
    public void CopiedDropCannotBorrowAnotherWindowsCandidate()
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = CreateContext(connection);
        var capture = SqliteRebuildArtifactContract.FromModel(context.GetService<IDesignTimeModel>().Model);
        var builder = CandidateHistory();
        var source = builder.Operations[0];
        var drop = (SafeMigrationOperation)builder.Operations[^1];
        var copy = new SafeMigrationOperation(drop.Intent, drop.Policy);

        // Act
        var original = capture.WithOperationHistory([source, drop]);
        var copied = capture.WithOperationHistory([source, copy]);

        // Assert
        Assert.True(original.MayRebuildMissingColumn(drop));
        Assert.False(original.MayRebuildMissingColumn(copy));
        Assert.True(copied.MayRebuildMissingColumn(copy));
        Assert.False(copied.MayRebuildMissingColumn(drop));
    }

    /// <summary>Cumulative history cannot extend the current window's authored constraint transitions.</summary>
    [Fact]
    public void ConstraintTransitionsRemainBoundToEachWindowAndCreatorReference()
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = CreateContext(connection);
        var capture = SqliteRebuildArtifactContract.FromModel(context.GetService<IDesignTimeModel>().Model);
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        builder.CreateTableIfNotExists("window_constraints", table => new
        {
            id = table.Column<int>(type: "INTEGER", nullable: false),
        });
        builder.AddPrimaryKeyIfNotExists("pk_window_constraints", "window_constraints", ["id"]);
        var creator = (SafeMigrationOperation)builder.Operations[0];
        var copy = new SafeMigrationOperation(creator.Intent, creator.Policy);

        // Act
        var beforeKey = capture.WithOperationHistory(builder.Operations, [creator]);
        var withKey = capture.WithOperationHistory(builder.Operations, builder.Operations);

        // Assert
        var first = Assert.IsType<SafeMigrationExpectedTableConstraints>(
            beforeKey.GetTableConstraintTransitions(creator));

        var second = Assert.IsType<SafeMigrationExpectedTableConstraints>(
            withKey.GetTableConstraintTransitions(creator));

        Assert.Empty(first.AllowedPrimaryKeys);
        Assert.Equal("pk_window_constraints", Assert.Single(second.AllowedPrimaryKeys).Name);
        Assert.Null(withKey.GetTableConstraintTransitions(copy));
    }

    /// <summary>A missing operation history is rejected rather than silently borrowing the captured one.</summary>
    [Fact]
    public void WithOperationHistoryRejectsNullHistory()
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = CreateContext(connection);
        var capture = SqliteRebuildArtifactContract.FromModel(context.GetService<IDesignTimeModel>().Model);

        // Act
        var exception = Record.Exception(() => capture.WithOperationHistory(null!));

        // Assert
        Assert.Equal("operations", Assert.IsType<ArgumentNullException>(exception).ParamName);
    }

    /// <summary>Alternating target models cannot lend missing-column ownership to a neighboring window.</summary>
    /// <param name="additionalFirst">Whether the unsupported target model appears before the supported one.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnalyzerKeepsDistinctInitializedModelsIndependent(bool additionalFirst)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateWindowTableAsync(connection);
        await using var basic = new WindowModelContext<BasicModel>(connection);
        await using var additional = new WindowModelContext<AdditionalColumnModel>(connection);
        var basicModel = basic.GetService<IDesignTimeModel>().Model;
        var additionalModel = additional.GetService<IDesignTimeModel>().Model;
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        builder.DropColumnIfExists("Legacy", "window_entities");
        _ = builder.Sql("SELECT 1;");
        builder.DropColumnIfExists("Legacy", "window_entities");
        var analyzer = CreateAnalyzer(basic);
        var firstModel = additionalFirst ? additionalModel : basicModel;
        var secondModel = additionalFirst ? basicModel : additionalModel;

        // Act
        var analyses = await analyzer.AnalyzeAsync(
            basic, builder.Operations, [firstModel, firstModel, secondModel]);

        // Assert
        Assert.Equal(2, analyses.Count);
        var supported = analyses[additionalFirst ? 1 : 0];
        var unsupported = analyses[additionalFirst ? 0 : 1];
        Assert.Equal(SafeMigrationObservedState.Matching, supported.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Unsupported, unsupported.ObservedState);
        Assert.Equal("table_rebuild_unmodeled_target_column", unsupported.Code);
    }

    /// <summary>A second analysis invocation must observe independent intervening catalog drift.</summary>
    [Fact]
    public async Task AnalyzerRefreshesItsCatalogBetweenModelSharingInvocations()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateWindowTableAsync(connection);
        await using var context = new WindowModelContext<BasicModel>(connection);
        var model = context.GetService<IDesignTimeModel>().Model;
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        builder.DropColumnIfExists("Legacy", "window_entities");
        var analyzer = CreateAnalyzer(context);
        var captures = await ObserveCatalogCapturesAsync(connection);

        // Act
        var before = await analyzer.AnalyzeAsync(context, builder.Operations, [model]);
        await ExecuteSqlAsync(connection, "ALTER TABLE window_entities ADD COLUMN Independent TEXT NULL;");
        var after = await analyzer.AnalyzeAsync(context, builder.Operations, [model]);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(before).ObservedState);
        Assert.Equal("table_rebuild_unmanaged_column", Assert.Single(after).Code);
        Assert.Equal(2, captures());
    }

    /// <summary>A sole structural window stays direct even when ordinary SQL or typed DML follows it.</summary>
    /// <param name="trailing">The nonstructural suffix following the only structural operation.</param>
    [Theory]
    [InlineData("none")]
    [InlineData("opaque")]
    [InlineData("insert")]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("mixed")]
    public async Task SingleStructuralWindowPreservesTrailingBoundaryBehavior(string trailing)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateWindowTableAsync(connection);
        await using var context = new WindowModelContext<BasicModel>(connection);
        var model = context.GetService<IDesignTimeModel>().Model;
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        builder.DropColumnIfExists("Legacy", "window_entities");
        if (trailing == "mixed")
        {
            foreach (var boundary in new[] { "opaque", "insert", "update", "delete" })
            {
                builder.Operations.Add(CreateBoundary(boundary));
            }
        }
        else if (trailing != "none")
        {
            builder.Operations.Add(CreateBoundary(trailing));
        }

        var analyzer = CreateAnalyzer(context);
        var captures = await ObserveCatalogCapturesAsync(connection);
        var models = builder.Operations.Select(_ => (IModel?)model).ToArray();

        // Act
        var analyses = await analyzer.AnalyzeAsync(context, builder.Operations, models);

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.Equal("classified_matching", analysis.Code);
        Assert.Equal(1, captures());
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('window_entities') WHERE name = 'Legacy';"));
    }

    /// <summary>Observes the catalog boundary on this connection without changing production instrumentation.</summary>
    private static async Task<Func<int>> ObserveCatalogCapturesAsync(SqliteConnection connection)
    {
        string version;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT sqlite_version();";
            version = Assert.IsType<string>(await command.ExecuteScalarAsync(CancellationToken.None));
        }

        var count = 0;
        connection.CreateFunction("sqlite_version", () =>
        {
            count++;

            return version;
        });

        return () => count;
    }

    /// <summary>Reads ownership identities without adding production diagnostics or mutable accessors.</summary>
    private static object? Field(SqliteRebuildArtifactContract contract, string name)
    {
        var field = typeof(SqliteRebuildArtifactContract).GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);

        return field.GetValue(contract);
    }

    /// <summary>Creates the smallest authored ADD-to-DROP history with an optional known or opaque boundary.</summary>
    private static MigrationBuilder CandidateHistory(MigrationOperation? boundary = null)
    {
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        builder.AddColumnIfNotExists<int>("source", "nodes", type: "INTEGER", nullable: true);
        if (boundary is not null)
        {
            builder.Operations.Add(boundary);
        }

        builder.DropColumnIfExists("source", "nodes");

        return builder;
    }

    /// <summary>Retains the distinct row-evidence boundaries without requiring extra database fixtures.</summary>
    private static MigrationOperation CreateBoundary(string boundary)
        => boundary switch
        {
            "opaque" => new SqlOperation { Sql = "SELECT 1;" },
            "insert" => new InsertDataOperation
            {
                Table = "nodes", Columns = ["id"], Values = new object[,] { { 1 } },
            },
            "update" => new UpdateDataOperation
            {
                Table = "nodes", KeyColumns = ["id"], KeyValues = new object[,] { { 1 } },
                Columns = ["value"], Values = new object[,] { { 2 } },
            },
            "delete" => new DeleteDataOperation
            {
                Table = "nodes", KeyColumns = ["id"], KeyValues = new object[,] { { 1 } },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
        };

    /// <summary>Creates the active provider analyzer without registering a test-only execution hook.</summary>
    private static SqliteSafeMigrationProviderAnalyzer CreateAnalyzer(DbContext context)
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    /// <summary>Creates one small table whose legacy column is deliberately absent from the target model.</summary>
    private static Task CreateWindowTableAsync(SqliteConnection connection)
        => ExecuteSqlAsync(connection,
            "CREATE TABLE window_entities (Id INTEGER NOT NULL, Legacy TEXT NULL, "
            + "CONSTRAINT pk_window_entities PRIMARY KEY (Id));");

    /// <summary>Owns a distinct immutable EF model identity without changing physical test setup.</summary>
    /// <typeparam name="TModel">The marker selecting a distinct target-model identity.</typeparam>
    /// <param name="connection">The independently owned physical test connection.</param>
    private sealed class WindowModelContext<TModel>(DbConnection connection) : DbContext
    {
        /// <inheritdoc />
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseSqlite(connection);

        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity("WindowEntity", entity =>
            {
                entity.ToTable("window_entities");
                entity.Property<int>("Id").ValueGeneratedNever();
                entity.HasKey("Id").HasName("pk_window_entities");
                if (typeof(TModel) == typeof(AdditionalColumnModel))
                {
                    entity.Property<string>("Missing").IsRequired(false);
                }
            });
        }
    }

    /// <summary>Selects the target model owning only the retained primary key.</summary>
    private sealed class BasicModel;

    /// <summary>Selects the distinct target model requiring an absent, unowned column.</summary>
    private sealed class AdditionalColumnModel;
}
