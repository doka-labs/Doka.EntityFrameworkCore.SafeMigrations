namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Verifies native concurrent-migration admission without machine-speed assumptions.</summary>
public sealed class SqliteConcurrentStartBarrierTests
{
    /// <summary>A failed or canceled worker withdraws before its healthy peer reaches admission.</summary>
    /// <param name="cancelBeforeAdmission">Whether the first worker is canceled instead of misconfigured.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAdmissionWithdrawsItsParticipantBeforePeerStarts(
        bool cancelBeforeAdmission
    )
    {
        // Arrange
        var databasePath = SqliteSafeMigrationExecutionIntegrationTests.CreateOwnedDatabasePath();
        var connectionString = SqliteSafeMigrationExecutionIntegrationTests.CreateOwnedFileConnectionString(
            databasePath);

        using var start = new Barrier(2);
        using var cancellation = new CancellationTokenSource();
        if (cancelBeforeAdmission)
        {
            await cancellation.CancelAsync();
        }

        Exception? failure;
        int remainingParticipants;
        Exception? primaryFailure = null;
        Task? peer = null;
        try
        {
            // Act
            failure = await Record.ExceptionAsync(() => ExecuteWorkerAsync(
                cancelBeforeAdmission
                    ? connectionString
                    : "unsupported_sqlite_option=1",
                start,
                cancellation.Token));

            remainingParticipants = start.ParticipantCount;

            // WHY: A broken admission implementation must fail this assertion,
            // not leave the regression's healthy peer waiting on an absent worker.
            if (remainingParticipants == 2)
            {
                start.RemoveParticipant();
            }

            // WHY: The awaited worker and failure drain finish before the owning barrier is disposed.
            // ReSharper disable once AccessToDisposedClosure
            peer = Task.Run(() => ExecuteWorkerAsync(connectionString, start, CancellationToken.None),
                CancellationToken.None);
            await peer;
        }
        catch (Exception exception)
        {
            primaryFailure = exception;

            throw;
        }
        finally
        {
            await OwnedTestOperationCleanup.CompleteAsync(primaryFailure,
                peer,
                null,
                null,
                afterDrain: () =>
                {
                    File.Delete(databasePath);

                    return Task.CompletedTask;
                });
        }

        // Assert
        Assert.Equal(1, remainingParticipants);
        if (cancelBeforeAdmission)
        {
            Assert.IsType<OperationCanceledException>(failure, exactMatch: false);
        }
        else
        {
            Assert.IsType<ArgumentException>(failure);
        }
    }

    /// <summary>An independently delayed peer is admitted only after its explicit release.</summary>
    [Fact]
    public async Task DelayedParticipantDoesNotCompleteOrDisposeTheWaitingWorker()
    {
        // Arrange
        var databasePath = SqliteSafeMigrationExecutionIntegrationTests.CreateOwnedDatabasePath();
        var connectionString = SqliteSafeMigrationExecutionIntegrationTests.CreateOwnedFileConnectionString(
            databasePath);

        using var start = new Barrier(2);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.CompletedTask;
        var second = Task.CompletedTask;
        bool waitingBeforeRelease;
        Exception? primaryFailure = null;
        try
        {
            // Act
            // WHY: The finally block drains both actual workers before disposing their shared barrier.
            // ReSharper disable once AccessToDisposedClosure
            first = Task.Run(() => ExecuteWorkerAsync(connectionString, start, CancellationToken.None));
            second = Task.Run(async () =>
            {
                await releaseSecond.Task;
                // WHY: The finally block releases and drains this worker before disposing its barrier.
                // ReSharper disable once AccessToDisposedClosure
                await ExecuteWorkerAsync(connectionString, start, CancellationToken.None);
            });

            while (start.ParticipantsRemaining != 1)
            {
                if (first.IsCompleted)
                {
                    await first;

                    throw new InvalidOperationException("The first migrator completed before its peer was released.");
                }

                await Task.Yield();
            }

            waitingBeforeRelease = !first.IsCompleted && !second.IsCompleted;
            releaseSecond.SetResult();
            await Task.WhenAll(first, second);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;

            throw;
        }
        finally
        {
            releaseSecond.TrySetResult();
            await OwnedTestOperationCleanup.CompleteAsync(
                primaryFailure,
                Task.WhenAll(first, second),
                null,
                null,
                afterDrain: () =>
                {
                    File.Delete(databasePath);

                    return Task.CompletedTask;
                });
        }

        // Assert
        Assert.True(waitingBeforeRelease);
    }

    /// <summary>Invokes the actual worker rather than reproducing its admission logic in the probe.</summary>
    /// <param name="connectionString">The independently owned SQLite database identity.</param>
    /// <param name="start">The same admission barrier consumed by the actual worker.</param>
    /// <param name="cancellationToken">The worker's operation cancellation.</param>
    /// <returns>The actual worker, including every initialization and migration failure.</returns>
    private static Task ExecuteWorkerAsync(
        string connectionString,
        Barrier start,
        CancellationToken cancellationToken
    )
    {
        var method = typeof(SqliteSafeMigrationExecutionIntegrationTests).GetMethod(
                "ExecuteConcurrentMigrationAsync",
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The real concurrent SQLite worker was not found.");

        return (Task)method.Invoke(null, [connectionString, start, cancellationToken])!;
    }
}
