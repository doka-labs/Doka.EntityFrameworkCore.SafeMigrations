namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies fixture database ownership independently of SQL Server container availability.
/// </summary>
public sealed class SqlServerDatabaseCleanupTests
{
    /// <summary>
    /// Removes a confirmed drop exactly once even when release is repeated.
    /// </summary>
    [Fact]
    public async Task Release_ConfirmedDropRemovesOwnershipAndRepeatedReleaseIsNoOp()
    {
        // Arrange
        var databases = new List<string> { "sm_owned" };
        var attempts = new List<string>();

        Task DropAsync(
            string database,
            CancellationToken cancellationToken
        )
        {
            attempts.Add(database);

            return Task.CompletedTask;
        }

        // Act
        await SqlServerContainerFixture.ReleaseOwnedDatabaseAsync(databases, "sm_owned", DropAsync,
            CancellationToken.None);
        await SqlServerContainerFixture.ReleaseOwnedDatabaseAsync(databases, "sm_owned", DropAsync,
            CancellationToken.None);

        // Assert
        Assert.Equal(["sm_owned"], attempts);
        Assert.Empty(databases);
    }

    /// <summary>
    /// Does not execute cleanup or change ownership for a database the fixture does not own.
    /// </summary>
    [Fact]
    public async Task Release_UnknownDatabaseDoesNotInvokeDropOrAlterOwnership()
    {
        // Arrange
        var databases = new List<string> { "sm_owned" };
        var attempts = 0;

        Task DropAsync(
            string database,
            CancellationToken cancellationToken
        )
        {
            attempts++;

            return Task.CompletedTask;
        }

        // Act
        await SqlServerContainerFixture.ReleaseOwnedDatabaseAsync(databases, "sm_unknown", DropAsync,
            CancellationToken.None);

        // Assert
        Assert.Equal(0, attempts);
        Assert.Equal(["sm_owned"], databases);
    }

    /// <summary>
    /// Retains ownership while a drop is still in flight, not only after a reported failure.
    /// </summary>
    [Fact]
    public async Task Release_PendingDropRemainsOwnedUntilSuccessfulCompletion()
    {
        // Arrange
        var databases = new List<string> { "sm_owned" };
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var release = SqlServerContainerFixture.ReleaseOwnedDatabaseAsync(databases, "sm_owned",
            (_, _) => completion.Task, CancellationToken.None);
        var ownershipWhilePending = databases.ToArray();
        var releaseWasPending = !release.IsCompleted;
        completion.SetResult();
        await release;

        // Assert
        Assert.True(releaseWasPending);
        Assert.Equal(["sm_owned"], ownershipWhilePending);
        Assert.Empty(databases);
    }

