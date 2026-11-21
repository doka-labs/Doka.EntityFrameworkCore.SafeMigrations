namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Benchmarks;

/// <summary>Measures the sixteen original native catalog/runtime column fixtures, not client generation.</summary>
[InvocationCount(1)]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public partial class PostgreSqlLiveColumnAttributionBenchmarks : IAsyncDisposable
{
    private string _catalogSql = string.Empty;
    private ExpectedColumnDefinition[] _columns = [];
    private IReadOnlyList<MigrationCommand> _commands = [];
    private NpgsqlConnection? _connection;
    private PostgreSqlLiveBenchmarkDatabase? _database;
    private bool _ready;
    private SafeMigrationOperation[] _repairs = [];
    private bool _runtimeCompleted;
    private bool _runtimeInvoked;
    private string _table = string.Empty;
    private NpgsqlTransaction? _transaction;

    /// <summary>Gets or sets the exact required-column count in the paired native fixture.</summary>
    [Params(1, 8, 16, 64)]
    public int ColumnCount { get; set; }

    /// <summary>Gets or sets whether visible, qualified and type-owned collations are mixed.</summary>
    [Params(false, true)]
    public bool MixedCollations { get; set; }

    /// <summary>Gets or sets whether only canonical comments initially carry a repairable difference.</summary>
    [Params(false, true)]
    public bool Drift { get; set; }

    private NpgsqlConnection Connection
        => _connection ?? throw new InvalidOperationException("Native column setup is incomplete.");

    /// <summary>Creates one populated native fixture and captures structural evidence outside measurements.</summary>
    /// <returns>The real native fixture and prepared catalog/runtime contracts.</returns>
    [GlobalSetup]
    public async Task Setup()
    {
        if (_database is not null)
        {
            throw new InvalidOperationException("The native column benchmark already owns a database.");
        }

        try
        {
            _database = await PostgreSqlLiveBenchmarkDatabase.CreateAsync(pooling: false, CancellationToken.None);
            _connection = await _database.OpenConnectionAsync(CancellationToken.None);
            _table = $"attribution_{ColumnCount}_{MixedCollations}_{Drift}";
            _columns = Columns(ColumnCount, MixedCollations);
            await PrepareFixtureAsync(CancellationToken.None);
            var context = _database.Context;
            var catalog = new PostgreSqlSafeMigrationCatalogSqlBuilder(
                context.GetService<IRelationalTypeMappingSource>(),
                context.GetService<ISqlGenerationHelper>());

            var operation = new SafeMigrationOperation(
                new EnsureTableIntent(
                    new ExpectedTableDefinition(_table, _columns),
                    SafeMigrationTableMode.StrictDefinition),
                SafeMigrationPolicy.ThrowIfDifferent);

            _repairs = _columns
                .Select(column => new SafeMigrationOperation(
                    new EnsureColumnIntent(_table, column),
                    SafeMigrationPolicy.RepairIfSafe))

                // WHY: Snapshot the complete ordered fixture before any measured native execution.
                // ReSharper disable once UseCollectionExpression
                .ToArray();

            var plan = catalog.Build(operation);
            _catalogSql = $"SELECT ({plan.RenderStateExpression()}), ({plan.Postcondition});";
            _commands = context
                .GetService<IMigrationsSqlGenerator>()
                .Generate(_repairs, context.Model);

            MaintenanceAttribution.Require(_commands.Count > 0, "Native column runtime commands were not generated.");
            await VerifyInitialAsync(CancellationToken.None);
            await WriteStructuralEvidenceAsync(CancellationToken.None);
            _ready = true;
        }
        catch (Exception exception)
        {
            var cleanupCompleted = await SafeMigrationAnalysisCleanup.DisposeAsync(
                this,
                exception,
                "SafeMigrations.PostgreSqlLiveColumnSetupCleanupException");

            if (exception is NpgsqlException nativeFailure)
            {
                throw PostgreSqlLiveBenchmarkDatabase.NativeFailure(nativeFailure, "column setup");
            }

            if (!cleanupCompleted
                && exception is not OperationCanceledException)
            {
                throw new InvalidOperationException(exception.Message + " Owned native cleanup also failed.");
            }

            throw;
        }
    }

    /// <summary>Executes the complete original native catalog classifier on the populated fixture.</summary>
    /// <returns>The actual native classification, consumed by BenchmarkDotNet.</returns>
    [Benchmark]
    public async Task<string> ClassifyCatalog()
    {
        try
        {
            return await ReadCatalogStateAsync(CancellationToken.None);
        }
        catch (NpgsqlException exception)
        {
            throw PostgreSqlLiveBenchmarkDatabase.NativeFailure(exception, "column classification");
        }
    }

    /// <summary>Executes the complete classifier with the caller's forward-work token.</summary>
    /// <param name="cancellationToken">Cancels native classification rather than only its observer.</param>
    /// <returns>The native state scalar.</returns>
    private Task<string> ReadCatalogStateAsync(
        CancellationToken cancellationToken
    ) => ReadScalarAsync(_catalogSql, cancellationToken);

    /// <summary>Completes a native scalar and its command cleanup without masking a work failure.</summary>
    /// <param name="sql">The source-controlled scalar query.</param>
    /// <param name="cancellationToken">Cancels actual native execution.</param>
    /// <returns>The invariant native scalar text.</returns>
    private async Task<string> ReadScalarAsync(
        string sql,
        CancellationToken cancellationToken
    )
    {
        var command = Connection.CreateCommand();
        Exception? failure = null;
        try
        {
            command.CommandText = sql;

            return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture)
                ?? "<null>";
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
                "SafeMigrations.PostgreSqlLiveColumnScalarCommandCleanupException");
        }
    }

    /// <summary>Creates a fresh rollback owner before each state-mutating measured invocation.</summary>
    [IterationSetup(Target = nameof(ExecuteRuntime))]
    public void PrepareRuntimeIteration()
    {
        if (_transaction is not null)
        {
            throw new InvalidOperationException("The previous native runtime transaction is still owned.");
        }

        // WHY: Acquire the owned transaction outside the measured runtime operation. This existing
        // synchronous native path never blocks on a Task or adds setup work to the timed operation.
        try
        {
            _transaction = Connection.BeginTransaction();
        }
        catch (NpgsqlException exception)
        {
            throw PostgreSqlLiveBenchmarkDatabase.NativeFailure(exception, "runtime iteration setup");
        }

        _runtimeInvoked = false;
        _runtimeCompleted = false;
    }

    /// <summary>Executes the complete generated native runtime commands inside the prepared rollback owner.</summary>
    /// <returns>The actual number of native generated-command round trips.</returns>
    [Benchmark]
    public async Task<int> ExecuteRuntime()
    {
        if (_runtimeInvoked || _transaction is null)
        {
            // WHY: CLI job overrides must not execute a second mutation against an already-repaired fixture.
            throw new InvalidOperationException(
                "Native runtime measurement requires exactly one invocation per reset.");
        }

        _runtimeInvoked = true;
        try
        {
            await ExecuteCommandsAsync(_transaction, CancellationToken.None);
            _runtimeCompleted = true;

            return _commands.Count;
        }
        catch (NpgsqlException exception)
        {
            throw PostgreSqlLiveBenchmarkDatabase.NativeFailure(exception, "column runtime");
        }
    }
}
