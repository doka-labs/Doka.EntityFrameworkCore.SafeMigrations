namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    private async Task<MySqlDataProbeResult?[]> ResolveDataProbeResultsAsync(
        DbConnection connection,
        SafeMigrationOperation[] operations,
        MySqlSafeMigrationRuntimePlan[] plans,
        MySqlShortCircuitState?[] shortCircuitStates,
        Dictionary<MySqlDataProbeContract, MySqlDataProbeResult> cache,
        Dictionary<MySqlDataProbeIdentity, bool> blockingDataCache,
        IModel model,
        MySqlExpectedUniqueIndexCatalog expectedUniqueIndexes,
        IReadOnlyDictionary<(string? Schema, string Table), SafeMigrationExpectedTableConstraints>
            expectedTableConstraints,
        MySqlBackfillConstraintCatalog backfillConstraints,
        int maximumPayloadBytes,
        int? commandTimeout,
        int operationOffset,
        CancellationToken cancellationToken
    )
    {
        var results = new MySqlDataProbeResult?[plans.Length];
        var candidates = new Dictionary<MySqlDataProbeContract, MySqlDataProbeCandidate>();
        // WHY: Keep data-probe plans, short-circuit states and result slots aligned by ordinal.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < plans.Length; ordinal++)
        {
            var probe = plans[ordinal].DataProbe;
            if (probe is null
                || shortCircuitStates[ordinal] is not null)
            {
                continue;
            }

            var identity = new MySqlDataProbeIdentity(probe.Table, probe.Column, probe.TargetLength);
            var contract = CreateDataProbeContract(identity, operations[ordinal]);
            if (cache.TryGetValue(contract, out var cached))
            {
                results[ordinal] = cached;

                continue;
            }

            candidates.TryAdd(
                contract,
                new MySqlDataProbeCandidate(identity, operations[ordinal], probe, operationOffset + ordinal, contract));
        }

        if (candidates.Count > 0)
        {
            var candidateArray = candidates.Values.ToArray();
            var transitionPlans = CapturePlans(
                candidateArray
                    .Select(static candidate => candidate.Operation)
                    .ToArray(),
                model,
                expectedUniqueIndexes,
                expectedTableConstraints,
                backfillConstraints,
                includeTransitionEvidence: true);

            var resolved = await FindRequiredDataProbesAsync(
                connection,
                candidateArray,
                transitionPlans,
                maximumPayloadBytes,
                commandTimeout,
                cancellationToken);

            // WHY: Exact source and target metadata belong to the operation,
            // not to the physical row probe. Distinct Ensure/Alter contracts
            // may share one length scan only after independent eligibility.
            await FindBlockingDataAsync(
                connection,
                candidateArray
                    .Where(candidate => resolved[candidate.Contract].IsRequired
                        && !blockingDataCache.ContainsKey(candidate.Identity))
                    .DistinctBy(static candidate => candidate.Identity),
                blockingDataCache,
                maximumPayloadBytes,
                commandTimeout,
                cancellationToken);

            foreach (var candidate in candidateArray)
            {
                var result = resolved[candidate.Contract];
                cache.Add(
                    candidate.Contract,
                    result with
                    {
                        IsBlocked = result.IsRequired && blockingDataCache[candidate.Identity],
                    });
            }
        }

        // WHY: Keep data-probe plans, short-circuit states and result slots aligned by ordinal.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < plans.Length; ordinal++)
        {
            var probe = plans[ordinal].DataProbe;
            if (probe is null
                || shortCircuitStates[ordinal] is not null)
            {
                continue;
            }

            var identity = new MySqlDataProbeIdentity(probe.Table, probe.Column, probe.TargetLength);
            results[ordinal] = cache.TryGetValue(CreateDataProbeContract(identity, operations[ordinal]), out var result)
                ? result
                : throw new InvalidOperationException("The MySQL narrowing probe did not resolve every candidate.");
        }

        return results;
    }

    private async Task<Dictionary<MySqlDataProbeContract, MySqlDataProbeResult>> FindRequiredDataProbesAsync(
        DbConnection connection,
        IReadOnlyList<MySqlDataProbeCandidate> candidates,
        MySqlSafeMigrationRuntimePlan[] transitionPlans,
        int maximumPayloadBytes,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var results = new Dictionary<MySqlDataProbeContract, MySqlDataProbeResult>(candidates.Count);
        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection,
            candidates.Count,
            maximumPayloadBytes,
            commandTimeout,
            (command, offset) =>
            {
                var count = Math.Min(
                    SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement,
                    candidates.Count - offset);

                var parameterizer = new MySqlCatalogQueryParameterizer(command, _typeMappingSource);
                var selections = new List<string>(count);
                // WHY: Bind each bounded candidate to the transition plan at the same window offset.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var index = 0; index < count; index++)
                {
                    var candidate = candidates[offset + index];
                    var transitionPlan = transitionPlans[offset + index];
                    var transitionProbe = transitionPlan.DataProbe
                        ?? throw new InvalidOperationException(
                            "The MySQL transition capture returned no data-probe plan.");

                    var transitionExpression = MySqlCatalogSqlTemplate.Render(
                        transitionProbe.TransitionInvariantExpression
                        ?? throw new InvalidOperationException(
                            "The MySQL transition capture returned no physical invariant."),
                        transitionPlan.ParameterValues,
                        parameterizer.Add);

                    var narrowingExpression = MySqlCatalogSqlTemplate.Render(
                        transitionProbe.NarrowingExpression,
                        transitionPlan.ParameterValues,
                        parameterizer.Add);

                    selections.Add(
                        $"SELECT {candidate.Ordinal.ToString(CultureInfo.InvariantCulture)}, "
                        + $"COALESCE(({transitionExpression}), FALSE), "
                        + $"COALESCE(({narrowingExpression}), FALSE)");
                }

                command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
                    + SafeMigrationCatalogQueryLimits.Trailer;

                return new SafeMigrationCatalogProbeStatement(
                    count,
                    parameterizer.Utf8PayloadBytes,
                    candidates[offset].Ordinal);
            },
            async (reader, offset, count, token) =>
            {
                var row = 0;
                while (await reader.ReadAsync(token))
                {
                    if (row >= count
                        || reader.GetInt32(0) != candidates[offset + row].Ordinal)
                    {
                        throw new InvalidOperationException(
                            "The MySQL narrowing prerequisite query returned an invalid ordinal.");
                    }

                    var transitionEligible = reader.GetBoolean(1);
                    var narrowing = reader.GetBoolean(2);

                    results.Add(
                        candidates[offset + row].Contract,
                        new MySqlDataProbeResult(
                            transitionEligible,
                            IsRequired: transitionEligible && narrowing,
                            IsBlocked: false));

                    row++;
                }

                if (row != count)
                {
                    throw new InvalidOperationException(
                        "The MySQL narrowing prerequisite query returned an inconsistent row count.");
                }
            },
            cancellationToken);

        return results;
    }

    private static async Task FindBlockingDataAsync(
        DbConnection connection,
        IEnumerable<MySqlDataProbeCandidate> requiredCandidates,
        Dictionary<MySqlDataProbeIdentity, bool> cache,
        int maximumPayloadBytes,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var statements = requiredCandidates
            .GroupBy(static candidate => candidate.Probe.DelimitedTable, StringComparer.Ordinal)
            .SelectMany(static group => group.Chunk(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement))
            .ToArray();

        // WHY: Eligibility has already been evaluated for every candidate.
        // Independent table reads may now share transport, but must never be
        // prepared in the earlier catalog phase that proves their relations exist.
        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection,
            statements.Length,
            maximumPayloadBytes,
            commandTimeout,
            (command, offset) =>
            {
                var candidates = statements[offset];
                var table = candidates[0].Probe.DelimitedTable;

                if (candidates.Length == 1)
                {
                    var probe = candidates[0].Probe;
                    command.CommandText = "SELECT EXISTS(SELECT 1 FROM "
                        + $"{table} WHERE {probe.DelimitedColumn} IS NOT NULL "
                        + $"AND CHAR_LENGTH({probe.DelimitedColumn}) "
                        + $"> {probe.TargetLength.ToString(CultureInfo.InvariantCulture)} LIMIT 1);";
                }
                else
                {
                    var selections = new List<string>(candidates.Length);
                    foreach (var candidate in candidates)
                    {
                        var probe = candidate.Probe;
                        selections.Add(
                            "COALESCE(MAX(CASE WHEN "
                            + $"{probe.DelimitedColumn} IS NOT NULL AND CHAR_LENGTH({probe.DelimitedColumn}) "
                            + $"> {probe.TargetLength.ToString(CultureInfo.InvariantCulture)} THEN 1 ELSE 0 END), 0)");
                    }

                    command.CommandText = $"SELECT {string.Join(", ", selections)} FROM {table};";
                }

                return new SafeMigrationCatalogProbeStatement(1, ParameterPayloadBytes: 0, candidates[0].Ordinal);
            },
            async (reader, offset, _, token) =>
            {
                var candidates = statements[offset];

                if (reader.FieldCount != candidates.Length
                    || !await reader.ReadAsync(token))
                {
                    throw new InvalidOperationException("The MySQL narrowing data query returned an invalid result.");
                }

                // WHY: Read each scalar result into the cache entry for the matching candidate ordinal.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var index = 0; index < candidates.Length; index++)
                {
                    var blocked = Convert.ToBoolean(reader.GetValue(index), CultureInfo.InvariantCulture);

                    var identity = candidates[index].Identity;
                    cache.Add(identity, blocked);
                }

                if (await reader.ReadAsync(token))
                {
                    throw new InvalidOperationException(
                        "The MySQL narrowing data query returned more than one result row.");
                }
            },
            cancellationToken);
    }

    private readonly record struct MySqlDataProbeIdentity(
        string Table,
        string Column,
        int TargetLength
    );

    /// <summary>Retains exact immutable definition identities without allocating serialized contracts.</summary>
    private static MySqlDataProbeContract CreateDataProbeContract(
        MySqlDataProbeIdentity identity,
        SafeMigrationOperation operation
    ) => operation.Intent switch
    {
        EnsureColumnIntent intent => new(identity, intent.Definition, null, intent.Schema, operation.Policy),
        AlterColumnIntent intent => new(
            identity,
            intent.Definition,
            intent.OldDefinition,
            intent.Schema,
            operation.Policy),
        _ => throw new InvalidOperationException("A MySQL column data probe requires a column intent."),
    };

    private readonly record struct MySqlDataProbeContract(
        MySqlDataProbeIdentity Identity,
        ExpectedColumnDefinition Target,
        ExpectedColumnDefinition? Source,
        string? Schema,
        SafeMigrationPolicy Policy
    );

    private readonly record struct MySqlDataProbeResult(
        bool IsTransitionEligible,
        bool IsRequired,
        bool IsBlocked
    );

    private sealed record MySqlDataProbeCandidate(
        MySqlDataProbeIdentity Identity,
        SafeMigrationOperation Operation,
        MySqlSafeMigrationDataProbe Probe,
        int Ordinal,
        MySqlDataProbeContract Contract
    );
}
