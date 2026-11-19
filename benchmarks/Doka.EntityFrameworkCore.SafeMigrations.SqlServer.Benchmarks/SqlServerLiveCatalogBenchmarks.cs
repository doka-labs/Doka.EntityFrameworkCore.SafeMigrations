namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

/// <summary>Measures pooled full-runner analysis on the 100-managed/1000-foreign native catalog fixture.</summary>
/// <remarks>Available only through --live-catalog with an explicit inherited SQL Server connection.</remarks>
[MemoryDiagnoser]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public class SqlServerLiveCatalogBenchmarks : IAsyncDisposable
{
    private const int ManagedTableCount = 100;
    private const int ForeignTableCount = 1000;
    private const int CommandTimeoutSeconds = 180;
    private readonly SafeMigrationRunOptions _options = new("sqlserver-live-catalog-benchmark");
    private CatalogContext? _context;
    private string? _database;
    private SafeMigrationRunReport? _lastReport;
    private IReadOnlyList<MigrationOperation> _operations = [];
    private SqlConnection? _poolOwner;
    private SqlConnection? _root;
    private ISafeMigrationRunner? _runner;

    /// <summary>Gets or sets whether 1000 unrelated indexed tables accompany the 100 managed tables.</summary>
    [Params(false, true)]
    public bool ForeignObjects { get; set; }

    /// <summary>Drains the final validation and every owned native resource without a blocking async bridge.</summary>
    /// <returns>The completion of validation and protected resource cleanup.</returns>
    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore();
        GC.SuppressFinalize(this);
    }

    /// <summary>Creates only a GUID-owned database and validates its ordered report before measurement.</summary>
    /// <returns>The completion of isolated native fixture creation and functional qualification.</returns>
    [GlobalSetup]
    public async Task Setup()
    {
        var configured = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_SQLSERVER_BENCHMARK_CONNECTION");
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                "Live catalog benchmarks require SAFE_MIGRATIONS_SQLSERVER_BENCHMARK_CONNECTION.");
        }

        try
        {
            var settings = new SqlConnectionStringBuilder(configured)
            {
                InitialCatalog = "master",
                Pooling = false,
                ApplicationName = "SafeMigrations.LiveCatalogBenchmark",
            };

            _root = new SqlConnection(settings.ConnectionString);
            await _root.OpenAsync(CancellationToken.None);
            _database = "sm_live_catalog_"
                + Guid
                    .NewGuid()
                    .ToString("N");

            // WHY: Record provisional ownership before dispatch for ambiguous partial setup failures.
            try
            {
                await ExecuteAsync(_root, $"CREATE DATABASE [{_database}];", CancellationToken.None);
            }
            catch (SqlException exception) when (exception.Number == 1801)
            {
                // WHY: A confirmed name collision belongs to another owner, even for a generated GUID name.
                _database = null;

                throw;
            }

            settings.InitialCatalog = _database;
            settings.Pooling = true;
            settings.MaxPoolSize = 4;
            _poolOwner = new SqlConnection(settings.ConnectionString);
            await _poolOwner.OpenAsync(CancellationToken.None);
            await ExecuteAsync(
                _poolOwner,
                BuildTables("expected_perf_", ManagedTableCount, false),
                CancellationToken.None);

            if (ForeignObjects)
            {
                await ExecuteAsync(
                    _poolOwner,
                    BuildTables("foreign_perf_", ForeignTableCount, true),
                    CancellationToken.None);
            }

            await _poolOwner.CloseAsync();
            _context = new CatalogContext(settings.ConnectionString);
            var builder = new MigrationBuilder(_context.Database.ProviderName
                ?? throw new InvalidOperationException("The SQL Server provider is unavailable."));

            for (var ordinal = 0; ordinal < ManagedTableCount; ordinal++)
            {
                builder.EnsureTable(
                    new ExpectedTableDefinition(
                        "expected_perf_" + ordinal.ToString("D4", CultureInfo.InvariantCulture),
                        [new ExpectedColumnDefinition("id", typeof(int), false, "int")]),
                    SafeMigrationTableMode.StrictDefinition,
                    SafeMigrationPolicy.ThrowIfDifferent);
            }

            _operations = builder.Operations;
            _runner = _context.GetService<ISafeMigrationRunner>();
            _ = await Analyze();
            ValidateReport();
        }
        catch (Exception exception)
        {
            await DisposeOwnedAsync(exception);

            // WHY: Native errors and connection parser messages can contain caller configuration.
            // Keep the original private to this invocation, never in BDN reports or child error text.
            throw new InvalidOperationException("The isolated SQL Server live catalog setup failed.");
        }
    }

    /// <summary>Runs full product analysis, including native work and pooled connection lifetime.</summary>
    /// <returns>The complete report retained for untimed structural validation.</returns>
    [Benchmark]
    public async Task<SafeMigrationRunReport> Analyze()
    {
        try
        {
            _lastReport = await (_runner ?? throw new InvalidOperationException("Setup has not completed."))
                .AnalyzeAsync(
                    _context ?? throw new InvalidOperationException("Setup has not completed."),
                    _operations,
                    _options,
                    CancellationToken.None);

            return _lastReport;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The isolated SQL Server live catalog analysis failed.");
        }
    }

    /// <summary>Validates every ordinal, identity, classification and foreign-object shape outside timing.</summary>
    [IterationCleanup]
    public void ValidateReport()
    {
        var report = _lastReport ?? throw new InvalidOperationException("No catalog report was produced.");
        MaintenanceAttribution.Require(
            report.Assessments.Count == ManagedTableCount,
            "Managed assessment count changed.");

        for (var ordinal = 0; ordinal < ManagedTableCount; ordinal++)
        {
            var assessment = report.Assessments[ordinal];
            MaintenanceAttribution.Require(
                assessment.Ordinal == ordinal
                && assessment.OperationType == typeof(SafeMigrationOperation).FullName
                && assessment.IsSafeOperation
                && assessment.OperationKind == SafeMigrationOperationKind.EnsureTable
                && assessment.ObjectName == "expected_perf_" + ordinal.ToString("D4", CultureInfo.InvariantCulture)
                && assessment.ObservedState == SafeMigrationObservedState.Matching
                && assessment.Action == SafeMigrationAction.NoOp
                && assessment.PostconditionSatisfied == true,
                "A managed catalog assessment changed its ordered identity or matching contract.");
        }

        MaintenanceAttribution.Require(
            report.UnexpectedObjects.Count
            == (ForeignObjects
                ? ForeignTableCount
                : 0)
            && report.UnexpectedObjects.All(static value => value.ObjectKind == SafeMigrationDatabaseObjectKind.Table),
            "The clean or foreign catalog inventory changed its expected object shape.");
    }

    /// <summary>Validates the final result and drains owned cleanup without modifying caller databases.</summary>
    /// <returns>The final noncancellable native cleanup completion.</returns>
    [GlobalCleanup]
    public async Task Cleanup() => await DisposeAsync();

    /// <summary>Validates the final native result and drains the instance-owned database lifetime.</summary>
    /// <returns>The completion of all native cleanup stages.</returns>
    protected virtual async ValueTask DisposeAsyncCore()
    {
        Exception? failure = null;
        try
        {
            if (_lastReport is not null)
            {
                ValidateReport();
            }
        }
        catch (Exception exception)
        {
            failure = exception;

            throw;
        }
        finally
        {
            await DisposeOwnedAsync(failure);
        }
    }

    /// <summary>Retains primary failure while draining context, pool, owned database and root cleanup.</summary>
    private async Task DisposeOwnedAsync(
        Exception? primaryFailure
    )
    {
        var failure = primaryFailure;
        await DisposeResourceAsync(_context, "SafeMigrations.LiveCatalogContextCleanupException");
        _context = null;
        _runner = null;
        if (_poolOwner is not null)
        {
            try
            {
                // WHY: Clear only this benchmark's unique database pool, never another application's pools.
                SqlConnection.ClearPool(_poolOwner);
            }
            catch (Exception exception)
            {
                Retain(exception, "SafeMigrations.LiveCatalogPoolClearException");
            }
        }

        await DisposeResourceAsync(_poolOwner, "SafeMigrations.LiveCatalogPoolCleanupException");
        _poolOwner = null;
        try
        {
            if (_database is not null)
            {
                var root = _root
                    ?? throw new InvalidOperationException("The owned database cleanup connection is unavailable.");

                // WHY: Tracked database ownership survives a closed or broken admin session.
                // Reopen once without cancellation; a failed reopen must be a cleanup failure, not false success.
                if (root.State == ConnectionState.Broken)
                {
                    await root.CloseAsync();
                }

                if (root.State != ConnectionState.Open)
                {
                    await root.OpenAsync(CancellationToken.None);
                }

                await ExecuteAsync(root,
                    $"""
                    IF DB_ID(N'{_database}') IS NOT NULL
                    BEGIN
                        ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                        DROP DATABASE [{_database}];
                    END;
                    """,
                    CancellationToken.None);

                _database = null;
            }
        }
        catch (Exception exception)
        {
            Retain(exception, "SafeMigrations.LiveCatalogDatabaseCleanupException");
        }

        await DisposeResourceAsync(_root, "SafeMigrations.LiveCatalogRootCleanupException");
        _root = null;
        if (primaryFailure is null
            && failure is not null)
        {
            throw new InvalidOperationException("The isolated SQL Server live catalog cleanup failed.");
        }

        return;

        async Task DisposeResourceAsync(
            IAsyncDisposable? resource,
            string key
        )
        {
            try
            {
                await SafeMigrationAnalysisCleanup.DisposeAsync(resource, failure, key);
            }
            catch (Exception exception)
            {
                Retain(exception, key);
            }
        }

        void Retain(
            Exception exception,
            string key
        )
        {
            if (failure is null)
            {
                failure = exception;
            }
            else
            {
                SafeMigrationAnalysisCleanup.RetainFailure(failure, exception, key);
            }
        }
    }

    /// <summary>Executes trusted fixture SQL using the existing finite native command timeout.</summary>
    private static async Task ExecuteAsync(
        SqlConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        var command = connection.CreateCommand();
        Exception? failure = null;
        try
        {
            command.CommandTimeout = CommandTimeoutSeconds;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;

            throw;
        }
        finally
        {
            await SafeMigrationAnalysisCleanup.DisposeAsync(
                command,
                failure,
                "SafeMigrations.LiveCatalogCommandCleanupException");
        }
    }

    /// <summary>Creates the original deterministic managed or unrelated indexed table shape.</summary>
    private static string BuildTables(
        string prefix,
        int count,
        bool includeIndex
    )
    {
        var sql = new StringBuilder();
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var table = prefix + ordinal.ToString("D4", CultureInfo.InvariantCulture);
            sql
                .Append("CREATE TABLE dbo.[")
                .Append(table)
                .Append("] (id int NOT NULL); ");

            if (includeIndex)
            {
                sql
                    .Append("CREATE INDEX [ix_")
                    .Append(table)
                    .Append("] ON dbo.[")
                    .Append(table)
                    .Append("] (id); ");
            }
        }

        return sql.ToString();
    }

    private sealed class CatalogContext : DbContext
    {
        private readonly string _connectionString;

        internal CatalogContext(
            string connectionString
        )
        {
            _connectionString = connectionString;
        }

        /// <inheritdoc />
        protected override void OnConfiguring(
            DbContextOptionsBuilder optionsBuilder
        )
        {
            optionsBuilder.UseSqlServer(_connectionString, provider => provider.CommandTimeout(CommandTimeoutSeconds));
            optionsBuilder.UseSqlServerSafeMigrations<CatalogContext>();
        }
    }
}
