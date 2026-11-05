namespace Doka.EntityFrameworkCore.SafeMigrations.Benchmarks;

/// <summary>Attributes real bounded narrowing discovery and transport phases with deterministic ADO results.</summary>
public class PostgreSqlMaintenanceAttribution : IDisposable
{
    private CatalogStatementTestConnection _connection = null!;
    private PostgreSqlGenerationContext _context = null!;
    private bool _disposed;
    private SafeMigrationOperation[] _operations = null!;

    private Func<DbConnection, IReadOnlyList<SafeMigrationOperation>, SafeMigrationObservedState?[], CancellationToken,
        Task> _resolve = null!;

    private SafeMigrationObservedState?[] _states = null!;

    /// <summary>Gets or sets the operation count surrounding statement and transport batch boundaries.</summary>
    [Params(
        31,
        32,
        33,
        511,
        512,
        513,
        4097)]
    public int OperationCount { get; set; }

    /// <summary>Gets or sets whether deterministic ADO supports native batching or command fallback.</summary>
    [Params(false, true)]
    public bool NativeBatch { get; set; }

    /// <summary>Releases the deterministic transport and provider service context.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Runs exact native-batch and fallback windows without claiming native database timings.</summary>
    /// <param name="args">The BenchmarkDotNet command-line arguments.</param>
    /// <returns>The shared benchmark runner exit code.</returns>
    internal static int Run(
        string[] args
    ) => BenchmarkProgram.Run(args, typeof(PostgreSqlMaintenanceAttribution));

