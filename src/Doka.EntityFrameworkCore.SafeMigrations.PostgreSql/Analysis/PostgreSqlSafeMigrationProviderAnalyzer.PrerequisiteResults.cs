namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationProviderAnalyzer
{
    private async Task<List<int>?> ReadPrerequisiteBatchAsync(
        SafeMigrationCatalogBatch batch,
        SafeMigrationObservedState?[] states,
        List<int> selectedOrdinals,
        CancellationToken cancellationToken
    )
    {
        var rowsRead = 0;
        List<int>? indexInputOrdinals = null;
        await batch.ForEachResultSetAsync(
            async (reader, callbackCancellationToken) =>
            {
                while (await reader.ReadAsync(callbackCancellationToken))
                {
                    var resultOrdinal = reader.GetInt32(0);
                    if (rowsRead >= selectedOrdinals.Count
                        || resultOrdinal != selectedOrdinals[rowsRead])
                    {
                        throw new InvalidOperationException(
                            "The PostgreSQL SafeMigrations prerequisite classifier returned an invalid ordinal.");
                    }

                    var state = reader.IsDBNull(1)
                        ? null
                        : reader.GetString(1);

                    switch (state)
                    {
                        case "index_input_required":
                            (indexInputOrdinals ??= []).Add(resultOrdinal);
                            break;
                        case SchemaIdentityMismatchCode:
                            _schemaIdentityMismatchOrdinals.Add(resultOrdinal);
                            states[resultOrdinal] = SafeMigrationObservedState.Unsupported;
                            break;
                        case "index_input_operator_class" or "index_input_binding_unproven":
                            (_indexInputShortCircuitCodes ??= []).Add(resultOrdinal, state);
                            states[resultOrdinal] = state == "index_input_operator_class"
                                ? SafeMigrationObservedState.Unsupported
                                : SafeMigrationObservedState.PrerequisiteMissing;

                            break;
                        default:
                            states[resultOrdinal] = state is null
                                ? null
                                : ParseState(state);

                            break;
                    }

                    rowsRead++;
                }
            },
            cancellationToken);

        if (rowsRead != selectedOrdinals.Count)
        {
            throw new InvalidOperationException(
                "The PostgreSQL SafeMigrations prerequisite classifier returned an inconsistent row count.");
        }

        return indexInputOrdinals;
    }

    internal static async Task<Dictionary<int, string>?> ReadStateEvaluationGuardBatchAsync(
        SafeMigrationCatalogBatch batch,
        SafeMigrationObservedState?[] states,
        IReadOnlyList<StateEvaluationGuardRequest> selectedOrdinals,
        CancellationToken cancellationToken
    )
    {
        var row = 0;
        Dictionary<int, string>? reasons = null;
        await batch.ForEachResultSetAsync(
            async (reader, callbackCancellationToken) =>
            {
                while (await reader.ReadAsync(callbackCancellationToken))
                {
                    if (reader.FieldCount != 3
                        || row >= selectedOrdinals.Count
                        || reader.GetValue(0) is not int resultOrdinal
                        || resultOrdinal != selectedOrdinals[row].Ordinal)
                    {
                        throw new InvalidOperationException(
                            "The PostgreSQL SafeMigrations state-evaluation guard classifier "
                            + "returned an invalid ordinal.");
                    }

                    var request = selectedOrdinals[row];
                    if (reader.IsDBNull(1))
                    {
                        if (!reader.IsDBNull(2))
                        {
                            throw InvalidStateEvaluationGuardEvidence();
                        }

                        states[resultOrdinal] = null;
                    }
                    else
                    {
                        if (reader.GetValue(1) is not string stateValue
                            || reader.GetValue(2) is not string code
                            || (code != request.DefaultCode
                                && !(request.HasCapturedBinding
                                    && code is "captured_sql_identifier"
                                        or "captured_sql_type_binding"
                                        or "captured_sql_function_binding"
                                        or "captured_sql_operator_binding")))
                        {
                            throw InvalidStateEvaluationGuardEvidence();
                        }

                        var state = ParseState(stateValue);
                        if (code is "captured_sql_identifier"
                                or "captured_sql_type_binding"
                                or "captured_sql_function_binding"
                                or "captured_sql_operator_binding"
                            && state != SafeMigrationObservedState.Unsupported)
                        {
                            throw InvalidStateEvaluationGuardEvidence();
                        }

                        states[resultOrdinal] = state;
                        if (state == SafeMigrationObservedState.Unsupported)
                        {
                            (reasons ??= [])[resultOrdinal] = code;
                        }
                    }

                    row++;
                }
            },
            cancellationToken);

        if (row != selectedOrdinals.Count)
        {
            throw new InvalidOperationException(
                "The PostgreSQL SafeMigrations state-evaluation guard classifier returned "
                + "an inconsistent row count.");
        }

        return reasons;
    }

    /// <summary>
    /// Rejects malformed native proof rows without copying their untrusted contents into diagnostics.
    /// </summary>
    private static InvalidOperationException InvalidStateEvaluationGuardEvidence() => new(
        "The PostgreSQL SafeMigrations state-evaluation guard classifier "
        + "returned invalid capability evidence.");

    /// <summary>Scopes one ordered guard result to its exact expected bounded capability-code family.</summary>
    /// <param name="Ordinal">The immutable operation-stream ordinal.</param>
    /// <param name="DefaultCode">The existing reason retained when the earlier provider guard fails.</param>
    /// <param name="HasCapturedBinding">Whether native source keyword/type binding reasons are permitted.</param>
    internal readonly record struct StateEvaluationGuardRequest(
        int Ordinal,
        string DefaultCode,
        bool HasCapturedBinding
    );
}
