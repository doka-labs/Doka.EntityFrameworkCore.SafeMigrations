namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies identity recovery retains pending native work and the original execution failure.</summary>
public sealed class SqlServerIdentityCleanupOwnershipTests
{
    /// <summary>Recovery completes each driver boundary before releasing its owning command or session.</summary>
    /// <param name="boundary">The actual native operation retained by the controlled provider.</param>
    [Theory]
    [InlineData("execute")]
    [InlineData("command-dispose")]
    [InlineData("close")]
    [InlineData("connection-dispose")]
    public async Task ExecuteNonQueryAsync_PendingRecovery_RetainsOwnershipDespiteCallerCancellation(
        string boundary
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var original = new OperationCanceledException(cancellation.Token);
        await using var session = new SqlServerCleanupOwnershipConnection
        {
            HeldBoundary = boundary,
            CleanupFailure = boundary is "close" or "connection-dispose"
                ? new InvalidOperationException("Recovery SQL failed.")
                : null,
            CloseFailure = boundary == "connection-dispose"
                ? new InvalidOperationException("Physical close failed.")
                : null,
        };

        await using var context = SqlServerCleanupOwnershipTests.CreateContext(session);
        var analyzer = SqlServerCleanupOwnershipTests.CreateAnalyzer(context);
        var dependencies = context.GetService<MigrationsSqlGeneratorDependencies>();
        var originalCommand = new FailurePriorityCommand(dependencies, original, cancellation.Cancel);
        var command = new SqlServerSafeMigrationIdentityInsertCommand(
            originalCommand,
            "SET IDENTITY_INSERT [dbo].[roles] OFF;",
            dependencies,
            analyzer);

        bool enteredBoundary;
        bool returnedBeforeRelease;
        bool disposedWhileExecuting;
        Exception? observed;

        // Act
        var execution = Record.ExceptionAsync(() => command.ExecuteNonQueryAsync(
            context.GetService<IRelationalConnection>(),
            cancellationToken: cancellation.Token));

        try
        {
            await Task.WhenAny(session.Entered.Task, execution);
            enteredBoundary = session.Entered.Task.IsCompletedSuccessfully;
            returnedBeforeRelease = execution.IsCompleted;
            disposedWhileExecuting = boundary == "execute" && session.CommandDisposeCount != 0;
        }
        finally
        {
            session.Release.TrySetResult();
            observed = await execution;
        }

        // Assert
        Assert.True(enteredBoundary);
        Assert.False(returnedBeforeRelease);
        Assert.False(disposedWhileExecuting);
        Assert.Same(original, observed);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(CancellationToken.None, session.CleanupToken);
        Assert.True(session.ExecutionCompleted);
        Assert.Equal(1, session.CommandDisposeCount);
        Assert.Equal(1, originalCommand.ExecutionCount);
        if (boundary is "close" or "connection-dispose")
        {
            Assert.Equal(
                boundary == "close"
                    ? "session_closed"
                    : "session_disposed",
                original.Data[SqlServerSafeMigrationIdentityInsertCommand.RecoveryFailureDataKey]);

            Assert.IsType<InvalidOperationException>(Record.Exception(() => analyzer.ValidateContext(context)));
        }
    }

    /// <summary>A failed bootstrap's own disposal cannot hide the failure that prevented dispatch.</summary>
    [Fact]
    public async Task ExecuteNonQueryAsync_BootstrapAndDisposalFail_PreservesBootstrapFailure()
    {
        // Arrange
        var original = new InvalidOperationException("Bootstrap SQL failed.");
        var secondary = new InvalidOperationException("Bootstrap command disposal failed.");
        await using var session = new SqlServerCleanupOwnershipConnection
        {
            BootstrapFailure = original,
            CommandDisposeFailure = secondary,
            FailBootstrapDisposal = true,
        };

        await using var context = SqlServerCleanupOwnershipTests.CreateContext(session);
        var analyzer = SqlServerCleanupOwnershipTests.CreateAnalyzer(context);
        var dependencies = context.GetService<MigrationsSqlGeneratorDependencies>();
        var originalCommand = new FailurePriorityCommand(
            dependencies,
            new InvalidOperationException("The original command must not dispatch."));

        var command = new SqlServerSafeMigrationIdentityInsertCommand(
            originalCommand,
            "SET IDENTITY_INSERT [dbo].[roles] OFF;",
            dependencies,
            analyzer);

        // Act
        var observed = await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync(
            context.GetService<IRelationalConnection>(),
            cancellationToken: CancellationToken.None));

        // Assert
        Assert.Same(original, observed);
        Assert.Same(secondary, original.Data["SafeMigrations.AnalysisCommandDisposeException"]);
        Assert.Equal(0, originalCommand.ExecutionCount);
        Assert.Equal(CancellationToken.None, session.CleanupToken);
    }

    /// <summary>Faults the real migration-command boundary after optionally canceling its caller.</summary>
    internal sealed class FailurePriorityCommand : MigrationCommand
    {
        private readonly Exception _failure;
        private readonly Action? _onDispatch;

        /// <summary>Creates a provider command that reports the exact injected failure after dispatch.</summary>
        /// <param name="dependencies">The real EF command metadata services.</param>
        /// <param name="failure">The precise failure identity expected at the outer boundary.</param>
        /// <param name="onDispatch">An optional caller-cancellation action after original-command admission.</param>
        internal FailurePriorityCommand(
            MigrationsSqlGeneratorDependencies dependencies,
            Exception failure,
            Action? onDispatch = null
        ) : base(
            dependencies
                .CommandBuilderFactory
                .Create()
                .Build(),
            dependencies.CurrentContext.Context,
            dependencies.Logger)
        {
            _failure = failure;
            _onDispatch = onDispatch;
        }

        /// <summary>Gets the number of admitted original-command executions.</summary>
        internal int ExecutionCount { get; private set; }

        /// <inheritdoc />
        public override int ExecuteNonQuery(
            IRelationalConnection connection,
            IReadOnlyDictionary<string, object?>? parameterValues = null
        )
        {
            ExecutionCount++;
            _onDispatch?.Invoke();

            throw _failure;
        }

        /// <inheritdoc />
        public override Task<int> ExecuteNonQueryAsync(
            IRelationalConnection connection,
            IReadOnlyDictionary<string, object?>? parameterValues = null,
            CancellationToken cancellationToken = default
        )
        {
            ExecutionCount++;
            _onDispatch?.Invoke();

            return Task.FromException<int>(_failure);
        }
    }
}
