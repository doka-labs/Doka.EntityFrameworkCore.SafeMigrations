namespace Doka.EntityFrameworkCore.SafeMigrations.Benchmarks;

/// <summary>Separates model-owned rebuild capture from complete embedded SQLite structural-window analysis.</summary>
internal sealed class SqliteMaintenanceAttributionFixture : IDisposable, IAsyncDisposable
{
    private const string Table = "maintenance_model_0000";
    private readonly SqliteSafeMigrationProviderAnalyzer _analyzer;
    private readonly bool _changingModel;
    private readonly SqliteConnection _connection;
    private readonly DbContext _first;
    private readonly bool _interleaved;
    private readonly IModel[] _modelSequence;
    private readonly IModel?[] _models;
    private readonly MigrationOperation[] _operations;
    private readonly MigrationOperation[][] _prefixes;
    private readonly DbContext _second;
    private readonly MigrationOperation[][] _segments;
    private readonly int _tableCount;
    private readonly int _windows;
    private int _captureCount;

    /// <summary>Prepares one embedded workload and verifies fresh capture and operation-reference ownership.</summary>
    /// <param name="tableCount">The complete model-only table count.</param>
    /// <param name="windows">The safe-operation count and interleaved structural-window count.</param>
    /// <param name="changingModel">Whether consecutive windows alternate model references.</param>
    /// <param name="interleaved">Whether opaque SQL boundaries split structural windows.</param>
    internal SqliteMaintenanceAttributionFixture(
        int tableCount,
        int windows,
        bool changingModel,
        bool interleaved
    )
    {
        _connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        _tableCount = tableCount;
        _windows = windows;
        _changingModel = changingModel;
        _interleaved = interleaved;
        _first = Context(_connection, tableCount, alternate: false);
        _second = Context(_connection, tableCount, alternate: true);
        var firstModel = _first.GetService<IDesignTimeModel>()
            .Model;

        var secondModel = _second.GetService<IDesignTimeModel>()
            .Model;

        MaintenanceAttribution.Require(
            !ReferenceEquals(firstModel, secondModel),
            "The changing-model workload accidentally reuses the same EF model reference.");

        VerifyReferenceOwnership(firstModel);
        _analyzer = new SqliteSafeMigrationProviderAnalyzer(
            _first.GetService<IRelationalTypeMappingSource>(),
            _first.GetService<ISqlGenerationHelper>());

        _operations = Operations(windows, interleaved);
        _models = _operations
            .Select((_, ordinal) => changingModel && ordinal % 4 >= 2
                ? secondModel
                : firstModel)

            // WHY: Retain fluent materialization and the resulting snapshot ownership.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        _prefixes = Enumerable
            .Range(0, windows)
            .Select(index => _operations
                .Take(
                    index
                    * (interleaved
                        ? 2
                        : 1)
                    + 1)

                // WHY: Retain fluent materialization and the resulting snapshot ownership.
                // ReSharper disable once UseCollectionExpression
                .ToArray())

            // WHY: Retain fluent materialization and the resulting snapshot ownership.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        _segments = Enumerable
            .Range(0, windows)
            .Select(index => new[]
            {
                _operations[index
                    * (interleaved
                        ? 2
                        : 1)],
            })

            // WHY: Retain fluent materialization and the resulting snapshot ownership.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        _modelSequence = Enumerable
            .Range(0, windows)
            .Select(index => changingModel && index % 2 != 0
                ? secondModel
                : firstModel)

            // WHY: Retain fluent materialization and the resulting snapshot ownership.
            // ReSharper disable once UseCollectionExpression
            .ToArray();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _second.DisposeAsync();
        }
        finally
        {
            try
            {
                await _first.DisposeAsync();
            }
            finally
            {
                await _connection.DisposeAsync();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _second.Dispose();
        _first.Dispose();
        _connection.Dispose();
    }

    /// <summary>Initializes the owned embedded database and verifies fresh classification and replay.</summary>
    /// <param name="cancellationToken">The token controlling setup and optional evidence persistence.</param>
    /// <returns>The complete asynchronous initialization.</returns>
    internal async Task InitializeAsync(
        CancellationToken cancellationToken
    )
    {
        await _connection.OpenAsync(cancellationToken);
        await using (var command = _connection.CreateCommand())
        {
            command.CommandText = $"CREATE TABLE {Table} (id INTEGER NOT NULL PRIMARY KEY);";
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        ObserveCaptures(_connection);
        var observed = await _analyzer.AnalyzeAsync(_first, _operations, _models, cancellationToken);

        MaintenanceAttribution.Require(
            observed.Count == _windows
            && observed.All(static result => result.ObservedState == SafeMigrationObservedState.Missing)
            && _captureCount == 1,
            "The SQLite attribution fixture changed classification or recaptured its catalog.");

        // WHY: Compare exact ordered classification output on replay, not workload identity. Different
        // models can intentionally yield the same states; ModelTables/Windows/ChangingModel identify the case.
        var stateHash = MaintenanceAttribution.Hash(
            string.Join(
                "\n",
                observed.Select(static result => $"{result.ObservedState}:{result.Code}")));

        _captureCount = 0;
        var replay = await _analyzer.AnalyzeAsync(_first, _operations, _models, cancellationToken);

        var replayHash = MaintenanceAttribution.Hash(
            string.Join(
                "\n",
                replay.Select(static result => $"{result.ObservedState}:{result.Code}")));

        MaintenanceAttribution.Require(
            stateHash == replayHash && _captureCount == 1,
            "SQLite attribution replay reused a stale catalog or changed exact classification output.");

        var output = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_ATTRIBUTION_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            await MaintenanceAttribution.WriteAsync(
                Path.Combine(output, $"sqlite-{_tableCount}-{_windows}-{_interleaved}-{_changingModel}.json"),
                "Functional/source evidence; SQLite embedded engine and client substrate; not timing evidence",
                [
                    new
                    {
                        Id = "PERF-05",
                        Workload = _interleaved
                            ? "interleaved_structural_windows"
                            : "contiguous_structural_window",
                        ModelTables = _tableCount,
                        Windows = _windows,
                        ChangingModel = _changingModel,
                        CatalogCapturesPerInvocation = 1,
                        ExactStateHash = stateHash,
                    },
                ],
                cancellationToken);
        }
    }

    /// <summary>Captures one model-owned rebuild contract per prebuilt structural window.</summary>
    /// <returns>The source-candidate checksum consumed by the benchmark.</returns>
    internal int Capture()
    {
        var checksum = 0;
        for (var window = 0; window < _segments.Length; window++)
        {
            var contract = SqliteRebuildArtifactContract.FromModel(
                _modelSequence[window],
                _prefixes[window],
                _segments[window]);

            var operation = (SafeMigrationOperation)_segments[window][0];
            checksum += contract.MayRebuildMissingColumn(operation)
                ? 1
                : 0;
        }

        return checksum;
    }

    /// <summary>Analyzes one complete operation stream without retaining capture counters across invocations.</summary>
    /// <param name="cancellationToken">The token controlling the measured provider analysis.</param>
    /// <returns>The complete observed operation count consumed by the benchmark.</returns>
    internal async Task<int> AnalyzeAsync(
        CancellationToken cancellationToken
    )
    {
        // WHY: BDN owns invocation counts; fixture observations must stay bounded during calibration too.
        _captureCount = 0;

        var observed = await _analyzer.AnalyzeAsync(_first, _operations, _models, cancellationToken);

        return observed.Count;
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
        var operations = new List<MigrationOperation>(
            count
            * (interleaved
                ? 2
                : 1));

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

        // WHY: Retain fluent materialization and the resulting snapshot ownership.
        // ReSharper disable once UseCollectionExpression
        return operations.ToArray();
    }

    /// <summary>Counts the actual first command of each catalog capture without introducing an analyzer hook.</summary>
    /// <param name="connection">The owned embedded SQLite connection.</param>
    private void ObserveCaptures(
        SqliteConnection connection
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        var version = (string)(command.ExecuteScalar()
            ?? throw new InvalidOperationException("SQLite did not report its native version."));

        connection.CreateFunction(
            "sqlite_version",
            () =>
            {
                _captureCount++;

                return version;
            });
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
    private sealed class ModelContext<TMarker> : DbContext
    {
        private readonly SqliteConnection _connection;
        private readonly int _count;

        /// <summary>Creates one independent EF model-cache identity.</summary>
        /// <param name="connection">The owned physical database.</param>
        /// <param name="count">The number of model-only tables to capture.</param>
        internal ModelContext(
            SqliteConnection connection,
            int count
        )
        {
            _connection = connection;
            _count = count;
        }

        /// <inheritdoc />
        protected override void OnConfiguring(
            DbContextOptionsBuilder optionsBuilder
        )
        {
            optionsBuilder.UseSqlite(_connection);
            optionsBuilder.UseSqliteSafeMigrations();
        }

        /// <inheritdoc />
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            for (var index = 0; index < _count; index++)
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

    // WHY: The generic EF context uses this type as an independent model-cache identity.
    // ReSharper disable once ClassNeverInstantiated.Local
    private sealed class Ten;

    // WHY: The generic EF context uses this type as an independent model-cache identity.
    // ReSharper disable once ClassNeverInstantiated.Local
    private sealed class OtherTen;

    // WHY: The generic EF context uses this type as an independent model-cache identity.
    // ReSharper disable once ClassNeverInstantiated.Local
    private sealed class Hundred;

    // WHY: The generic EF context uses this type as an independent model-cache identity.
    // ReSharper disable once ClassNeverInstantiated.Local
    private sealed class OtherHundred;

    // WHY: The generic EF context uses this type as an independent model-cache identity.
    // ReSharper disable once ClassNeverInstantiated.Local
    private sealed class Thousand;

    // WHY: The generic EF context uses this type as an independent model-cache identity.
    // ReSharper disable once ClassNeverInstantiated.Local
    private sealed class OtherThousand;
}
