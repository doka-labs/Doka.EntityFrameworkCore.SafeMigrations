namespace Doka.EntityFrameworkCore.SafeMigrations.Benchmarks;

/// <summary>Separates model-owned rebuild capture from complete embedded SQLite structural-window analysis.</summary>
internal static class SqliteMaintenanceAttribution
{
    private const string Table = "maintenance_model_0000";

    /// <summary>Records same/changing-model and contiguous/interleaved windows with no timing gate.</summary>
    /// <param name="outputPath">The caller-owned raw JSON evidence file.</param>
    /// <returns>Zero after exact state, snapshot freshness and reference-ownership controls pass.</returns>
    internal static int Run(
        string outputPath
    )
    {
        var results = new List<object>();
        foreach (var tableCount in new[] { 10, 100, 1000 })
        {
            using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"CREATE TABLE {Table} (id INTEGER NOT NULL PRIMARY KEY);";
                _ = command.ExecuteNonQuery();
            }

            using var first = Context(connection, tableCount, alternate: false);
            using var second = Context(connection, tableCount, alternate: true);
            var firstModel = first.GetService<IDesignTimeModel>().Model;
            var secondModel = second.GetService<IDesignTimeModel>().Model;
            MaintenanceAttribution.Require(
                !ReferenceEquals(firstModel, secondModel),
                "The changing-model workload accidentally reuses the same EF model reference.");

            VerifyReferenceOwnership(firstModel);
            var analyzer = new SqliteSafeMigrationProviderAnalyzer(
                first.GetService<IRelationalTypeMappingSource>(),
                first.GetService<ISqlGenerationHelper>());

            var captures = ObserveCaptures(connection);
            foreach (var windows in new[] { 1, 16, 128 })
            {
                foreach (var changingModel in new[] { false, true })
                {
                    var operations = Operations(windows, interleaved: true);
                    var models = operations
                        .Select((_, ordinal) => changingModel && ordinal % 4 >= 2 ? secondModel : firstModel)
                        .ToArray();

                    var prefixes = Enumerable
                        .Range(0, windows)
                        .Select(index => operations
                            .Take((index * 2) + 1)
                            .ToArray())
                        .ToArray();

                    var segments = Enumerable
                        .Range(0, windows)
                        .Select(index => new[] { operations[index * 2] })
                        .ToArray();

                    var modelSequence = Enumerable
                        .Range(0, windows)
                        .Select(index => changingModel && index % 2 != 0 ? secondModel : firstModel)
                        .ToArray();

                    var before = captures();
                    var observed = analyzer
                        .AnalyzeAsync(first, operations, models)
                        .GetAwaiter()
                        .GetResult();

                    var captureCount = captures() - before;
                    MaintenanceAttribution.Require(
                        observed.Count == windows
                        && observed.All(static result => result.ObservedState == SafeMigrationObservedState.Missing)
                        && captureCount == 1,
                        "The SQLite interleaved fixture changed classification or recaptured its catalog.");

                    results.Add(
                        new
                        {
                            Id = "PERF-05",
                            Workload = "interleaved_structural_windows",
                            ModelTables = tableCount,
                            Windows = windows,
                            ChangingModel = changingModel,
                            CatalogCapturesPerInvocation = captureCount,
                            ExactStateHash =
                                MaintenanceAttribution.Hash(
                                    string.Join(
                                        "\n",
                                        observed.Select(static result => $"{result.ObservedState}:{result.Code}"))),
                            CaptureSamples = MaintenanceAttribution.Measure(
                                () =>
                                {
                                    var checksum = 0;
                                    for (var window = 0; window < windows; window++)
                                    {
                                        var contract = SqliteRebuildArtifactContract.FromModel(
                                            modelSequence[window],
                                            prefixes[window],
                                            segments[window]);

                                        var operation = (SafeMigrationOperation)segments[window][0];

                                        checksum += contract.MayRebuildMissingColumn(operation) ? 1 : 0;
                                    }

                                    return checksum;
                                },
                                tableCount >= 100 || windows == 128 ? 1 : 4),
                            AnalysisSamples = MaintenanceAttribution.Measure(
                                () => analyzer
                                    .AnalyzeAsync(first, operations, models)
                                    .GetAwaiter()
                                    .GetResult()
                                    .Count,
                                tableCount >= 100 || windows == 128 ? 1 : 4),
                        });
                }

                var contiguous = Operations(windows, interleaved: false);
                var contiguousModels = Enumerable
                    .Repeat<IModel?>(firstModel, contiguous.Length)
                    .ToArray();

                var beforeContiguous = captures();
                var contiguousResult = analyzer
                    .AnalyzeAsync(first, contiguous, contiguousModels)
                    .GetAwaiter()
                    .GetResult();

                MaintenanceAttribution.Require(
                    contiguousResult.Count == windows
                    && contiguousResult.All(static result => result.ObservedState == SafeMigrationObservedState.Missing)
                    && captures() - beforeContiguous == 1,
                    "The SQLite contiguous fixture changed classification or catalog capture count.");

                results.Add(
                    new
                    {
                        Id = "PERF-05",
                        Workload = "contiguous_structural_window",
                        ModelTables = tableCount,
                        Operations = windows,
                        CatalogCapturesPerInvocation = 1,
                        Samples = MaintenanceAttribution.Measure(
                            () => analyzer
                                .AnalyzeAsync(first, contiguous, contiguousModels)
                                .GetAwaiter()
                                .GetResult()
                                .Count,
                            tableCount >= 100 ? 1 : 4),
                    });
            }
        }

