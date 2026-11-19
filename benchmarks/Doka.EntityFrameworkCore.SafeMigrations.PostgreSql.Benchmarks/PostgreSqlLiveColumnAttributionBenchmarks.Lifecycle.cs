namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Benchmarks;

public partial class PostgreSqlLiveColumnAttributionBenchmarks
{
    /// <summary>Verifies committed application and replay, then awaits all acquired resource cleanup.</summary>
    /// <returns>Final native verification and complete noncancellable cleanup.</returns>
    public async ValueTask DisposeAsync()
    {
        Exception? failure = null;
        var cleanupCompleted = true;
        await DisposeResourceAsync(_transaction, "SafeMigrations.PostgreSqlLiveColumnTransactionCleanupException");
        _transaction = null;
        try
        {
            if (_ready && failure is null)
            {
                await ExecuteCommandsAsync(transaction: null, CancellationToken.None);
                await ExecuteCommandsAsync(transaction: null, CancellationToken.None);
                var context = _database?.Context
                    ?? throw new InvalidOperationException("The native fixture owner disappeared.");

                var replay = await context
                    .GetService<ISafeMigrationProviderAnalyzer>()
                    .AnalyzeAsync(
                        context,
                        _repairs,
                        CancellationToken.None);

                MaintenanceAttribution.Require(
                    replay.Count == ColumnCount
                    && replay.All(static value => value.ObservedState == SafeMigrationObservedState.Matching),
                    "The complete native column repair did not replay as matching.");

                await VerifyPreservedRowsAsync(CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        await DisposeResourceAsync(_connection, "SafeMigrations.PostgreSqlLiveColumnConnectionCleanupException");
        _connection = null;
        await DisposeResourceAsync(_database, "SafeMigrations.PostgreSqlLiveColumnDatabaseCleanupException");
        _database = null;
        _ready = false;
        GC.SuppressFinalize(this);
        if (failure is not null)
        {
            if (failure is NpgsqlException nativeFailure)
            {
                throw PostgreSqlLiveBenchmarkDatabase.NativeFailure(nativeFailure, "column finalization");
            }

            if (!cleanupCompleted
                && failure is not OperationCanceledException)
            {
                throw new InvalidOperationException(failure.Message + " Owned native cleanup also failed.");
            }

            ExceptionDispatchInfo
                .Capture(failure)
                .Throw();
        }

        return;

        async Task DisposeResourceAsync(
            IAsyncDisposable? resource,
            string evidenceKey
        )
        {
            try
            {
                cleanupCompleted &= await SafeMigrationAnalysisCleanup.DisposeAsync(resource, failure, evidenceKey);
            }
            catch (Exception exception)
            {
                cleanupCompleted = false;

                // WHY: Continue to the next actual owner even when cleanup itself is the first failure.
                if (failure is null)
                {
                    failure = exception;
                }
                else
                {
                    SafeMigrationAnalysisCleanup.RetainFailure(failure, exception, evidenceKey);
                }
            }
        }
    }

    /// <summary>Checks successful runtime effects and restores identical initial drift outside timed work.</summary>
    [IterationCleanup(Target = nameof(ExecuteRuntime))]
    public void CompleteRuntimeIteration()
    {
        var transaction = _transaction;
        if (transaction is null)
        {
            return;
        }

        Exception? failure = null;
        NpgsqlCommand? classification = null;
        var cleanupFailed = false;
        try
        {
            if (_runtimeCompleted)
            {
                classification = Connection.CreateCommand();
                classification.Transaction = transaction;
                classification.CommandText = _catalogSql;
                MaintenanceAttribution.Require(
                    Convert.ToString(classification.ExecuteScalar(), CultureInfo.InvariantCulture) == "matching",
                    "Native runtime did not satisfy the complete column contract.");
            }
        }
        catch (Exception exception)
        {
            Retain(exception, "SafeMigrations.PostgreSqlLiveColumnIterationVerificationException");
        }
        finally
        {
            try
            {
                classification?.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailed = true;
                Retain(exception, "SafeMigrations.PostgreSqlLiveColumnIterationCommandDisposeException");
            }
        }

        try
        {
            // WHY: This framework Action boundary uses native synchronous rollback, never sync-over-async.
            transaction.Rollback();
        }
        catch (Exception exception)
        {
            cleanupFailed = true;
            Retain(exception, "SafeMigrations.PostgreSqlLiveColumnIterationRollbackException");
        }

        try
        {
            transaction.Dispose();
        }
        catch (Exception exception)
        {
            cleanupFailed = true;
            Retain(exception, "SafeMigrations.PostgreSqlLiveColumnIterationDisposeException");
        }

        _transaction = null;
        if (failure is not null)
        {
            if (failure is NpgsqlException nativeFailure)
            {
                throw PostgreSqlLiveBenchmarkDatabase.NativeFailure(nativeFailure, "runtime iteration cleanup");
            }

            if (cleanupFailed && failure is not OperationCanceledException)
            {
                throw new InvalidOperationException(failure.Message + " Owned native cleanup also failed.");
            }

            ExceptionDispatchInfo
                .Capture(failure)
                .Throw();
        }

        return;

        void Retain(
            Exception exception,
            string evidenceKey
        )
        {
            if (failure is null)
            {
                failure = exception;
            }
            else
            {
                SafeMigrationAnalysisCleanup.RetainFailure(failure, exception, evidenceKey);
            }
        }
    }

    /// <summary>Performs final verification and releases only the generated native database.</summary>
    /// <returns>The actual asynchronous finalization.</returns>
    [GlobalCleanup]
    public async Task Cleanup() => await DisposeAsync();
}