    /// <summary>Checks exact SQL, ordinal ownership, replay and cancellation outside measurements.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _disposed = false;
        _context = new PostgreSqlGenerationContext();
        var catalog = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            _context.GetService<IRelationalTypeMappingSource>(),
            _context.GetService<ISqlGenerationHelper>());

        var analyzer = new PostgreSqlSafeMigrationProviderAnalyzer(
            _context.GetService<IRelationalTypeMappingSource>(),
            _context.GetService<ISqlGenerationHelper>());

        _resolve = Bind(analyzer);
        _operations = Operations(OperationCount);
        _states = new SafeMigrationObservedState?[OperationCount];
        _connection = new CatalogStatementTestConnection(NativeBatch, EligibilityRows);
        var resolved = ResolveEligibility();
        resolved
            .GetAwaiter()
            .GetResult();

        var observed = ReadResult(resolved);
        var expected = _operations
            .Chunk(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
            .Select((chunk,
                    window) => string.Join(
                    SafeMigrationCatalogQueryLimits.Separator,
                    chunk.Select((operation,
                        ordinal) => EligibilitySql(
                        catalog,
                        operation,
                        window * SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement + ordinal)))
                + SafeMigrationCatalogQueryLimits.Trailer)
            .ToArray();

        MaintenanceAttribution.Require(
            expected.SequenceEqual(_connection.Submissions.Select(static submission => submission.Sql)),
            "The measured PostgreSQL window lost exact SQL or ordinal ownership.");

        MaintenanceAttribution.Require(
            observed.Length == OperationCount
            && _connection.Submissions.All(static submission => submission is { Timeout: 71, Parameters.Count: 0 }
                && Encoding.UTF8.GetByteCount(submission.Sql)
                <= SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes),
            "The PostgreSQL attribution transport or result shape is invalid.");

        VerifyFacts(observed);
        var batches = _connection.BatchCounts.ToArray();
        var sqlBytes = _connection.Submissions.Sum(static submission =>
            Encoding.UTF8.GetByteCount(submission.Sql));

        var hash = MaintenanceAttribution.Hash(string.Join("\n", expected));
        _connection.ResetCaptures();
        var replay = ResolveEligibility();
        replay
            .GetAwaiter()
            .GetResult();

        VerifyFacts(ReadResult(replay));
        MaintenanceAttribution.Require(
            expected.SequenceEqual(_connection.Submissions.Select(static submission => submission.Sql))
            && batches.SequenceEqual(_connection.BatchCounts),
            "PostgreSQL attribution replay accumulated captures or changed transport ownership.");

        VerifyRepeatedSkippedAndCancelled(_resolve);
        var output = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_ATTRIBUTION_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            _ = MaintenanceAttribution.Write(
                Path.Combine(output, $"postgresql-eligibility-{OperationCount}-{NativeBatch}.json"),
                "Functional/source evidence; PostgreSQL deterministic client ADO workload; not timing evidence",
                [
                    new
                    {
                        Id = "PERF-03",
                        Workload = "eligibility_windows",
                        Operations = OperationCount,
                        NativeBatch,
                        Statements = expected.Length,
                        Batches = batches,
                        SqlUtf8Bytes = sqlBytes,
                        ExactSqlHash = hash,
                    },
                ]);
        }

        // WHY: Keep exact SQL/replay assertions above, but never retain or clear diagnostic observations
        // in measured invocations. IterationSetup would force single-invocation calibration instead.
        _connection.Dispose();
        _connection = new CatalogStatementTestConnection(NativeBatch, EligibilityRows) { CaptureObservations = false };
        var unrecorded = ResolveEligibility();
        unrecorded
            .GetAwaiter()
            .GetResult();

        VerifyFacts(ReadResult(unrecorded));
    }

    /// <summary>Resolves one complete eligibility workload without recording diagnostic observations.</summary>
    /// <returns>The real asynchronous phase completion, consumed by BenchmarkDotNet.</returns>
    [Benchmark]
    public Task ResolveEligibility() => _resolve(_connection, _operations, _states, CancellationToken.None);

    /// <summary>Disposes the deterministic transport and provider service context.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Releases owned managed resources once while supporting generated benchmark subclasses.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (!disposing || _disposed)
        {
            return;
        }

        _connection.Dispose();
        _context.Dispose();
        _disposed = true;
    }

    /// <summary>Binds the existing private phase once so reflection and argument arrays are never measured.</summary>
    /// <param name="analyzer">The real analyzer owning the measured phase.</param>
    /// <returns>The typed synchronous-completion task delegate.</returns>
    private static Func<DbConnection, IReadOnlyList<SafeMigrationOperation>, SafeMigrationObservedState?[],
        CancellationToken, Task> Bind(
        PostgreSqlSafeMigrationProviderAnalyzer analyzer
    )
    {
        var method =
            typeof(PostgreSqlSafeMigrationProviderAnalyzer).GetMethod(
                "ResolveDataProbeResultsAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The bounded PostgreSQL probe phase is missing.");

        var connection = Expression.Parameter(typeof(DbConnection));
        var operations = Expression.Parameter(typeof(IReadOnlyList<SafeMigrationOperation>));
        var states = Expression.Parameter(typeof(SafeMigrationObservedState?[]));
        var cancellation = Expression.Parameter(typeof(CancellationToken));
        var call = Expression.Call(
            Expression.Constant(analyzer),
            method,
            connection,
            operations,
            states,
            Expression.Convert(Expression.Constant(71), typeof(int?)),
            cancellation);

        return Expression
            .Lambda<Func<DbConnection, IReadOnlyList<SafeMigrationOperation>, SafeMigrationObservedState?[],
                CancellationToken, Task>>(call, connection, operations, states, cancellation)
            .Compile();
    }

    /// <summary>Reads private results outside measurements without adding a production attribution API.</summary>
    /// <param name="task">The completed real analysis phase.</param>
    /// <returns>The boxed private-result array.</returns>
    private static Array ReadResult(
        Task task
    ) => (Array)(task
            .GetType()
            .GetProperty("Result")
            ?.GetValue(task)
        ?? throw new InvalidOperationException("The PostgreSQL probe result is absent."));

    /// <summary>Checks each eligibility result and its independently generated negative slots.</summary>
    /// <param name="output">The real private phase result array.</param>
    private static void VerifyFacts(
        Array output
    )
    {
        for (var ordinal = 0; ordinal < output.Length; ordinal++)
        {
            var result = output.GetValue(ordinal)
                ?? throw new InvalidOperationException("A required PostgreSQL result slot is absent.");

            var type = result.GetType();
            var eligible = type
                    .GetProperty("IsTransitionEligible")
                    ?.GetValue(result) as bool?
                ?? throw new InvalidOperationException("The transition-eligibility result field is absent.");

            var required = type
                    .GetProperty("IsRequired")
                    ?.GetValue(result) as bool?
                ?? throw new InvalidOperationException("The required-probe result field is absent.");

            var blocked = type
                    .GetProperty("IsBlocked")
                    ?.GetValue(result) as bool?
                ?? throw new InvalidOperationException("The blocked-data result field is absent.");

            MaintenanceAttribution.Require(
                eligible == (ordinal % 3 != 0) && !required && !blocked,
                "PostgreSQL data evidence was assigned to the wrong candidate.");
        }
    }

    /// <summary>Retains deduplication, skipped-ordinal and cancellation controls outside measurement.</summary>
    /// <param name="resolve">The one-time bound real phase delegate.</param>
    private static void VerifyRepeatedSkippedAndCancelled(
        Func<DbConnection, IReadOnlyList<SafeMigrationOperation>, SafeMigrationObservedState?[], CancellationToken,
            Task> resolve
    )
    {
        var operations = Operations(514);
        operations[512] = operations[1];
        operations[513] = operations[2];
        var states = new SafeMigrationObservedState?[operations.Length];
        states[0] = SafeMigrationObservedState.PrerequisiteMissing;
        states[511] = SafeMigrationObservedState.Unsupported;
        using var connection = new CatalogStatementTestConnection(true, EligibilityRows);
        var task = resolve(connection, operations, states, CancellationToken.None);

        task
            .GetAwaiter()
            .GetResult();

        var output = ReadResult(task);

        MaintenanceAttribution.Require(
            output.GetValue(0) is null
            && output.GetValue(511) is null
            && Equals(output.GetValue(1), output.GetValue(512))
            && Equals(output.GetValue(2), output.GetValue(513))
            && connection.Submissions.Count == 16,
            "The PostgreSQL repeated/skipped candidate fixture no longer owns its result slots.");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = false;
        try
        {
            resolve(connection, operations, states, cancellation.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        MaintenanceAttribution.Require(cancelled, "PostgreSQL probe resolution ignored cancellation.");
    }

    /// <summary>Creates target/source lengths outside all timed phases.</summary>
    /// <param name="count">The operation count surrounding native batching boundaries.</param>
    /// <returns>The independently identified immutable column transitions.</returns>
    private static SafeMigrationOperation[] Operations(
        int count
    ) => Enumerable
        .Range(0, count)
        .Select(static index => new SafeMigrationOperation(
            new AlterColumnIntent(
                "maintenance_rows",
                new ExpectedColumnDefinition($"value_{index:D5}", typeof(string), true, "character varying(40)"),
                new ExpectedColumnDefinition($"value_{index:D5}", typeof(string), true, "character varying(80)")),
            SafeMigrationPolicy.RepairIfSafe))
        .ToArray();

    /// <summary>Builds the independent pre-materialized eligibility oracle before the measured private phase.</summary>
    /// <param name="catalog">The real catalog builder used only for the structural oracle.</param>
    /// <param name="operation">The exact immutable source/target transition.</param>
    /// <param name="ordinal">The authored operation slot.</param>
    /// <returns>The exact eligibility selection.</returns>
    private static string EligibilitySql(
        PostgreSqlSafeMigrationCatalogSqlBuilder catalog,
        SafeMigrationOperation operation,
        int ordinal
    )
    {
        var probe = catalog.Build(operation, includeTransitionEvidence: true)
                .DataProbe
            ?? throw new InvalidOperationException("The PostgreSQL attribution operation has no narrowing probe.");

        return $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, "
            + $"COALESCE(({probe.TransitionInvariantExpression}), FALSE), "
            + $"COALESCE(({probe.NarrowingExpression}), FALSE)";
    }

    /// <summary>Returns bounded deterministic catalog eligibility with no live row probes required.</summary>
    /// <param name="sql">The actually submitted statement.</param>
    /// <param name="parameters">The actually submitted bound parameters.</param>
    /// <returns>Statement-local ordered eligibility rows.</returns>
    private static DataTable EligibilityRows(
        string sql,
        IReadOnlyList<DbParameter> parameters
    )
    {
        MaintenanceAttribution.Require(parameters.Count == 0, "Eligibility unexpectedly bound query parameters.");
        var rows = new DataTable();
        rows.Columns.Add("ordinal", typeof(int));
        rows.Columns.Add("eligible", typeof(bool));
        rows.Columns.Add("narrowing", typeof(bool));
        foreach (var selection in sql.Split(SafeMigrationCatalogQueryLimits.Separator))
        {
            var start = selection.IndexOf("SELECT ", StringComparison.Ordinal) + 7;
            var end = selection.IndexOf(',', start);
            var ordinal = int.Parse(selection.AsSpan(start, end - start), CultureInfo.InvariantCulture);
            rows.Rows.Add(ordinal, ordinal % 3 != 0, false);
        }

        return rows;
    }
}
