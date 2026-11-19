namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlProviderAnalysisCleanupTests
{
    /// <summary>Injects faults at actual scalar, reader-acquisition and command-disposal boundaries.</summary>
    private sealed class ProbeCleanupFailureCommand : DbCommand
    {
        private readonly DbCommand _inner;
        private readonly ProbeCleanupFailureState _state;

        /// <summary>Borrows the deterministic command until the analyzer actually disposes this wrapper.</summary>
        /// <param name="inner">The statement-aware command supplying real deterministic result shapes.</param>
        /// <param name="state">This invocation's exact failure and native readiness controls.</param>
        internal ProbeCleanupFailureCommand(
            DbCommand inner,
            ProbeCleanupFailureState state
        )
        {
            _inner = inner;
            _state = state;
        }

        /// <inheritdoc />
        [AllowNull]
        public override string CommandText { get => _inner.CommandText; set => _inner.CommandText = value; }

        /// <inheritdoc />
        public override int CommandTimeout { get => _inner.CommandTimeout; set => _inner.CommandTimeout = value; }

        /// <inheritdoc />
        public override CommandType CommandType { get => _inner.CommandType; set => _inner.CommandType = value; }

        /// <inheritdoc />
        public override bool DesignTimeVisible
        {
            get => _inner.DesignTimeVisible;
            set => _inner.DesignTimeVisible = value;
        }

        /// <inheritdoc />
        public override UpdateRowSource UpdatedRowSource
        {
            get => _inner.UpdatedRowSource;
            set => _inner.UpdatedRowSource = value;
        }

        /// <inheritdoc />
        protected override DbConnection? DbConnection { get => _inner.Connection; set => _inner.Connection = value; }

        /// <inheritdoc />
        protected override DbTransaction? DbTransaction
        {
            get => _inner.Transaction;
            set => _inner.Transaction = value;
        }

        /// <inheritdoc />
        protected override DbParameterCollection DbParameterCollection => _inner.Parameters;

        /// <inheritdoc />
        protected override DbParameter CreateDbParameter() => _inner.CreateParameter();

        /// <inheritdoc />
        public override void Cancel() => _inner.Cancel();

        /// <inheritdoc />
        public override void Prepare() => _inner.Prepare();

        /// <inheritdoc />
        public override int ExecuteNonQuery() => throw new InvalidOperationException("No DDL is permitted.");

        /// <inheritdoc />
        public override Task<int> ExecuteNonQueryAsync(
            CancellationToken cancellationToken
        )
        {
            _state.CleanupAttempts++;
            _state.CleanupToken = cancellationToken;

            return Task.FromException<int>(_state.CleanupQueryFailure
                ?? new InvalidOperationException("No proof-release command is expected."));
        }

        /// <inheritdoc />
        public override object? ExecuteScalar() => throw new InvalidOperationException("Use actual asynchronous work.");

        /// <inheritdoc />
        protected override DbDataReader ExecuteDbDataReader(
            CommandBehavior behavior
        ) => throw new InvalidOperationException("Use actual asynchronous work.");

        /// <inheritdoc />
        public override Task<object?> ExecuteScalarAsync(
            CancellationToken cancellationToken
        )
        {
            _state.ObserveWork("database", cancellationToken);

            return _inner.ExecuteScalarAsync(cancellationToken);
        }

        /// <inheritdoc />
        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken
        )
        {
            var transport = CommandText == "SELECT @@max_allowed_packet, @@lower_case_table_names;";
            if (transport)
            {
                _state.ObserveWork("transport_command", cancellationToken);
            }

            if (_state.TargetsEnvironmentStatement(CommandText))
            {
                _state.EnvironmentCommandText = CommandText;
                if (_state.Stage.EndsWith("_command", StringComparison.Ordinal))
                {
                    _state.ObserveWork(_state.Stage, cancellationToken);
                }
            }

            var reader = await _inner.ExecuteReaderAsync(behavior, cancellationToken);

            return (transport && _state.Stage == "transport_reader")
                || _state.TargetsBatchStatement(CommandText)
                || (_state.TargetsEnvironmentStatement(CommandText)
                    && _state.Stage.EndsWith("_reader", StringComparison.Ordinal))
                    ? new ProbeCleanupFailureReader(reader, _state)
                    : reader;
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            try
            {
                await _inner.DisposeAsync();
                if (_state.TargetsBatchStatement(CommandText)
                    || (_state.TargetsEnvironmentStatement(CommandText)
                        && _state.Stage.EndsWith("_command", StringComparison.Ordinal))
                    || (_state.Stage == "database" && CommandText == "SELECT DATABASE();")
                    || (_state.Stage == "transport_command"
                        && CommandText == "SELECT @@max_allowed_packet, @@lower_case_table_names;"))
                {
                    await _state.DisposeTargetAsync("command");
                }
                else if ((_state.Stage == "transport_reader"
                        && CommandText == "SELECT @@max_allowed_packet, @@lower_case_table_names;")
                    || (_state.TargetsEnvironmentStatement(CommandText)
                        && _state.Stage.EndsWith("_reader", StringComparison.Ordinal)))
                {
                    _state.CompletedStages.Add("command");
                    if (_state.CommandAfterReaderFailure is { } failure)
                    {
                        throw failure;
                    }
                }
            }
            finally
            {
                await base.DisposeAsync();
            }
        }
    }
}
