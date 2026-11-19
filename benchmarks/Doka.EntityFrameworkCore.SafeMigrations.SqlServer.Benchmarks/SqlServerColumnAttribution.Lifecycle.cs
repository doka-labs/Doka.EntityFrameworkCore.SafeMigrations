namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

public partial class SqlServerColumnAttribution
{
    /// <summary>Releases managed diagnostic resources when the benchmark owner is disposed.</summary>
    /// <param name="disposing">Whether managed resources are eligible for disposal.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (disposing)
        {
            DisposeDatabase();
        }
    }

    private void DisposeDatabase()
    {
        _context?.Dispose();
        _context = null;
        _generator = null;
        _analyzer = null;
        _original = string.Empty;
        _connection?.Dispose();
        _connection = null;
        try
        {
            if (_database is not null
                && _root is not null)
            {
                // WHY: Only this generated database is eligible; caller databases are never reset.
                Execute(
                    _root,
                    $"""
                    ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{_database}];
                    """);

                _database = null;
            }
        }
        finally
        {
            _root?.Dispose();
            _root = null;
        }
    }

    /// <summary>Awaits native cleanup without canceling an acquired database lifetime.</summary>
    /// <returns>The completion of every owned cleanup stage.</returns>
    private async Task DisposeDatabaseAsync()
    {
        Exception? failure = null;
        await DisposeOwnedAsync(_context, "SafeMigrations.BenchmarkContextCleanupException");
        _context = null;
        _generator = null;
        _analyzer = null;
        _original = string.Empty;
        await DisposeOwnedAsync(_connection, "SafeMigrations.BenchmarkConnectionCleanupException");
        _connection = null;
        try
        {
            if (_database is not null
                && _root is not null)
            {
                // WHY: Cleanup must finish the native DROP even when the measured operation was canceled.
                await ExecuteAsync(
                    _root,
                    $"""
                    ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{_database}];
                    """,
                    CancellationToken.None);

                _database = null;
            }
        }
        catch (Exception exception)
        {
            Retain(exception, "SafeMigrations.BenchmarkDatabaseCleanupException");
        }

        await DisposeOwnedAsync(_root, "SafeMigrations.BenchmarkRootCleanupException");
        _root = null;
        if (failure is not null)
        {
            ExceptionDispatchInfo
                .Capture(failure)
                .Throw();
        }

        return;

        async Task DisposeOwnedAsync(
            IAsyncDisposable? resource,
            string evidenceKey
        )
        {
            try
            {
                _ = await SafeMigrationAnalysisCleanup.DisposeAsync(resource, failure, evidenceKey);
            }
            catch (Exception exception)
            {
                Retain(exception, evidenceKey);
            }
        }

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
}
