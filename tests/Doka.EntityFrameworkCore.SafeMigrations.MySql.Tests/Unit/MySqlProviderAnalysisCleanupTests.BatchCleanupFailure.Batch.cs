namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlProviderAnalysisCleanupTests
{
    /// <summary>Preserves native batch execution while exposing its actual read and disposal boundaries.</summary>
    private sealed class BatchCleanupFailureBatch : DbBatch
    {
        private readonly DbBatch _inner;
        private readonly ProbeCleanupFailureState _state;

        /// <summary>Borrows the deterministic native batch for this exact analyzer-owned lifetime.</summary>
        /// <param name="inner">The real deterministic batch supplying ordered result sets.</param>
        /// <param name="state">The exact classifier marker, read failure and native disposal controls.</param>
        internal BatchCleanupFailureBatch(
            DbBatch inner,
            ProbeCleanupFailureState state
        )
        {
            _inner = inner;
            _state = state;
        }

        /// <inheritdoc />
        public override int Timeout { get => _inner.Timeout; set => _inner.Timeout = value; }

        /// <inheritdoc />
        protected override DbConnection? DbConnection { get => _inner.Connection; set => _inner.Connection = value; }

        /// <inheritdoc />
        protected override DbTransaction? DbTransaction
        {
            get => _inner.Transaction;
            set => _inner.Transaction = value;
        }

        /// <inheritdoc />
        protected override DbBatchCommandCollection DbBatchCommands => _inner.BatchCommands;

        /// <inheritdoc />
        protected override DbBatchCommand CreateDbBatchCommand() => _inner.CreateBatchCommand();

        /// <inheritdoc />
        public override void Cancel() => _inner.Cancel();

        /// <inheritdoc />
        public override void Prepare() => _inner.Prepare();

        /// <inheritdoc />
        public override Task PrepareAsync(
            CancellationToken cancellationToken = default
        ) => _inner.PrepareAsync(cancellationToken);

        /// <inheritdoc />
        public override int ExecuteNonQuery() => throw new InvalidOperationException("Read-only classifier only.");

        /// <inheritdoc />
        public override Task<int> ExecuteNonQueryAsync(
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Read-only classifier only.");

        /// <inheritdoc />
        public override object? ExecuteScalar() => throw new InvalidOperationException("Read-only classifier only.");

        /// <inheritdoc />
        public override Task<object?> ExecuteScalarAsync(
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Read-only classifier only.");

        /// <inheritdoc />
        protected override DbDataReader ExecuteDbDataReader(
            CommandBehavior behavior
        ) => throw new InvalidOperationException("Use actual asynchronous native work.");

        /// <inheritdoc />
        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken
        )
        {
            var reader = await _inner.ExecuteReaderAsync(behavior, cancellationToken);

            return BatchCommands.Any(command => _state.TargetsBatchStatement(command.CommandText))
                ? new ProbeCleanupFailureReader(reader, _state)
                : reader;
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            try
            {
                await _inner.DisposeAsync();
                if (BatchCommands.Any(command => _state.TargetsBatchStatement(command.CommandText)))
                {
                    await _state.DisposeTargetAsync("batch");
                }
            }
            finally
            {
                await base.DisposeAsync();
            }
        }
    }
}
