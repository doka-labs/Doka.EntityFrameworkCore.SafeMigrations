namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>
/// Protects deterministic native admission coordination without using a database or elapsed deadlines.
/// </summary>
public sealed class MySqlMigrationAdmissionTests
{
    /// <summary>Remains pending until the actual admission signal, without requiring migration completion.</summary>
    [Fact]
    public async Task ActualAdmissionReleasesTheWait()
    {
        // Arrange
        var migration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var wait = MySqlLifecycleIntegrationTests.AwaitMigrationAdmissionAsync(
            migration.Task,
            admission.Task,
            CancellationToken.None);

        var pendingBeforeAdmission = !wait.IsCompleted;
        admission.SetResult();
        await wait;

        // Assert
        Assert.True(pendingBeforeAdmission);
        Assert.False(migration.Task.IsCompleted);
        Assert.True(wait.IsCompletedSuccessfully);
    }

    /// <summary>Preserves the producer failure rather than replacing it with an observer timeout.</summary>
    [Fact]
    public async Task FailedMigrationCannotFakeAdmission()
    {
        // Arrange
        var expected = new InvalidOperationException("owned-migrator-failure");
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var failure = await Record.ExceptionAsync(() => MySqlLifecycleIntegrationTests.AwaitMigrationAdmissionAsync(
            Task.FromException(expected),
            admission.Task,
            CancellationToken.None));

        // Assert
        Assert.Same(expected, failure);
        Assert.False(admission.Task.IsCompleted);
    }

    /// <summary>Rejects successful producer completion that never reached the required admission checkpoint.</summary>
    [Fact]
    public async Task SuccessfulCompletionCannotFakeAdmission()
    {
        // Arrange
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var failure = await Record.ExceptionAsync(() => MySqlLifecycleIntegrationTests.AwaitMigrationAdmissionAsync(
            Task.CompletedTask,
            admission.Task,
            CancellationToken.None));

        // Assert
        Assert.Equal("The migrator completed without entering its target DDL.",
            Assert.IsType<InvalidOperationException>(failure)
                .Message);

        Assert.False(admission.Task.IsCompleted);
    }

    /// <summary>
    /// Observes either failed owner immediately even when its independent peer is still held at a gate.
    /// </summary>
    /// <param name="firstFails">Whether the first rather than second owner fails before admission.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedOwnerIsNotHiddenBehindItsPendingPeer(
        bool firstFails
    )
    {
        // Arrange
        var expected = new InvalidOperationException("independent-owner-failure");
        var peer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admissions = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = Task.FromException(expected);

        // Act
        var failure = await Record.ExceptionAsync(() => MySqlLifecycleIntegrationTests.AwaitMigrationAdmissionsAsync(
            firstFails
                ? failed
                : peer.Task,
            firstFails
                ? peer.Task
                : failed,
            admissions.Task,
            CancellationToken.None));

        var peerStillPending = !peer.Task.IsCompleted;
        peer.SetResult();
        await peer.Task;

        // Assert
        Assert.Same(expected, failure);
        Assert.True(peerStillPending);
        Assert.False(admissions.Task.IsCompleted);
    }
}
