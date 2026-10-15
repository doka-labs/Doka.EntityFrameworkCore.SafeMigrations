namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    private const string BinaryDefaultStatementName = "doka_sm_binary_default_statement";
    private const string BinaryDefaultSqlVariable = "@doka_sm_binary_default_sql";

    /// <summary>Captures exact native literal defaults behind fresh metadata barriers in bounded batches.</summary>
    /// <param name="connection">The validated, already-open analysis connection.</param>
    /// <param name="plans">The current immutable operation window, updated with operation-local Boolean facts.</param>
    /// <param name="states">The independently completed database-identity and prerequisite results.</param>
    /// <param name="maximumPayloadBytes">The qualified aggregate transport limit.</param>
    /// <param name="commandTimeout">The caller's command timeout.</param>
    /// <param name="operationOffset">The original migration-stream position of this window.</param>
    /// <param name="cancellationToken">The token canceling query construction, execution and reading.</param>
    /// <returns>
    /// A task completed after every selected proof has been materialized and its SQL resources released.
    /// </returns>
    private async Task FindBinaryDefaultProofsAsync(
        DbConnection connection,
        MySqlSafeMigrationRuntimePlan[] plans,
        MySqlShortCircuitState?[] states,
        int maximumPayloadBytes,
        int? commandTimeout,
        int operationOffset,
        CancellationToken cancellationToken
    )
    {
        List<int>? ordinals = null;
        for (var ordinal = 0; ordinal < plans.Length; ordinal++)
        {
            if (states[ordinal] is null && !plans[ordinal].IsStaticallyUnsupported
                && plans[ordinal].BinaryDefaultProofs.Count > 0)
            {
                (ordinals ??= []).Add(ordinal);
            }
        }

        if (ordinals is null)
        {
            return;
        }

        var offset = 0;
        while (offset < ordinals.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            if (commandTimeout is { } timeout)
            {
                command.CommandTimeout = timeout;
            }

            var parameterizer = new MySqlCatalogQueryParameterizer(command, _typeMappingSource);
            var setup = new StringBuilder();
            var selections = new List<string>();
            var selected = new List<int>();
            var proofCount = 0;
            var sqlBytes = 0;
            var selectionBytes = 0;
            while (offset < ordinals.Count
                && selected.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ordinal = ordinals[offset];
                var plan = plans[ordinal];
                var checkpoint = parameterizer.Capture();
                var eligibility = new string[plan.BinaryDefaultProofs.Count];
                for (var proof = 0; proof < eligibility.Length; proof++)
                {
                    eligibility[proof] = plan.RenderBinaryDefaultEligibilityExpression(
                        plan.BinaryDefaultProofs[proof], parameterizer.Add);
                }

                var evaluation = MySqlSafeMigrationBinaryDefaultProof.BuildEvaluationSql(
                    plan.BinaryDefaultProofs, eligibility, "TRUE", BinaryDefaultStatementName,
                    BinaryDefaultSqlVariable, proofCount);

                var evaluationBytes = Encoding.UTF8.GetByteCount(evaluation);
                var candidateSelections = new string[eligibility.Length];
                var candidateSelectionBytes = 0;
                for (var proof = 0; proof < candidateSelections.Length; proof++)
                {
                    var index = proofCount + proof;
                    candidateSelections[proof] = "SELECT " + index.ToString(CultureInfo.InvariantCulture)
                        + ", " + MySqlSafeMigrationBinaryDefaultProof.Variable(index);
                    candidateSelectionBytes += Encoding.UTF8.GetByteCount(candidateSelections[proof])
                        + SafeMigrationCatalogQueryLimits.Separator.Length;
                }

                // WHY: The existing operation/parameter/packet limits govern
                // these scripts too. A fixed per-variable allowance bounds the
                // final cleanup without repeatedly materializing its growing
                // SET text while filling a batch.
                var prospectiveProofCount = checked(proofCount + eligibility.Length);
                var payload = checked(sqlBytes + evaluationBytes + selectionBytes + candidateSelectionBytes
                    + (prospectiveProofCount * 64) + 1024 + parameterizer.Utf8PayloadBytes);
                if (SafeMigrationCatalogQueryLimits.Exceeded(parameterizer.Count, payload, maximumPayloadBytes))
                {
                    var parameterCount = parameterizer.Count;
                    parameterizer.Rollback(checkpoint);
                    if (selected.Count == 0)
                    {
                        throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                            operationOffset + ordinal, parameterCount, payload);
                    }

                    break;
                }

                setup.Append(evaluation);
                selections.AddRange(candidateSelections);
                selected.Add(ordinal);
                proofCount = prospectiveProofCount;
                sqlBytes += evaluationBytes;
                selectionBytes += candidateSelectionBytes;
                offset++;
            }

            var reset = MySqlSafeMigrationBinaryDefaultProof.BuildResetSql(proofCount, 0, clear: true)
                + "SET " + BinaryDefaultSqlVariable + " = NULL;";

            command.CommandText = setup.Append(string.Join(SafeMigrationCatalogQueryLimits.Separator, selections))
                .Append(';').Append(reset).ToString();

            var completed = false;
            Exception? failure = null;
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (reader.FieldCount == 0 && await reader.NextResultAsync(cancellationToken))
                {
                    // WHY: Multi-statement commands can expose metadata-free
                    // status headers before the owned two-column result set.
                }

                if (reader.FieldCount != 2)
                {
                    throw new InvalidOperationException(
                        "The MySQL binary-default proof returned an invalid result shape.");
                }

                var expected = 0;
                foreach (var ordinal in selected)
                {
                    var plan = plans[ordinal];
                    var bindings = new MySqlSafeMigrationBinaryDefaultProof[plan.BinaryDefaultProofs.Count];
                    for (var proof = 0; proof < bindings.Length; proof++)
                    {
                        if (!await reader.ReadAsync(cancellationToken)
                            || reader.GetInt32(0) != expected || reader.IsDBNull(1))
                        {
                            throw new InvalidOperationException(
                                "The MySQL binary-default proof returned an invalid row.");
                        }

                        bindings[proof] = plan.BinaryDefaultProofs[proof] with
                        {
                            Binding = reader.GetBoolean(1) ? "TRUE" : "FALSE",
                        };

                        expected++;
                    }

                    plans[ordinal] = plan with { BinaryDefaultProofs = bindings };
                }

                if (await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException("The MySQL binary-default proof returned extra results.");
                }

                while (await reader.NextResultAsync(cancellationToken))
                {
                    if (reader.FieldCount > 0)
                    {
                        throw new InvalidOperationException(
                            "The MySQL binary-default proof returned extra result sets.");
                    }
                }

                // WHY: NextResult consumes the trailing same-command reset.
                // The successful path needs no cleanup network roundtrip;
                // cancellation/failure still independently releases an acquired
                // PREPARE and every result variable without the canceled token.
                completed = true;
            }
            catch (Exception exception)
            {
                failure = exception;

                throw;
            }
            finally
            {
                if (!completed && connection.State == System.Data.ConnectionState.Open)
                {
                    try
                    {
                        await using var cleanup = connection.CreateCommand();
                        if (commandTimeout is { } cleanupTimeout)
                        {
                            cleanup.CommandTimeout = cleanupTimeout;
                        }

                        cleanup.CommandText = "PREPARE " + BinaryDefaultStatementName + " FROM 'DO 0';"
                            + "DEALLOCATE PREPARE " + BinaryDefaultStatementName + ";" + reset;
                        await cleanup.ExecuteNonQueryAsync(CancellationToken.None);
                    }
                    catch (Exception exception) when (failure is not null)
                    {
                        failure.Data["SafeMigrations.BinaryDefaultProofCleanupException"] = exception;
                    }
                }
            }
        }
    }
}
