namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
    /// <summary>Captures independent member destination presence within the validated catalog scope.</summary>
    /// <param name="connection">The open metadata-visible and identifier-qualified analysis connection.</param>
    /// <param name="transaction">The existing caller transaction, forwarded without ownership changes.</param>
    /// <param name="operations">The immutable ordered safe operation stream.</param>
    /// <param name="results">The completed live classifications, enriched only after complete capture.</param>
    /// <param name="commandTimeout">The caller's configured command timeout.</param>
    /// <param name="cancellationToken">The token cancelling construction, transport and reading.</param>
    /// <returns>A task completed after every eligible destination has been validated.</returns>
    internal async Task PopulateMemberRenameTargetEvidenceAsync(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis[] results,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<int>? candidates = null;

        // WHY: Select rename candidates from their corresponding analysis slots.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            if (operations[ordinal].Intent is RenameColumnIntent or RenameIndexIntent
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

        var targets = new (bool Target, bool? SourceSupported)[candidates.Count];

        // WHY: Earlier accepted creation can make a live Missing source present.
        // The destination remains a separate physical name; query it directly,
        // using the runtime's owner-local catalog semantics, not CLR case folding.
        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection,
            candidates.Count,
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes,
            commandTimeout,
            (
                command,
                offset
            ) =>
            {
                var count = Math.Min(
                    SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement,
                    candidates.Count - offset);

                var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
                var parameterBytes = 0;

                var builder = new SqlServerSafeMigrationCatalogSqlBuilder(
                    _typeMappingSource,
                    _sqlGenerationHelper,
                    AddParameter);

                var selections = new string[count];
                // WHY: Keep bounded selection slots aligned with original rename ordinals.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var index = 0; index < count; index++)
                {
                    var ordinal = candidates[offset + index];
                    var sourceSupported = operations[ordinal].Intent is RenameIndexIntent rename
                        ? builder.BuildIndexRenameSourceSupportedExpression(rename)
                        : "CAST(NULL AS bit)";

                    selections[index] = $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, "
                        + builder.BuildMemberRenameDestinationExpression(operations[ordinal].Intent)
                        + $", {sourceSupported}";
                }

                command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
                    + SafeMigrationCatalogQueryLimits.Trailer;

                return new SafeMigrationCatalogProbeStatement(count, parameterBytes, candidates[offset]);

                string AddParameter(
                    string value
                )
                {
                    if (parameters.TryGetValue(value, out var existing))
                    {
                        return existing;
                    }

                    var parameter = command.CreateParameter();
                    parameter.ParameterName = "@doka_sm_rename_"
                        + parameters.Count.ToString(CultureInfo.InvariantCulture);
                    parameter.DbType = System.Data.DbType.String;
                    parameter.Value = value;
                    command.Parameters.Add(parameter);
                    parameters.Add(value, parameter.ParameterName);
                    parameterBytes = checked(parameterBytes
                        + Math.Max(Encoding.UTF8.GetByteCount(value), value.Length * 2)
                        + 64);

                    return parameter.ParameterName;
                }
            },
            async (reader, offset, count, token) =>
            {
                if (reader.FieldCount != 3)
                {
                    throw InvalidMemberRenameTargetResult();
                }

                var row = 0;
                while (await reader.ReadAsync(token))
                {
                    var hasSourceSupported = row < count
                        && operations[candidates[offset + row]].Intent is RenameIndexIntent;

                    if (row >= count
                        || reader.GetInt32(0) != candidates[offset + row]
                        || reader.GetValue(1) is not bool target
                        || reader.IsDBNull(2) == hasSourceSupported
                        || (hasSourceSupported && reader.GetValue(2) is not bool))
                    {
                        throw InvalidMemberRenameTargetResult();
                    }

                    targets[offset + row] = (target, hasSourceSupported ? reader.GetBoolean(2) : null);
                    row++;
                }

                if (row != count)
                {
                    throw InvalidMemberRenameTargetResult();
                }
            },
            cancellationToken,
            transaction,
            SqlServerCatalogParameterBindings.MaximumParameters);

        // WHY: A later malformed result or cancellation must not expose
        // partial destination facts to ordered projection.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < candidates.Count; index++)
        {
            var ordinal = candidates[index];
            results[ordinal] = results[ordinal].WithRenameTargetExists(targets[index].Target);
            if (targets[index].SourceSupported is { } supported)
            {
                // WHY: A target drop cannot turn a constraint-owned backing
                // index into an independently renameable source.
                results[ordinal] = results[ordinal]
                    .WithRenameSourceSupported(supported);
            }
        }
    }

    /// <summary>Rejects destination protocol faults without exposing physical identifiers or data.</summary>
    /// <returns>The low-cardinality ownership error.</returns>
    private static InvalidOperationException InvalidMemberRenameTargetResult() => new(
        "The SQL Server member rename destination query returned malformed or incomplete evidence.");
}
