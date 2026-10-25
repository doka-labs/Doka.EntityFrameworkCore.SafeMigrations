namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    /// <summary>Captures each qualified required-add catalog status once before any row-dependent guard.</summary>
    /// <param name="connection">The already-open analysis connection.</param>
    /// <param name="plans">The current operation window, updated only with immutable operation-local facts.</param>
    /// <param name="states">The preceding selected-database and prerequisite results.</param>
    /// <param name="maximumPayloadBytes">The qualified aggregate transport bound.</param>
    /// <param name="commandTimeout">The caller's configured command timeout.</param>
    /// <param name="operationOffset">The window's original migration-stream offset.</param>
    /// <param name="cancellationToken">The token that cancels construction, transport and reading.</param>
    /// <returns>A task completed after every selected status has been validated and bound.</returns>
    private async Task FindRequiredColumnAddFactsAsync(
        DbConnection connection,
        MySqlSafeMigrationRuntimePlan[] plans,
        MySqlShortCircuitState?[] states,
        int maximumPayloadBytes,
        int? commandTimeout,
        int operationOffset,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<int>? ordinals = null;
        // WHY: Select required-add plans using their matching short-circuit state ordinals.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < plans.Length; ordinal++)
        {
            if (states[ordinal] is null
                && !plans[ordinal].IsStaticallyUnsupported
                && plans[ordinal].RequiredColumnAddPlan is not null)
            {
                (ordinals ??= []).Add(ordinal);
            }
        }

        if (ordinals is null)
        {
            return;
        }

        var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
        var trailerBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);

        // WHY: Status is catalog-only and cannot authorize row safety. Resolve
        // its repeated ownership CASE once, after identity and prerequisites,
        // while subsequent NULL/empty probes retain their separate barriers.
        // Render through the existing window snapshot when one is available;
        // this phase must not reintroduce MariaDB's live-view materializations.
        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection, ordinals.Count, maximumPayloadBytes, commandTimeout,
            (command, offset) =>
            {
                var parameterizer = new MySqlCatalogQueryParameterizer(command, _typeMappingSource);
                var selections = new List<string>(Math.Min(
                    SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement, ordinals.Count - offset));

                var sqlBytes = trailerBytes;
                while (offset + selections.Count < ordinals.Count
                    && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                {
                    var ordinal = ordinals[offset + selections.Count];
                    var checkpoint = parameterizer.Capture();
                    var status = plans[ordinal].RenderRequiredColumnAddStatusExpression(parameterizer.Add);
                    var selection = $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, ({status})";

                    var selectionBytes = Encoding.UTF8.GetByteCount(selection)
                        + (selections.Count == 0 ? 0 : separatorBytes);

                    var payloadBytes = checked(sqlBytes + selectionBytes + parameterizer.Utf8PayloadBytes);
                    if (SafeMigrationCatalogQueryLimits.Exceeded(
                            parameterizer.Count, payloadBytes, maximumPayloadBytes))
                    {
                        var parameterCount = parameterizer.Count;
                        parameterizer.Rollback(checkpoint);
                        if (selections.Count == 0)
                        {
                            throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                                operationOffset + ordinal, parameterCount, payloadBytes);
                        }

                        break;
                    }

                    selections.Add(selection);
                    sqlBytes += selectionBytes;
                }

                command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
                    + SafeMigrationCatalogQueryLimits.Trailer;

                return new SafeMigrationCatalogProbeStatement(
                    selections.Count, parameterizer.Utf8PayloadBytes, operationOffset + ordinals[offset]);
            },
            async (reader, offset, count, token) =>
            {
                var row = 0;
                while (await reader.ReadAsync(token))
                {
                    if (row >= count || reader.GetInt32(0) != ordinals[offset + row] || reader.IsDBNull(1))
                    {
                        throw new InvalidOperationException(
                            "The MySQL required-add facts query returned an invalid result.");
                    }

                    var ordinal = ordinals[offset + row];
                    var plan = plans[ordinal];
                    var add = plan.RequiredColumnAddPlan
                        ?? throw new InvalidOperationException("The MySQL required-add fact lost its owned plan.");

                    var binding = RequiredColumnAddStatusBinding(reader.GetString(1));
                    plans[ordinal] = plan with { RequiredColumnAddPlan = add with { StatusBinding = binding } };
                    row++;
                }

                if (row != count)
                {
                    throw new InvalidOperationException(
                        "The MySQL required-add facts query returned an inconsistent row count.");
                }
            },
            cancellationToken);
    }

    /// <summary>Maps a closed catalog status to a fixed SQL literal without accepting server-provided SQL.</summary>
    /// <param name="status">The exact provider-owned status returned by the catalog-only expression.</param>
    /// <returns>The fixed SQL literal used by this operation's later analysis phases.</returns>
    /// <exception cref="InvalidOperationException">
    /// The result is outside the provider's closed status contract.
    /// </exception>
    internal static string RequiredColumnAddStatusBinding(string? status) => status switch
    {
        "complete" => "'complete'",
        "fresh" => "'fresh'",
        "staged" => "'staged'",
        "conflict" => "'conflict'",
        "unavailable" => "'unavailable'",
        "set_null" => "'set_null'",
        "ordinary" => "'ordinary'",
        _ => throw new InvalidOperationException("The MySQL required-add facts query returned an unknown status."),
    };
}
