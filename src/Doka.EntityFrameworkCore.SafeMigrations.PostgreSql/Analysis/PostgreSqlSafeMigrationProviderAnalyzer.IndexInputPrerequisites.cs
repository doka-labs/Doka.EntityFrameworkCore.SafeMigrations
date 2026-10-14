namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationProviderAnalyzer
{
    /// <summary>Qualifies only independently eligible Missing-name candidates before binding any data SQL.</summary>
    private async Task FindIndexInputStatesAsync(DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations, SafeMigrationObservedState?[] states,
        List<int> ordinals, int? commandTimeout, CancellationToken cancellationToken)
    {
        // WHY: Embedding every semantic-alias/type plan into a clean Matching
        // prerequisite packet makes PostgreSQL compile thousands of unconsumed
        // expressions. Exact occupied names need ordinary classification, not
        // a CREATE capability proof. Only the first stage's proven owner/member
        // candidates reach this bounded metadata fence before any data probe.
        var names = PostgreSqlIndexPrerequisiteCertificates.ReserveNames(operations);
        await SafeMigrationCatalogProbeBatch.ReadAsync(connection, ordinals.Count,
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes, commandTimeout,
            (command, offset) => BuildIndexInputPrerequisiteStatement(command, operations, ordinals, names, offset),
            async (reader, offset, count, token) =>
            {
                if (reader.FieldCount != 2)
                {
                    throw InvalidLivePrerequisiteEvidence();
                }

                var next = offset;
                while (await reader.ReadAsync(token))
                {
                    if (next >= offset + count || reader.GetValue(0) is not int ordinal
                        || ordinal != ordinals[next])
                    {
                        throw InvalidLivePrerequisiteEvidence();
                    }

                    var state = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (state is not (null or "index_input_operator_class" or "index_input_binding_unproven"))
                    {
                        throw InvalidLivePrerequisiteEvidence();
                    }

                    if (state is not null)
                    {
                        (_indexInputShortCircuitCodes ??= []).Add(ordinal, state);
                        states[ordinal] = state == "index_input_operator_class"
                            ? SafeMigrationObservedState.Unsupported : SafeMigrationObservedState.PrerequisiteMissing;
                    }

                    next++;
                }

                if (next != offset + count)
                {
                    throw InvalidLivePrerequisiteEvidence();
                }
            }, cancellationToken);
    }

    /// <summary>
    /// Accounts for the full lazy WITH prefix under existing parameter, payload and statement bounds.
    /// </summary>
    private SafeMigrationCatalogProbeStatement BuildIndexInputPrerequisiteStatement(SafeMigrationCatalogCommand command,
        IReadOnlyList<SafeMigrationOperation> operations, List<int> ordinals, IReadOnlyList<string> names,
        int offset)
    {
        var parameters = new PostgreSqlCatalogQueryParameters(command, _typeMappingSource);
        var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(_typeMappingSource, _sqlGenerationHelper,
            parameters.AddString, parameters.Add);

        var certificates = new PostgreSqlIndexPrerequisiteCertificates(names);
        var selections = new List<string>();
        var sqlBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);
        while (offset + selections.Count < ordinals.Count
            && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
        {
            var ordinal = ordinals[offset + selections.Count];
            var parameterCheckpoint = parameters.Capture();
            var certificateCheckpoint = certificates.Count;
            var intent = (EnsureIndexIntent)operations[ordinal].Intent;
            var state = builder.BuildIndexInputPrerequisiteState(intent, certificates);
            var selection = "SELECT " + ordinal.ToString(CultureInfo.InvariantCulture) + ", (" + state + ")";
            var nextBytes = sqlBytes + Encoding.UTF8.GetByteCount(selection)
                + (selections.Count == 0 ? 0 : Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator));

            var payload = nextBytes + parameters.Utf8PayloadBytes + certificates.Utf8PrefixBytes;
            if (SafeMigrationCatalogQueryLimits.Exceeded(parameters.Count, payload,
                SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes))
            {
                var rejectedParameters = parameters.Count;
                parameters.Rollback(parameterCheckpoint);
                certificates.Rollback(certificateCheckpoint);
                if (selections.Count == 0)
                {
                    throw SafeMigrationCatalogQueryLimits.OversizedOperation(ordinal, rejectedParameters, payload);
                }

                break;
            }

            selections.Add(selection);
            sqlBytes = nextBytes;
        }

        command.CommandText = certificates.RenderPrefix()
            + string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
            + SafeMigrationCatalogQueryLimits.Trailer;

        return new SafeMigrationCatalogProbeStatement(selections.Count, parameters.Utf8PayloadBytes, offset);
    }
}
