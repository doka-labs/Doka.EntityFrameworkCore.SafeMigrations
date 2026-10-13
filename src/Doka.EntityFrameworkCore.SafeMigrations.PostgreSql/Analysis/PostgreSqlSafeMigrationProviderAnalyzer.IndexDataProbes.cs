namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationProviderAnalyzer
{
    /// <summary>Reads duplicate-row evidence only after independent metadata and native input qualification.</summary>
    internal async Task PopulateIndexDataProofsAsync(DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations, SafeMigrationProviderAnalysis[] results,
        int? commandTimeout, CancellationToken cancellationToken)
    {
        List<int>? ordinals = null;
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (results[ordinal].ObservedState is SafeMigrationObservedState.Missing
                    or SafeMigrationObservedState.Different
                && operations[ordinal].Intent is EnsureIndexIntent { Definition.Unique: true } index
                && CanBindLiveIndexDataProbe(index.Definition))
            {
                (ordinals ??= []).Add(ordinal);
            }
        }

        if (ordinals is null)
        {
            return;
        }

        var blocked = new bool[ordinals.Count];
        await SafeMigrationCatalogProbeBatch.ReadAsync(connection, ordinals.Count,
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes, commandTimeout,
            (command, offset) => BuildIndexDataProbeStatement(command, operations, ordinals, offset),
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
                        || ordinal != ordinals[next] || reader.GetValue(1) is not bool duplicate)
                    {
                        throw InvalidLivePrerequisiteEvidence();
                    }

                    blocked[next++] = duplicate;
                }

                if (next != offset + count)
                {
                    throw InvalidLivePrerequisiteEvidence();
                }
            }, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        // WHY: No partially read row evidence may escape a malformed or
        // cancelled packet. A type/opclass proof permits binding, never rows.
        // Exact-name Different keeps its ordinary conflict while the code
        // retains duplicate evidence for an accepted ordered replacement.
        for (var index = 0; index < ordinals.Count; index++)
        {
            if (!blocked[index])
            {
                continue;
            }

            var ordinal = ordinals[index];
            var previous = results[ordinal];
            var different = previous.ObservedState == SafeMigrationObservedState.Different;
            results[ordinal] = new SafeMigrationProviderAnalysis(
                different ? SafeMigrationObservedState.Different : SafeMigrationObservedState.DataBlocked,
                previous.RepairCapability, previous.PostconditionSatisfied,
                different ? "index_replacement_data_blocked" : "classified_data_blocked",
                previous.OperationalImpact, previous.Differences)
            {
                RequiresLiveDataProof = previous.RequiresLiveDataProof,
                IsInvariantUnsupported = previous.IsInvariantUnsupported,
                MatchedObjectName = previous.MatchedObjectName,
            };
        }
    }

    /// <summary>Uses only actual native input types, never authored future or rejected column alternatives.</summary>
    private bool CanBindLiveIndexDataProbe(ExpectedIndexDefinition definition)
    {
        if (_indexInputProofs?.TryGetValue(IndexInputIdentity(0, definition, null, null),
                out var method) != true || method != true)
        {
            return false;
        }

        foreach (var key in definition.Keys)
        {
            if (PostgreSqlSafeMigrationCatalogSqlBuilder.IndexPhysicalMember(key) is not { } member)
            {
                // WHY: Computed expression result binding remains the existing
                // native execution contract; its operand OID is not its type.
                continue;
            }

            if (!IndexMemberTableMatches(definition, member)
                || _livePrerequisiteColumns?.TryGetValue(LiveColumnKey(
                    definition.Table, definition.Schema, member.Name), out var storage) != true
                || _indexInputProofs.TryGetValue(IndexInputIdentity(storage.TypeOid,
                    definition, key, member), out var proof) != true || proof != true)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Preserves existing bounded statement, parameter, payload, timeout and transport contracts.</summary>
    private SafeMigrationCatalogProbeStatement BuildIndexDataProbeStatement(SafeMigrationCatalogCommand command,
        IReadOnlyList<SafeMigrationOperation> operations, List<int> ordinals, int offset)
    {
        var parameters = new PostgreSqlCatalogQueryParameters(command, _typeMappingSource);
        var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(_typeMappingSource, _sqlGenerationHelper,
            parameters.AddString, parameters.Add);

        var selections = new List<string>();
        var sqlBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);
        while (offset + selections.Count < ordinals.Count
            && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
        {
            var ordinal = ordinals[offset + selections.Count];
            var checkpoint = parameters.Capture();
            var definition = ((EnsureIndexIntent)operations[ordinal].Intent).Definition;
            var selection = "SELECT " + ordinal.ToString(CultureInfo.InvariantCulture)
                + ", (" + builder.UniqueIndexDataBlocked(definition) + ")";

            var nextBytes = sqlBytes + Encoding.UTF8.GetByteCount(selection)
                + (selections.Count == 0 ? 0 : Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator));

            if (SafeMigrationCatalogQueryLimits.Exceeded(parameters.Count,
                nextBytes + parameters.Utf8PayloadBytes, SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes))
            {
                var rejectedParameters = parameters.Count;
                var rejectedBytes = nextBytes + parameters.Utf8PayloadBytes;
                parameters.Rollback(checkpoint);
                if (selections.Count == 0)
                {
                    throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                        ordinal, rejectedParameters, rejectedBytes);
                }

                break;
            }

            selections.Add(selection);
            sqlBytes = nextBytes;
        }

        command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
            + SafeMigrationCatalogQueryLimits.Trailer;

        return new SafeMigrationCatalogProbeStatement(selections.Count, parameters.Utf8PayloadBytes, ordinals[offset]);
    }
}
