namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationProviderAnalyzer
{
    /// <summary>Resolves invocation-local narrowing evidence without retaining plans beyond bounded windows.</summary>
    /// <param name="connection">The current analysis connection.</param>
    /// <param name="operations">The original ordered safe operation stream.</param>
    /// <param name="shortCircuitStates">Catalog-proven states that need no row probe.</param>
    /// <param name="commandTimeout">The caller's command timeout.</param>
    /// <param name="cancellationToken">The cancellation token for all discovery and row reads.</param>
    /// <returns>Ordinal-aligned transition and row evidence for this invocation only.</returns>
    private async Task<PostgreSqlDataProbeResult?[]> ResolveDataProbeResultsAsync(
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationObservedState?[] shortCircuitStates,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var results = new PostgreSqlDataProbeResult?[operations.Count];
        var identities = new PostgreSqlDataProbeIdentity?[operations.Count];
        var candidates = new Dictionary<PostgreSqlDataProbeIdentity, PostgreSqlDataProbeCandidate>();
        // WHY: Retain probe identities at their original non-short-circuited ordinals.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            if (shortCircuitStates[ordinal] is not null)
            {
                continue;
            }

            var operation = operations[ordinal]
                ?? throw new ArgumentException("The operation batch cannot contain null entries.", nameof(operations));

            var probe = _catalogSqlBuilder.BuildDataProbe(operation);
            if (probe is null)
            {
                continue;
            }

            var identity = new PostgreSqlDataProbeIdentity(
                probe.Schema,
                probe.Table,
                probe.Column,
                probe.TargetLength,
                operation.Intent switch
                {
                    EnsureColumnIntent value => value.Definition,
                    AlterColumnIntent value => value.Definition,
                    _ => throw new UnreachableException(),
                },
                (operation.Intent as AlterColumnIntent)?.OldDefinition);

            identities[ordinal] = identity;
            // WHY: Row probes consume only the immutable column/length identity.
            // Keeping the discovery probe's rendered narrowing predicate here
            // would retain SQL for every candidate beyond the bounded window.
            candidates.TryAdd(identity, new PostgreSqlDataProbeCandidate(identity, operation, ordinal));
        }

        var cache = new Dictionary<PostgreSqlDataProbeIdentity, PostgreSqlDataProbeResult>(candidates.Count);
        if (candidates.Count > 0)
        {
            var values = candidates.Values.ToArray();
            var resolved = await FindRequiredDataProbesAsync(connection, values, commandTimeout, cancellationToken);

            foreach (var entry in resolved)
            {
                cache.Add(entry.Key, entry.Value);
            }

            await FindBlockingDataAsync(
                connection,
                values.Where(candidate => cache[candidate.Identity].IsRequired),
                cache,
                commandTimeout,
                cancellationToken);
        }

        // WHY: Store each resolved probe in the result slot owning that identity.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            if (shortCircuitStates[ordinal] is not null)
            {
                continue;
            }

            if (identities[ordinal] is not { } identity)
            {
                continue;
            }

            results[ordinal] = cache.TryGetValue(identity, out var result)
                ? result
                : throw new InvalidOperationException(
                    "The PostgreSQL narrowing probe did not resolve every candidate.");
        }

        return results;
    }

    /// <summary>Qualifies complete source/target contracts before any corresponding table-row read.</summary>
    private async Task<Dictionary<PostgreSqlDataProbeIdentity, PostgreSqlDataProbeResult>> FindRequiredDataProbesAsync(
        DbConnection connection,
        IReadOnlyList<PostgreSqlDataProbeCandidate> candidates,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var results = new Dictionary<PostgreSqlDataProbeIdentity, PostgreSqlDataProbeResult>(candidates.Count);
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

                var selections = new List<string>(count);
                // WHY: Build only this bounded candidate window while preserving global ordinals.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var index = 0; index < count; index++)
                {
                    // WHY: Eligibility needs only this candidate's predicates.
                    // Rebuild those predicates inside this bounded window rather
                    // than retaining discovery SQL for the whole migration stream.
                    var probe = _catalogSqlBuilder.BuildDataProbe(
                            candidates[offset + index].Operation,
                            includeTransitionEvidence: true)
                        ?? throw new InvalidOperationException(
                            "The PostgreSQL transition build returned no data-probe plan.");

                    selections.Add(
                        $"SELECT {candidates[offset + index].Ordinal.ToString(CultureInfo.InvariantCulture)}, "
                        + $"COALESCE(({probe.TransitionInvariantExpression}), FALSE), "
                        + $"COALESCE(({probe.NarrowingExpression}), FALSE)");
                }

                command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
                    + SafeMigrationCatalogQueryLimits.Trailer;

                return new SafeMigrationCatalogProbeStatement(
                    count,
                    ParameterPayloadBytes: 0,
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
                            "The PostgreSQL narrowing prerequisite query returned an invalid ordinal.");
                    }

                    var transitionEligible = reader.GetBoolean(1);
                    var narrowing = reader.GetBoolean(2);

                    results.Add(
                        candidates[offset + row].Identity,
                        new PostgreSqlDataProbeResult(
                            transitionEligible,
                            IsRequired: transitionEligible && narrowing,
                            IsBlocked: false));

                    row++;
                }

                if (row != count)
                {
                    throw new InvalidOperationException(
                        "The PostgreSQL narrowing prerequisite query returned an inconsistent row count.");
                }
            },
            cancellationToken);

        return results;
    }

    /// <summary>Binds shared physical row facts back to every independently qualified transition identity.</summary>
    private async Task FindBlockingDataAsync(
        DbConnection connection,
        IEnumerable<PostgreSqlDataProbeCandidate> requiredCandidates,
        Dictionary<PostgreSqlDataProbeIdentity, PostgreSqlDataProbeResult> cache,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        // WHY: Physical eligibility belongs to a complete old/target contract,
        // while the same column/limit row fact is safe to share after that gate.
        var rowGroups = requiredCandidates
            .GroupBy(static candidate => candidate.RowIdentity)
            .ToDictionary(static group => group.Key, static group => group.ToArray());

        var statements = rowGroups
            .Values
            .Select(static candidates => candidates[0])
            .GroupBy(static candidate => new PostgreSqlTableIdentity(
                candidate.Identity.Schema,
                candidate.Identity.Table))
            .SelectMany(static group => group.Chunk(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement))
            .ToArray();

        // WHY: Only catalog-qualified relations reach this phase. Packing
        // independent row probes must not combine them with prerequisite SQL,
        // which PostgreSQL plans before evaluating conditional expressions.
        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection,
            statements.Length,
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes,
            commandTimeout,
            (command, offset) =>
            {
                var candidates = statements[offset];
                var identity = candidates[0].Identity;
                var table = identity.Schema is null
                    ? _sqlGenerationHelper.DelimitIdentifier(identity.Table)
                    : _sqlGenerationHelper.DelimitIdentifier(identity.Table, identity.Schema);

                if (candidates.Length == 1)
                {
                    var column = _sqlGenerationHelper.DelimitIdentifier(identity.Column);
                    command.CommandText = "SELECT EXISTS(SELECT 1 FROM "
                        + $"{table} WHERE {column} IS NOT NULL AND char_length({column}) "
                        + $"> {identity.TargetLength.ToString(CultureInfo.InvariantCulture)} LIMIT 1);";
                }
                else
                {
                    var selections = new List<string>(candidates.Length);
                    foreach (var candidate in candidates)
                    {
                        var column = _sqlGenerationHelper.DelimitIdentifier(candidate.Identity.Column);
                        selections.Add(
                            "COALESCE(bool_or("
                            + $"{column} IS NOT NULL AND char_length({column}) "
                            + $"> {candidate.Identity.TargetLength.ToString(CultureInfo.InvariantCulture)}), FALSE)");
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
                    throw new InvalidOperationException(
                        "The PostgreSQL narrowing data query returned an invalid result.");
                }

                // WHY: Read each result column into the corresponding row-probe identity.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var index = 0; index < candidates.Length; index++)
                {
                    var blocked = reader.GetBoolean(index);
                    foreach (var candidate in rowGroups[candidates[index].RowIdentity])
                    {
                        cache[candidate.Identity] = cache[candidate.Identity] with { IsBlocked = blocked };
                    }
                }

                if (await reader.ReadAsync(token))
                {
                    throw new InvalidOperationException(
                        "The PostgreSQL narrowing data query returned more than one result row.");
                }
            },
            cancellationToken);
    }

    /// <summary>Owns both physical row coordinates and the exact source/target transition contract.</summary>
    private readonly record struct PostgreSqlDataProbeIdentity(
        string? Schema,
        string Table,
        string Column,
        int TargetLength,
        ExpectedColumnDefinition Definition,
        ExpectedColumnDefinition? OldDefinition
    );

    /// <summary>Identifies the row fact that can be shared only after per-transition catalog qualification.</summary>
    private readonly record struct PostgreSqlDataProbeRowIdentity(
        string? Schema,
        string Table,
        string Column,
        int TargetLength
    );

    /// <summary>Keeps catalog eligibility, row-read necessity and the observed data blocker distinct.</summary>
    private readonly record struct PostgreSqlDataProbeResult(
        bool IsTransitionEligible,
        bool IsRequired,
        bool IsBlocked
    );

    /// <summary>Groups eligible row probes under their exact provider-local table identity.</summary>
    private readonly record struct PostgreSqlTableIdentity(
        string? Schema,
        string Table
    );

    /// <summary>Retains only immutable operation identity and ordinal, never a whole-stream rendered plan.</summary>
    private sealed record PostgreSqlDataProbeCandidate(
        PostgreSqlDataProbeIdentity Identity,
        SafeMigrationOperation Operation,
        int Ordinal
    )
    {
        /// <summary>Gets the smaller physical row identity used after full transition eligibility is proven.</summary>
        public PostgreSqlDataProbeRowIdentity RowIdentity =>
            new(Identity.Schema, Identity.Table, Identity.Column, Identity.TargetLength);
    }
}
