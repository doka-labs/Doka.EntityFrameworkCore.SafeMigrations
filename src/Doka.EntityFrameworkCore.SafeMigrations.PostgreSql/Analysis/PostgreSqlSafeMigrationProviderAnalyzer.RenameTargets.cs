namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationProviderAnalyzer
{
    /// <summary>Captures destination occupancy without changing idempotent missing-source classification.</summary>
    /// <param name="connection">The identity-qualified open analysis connection.</param>
    /// <param name="operations">The immutable ordered safe operation stream.</param>
    /// <param name="results">The corresponding live classifications, published only after complete capture.</param>
    /// <param name="commandTimeout">The caller's configured command timeout.</param>
    /// <param name="cancellationToken">The token cancelling construction, transport and reading.</param>
    /// <returns>A task completed after every eligible destination has been verified.</returns>
    internal async Task PopulateRenameTargetEvidenceAsync(
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis[] results,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<int>? candidates = null;
        // WHY: Keep rename candidates aligned with their original analysis ordinals.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            if (operations[ordinal].Intent is RenameTableIntent or RenameColumnIntent or RenameIndexIntent
                && results[ordinal].ObservedState is not (SafeMigrationObservedState.Unsupported
                    or SafeMigrationObservedState.PrerequisiteMissing))
            {
                (candidates ??= []).Add(ordinal);
            }
        }

        if (candidates is null)
        {
            return;
        }

        var targets = new (bool Target, bool? Intermediate, bool? SourceSupported)[candidates.Count];
        var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
        var trailerBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);

        // WHY: Missing source remains a valid replay result, but an earlier
        // accepted creation can establish that source during ordered projection.
        // Its destination therefore needs independent namespace-bound evidence.
        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection,
            candidates.Count,
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes,
            commandTimeout,
            (command, offset) =>
            {
                var parameters = new PostgreSqlCatalogQueryParameters(command, _typeMappingSource);
                var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
                    _typeMappingSource, _sqlGenerationHelper, parameters.AddString, parameters.Add);

                var selections = new List<string>(Math.Min(
                    SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement, candidates.Count - offset));

                var sqlBytes = trailerBytes;
                while (offset + selections.Count < candidates.Count
                    && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                {
                    var ordinal = candidates[offset + selections.Count];
                    var intent = operations[ordinal].Intent;
                    var checkpoint = parameters.Capture();
                    var intermediate = intent is RenameTableIntent table
                        ? builder.BuildRenameIntermediateTargetExistsExpression(table) ?? "NULL::boolean"
                        : "NULL::boolean";

                    var sourceSupported = intent is RenameIndexIntent index
                        ? builder.BuildIndexRenameSourceSupportedExpression(index)
                        : "NULL::boolean";

                    var selection = $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, "
                        + builder.BuildRenameDestinationExistsExpression(intent)
                        + $", {intermediate}, {sourceSupported}";

                    var selectionBytes = Encoding.UTF8.GetByteCount(selection)
                        + (selections.Count == 0 ? 0 : separatorBytes);

                    var payloadBytes = checked(sqlBytes + selectionBytes + parameters.Utf8PayloadBytes);
                    if (SafeMigrationCatalogQueryLimits.Exceeded(parameters.Count, payloadBytes,
                        SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes))
                    {
                        var parameterCount = parameters.Count;
                        parameters.Rollback(checkpoint);
                        if (selections.Count == 0)
                        {
                            throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                                ordinal, parameterCount, payloadBytes);
                        }

                        break;
                    }

                    selections.Add(selection);
                    sqlBytes += selectionBytes;
                }

                command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
                    + SafeMigrationCatalogQueryLimits.Trailer;

                return new SafeMigrationCatalogProbeStatement(
                    selections.Count, parameters.Utf8PayloadBytes, candidates[offset]);
            },
            async (reader, offset, count, token) =>
            {
                if (reader.FieldCount != 4)
                {
                    throw InvalidRenameTargetResult();
                }

                var row = 0;
                while (await reader.ReadAsync(token))
                {
                    if (row >= count
                        || reader.GetInt32(0) != candidates[offset + row])
                    {
                        throw InvalidRenameTargetResult();
                    }

                    var intent = operations[candidates[offset + row]].Intent;
                    var hasIntermediate = intent is RenameTableIntent table
                        && table.NewName is not null && table.NewName != table.Name
                        && table.NewSchema is not null && table.NewSchema != table.Schema;

                    var hasSourceSupported = intent is RenameIndexIntent;
                    if (reader.GetValue(1) is not bool target
                        || reader.IsDBNull(2) == hasIntermediate
                        || hasIntermediate && reader.GetValue(2) is not bool
                        || reader.IsDBNull(3) == hasSourceSupported
                        || hasSourceSupported && reader.GetValue(3) is not bool)
                    {
                        throw InvalidRenameTargetResult();
                    }

                    targets[offset + row] = (target, hasIntermediate ? reader.GetBoolean(2) : null,
                        hasSourceSupported ? reader.GetBoolean(3) : null);

                    row++;
                }

                if (row != count)
                {
                    throw InvalidRenameTargetResult();
                }
            },
            cancellationToken);

        // WHY: A later malformed result or cancellation cannot expose a
        // partially certified destination set to ordered projection.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < candidates.Count; index++)
        {
            var ordinal = candidates[index];
            results[ordinal] = results[ordinal].WithRenameTargetExists(
                targets[index].Target, targets[index].Intermediate);
            if (targets[index].SourceSupported is { } supported)
            {
                // WHY: Different can describe a wrong owner or attached
                // constraint index, not just an occupied destination. A later
                // target drop discharges only the latter conflict.
                results[ordinal] = results[ordinal].WithRenameSourceSupported(supported);
            }
        }
    }

    /// <summary>Rejects destination protocol faults without exposing identifiers or data.</summary>
    /// <returns>The low-cardinality ownership error.</returns>
    private static InvalidOperationException InvalidRenameTargetResult() =>
        new("The PostgreSQL rename destination query returned malformed or incomplete evidence.");
}
