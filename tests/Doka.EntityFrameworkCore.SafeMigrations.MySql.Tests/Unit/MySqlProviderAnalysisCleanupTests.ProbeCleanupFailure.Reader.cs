namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlProviderAnalysisCleanupTests
{
    /// <summary>Retains actual reader work and disposal without changing the native result shape.</summary>
    private sealed class ProbeCleanupFailureReader : DbDataReader
    {
        private readonly DbDataReader _inner;
        private readonly ProbeCleanupFailureState _state;

        /// <summary>Borrows one native transport-profile reader until the analyzer actually disposes it.</summary>
        /// <param name="inner">The actual deterministic two-column transport reader.</param>
        /// <param name="state">The exact work and resource-disposal failure controls.</param>
        internal ProbeCleanupFailureReader(
            DbDataReader inner,
            ProbeCleanupFailureState state
        )
        {
            _inner = inner;
            _state = state;
        }

        /// <inheritdoc />
        public override object this[
            int ordinal
        ] => _inner[ordinal];

        /// <inheritdoc />
        public override object this[
            string name
        ] => _inner[name];

        /// <inheritdoc />
        public override int Depth => _inner.Depth;

        /// <inheritdoc />
        public override int FieldCount => _inner.FieldCount;

        /// <inheritdoc />
        public override bool HasRows => _inner.HasRows;

        /// <inheritdoc />
        public override bool IsClosed => _inner.IsClosed;

        /// <inheritdoc />
        public override int RecordsAffected => _inner.RecordsAffected;

        /// <inheritdoc />
        public override bool GetBoolean(
            int ordinal
        ) => _inner.GetBoolean(ordinal);

        /// <inheritdoc />
        public override byte GetByte(
            int ordinal
        ) => _inner.GetByte(ordinal);

        /// <inheritdoc />
        public override char GetChar(
            int ordinal
        ) => _inner.GetChar(ordinal);

        /// <inheritdoc />
        public override string GetDataTypeName(
            int ordinal
        ) => _inner.GetDataTypeName(ordinal);

        /// <inheritdoc />
        public override DateTime GetDateTime(
            int ordinal
        ) => _inner.GetDateTime(ordinal);

        /// <inheritdoc />
        public override decimal GetDecimal(
            int ordinal
        ) => _inner.GetDecimal(ordinal);

        /// <inheritdoc />
        public override double GetDouble(
            int ordinal
        ) => _inner.GetDouble(ordinal);

        /// <inheritdoc />
        public override IEnumerator GetEnumerator() => _inner.GetEnumerator();

        /// <inheritdoc />
        public override Type GetFieldType(
            int ordinal
        ) => _inner.GetFieldType(ordinal);

        /// <inheritdoc />
        public override float GetFloat(
            int ordinal
        ) => _inner.GetFloat(ordinal);

        /// <inheritdoc />
        public override Guid GetGuid(
            int ordinal
        ) => _inner.GetGuid(ordinal);

        /// <inheritdoc />
        public override short GetInt16(
            int ordinal
        ) => _inner.GetInt16(ordinal);

        /// <inheritdoc />
        public override int GetInt32(
            int ordinal
        ) => _inner.GetInt32(ordinal);

        /// <inheritdoc />
        public override long GetInt64(
            int ordinal
        ) => _inner.GetInt64(ordinal);

        /// <inheritdoc />
        public override string GetName(
            int ordinal
        ) => _inner.GetName(ordinal);

        /// <inheritdoc />
        public override int GetOrdinal(
            string name
        ) => _inner.GetOrdinal(name);

        /// <inheritdoc />
        public override string GetString(
            int ordinal
        ) => _inner.GetString(ordinal);

        /// <inheritdoc />
        public override object GetValue(
            int ordinal
        ) => _inner.GetValue(ordinal);

        /// <inheritdoc />
        public override int GetValues(
            object[] values
        ) => _inner.GetValues(values);

        /// <inheritdoc />
        public override bool IsDBNull(
            int ordinal
        ) => _inner.IsDBNull(ordinal);

        /// <inheritdoc />
        public override bool NextResult() => _inner.NextResult();

        /// <inheritdoc />
        public override bool Read() => _inner.Read();

        /// <inheritdoc />
        public override void Close() => _inner.Close();

        /// <inheritdoc />
        public override long GetBytes(
            int ordinal,
            long dataOffset,
            byte[]? buffer,
            int bufferOffset,
            int length
        ) => _inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);

        /// <inheritdoc />
        public override long GetChars(
            int ordinal,
            long dataOffset,
            char[]? buffer,
            int bufferOffset,
            int length
        ) => _inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);

        /// <inheritdoc />
        public override Task<bool> ReadAsync(
            CancellationToken cancellationToken
        )
        {
            _state.ObserveWork(
                _state.BatchMarker is null && _state.EnvironmentMarker is null
                    ? "transport_reader"
                    : _state.Stage,
                cancellationToken);

            return _inner.ReadAsync(cancellationToken);
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            try
            {
                await _inner.DisposeAsync();
                if (_state.BatchMarker is null)
                {
                    await _state.DisposeTargetAsync("reader");
                }
            }
            finally
            {
                await base.DisposeAsync();
            }
        }
    }
}