    /// <summary>
    /// Retains an open or drop failure for a successful eager retry or final fixture cleanup.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Release_OpenOrDropFailureRemainsOwnedForRetryOrFinalCleanup(
        bool failDuringDrop,
        bool useFinalCleanup
    )
    {
        // Arrange
        var databases = new List<string> { "sm_owned" };
        var expected = new InvalidOperationException("Injected root open or drop failure.");
        var openAttempts = 0;
        var dropAttempts = 0;

        Task DropAsync(
            string database,
            CancellationToken cancellationToken
        )
        {
            openAttempts++;

            if (openAttempts == 1 && !failDuringDrop)
            {
                return Task.FromException(expected);
            }

            dropAttempts++;

            if (openAttempts == 1)
            {
                return Task.FromException(expected);
            }

            return Task.CompletedTask;
        }

        // Act
        var observed = await Record.ExceptionAsync(() => SqlServerContainerFixture.ReleaseOwnedDatabaseAsync(
            databases, "sm_owned", DropAsync, CancellationToken.None));
        var ownershipAfterFailure = databases.ToArray();

        if (useFinalCleanup)
        {
            await SqlServerContainerFixture.DropOwnedDatabasesAsync(databases, DropAsync, CancellationToken.None);
        }
        else
        {
            await SqlServerContainerFixture.ReleaseOwnedDatabaseAsync(databases, "sm_owned", DropAsync,
                CancellationToken.None);
        }

        // Assert
        Assert.Same(expected, observed);
        Assert.Equal(["sm_owned"], ownershipAfterFailure);
        Assert.Equal(2, openAttempts);
        Assert.Equal(failDuringDrop ? 2 : 1, dropAttempts);
        Assert.Empty(databases);
    }

    /// <summary>
    /// Preserves ownership when cancellation interrupts opening or dropping before a later cleanup succeeds.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Release_OpenOrDropCancellationRemainsOwnedForRetryOrFinalCleanup(
        bool cancelDuringDrop,
        bool useFinalCleanup
    )
    {
        // Arrange
        var databases = new List<string> { "sm_owned" };
        using var cancellation = new CancellationTokenSource();
        var openAttempts = 0;
        var dropAttempts = 0;
        var tokens = new List<CancellationToken>();

        Task DropAsync(
            string database,
            CancellationToken cancellationToken
        )
        {
            tokens.Add(cancellationToken);
            openAttempts++;

            if (openAttempts == 1 && !cancelDuringDrop)
            {
                cancellation.Cancel();

                return Task.FromCanceled(cancellationToken);
            }

            dropAttempts++;

            if (openAttempts == 1)
            {
                cancellation.Cancel();

                return Task.FromCanceled(cancellationToken);
            }

            return Task.CompletedTask;
        }

        // Act
        var observed = await Record.ExceptionAsync(() => SqlServerContainerFixture.ReleaseOwnedDatabaseAsync(
            databases, "sm_owned", DropAsync, cancellation.Token));
        var ownershipAfterCancellation = databases.ToArray();

        if (useFinalCleanup)
        {
            await SqlServerContainerFixture.DropOwnedDatabasesAsync(databases, DropAsync, CancellationToken.None);
        }
        else
        {
            await SqlServerContainerFixture.ReleaseOwnedDatabaseAsync(databases, "sm_owned", DropAsync,
                CancellationToken.None);
        }

        // Assert
        Assert.Equal(cancellation.Token,
            Assert.IsAssignableFrom<OperationCanceledException>(observed).CancellationToken);
        Assert.Equal(["sm_owned"], ownershipAfterCancellation);
        Assert.Equal([cancellation.Token, CancellationToken.None], tokens);
        Assert.Equal(2, openAttempts);
        Assert.Equal(cancelDuringDrop ? 2 : 1, dropAttempts);
        Assert.Empty(databases);
    }

    /// <summary>
    /// Rejects an already cancelled release before invoking the drop or forgetting ownership.
    /// </summary>
    [Fact]
    public async Task Release_AlreadyCanceledTokenRetainsOwnershipWithoutInvokingDrop()
    {
        // Arrange
        var databases = new List<string> { "sm_owned" };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var attempts = 0;

        Task DropAsync(
            string database,
            CancellationToken cancellationToken
        )
        {
            attempts++;

            return Task.CompletedTask;
        }

        // Act
        var observed = await Record.ExceptionAsync(() => SqlServerContainerFixture.ReleaseOwnedDatabaseAsync(
            databases, "sm_owned", DropAsync, cancellation.Token));

        // Assert
        Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(observed).CancellationToken);
        Assert.Equal(0, attempts);
        Assert.Equal(["sm_owned"], databases);
    }

    /// <summary>
    /// Allows an idempotent retry when the database was dropped but completion was not acknowledged.
    /// </summary>
    [Fact]
    public async Task Release_UncertainDropCompletionRetainsOwnershipUntilAbsentDatabaseRetrySucceeds()
    {
        // Arrange
        var databases = new List<string> { "sm_owned" };
        var expected = new InvalidOperationException("Injected lost drop acknowledgement.");
        var databaseExists = true;
        var attempts = 0;
        var physicalDrops = 0;

        Task DropAsync(
            string database,
            CancellationToken cancellationToken
        )
        {
            attempts++;

            if (!databaseExists)
            {
                return Task.CompletedTask;
            }

            databaseExists = false;
            physicalDrops++;

            return Task.FromException(expected);
        }

        // Act
        var observed = await Record.ExceptionAsync(() => SqlServerContainerFixture.ReleaseOwnedDatabaseAsync(
            databases, "sm_owned", DropAsync, CancellationToken.None));
        var ownershipAfterUncertainCompletion = databases.ToArray();
        await SqlServerContainerFixture.ReleaseOwnedDatabaseAsync(databases, "sm_owned", DropAsync,
            CancellationToken.None);

        // Assert
        Assert.Same(expected, observed);
        Assert.Equal(["sm_owned"], ownershipAfterUncertainCompletion);
        Assert.False(databaseExists);
        Assert.Equal(2, attempts);
        Assert.Equal(1, physicalDrops);
        Assert.Empty(databases);
    }

    /// <summary>
    /// Removes successful drops individually and retries only unfinished final-cleanup entries.
    /// </summary>
    [Fact]
    public async Task Cleanup_PartialFailureRetainsOnlyUnfinishedDatabasesForRetry()
    {
        // Arrange
        var databases = new List<string> { "sm_first", "sm_second", "sm_third" };
        var expected = new InvalidOperationException("Injected second database drop failure.");
        var attempts = new List<string>();

        Task DropAsync(
            string database,
            CancellationToken cancellationToken
        )
        {
            attempts.Add(database);

            return attempts.Count == 2 ? Task.FromException(expected) : Task.CompletedTask;
        }

        // Act
        var observed = await Record.ExceptionAsync(() =>
            SqlServerContainerFixture.DropOwnedDatabasesAsync(databases, DropAsync, CancellationToken.None));
        var ownershipAfterFailure = databases.ToArray();
        await SqlServerContainerFixture.DropOwnedDatabasesAsync(databases, DropAsync, CancellationToken.None);

        // Assert
        Assert.Same(expected, observed);
        Assert.Equal(["sm_second", "sm_third"], ownershipAfterFailure);
        Assert.Equal(["sm_first", "sm_second", "sm_second", "sm_third"], attempts);
        Assert.Empty(databases);
    }

    /// <summary>
    /// Retains a cancelled drop and unattempted databases without restoring successful prior drops.
    /// </summary>
    [Fact]
    public async Task Cleanup_PartialCancellationRetainsOnlyUnfinishedDatabasesForRetry()
    {
        // Arrange
        var databases = new List<string> { "sm_first", "sm_second", "sm_third" };
        using var cancellation = new CancellationTokenSource();
        var attempts = new List<string>();

        Task DropAsync(
            string database,
            CancellationToken cancellationToken
        )
        {
            attempts.Add(database);

            if (attempts.Count == 2)
            {
                cancellation.Cancel();

                return Task.FromCanceled(cancellationToken);
            }

            return Task.CompletedTask;
        }

        // Act
        var observed = await Record.ExceptionAsync(() =>
            SqlServerContainerFixture.DropOwnedDatabasesAsync(databases, DropAsync, cancellation.Token));
        var ownershipAfterCancellation = databases.ToArray();
        await SqlServerContainerFixture.DropOwnedDatabasesAsync(databases, DropAsync, CancellationToken.None);

        // Assert
        Assert.Equal(cancellation.Token,
            Assert.IsAssignableFrom<OperationCanceledException>(observed).CancellationToken);
        Assert.Equal(["sm_second", "sm_third"], ownershipAfterCancellation);
        Assert.Equal(["sm_first", "sm_second", "sm_second", "sm_third"], attempts);
        Assert.Empty(databases);
    }

    /// <summary>
    /// Does not execute database cleanup when there are no outstanding owned databases.
    /// </summary>
    [Fact]
    public async Task Cleanup_EmptyOwnershipDoesNotInvokeDrop()
    {
        // Arrange
        var databases = new List<string>();
        var attempts = 0;

        Task DropAsync(
            string database,
            CancellationToken cancellationToken
        )
        {
            attempts++;

            return Task.CompletedTask;
        }

        // Act
        await SqlServerContainerFixture.DropOwnedDatabasesAsync(databases, DropAsync, CancellationToken.None);

        // Assert
        Assert.Equal(0, attempts);
        Assert.Empty(databases);
    }

    /// <summary>
    /// Guards both SQL cleanup statements so an already physically absent owned database can be retried.
    /// </summary>
    [Fact]
    public void DropCommand_GuardsAlterAndDropAgainstAnAbsentDatabase()
    {
        // Arrange
        const string database = "sm_owned";

        // Act
        var commandText = SqlServerContainerFixture.DropDatabaseCommandText(database);

        // Assert
        Assert.Equal("IF DB_ID(N'sm_owned') IS NOT NULL BEGIN "
            + "ALTER DATABASE [sm_owned] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; "
            + "DROP DATABASE [sm_owned]; END;", commandText);
    }
}