        return MaintenanceAttribution.Write(outputPath, "SQLite embedded engine and client substrate", results);
    }

    /// <summary>Preserves exact operation-reference ownership across opaque and typed-DML boundaries.</summary>
    /// <param name="model">The independently initialized target model.</param>
    private static void VerifyReferenceOwnership(
        IModel model
    )
    {
        var added = new SafeMigrationOperation(
            new EnsureColumnIntent(Table, new ExpectedColumnDefinition("candidate", typeof(string), true, "TEXT")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var drop = new SafeMigrationOperation(
            new DropColumnIntent(name: "candidate", table: Table),
            SafeMigrationPolicy.ThrowIfDifferent);

        var clone = new SafeMigrationOperation(
            new DropColumnIntent(name: "candidate", table: Table),
            SafeMigrationPolicy.ThrowIfDifferent);

        var contract = SqliteRebuildArtifactContract.FromModel(model, [added, drop]);
        var opaque = SqliteRebuildArtifactContract.FromModel(
            model,
            [added, new SqlOperation { Sql = "SELECT 1;" }, drop]);

        var insert = new InsertDataOperation
        {
            Table = Table,
            Columns = ["id"],
            Values = new object[,] { { 1 } },
        };

        var data = SqliteRebuildArtifactContract.FromModel(model, [added, insert, drop]);

        MaintenanceAttribution.Require(
            contract.MayRebuildMissingColumn(drop),
            "The exact SQLite ADD-to-DROP operation lost its source candidate.");
        MaintenanceAttribution.Require(
            !contract.MayRebuildMissingColumn(clone),
            "A cloned SQLite DROP incorrectly inherited another operation's source candidate.");
        MaintenanceAttribution.Require(
            !opaque.MayRebuildMissingColumn(drop),
            "An opaque SQLite boundary incorrectly retained an earlier source candidate.");
        MaintenanceAttribution.Require(
            data.MayRebuildMissingColumn(drop),
            "Known SQLite DML incorrectly erased structural source-candidate ownership.");
    }

    /// <summary>Builds tiny immutable operation descriptions; each embedded fixture has one physical table.</summary>
    /// <param name="count">The safe-operation count.</param>
    /// <param name="interleaved">Whether opaque boundaries split the safe-operation windows.</param>
    /// <returns>The prebuilt ordered operation stream.</returns>
    private static MigrationOperation[] Operations(
        int count,
        bool interleaved
    )
    {
        var operations = new List<MigrationOperation>(count * (interleaved ? 2 : 1));
        for (var index = 0; index < count; index++)
        {
            operations.Add(
                new SafeMigrationOperation(
                    new EnsureColumnIntent(
                        Table,
                        new ExpectedColumnDefinition($"optional_{index:D4}", typeof(string), true, "TEXT")),
                    SafeMigrationPolicy.ThrowIfDifferent));

            if (interleaved)
            {
                operations.Add(new SqlOperation { Sql = "SELECT 1;" });
            }
        }

        return operations.ToArray();
    }

    /// <summary>Counts the actual first command of each catalog capture without introducing an analyzer hook.</summary>
    /// <param name="connection">The owned embedded SQLite connection.</param>
    /// <returns>The current capture-count reader.</returns>
    private static Func<int> ObserveCaptures(
        SqliteConnection connection
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        var version = (string)(command.ExecuteScalar()
            ?? throw new InvalidOperationException("SQLite did not report its native version."));

        var count = 0;
        connection.CreateFunction("sqlite_version", () =>
            {
                count++;

                return version;
            });

        return () => count;
    }

    /// <summary>Keeps target model size and reference identity explicit in EF's normal model-cache key.</summary>
    /// <param name="connection">The shared physical database for this prepared workload.</param>
    /// <param name="count">The number of model-only tables.</param>
    /// <param name="alternate">Whether to select a distinct EF model-cache identity.</param>
    /// <returns>The context owning that exact target model.</returns>
    private static DbContext Context(
        SqliteConnection connection,
        int count,
        bool alternate
    ) => (count, alternate) switch
    {
        (10, false) => new ModelContext<Ten>(connection, count),
        (10, true) => new ModelContext<OtherTen>(connection, count),
        (100, false) => new ModelContext<Hundred>(connection, count),
        (100, true) => new ModelContext<OtherHundred>(connection, count),
        (1000, false) => new ModelContext<Thousand>(connection, count),
        (1000, true) => new ModelContext<OtherThousand>(connection, count),
        _ => throw new ArgumentOutOfRangeException(nameof(count)),
    };

    /// <summary>Builds relational artifacts outside measurements, without creating a large database seed.</summary>
    /// <typeparam name="TMarker">The independent EF model-cache identity.</typeparam>
    /// <param name="connection">The owned physical database.</param>
    /// <param name="count">The number of model-only tables to capture.</param>
    private sealed class ModelContext<TMarker>(
        SqliteConnection connection,
        int count
    ) : DbContext
    {
        /// <inheritdoc />
        protected override void OnConfiguring(
            DbContextOptionsBuilder optionsBuilder
        )
        {
            optionsBuilder.UseSqlite(connection);
            optionsBuilder.UseSqliteSafeMigrations();
        }

        /// <inheritdoc />
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            for (var index = 0; index < count; index++)
            {
                var name = $"maintenance_model_{index:D4}";
                modelBuilder.SharedTypeEntity<Dictionary<string, object>>(
                    name,
                    entity =>
                    {
                        entity.ToTable(name);
                        entity.IndexerProperty<int>("id");
                        entity.IndexerProperty<string>("value");
                        entity.HasKey("id");
                        entity.HasIndex("value");
                    });
            }
        }
    }

    private sealed class Ten;

    private sealed class OtherTen;

    private sealed class Hundred;

    private sealed class OtherHundred;

    private sealed class Thousand;

    private sealed class OtherThousand;
}
