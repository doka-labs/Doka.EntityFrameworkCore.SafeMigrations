namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
    /// <summary>
    /// Reads one bounded immutable capture, grouping compatible classifiers without reordering projection.
    /// </summary>
    /// <param name="connection">The open analysis connection.</param>
    /// <param name="transaction">The active analysis transaction.</param>
    /// <param name="commandTimeout">The timeout applied to each catalog statement.</param>
    /// <param name="plans">The bounded capture, with null entries for already classified operations.</param>
    /// <param name="captureStart">The first original operation ordinal in this capture.</param>
    /// <param name="results">The result slots indexed by original migration ordinal.</param>
    /// <param name="cancellationToken">The token cancelling capture generation, transport, and reading.</param>
    internal static async Task ReadCatalogCaptureAsync(
        DbConnection connection,
        DbTransaction? transaction,
        int? commandTimeout,
        SqlServerSafeMigrationRuntimePlan?[] plans,
        int captureStart,
        SafeMigrationProviderAnalysis[] results,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(plans.Length,
            SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture);
        ArgumentOutOfRangeException.ThrowIfNegative(captureStart);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(captureStart, results.Length - plans.Length);
        cancellationToken.ThrowIfCancellationRequested();

        // WHY: These read-only classifiers have no cross-operation dependencies and use statement-local setup.
        // Partitioning the bounded capture avoids a transport split on every binding-mode change. Original
        // ordinals still own their result slots; the runner applies ordered projection afterwards, unchanged.
        var order = new int[plans.Length];
        var representatives = new int[plans.Length];
        var distinct = new Dictionary<SqlServerSafeMigrationRuntimePlan, int>(plans.Length);
        for (var index = 0; index < plans.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            representatives[index] = index;
            if (plans[index] is not { } plan)
            {
                continue;
            }

            // WHY: Analysis captures one immutable baseline before ordered projection. Exact plan equality
            // includes every physical, data, diagnostic and evidence facet, not merely the object name.
            // Coalesce only within this bounded capture; never retain a live result between invocations.
            if (distinct.TryGetValue(plan, out var representative))
            {
                representatives[index] = representative;
            }
            else
            {
                distinct.Add(plan, index);
            }
        }

        var count = 0;
        for (var mode = 0; mode < 2; mode++)
        {
            var delayed = mode == 1;
            var selected = SafeMigrationCatalogWorkOrder.Create(plans.Length,
                index => representatives[index] == index && plans[index] is { } plan
                    && RequiresDelayedCatalogBinding(plan) == delayed, cancellationToken);

            foreach (var index in selected)
            {
                order[count++] = index;
            }
        }

        var next = 0;
        while (next < count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            next = await ReadCatalogBatchAsync(connection, transaction, commandTimeout, plans,
                captureStart, order, count, next, results, cancellationToken);
        }

        for (var index = 0; index < plans.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (representatives[index] != index)
            {
                results[captureStart + index] = results[captureStart + representatives[index]];
            }
        }
    }

    /// <summary>Identifies classifiers that require isolated dynamic name binding or statement-local setup.</summary>
    private static bool RequiresDelayedCatalogBinding(SqlServerSafeMigrationRuntimePlan plan)
        => plan.RequiresDelayedBinding || plan.CatalogPreambleSql is not null;

    /// <summary>Reads one bounded transport batch and returns the next index in the stable classifier order.</summary>
    private static async Task<int> ReadCatalogBatchAsync(
        DbConnection connection,
        DbTransaction? transaction,
        int? commandTimeout,
        SqlServerSafeMigrationRuntimePlan?[] plans,
        int captureStart,
        int[] order,
        int count,
        int start,
        SafeMigrationProviderAnalysis[] results,
        CancellationToken cancellationToken
    )
    {
        await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout, transaction);
        var next = start;
        var payload = 0;
        var parameterCount = 0;
        var resultPlans = new List<(int Ordinal, SqlServerSafeMigrationRuntimePlan Plan)>(
            Math.Min(count - start, SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement
                * SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch));

        var resultSetSizes = new List<int>(resultPlans.Capacity);
        var payloadFull = false;
        while (next < count && !payloadFull
               && batch.Count < SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch)
        {
            var delayed = RequiresDelayedCatalogBinding(plans[order[next]]!);
            const string prefix = "";
            var trailer = delayed ? string.Empty : SafeMigrationCatalogQueryLimits.Trailer;
            var separator = delayed ? "\n" : SafeMigrationCatalogQueryLimits.Separator;
            var fixedPayload = Encoding.UTF8.GetByteCount(prefix) + Encoding.UTF8.GetByteCount(trailer);
            var statementPayload = fixedPayload;
            var separatorBytes = Encoding.UTF8.GetByteCount(separator);
            var selections = new List<string>(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement);
            var statementPlans = new List<(int Ordinal, SqlServerSafeMigrationRuntimePlan Plan)>(
                SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement);

            var statementParameters = 0;
            while (next < count && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var plan = plans[order[next]]!;
                if (RequiresDelayedCatalogBinding(plan) != delayed)
                {
                    break;
                }

                var ordinal = captureStart + order[next];
                var selection = delayed
                    ? BuildDelayedCatalogSelection(ordinal, plan, selections.Count)
                    : BuildCatalogSelection(ordinal, plan);

                var bytes = Encoding.UTF8.GetByteCount(selection);
                // WHY: Ordinal RPC metadata contributes to both bounds, including zero-source classifiers.
                // The full source-budget boundary uses a trusted integer dispatcher literal instead.
                var bindOrdinal = delayed && UsesDelayedOrdinalParameter(plan);
                var planParameters = plan.AnalysisParameters.Count + (bindOrdinal ? 1 : 0);
                var valueBytes = plan.AnalysisParameters.Sum(static value => value.PayloadBytes)
                    + (bindOrdinal ? 128 : 0);
                if (planParameters > SqlServerCatalogParameterBindings.MaximumParameters
                    || (long)fixedPayload + bytes + valueBytes
                    > SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes)
                {
                    throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                        ordinal, planParameters, checked(fixedPayload + bytes + valueBytes));
                }

                var addition = bytes + valueBytes + (selections.Count == 0 ? 0 : separatorBytes);
                if ((long)payload + statementPayload + addition
                    > SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes
                    || parameterCount + statementParameters + planParameters
                    > SqlServerCatalogParameterBindings.MaximumParameters)
                {
                    payloadFull = true;

                    break;
                }

                selections.Add(selection);
                statementPlans.Add((ordinal, plan));
                statementParameters += planParameters;
                resultPlans.Add((ordinal, plan));
                if (delayed)
                {
                    resultSetSizes.Add(1);
                }

                statementPayload += addition;
                next++;
            }

            if (selections.Count == 0)
            {
                break;
            }

            var command = batch.CreateCommand();
            command.CommandText = BuildCatalogCommandText(selections, prefix, separator, trailer);
            if (!delayed)
            {
                resultSetSizes.Add(selections.Count);
            }

            // WHY: EF's source mapping configures the real provider parameter, including converters
            // and SQL-specific facets. A factory command is never executed or attached to the batch.
            using var parameterFactory = statementParameters == 0 || command.SequentialCommand is not null
                ? null : connection.CreateCommand();

            for (var slot = 0; slot < statementPlans.Count; slot++)
            {
                var item = statementPlans[slot];
                if (delayed && UsesDelayedOrdinalParameter(item.Plan))
                {
                    // WHY: DbBatchCommand parameter creation is optional even when batching works.
                    // Use the same actual provider factory as source mappings, including native wrappers.
                    var ordinalParameter = (command.SequentialCommand ?? parameterFactory!).CreateParameter();
                    ordinalParameter.ParameterName = DelayedOrdinalParameterName(slot);
                    ordinalParameter.DbType = System.Data.DbType.Int32;
                    ordinalParameter.Value = item.Ordinal;
                    command.Parameters.Add(ordinalParameter);
                }

                for (var index = 0; index < item.Plan.AnalysisParameters.Count; index++)
                {
                    var value = item.Plan.AnalysisParameters[index];
                    command.Parameters.Add(value.Mapping.CreateParameter(
                        command.SequentialCommand ?? parameterFactory!,
                        delayed ? DelayedSourceParameterName(slot, index) : value.ExternalName,
                        value.Value, nullable: false));
                }
            }

            payload += statementPayload;
            parameterCount += statementParameters;
        }

        using var activity = SafeMigrationTelemetry.StartAnalysisStage("catalog-batch", resultPlans.Count);
        activity?.SetTag("safe_migrations.catalog.statement_count", batch.Count);
        activity?.SetTag("safe_migrations.catalog.parameter_count", parameterCount);
        activity?.SetTag("safe_migrations.catalog.payload_bytes", payload);

        var resultIndex = 0;
        var resultSetIndex = 0;
        await batch.ForEachResultSetAsync(async (reader, token) =>
        {
            if (resultSetIndex >= resultSetSizes.Count || reader.FieldCount != 9)
            {
                throw new InvalidOperationException("The SQL Server catalog batch returned an invalid result set.");
            }

            var expectedRows = resultSetSizes[resultSetIndex++];
            var rows = 0;
            while (await reader.ReadAsync(token))
            {
                if (++rows > expectedRows || resultIndex >= resultPlans.Count
                    || reader.GetInt32(0) != resultPlans[resultIndex].Ordinal)
                {
                    throw new InvalidOperationException("The SQL Server catalog batch returned an invalid ordinal.");
                }

                var expected = resultPlans[resultIndex];
                results[expected.Ordinal] = ReadAnalysis(reader, expected.Plan);
                resultIndex++;
            }

            // WHY: Total row counts alone could accept an empty classifier followed by one that
            // leaks two valid ordinals. Each isolated classifier must own exactly its one result set.
            if (rows != expectedRows)
            {
                throw new InvalidOperationException("The SQL Server catalog batch returned an inconsistent row count.");
            }
        }, cancellationToken);

        if (resultIndex != resultPlans.Count || resultSetIndex != resultSetSizes.Count)
        {
            throw new InvalidOperationException("The SQL Server catalog batch returned an inconsistent row count.");
        }

        return next;
    }

    /// <summary>Assembles an already bounded catalog statement without changing its SQL shape.</summary>
    internal static string BuildCatalogCommandText(
        IReadOnlyList<string> selections,
        string prefix,
        string separator,
        string trailer
    )
    {
        var length = checked(prefix.Length + trailer.Length
            + (Math.Max(0, selections.Count - 1) * separator.Length));

        for (var index = 0; index < selections.Count; index++)
        {
            length = checked(length + selections[index].Length);
        }

        // WHY: Joining first creates another complete SQL buffer. Fill the final string
        // directly; the caller has already enforced the independent UTF-8 payload limit.
        return string.Create(length, (selections, prefix, separator, trailer), static (destination, state) =>
        {
            state.prefix.AsSpan().CopyTo(destination);
            destination = destination[state.prefix.Length..];
            for (var index = 0; index < state.selections.Count; index++)
            {
                if (index > 0)
                {
                    state.separator.AsSpan().CopyTo(destination);
                    destination = destination[state.separator.Length..];
                }

                var selection = state.selections[index];
                selection.AsSpan().CopyTo(destination);
                destination = destination[selection.Length..];
            }

            state.trailer.AsSpan().CopyTo(destination);
        });
    }
}
