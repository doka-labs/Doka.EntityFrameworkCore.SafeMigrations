namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlProviderAnalysisCleanupTests
{
    /// <summary>Exposes native environment/close boundaries without starting a server or an observer timer.</summary>
    private sealed class EnvironmentCleanupFailureConnection : DbConnection
    {
        private ConnectionState _state;

        /// <summary>Gets the independent metadata failure.</summary>
        internal Exception? EnvironmentFailure { get; init; }

        /// <summary>Gets the independent command-creation failure.</summary>
        internal Exception? CommandFailure { get; init; }

        /// <summary>Gets the independent actual-close failure.</summary>
        internal Exception? CloseFailure { get; init; }

        /// <summary>Gets the action synchronized with metadata access, such as operation cancellation.</summary>
        internal Action? BeforeVersionRead { get; init; }

        /// <summary>Gets the callback synchronized with actual command creation.</summary>
        internal Action? BeforeCommand { get; init; }

        /// <summary>Gets the deterministic metadata command factory when successful inventory is required.</summary>
        internal Func<DbCommand>? CommandFactory { get; init; }

        /// <summary>Gets the optional actual native-batch factory for batch ownership counterprobes.</summary>
        internal Func<DbBatch>? BatchFactory { get; init; }

        /// <summary>Gets the actual native-close completion controlled by this test's owner.</summary>
        internal Task CloseCompletion { get; init; } = Task.CompletedTask;

        /// <summary>Gets the signal proving that native close has actually started.</summary>
        internal TaskCompletionSource CloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets the actual open token received from the analyzer.</summary>
        internal CancellationToken OpenToken { get; private set; }

        /// <summary>Gets the actual native close attempts.</summary>
        internal int CloseAttempts { get; private set; }

        /// <summary>Gets the number of native metadata getter calls.</summary>
        internal int VersionReads { get; private set; }

        /// <inheritdoc />
        [AllowNull]
        public override string ConnectionString { get; set; } =
            "Server=localhost;Database=environment_cleanup;AllowUserVariables=true;GuidFormat=Binary16";

        /// <inheritdoc />
        public override string Database => "environment_cleanup";

        /// <inheritdoc />
        public override string DataSource => "synthetic";

        /// <inheritdoc />
        public override string ServerVersion
        {
            get
            {
                VersionReads++;
                BeforeVersionRead?.Invoke();
                if (EnvironmentFailure is { } failure)
                {
                    throw failure;
                }

                return "8.4.13";
            }
        }

        /// <inheritdoc />
        public override ConnectionState State => _state;

        /// <inheritdoc />
        public override bool CanCreateBatch => BatchFactory is not null;

        /// <inheritdoc />
        protected override DbBatch CreateDbBatch() => BatchFactory?.Invoke()
            ?? throw new InvalidOperationException("This deterministic connection does not expose native batching.");

        /// <inheritdoc />
        public override void Open() => _state = ConnectionState.Open;

        /// <inheritdoc />
        public override Task OpenAsync(
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenToken = cancellationToken;
            Open();

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override void Close() => _state = ConnectionState.Closed;

        /// <inheritdoc />
        public override async Task CloseAsync()
        {
            CloseAttempts++;
            CloseStarted.TrySetResult();
            await CloseCompletion;
            if (CloseFailure is { } failure)
            {
                throw failure;
            }

            Close();
        }

        /// <inheritdoc />
        public override void ChangeDatabase(
            string databaseName
        ) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override DbTransaction BeginDbTransaction(
            IsolationLevel isolationLevel
        ) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override DbCommand CreateDbCommand()
        {
            BeforeCommand?.Invoke();
            if (CommandFailure is { } failure)
            {
                throw failure;
            }

            return CommandFactory?.Invoke()
                ?? throw new InvalidOperationException("Environment-only controls must not issue catalog commands.");
        }
    }

    /// <summary>Models a primary native exception that refuses additional diagnostic evidence.</summary>
    private sealed class ThrowingCleanupDataException : Exception
    {
        /// <summary>Gets the number of diagnostic attachment attempts.</summary>
        internal int DataAccesses { get; private set; }

        /// <inheritdoc />
        public override IDictionary Data
        {
            get
            {
                DataAccesses++;

                throw new InvalidOperationException("The native exception rejects diagnostic dictionary access.");
            }
        }
    }
}
