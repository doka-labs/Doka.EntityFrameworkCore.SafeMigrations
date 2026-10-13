namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    /// <summary>Recognizes only catalog-qualified staging checks owned by an unambiguous pending column add.</summary>
    /// <param name="context">The active provider context with the already-open inventory connection.</param>
    /// <param name="operations">The immutable pending migration stream.</param>
    /// <param name="findings">The current database's unmodified unexpected-object inventory.</param>
    /// <param name="isMariaDb">The engine detected on the inventory connection.</param>
    /// <param name="maximumPayloadBytes">The current bounded transport payload.</param>
    /// <param name="commandTimeout">The caller's configured command timeout.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The inventory excluding only precisely qualified owned staging checks.</returns>
    private async Task<List<SafeMigrationUnexpectedObject>> RemoveRequiredColumnAddRecoveryChecksAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        List<SafeMigrationUnexpectedObject> findings,
        bool isMariaDb,
        int maximumPayloadBytes,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var canonicalOwners = await ResolveRequiredColumnAddRecoveryOwnersAsync(
            context.Database.GetDbConnection(), operations, findings, maximumPayloadBytes,
            commandTimeout, cancellationToken);

        if (canonicalOwners is null)
        {
            return findings;
        }

        var candidates = FindRequiredColumnAddRecoveryCandidates(
            operations, findings, _currentDatabase, canonicalOwners);

        if (candidates.Count == 0)
        {
            return findings;
        }

        var candidateOperations = candidates.Select(static candidate => candidate.Operation).ToArray();
        var expectedUniqueIndexes = MySqlSafeMigrationPlanCapture.CreateExpectedUniqueIndexes(
            candidateOperations, _currentDatabase);

        var expectedTableConstraints = SafeMigrationExpectedTableConstraints.FromOperations(
            candidateOperations, schema => MySqlTableIdentity.NormalizeDatabase(schema, _currentDatabase));

        var backfillConstraints = MySqlBackfillConstraintCatalog.Create(candidateOperations, _currentDatabase);
        HashSet<int>? recovered = null;
        var offset = 0;

        foreach (var operationWindow in candidateOperations.Chunk(
                     SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // WHY: Guard names bind the untouched authored schema, while the
            // cross-operation catalogs use the verified physical database.
            // Capture the ordinary provider plan rather than a weaker second
            // definition of column/check ownership in the inventory path.
            var plans = CapturePlans(operationWindow, context.Model,
                expectedUniqueIndexes, expectedTableConstraints, backfillConstraints);

            List<(MySqlSafeMigrationRuntimePlan Plan, int FindingIndex)>? probes = null;
            for (var index = 0; index < plans.Length; index++)
            {
                var candidate = candidates[offset + index];
                var plan = plans[index];
                if (!plan.IsStaticallyUnsupported
                    && plan.RequiredColumnAddPlan is { } add
                    && add.IsMariaDb == isMariaDb
                    && StringComparer.Ordinal.Equals(add.GuardName, findings[candidate.FindingIndex].Name))
                {
                    (probes ??= []).Add((plan, candidate.FindingIndex));
                }
            }

            offset += operationWindow.Length;
            if (probes is null)
            {
                continue;
            }

            await SafeMigrationCatalogProbeBatch.ReadAsync(
                context.Database.GetDbConnection(), probes.Count, maximumPayloadBytes, commandTimeout,
                (command, ordinal) =>
                {
                    var parameters = new MySqlCatalogQueryParameterizer(command, _typeMappingSource);
                    var resume = probes[ordinal].Plan.RenderRequiredColumnAddResumeExpression(parameters.Add);

                    command.CommandText = $"SELECT COALESCE(({resume}), FALSE);";

                    return new SafeMigrationCatalogProbeStatement(1, parameters.Utf8PayloadBytes, ordinal);
                },
                async (reader, ordinal, _, token) =>
                {
                    if (!await reader.ReadAsync(token))
                    {
                        throw new InvalidOperationException("MySQL omitted a required-add recovery ownership result.");
                    }

                    var owned = reader.GetBoolean(0);
                    if (await reader.ReadAsync(token))
                    {
                        throw new InvalidOperationException("MySQL returned multiple required-add ownership results.");
                    }

                    if (owned)
                    {
                        // WHY: This recognizes an equivalent staging invariant,
                        // not historical provenance or empty/NULL-free rows.
                        // Final DDL still requires the normal fresh runtime proof.
                        (recovered ??= []).Add(probes[ordinal].FindingIndex);
                    }
                },
                cancellationToken);
        }

        if (recovered is null)
        {
            return findings;
        }

        var result = new List<SafeMigrationUnexpectedObject>(findings.Count - recovered.Count);
        for (var index = 0; index < findings.Count; index++)
        {
            if (!recovered.Contains(index))
            {
                result.Add(findings[index]);
            }
        }

        return result;
    }

    /// <summary>Selects exact guard names without treating a prefix or repeated column intent as ownership.</summary>
    /// <param name="operations">The complete immutable pending operation stream.</param>
    /// <param name="findings">The reported CHECK identities, before any inventory reconciliation.</param>
    /// <param name="currentDatabase">The verified selected database, when available.</param>
    /// <param name="canonicalOwners">The server-resolved physical owners, or null for exact-identity selection.</param>
    /// <returns>The sparse unambiguous operation/finding pairs requiring live catalog qualification.</returns>
    internal static IReadOnlyList<(SafeMigrationOperation Operation, int FindingIndex)>
        FindRequiredColumnAddRecoveryCandidates(
            IReadOnlyList<MigrationOperation> operations,
            IReadOnlyList<SafeMigrationUnexpectedObject> findings,
            string? currentDatabase,
            IReadOnlyDictionary<string, string?>? canonicalOwners = null
        )
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(findings);

        Dictionary<(string? Schema, string Table, string Name), int>? checks = null;
        HashSet<(string? Schema, string Table)>? owners = null;
        for (var index = 0; index < findings.Count; index++)
        {
            var finding = findings[index];
            if (finding.ObjectKind != SafeMigrationDatabaseObjectKind.CheckConstraint
                || finding.Table is null
                || !MySqlSafeMigrationCatalogSqlBuilder.IsRequiredColumnAddGuardNameCandidate(finding.Name))
            {
                continue;
            }

            var schema = MySqlTableIdentity.NormalizeDatabase(finding.Schema, currentDatabase);
            var key = (schema, finding.Table, finding.Name);

            if (!(checks ??= []).TryAdd(key, index))
            {
                // WHY: Duplicate reported physical identities are ambiguous,
                // not evidence that either list entry may disappear silently.
                checks[key] = -1;
            }

            (owners ??= []).Add((schema, finding.Table));
        }

        if (checks is null || owners is null)
        {
            return [];
        }

        HashSet<(string? Schema, string Table, string Column)>? targets = null;
        Dictionary<(string? Schema, string Table, string Column),
            (SafeMigrationOperation Operation, int FindingIndex)>? candidates = null;
        foreach (var operation in operations)
        {
            if (operation is not SafeMigrationOperation { Intent: EnsureColumnIntent ensure } safe
                || !MySqlSafeMigrationCatalogSqlBuilder.TargetsDatabase(ensure, currentDatabase))
            {
                continue;
            }

            var schema = MySqlTableIdentity.NormalizeDatabase(ensure.Schema, currentDatabase);
            var table = ensure.Table;
            if (canonicalOwners is not null)
            {
                if (!canonicalOwners.TryGetValue(table, out var resolved) || resolved is null)
                {
                    continue;
                }

                table = resolved;
            }

            if (!owners.Contains((schema, table)))
            {
                continue;
            }

            var columnKey = (schema, table, ensure.Definition.Name);
            if (!(targets ??= new(PhysicalForeignKeyMemberComparer.Instance)).Add(columnKey))
            {
                // WHY: Even an unsupported or differently cased later intent
                // makes this physical target ambiguous. Never choose whichever
                // definition happens to match the existing staging name.
                candidates?.Remove(columnKey);

                continue;
            }

            if (safe.GetAnnotations().Any()
                || !MySqlSafeMigrationCatalogSqlBuilder.CanUseRequiredColumnAddProtocol(ensure.Definition))
            {
                continue;
            }

            var guardName = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(ensure);
            if (checks.TryGetValue((schema, table, guardName), out var findingIndex) && findingIndex >= 0)
            {
                (candidates ??= new(PhysicalForeignKeyMemberComparer.Instance))
                    .Add(columnKey, (safe, findingIndex));
            }
        }

        return candidates is null ? [] : candidates.Values.ToArray();
    }
}
