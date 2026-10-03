namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Transports independent, already-qualified read-only probes under the catalog limits.</summary>
internal static class SafeMigrationCatalogProbeBatch
{
    /// <summary>Reads one ordered result set per bounded provider-built probe statement.</summary>
    /// <param name="connection">The open provider or compatible wrapper connection.</param>
    /// <param name="operationCount">The number of candidate slots in this analysis phase.</param>
    /// <param name="maximumPayloadBytes">The provider-qualified aggregate UTF-8 payload bound.</param>
    /// <param name="commandTimeout">The timeout applied to the native batch or sequential commands.</param>
    /// <param name="buildStatement">Builds the next statement at the supplied candidate offset.</param>
    /// <param name="readStatement">Validates and consumes that statement's exact candidate/result contract.</param>
    /// <param name="cancellationToken">The token that cancels construction, execution, and reading.</param>
    /// <param name="transaction">The caller-owned transaction to forward without changing its lifetime.</param>
    /// <param name="maximumParameters">The provider's stricter aggregate parameter bound, when required.</param>
    /// <returns>A task completed after every statement and result set has been verified.</returns>
    public static async Task ReadAsync(
        DbConnection connection,
        int operationCount,
        int maximumPayloadBytes,
        int? commandTimeout,
        Func<SafeMigrationCatalogCommand, int, SafeMigrationCatalogProbeStatement> buildStatement,
        Func<DbDataReader, int, int, CancellationToken, Task> readStatement,
        CancellationToken cancellationToken,
        DbTransaction? transaction = null,
        int maximumParameters = SafeMigrationCatalogQueryLimits.MaximumParameters
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(buildStatement);
        ArgumentNullException.ThrowIfNull(readStatement);
        ArgumentOutOfRangeException.ThrowIfNegative(operationCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumPayloadBytes, SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumParameters);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumParameters, SafeMigrationCatalogQueryLimits.MaximumParameters);

        cancellationToken.ThrowIfCancellationRequested();

        var offset = 0;
        while (offset < operationCount)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout, transaction);
            var statements = new List<(int Offset, int Count)>(
                SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch);

            var parameters = 0;
            var payloadBytes = 0;
            while (batch.Count < SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch
                   && offset < operationCount)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var command = batch.CreateCommand();
                var statement = buildStatement(command, offset);
                if (statement.OperationCount <= 0
                    || statement.OperationCount > SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement
                    || statement.OperationCount > operationCount - offset
                    || statement.ParameterPayloadBytes < 0
                    || statement.FirstOrdinal < 0
                    || string.IsNullOrEmpty(command.CommandText))
                {
                    throw new InvalidOperationException("The catalog probe builder returned an invalid statement.");
                }

                var statementPayloadBytes = checked(
                    Encoding.UTF8.GetByteCount(command.CommandText) + statement.ParameterPayloadBytes);

                if (command.Parameters.Count > maximumParameters
                    || SafeMigrationCatalogQueryLimits.Exceeded(
                        command.Parameters.Count, statementPayloadBytes, maximumPayloadBytes))
                {
                    throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                        statement.FirstOrdinal, command.Parameters.Count, statementPayloadBytes);
                }

                if (parameters + command.Parameters.Count > maximumParameters
                    || SafeMigrationCatalogQueryLimits.Exceeded(
                        parameters + command.Parameters.Count,
                        checked(payloadBytes + statementPayloadBytes),
                        maximumPayloadBytes))
                {
                    // WHY: Candidate SQL and evidence are immutable within this
                    // phase. Rebuild the unsubmitted statement in the next batch,
                    // never cache live results or cross a prerequisite/data barrier.
                    batch.RemoveLastCommand(command);

                    break;
                }

                statements.Add((offset, statement.OperationCount));
                offset += statement.OperationCount;
                parameters += command.Parameters.Count;
                payloadBytes += statementPayloadBytes;
            }

            var resultSet = 0;
            await batch.ForEachResultSetAsync(
                async (reader, token) =>
                {
                    if (resultSet >= statements.Count)
                    {
                        throw new InvalidOperationException(
                            "The catalog probe transport returned an invalid result-set count.");
                    }

                    var statement = statements[resultSet];
                    await readStatement(reader, statement.Offset, statement.Count, token);
                    resultSet++;
                },
                cancellationToken);

            if (resultSet != statements.Count)
            {
                throw new InvalidOperationException(
                    "The catalog probe transport returned an inconsistent result-set count.");
            }
        }
    }
}

/// <summary>Describes one provider-built statement without retaining its SQL or evidence values.</summary>
/// <param name="OperationCount">The number of candidate slots consumed by this statement.</param>
/// <param name="ParameterPayloadBytes">The provider's UTF-8 payload estimate for the bound parameters.</param>
/// <param name="FirstOrdinal">The original operation ordinal used for bounded-query diagnostics.</param>
internal readonly record struct SafeMigrationCatalogProbeStatement(
    int OperationCount,
    int ParameterPayloadBytes,
    int FirstOrdinal
);
