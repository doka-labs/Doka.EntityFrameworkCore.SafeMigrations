namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Benchmarks;

/// <summary>Measures the original warmed convergence generation through BenchmarkDotNet allocation sampling.</summary>

// WHY: BenchmarkDotNet's generated isolated harness derives from the benchmark type.
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public class MySqlLiveRuntimeGenerationBenchmarks : IAsyncDisposable
{
    private MySqlRuntimePerformanceDbContext? _context;
    private MySqlLiveBenchmarkDatabase? _database;
    private Exception? _failure;
    private IMigrationsSqlGenerator? _generator;
    private IReadOnlyList<MigrationOperation> _operations = [];

    /// <summary>Drains noncancellable context and database cleanup.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await ReleaseAsync(_failure);
            GC.SuppressFinalize(this);
        }
        catch (Exception exception)
        {
            throw MySqlLiveBenchmarkDatabase.ProtectFailure(exception);
        }
    }

    /// <summary>Acquires the native profile and warms the unchanged convergence operations outside timing.</summary>
    /// <returns>The asynchronous explicit native setup.</returns>
    [GlobalSetup]
    public async Task Setup()
    {
        try
        {
            _database = await MySqlLiveBenchmarkDatabase.CreateAsync(CancellationToken.None);
            _context = MySqlLiveRuntimeContext.Create(_database, new MySqlRuntimeCommandInterceptor());
            _generator = _context.GetService<IMigrationsSqlGenerator>();
            var migration = new MySqlRuntimeConvergenceMigration { ActiveProvider = _context.Database.ProviderName! };
            _operations = migration.UpOperations;
            _ = _generator.Generate(_operations, _context.Model);
        }
        catch (Exception exception)
        {
            _failure = exception;
            await ReleaseAsync(exception);

            throw MySqlLiveBenchmarkDatabase.ProtectFailure(exception);
        }
    }

    /// <summary>Generates real guarded SQL using BenchmarkDotNet's timing and allocation instrumentation.</summary>
    /// <returns>The generated command count consumed by BenchmarkDotNet.</returns>
    [Benchmark]
    public int Generate()
    {
        try
        {
            var generator = _generator ?? throw new InvalidOperationException("Setup has not completed.");
            var context = _context ?? throw new InvalidOperationException("Setup has not completed.");

            return generator.Generate(_operations, context.Model)
                .Count;
        }
        catch (Exception exception)
        {
            _failure = exception;

            throw MySqlLiveBenchmarkDatabase.ProtectFailure(exception);
        }
    }

    /// <summary>Releases only this diagnostic invocation's native resources.</summary>
    /// <returns>The asynchronous owned cleanup.</returns>
    [GlobalCleanup]
    public async Task Cleanup()
    {
        try
        {
            await DisposeAsync();
        }
        catch (Exception exception)
        {
            throw MySqlLiveBenchmarkDatabase.ProtectFailure(exception);
        }
    }

    private async ValueTask ReleaseAsync(
        Exception? originalFailure
    )
    {
        var failure = originalFailure;
        try
        {
            await SafeMigrationAnalysisCleanup.DisposeAsync(
                _context,
                originalFailure,
                "SafeMigrations.MySqlLiveGenerationContextDisposeException");
        }
        catch (Exception exception)
        {
            failure = exception;

            throw;
        }
        finally
        {
            await SafeMigrationAnalysisCleanup.DisposeAsync(
                _database,
                failure,
                "SafeMigrations.MySqlLiveGenerationDatabaseDisposeException");
        }
    }
}
