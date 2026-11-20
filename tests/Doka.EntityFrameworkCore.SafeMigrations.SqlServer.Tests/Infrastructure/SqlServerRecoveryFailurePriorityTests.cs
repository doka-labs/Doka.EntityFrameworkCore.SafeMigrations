namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Exercises protected recovery when the primary exception rejects diagnostic attachments.</summary>
public sealed class SqlServerRecoveryFailurePriorityTests
{
    /// <summary>Unavailable diagnostic storage cannot interrupt native async close or fallback disposal.</summary>
    /// <param name="cancellation">Whether the original provider failure represents cancellation.</param>
    /// <param name="readOnlyData">Whether Data returns a read-only dictionary instead of throwing.</param>
    /// <param name="closeFails">Whether recovery must drain physical disposal after close fails.</param>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task IdentityRecoveryAsync_HostileData_DrainsPhysicalBoundaryAndPreservesPrimary(
        bool cancellation,
        bool readOnlyData,
        bool closeFails
    )
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource();
        var original = HostileFailure(cancellation, readOnlyData, cancellationSource.Token);
        await using var session = Connection(closeFails, held: true);
        await using var context = SqlServerCleanupOwnershipTests.CreateContext(session);
        var analyzer = SqlServerCleanupOwnershipTests.CreateAnalyzer(context);
        var dependencies = context.GetService<MigrationsSqlGeneratorDependencies>();
        var originalCommand = new SqlServerIdentityCleanupOwnershipTests.FailurePriorityCommand(
            dependencies,
            original,
            cancellation
                ? cancellationSource.Cancel
                : null);

        var command = new SqlServerSafeMigrationIdentityInsertCommand(
            originalCommand,
            "SET IDENTITY_INSERT [dbo].[roles] OFF;",
            dependencies,
            analyzer);

        bool entered;
        bool returnedBeforeRelease;
        Exception? observed;

        // Act
        var execution = Record.ExceptionAsync(() => command.ExecuteNonQueryAsync(
            context.GetService<IRelationalConnection>(),
            cancellationToken: cancellationSource.Token));

        try
        {
            await Task.WhenAny(session.Entered.Task, execution);
            entered = session.Entered.Task.IsCompletedSuccessfully;
            returnedBeforeRelease = execution.IsCompleted;
        }
        finally
        {
            session.Release.TrySetResult();
            observed = await execution;
        }

        var quarantine = Record.Exception(() => analyzer.ValidateContext(context));

        // Assert
        Assert.True(entered);
        Assert.False(returnedBeforeRelease);
        Assert.Same(original, observed);
        Assert.Equal(1, originalCommand.ExecutionCount);
        Assert.Equal(1, session.CloseCount);
        Assert.Equal(closeFails
                ? 1
                : 0,
            session.ConnectionDisposeCount);

        Assert.Equal(1, session.CommandDisposeCount);
        Assert.Equal(CancellationToken.None, session.CleanupToken);
        Assert.Equal(30, session.CleanupCommandTimeout);
        Assert.IsType<InvalidOperationException>(quarantine);
    }

    /// <summary>The synchronous recovery path has the same failure priority without blocking async fake work.</summary>
    /// <param name="cancellation">Whether the original provider failure represents cancellation.</param>
    /// <param name="readOnlyData">Whether Data returns a read-only dictionary instead of throwing.</param>
    /// <param name="closeFails">Whether recovery must attempt physical disposal after close fails.</param>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void IdentityRecovery_HostileData_PreservesPrimaryAndAttemptsPhysicalRecovery(
        bool cancellation,
        bool readOnlyData,
        bool closeFails
    )
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource();
        var original = HostileFailure(cancellation, readOnlyData, cancellationSource.Token);
        using var session = Connection(closeFails, held: false);
        using var context = SqlServerCleanupOwnershipTests.CreateContext(session);
        var analyzer = SqlServerCleanupOwnershipTests.CreateAnalyzer(context);
        var dependencies = context.GetService<MigrationsSqlGeneratorDependencies>();
        var originalCommand = new SqlServerIdentityCleanupOwnershipTests.FailurePriorityCommand(dependencies, original);
        var command = new SqlServerSafeMigrationIdentityInsertCommand(
            originalCommand,
            "SET IDENTITY_INSERT [dbo].[roles] OFF;",
            dependencies,
            analyzer);

        // Act
        var observed = Record.Exception(() => command.ExecuteNonQuery(context.GetService<IRelationalConnection>()));
        var quarantine = Record.Exception(() => analyzer.ValidateContext(context));

        // Assert
        Assert.Same(original, observed);
        Assert.Equal(1, originalCommand.ExecutionCount);
        Assert.Equal(1, session.CloseCount);
        Assert.Equal(closeFails
                ? 1
                : 0,
            session.ConnectionDisposeCount);

        Assert.Equal(1, session.CommandDisposeCount);
        Assert.Equal(CancellationToken.None, session.CleanupToken);
        Assert.Equal(30, session.CleanupCommandTimeout);
        Assert.IsType<InvalidOperationException>(quarantine);
    }

    /// <summary>A failed temporary DROP retains the original exception after draining physical recovery.</summary>
    /// <param name="cancellation">Whether the original work failure is cancellation.</param>
    /// <param name="readOnlyData">Whether Data is read-only instead of throwing.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TemporaryCleanup_HostileData_PreservesPrimaryAfterPhysicalRecovery(
        bool cancellation,
        bool readOnlyData
    )
    {
        // Arrange
        var original = HostileFailure(cancellation, readOnlyData, CancellationToken.None);
        await using var session = Connection(closeFails: true, held: true);
        await using var context = SqlServerCleanupOwnershipTests.CreateContext(session);
        var analyzer = SqlServerCleanupOwnershipTests.CreateAnalyzer(context);
        bool entered;
        bool returnedBeforeRelease;
        Exception? observed;

        // Act
        var execution = Record.ExceptionAsync(async () =>
        {
            try
            {
                throw original;
            }
            finally
            {
                await analyzer.CleanupTemporaryScopeAsync(
                    session,
                    null,
                    "#doka_sm_inventory_00000000000000000000000000000001",
                    73,
                    original);
            }
        });

        try
        {
            await Task.WhenAny(session.Entered.Task, execution);
            entered = session.Entered.Task.IsCompletedSuccessfully;
            returnedBeforeRelease = execution.IsCompleted;
        }
        finally
        {
            session.Release.TrySetResult();
            observed = await execution;
        }

        var quarantine = Record.Exception(() => analyzer.ValidateContext(context));

        // Assert
        Assert.True(entered);
        Assert.False(returnedBeforeRelease);
        Assert.Same(original, observed);
        Assert.Equal(1, session.CloseCount);
        Assert.Equal(1, session.ConnectionDisposeCount);
        Assert.Equal(1, session.CommandDisposeCount);
        Assert.Equal(CancellationToken.None, session.CleanupToken);
        Assert.Equal(30, session.CleanupCommandTimeout);
        Assert.IsType<InvalidOperationException>(quarantine);
    }

    /// <summary>Creates a failed recovery command and an optionally held native physical boundary.</summary>
    private static SqlServerCleanupOwnershipConnection Connection(
        bool closeFails,
        bool held
    ) => new()
    {
        CleanupFailure = new InvalidOperationException("Recovery SQL failed."),
        CloseFailure = closeFails
            ? new InvalidOperationException("Physical close failed.")
            : null,
        HeldBoundary = held switch
        {
            false => null,
            true when closeFails => "connection-dispose",
            true => "close",
        },
    };

    /// <summary>
    /// Creates a primary error whose diagnostic extension point cannot accept status or failure evidence.
    /// </summary>
    private static Exception HostileFailure(
        bool cancellation,
        bool readOnlyData,
        CancellationToken cancellationToken
    ) => cancellation
        ? new HostileCancellationException(readOnlyData, cancellationToken)
        : new HostileProviderException(readOnlyData);

    /// <summary>Rejects diagnostic mutation through either a throwing getter or a read-only dictionary.</summary>
    private sealed class HostileProviderException : Exception
    {
        private readonly IDictionary _data = new Dictionary<string, object?>().AsReadOnly();
        private readonly bool _readOnlyData;

        /// <summary>Configures only the diagnostic storage failure mode, not the original work identity.</summary>
        internal HostileProviderException(
            bool readOnlyData
        ) : base("Original provider failure.")
        {
            _readOnlyData = readOnlyData;
        }

        /// <inheritdoc />
        public override IDictionary Data => _readOnlyData
            ? _data
            : throw new InvalidOperationException("Exception data is unavailable.");
    }

    /// <summary>Preserves cancellation identity while independently refusing diagnostic attachments.</summary>
    private sealed class HostileCancellationException : OperationCanceledException
    {
        private readonly IDictionary _data = new Dictionary<string, object?>().AsReadOnly();
        private readonly bool _readOnlyData;

        /// <summary>Retains the exact operation token and selected diagnostic storage failure mode.</summary>
        internal HostileCancellationException(
            bool readOnlyData,
            CancellationToken cancellationToken
        ) : base("Original cancellation.", cancellationToken)
        {
            _readOnlyData = readOnlyData;
        }

        /// <inheritdoc />
        public override IDictionary Data => _readOnlyData
            ? _data
            : throw new InvalidOperationException("Exception data is unavailable.");
    }
}
