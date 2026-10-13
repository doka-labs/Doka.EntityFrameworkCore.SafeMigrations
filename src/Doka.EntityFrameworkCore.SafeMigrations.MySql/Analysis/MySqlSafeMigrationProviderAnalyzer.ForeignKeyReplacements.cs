namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer : ISafeMigrationProjectedDependencyAnalyzer
{
    private Dictionary<SafeMigrationOperation, ForeignKeyReplacementProof>? _foreignKeyReplacementProofs;
    private HashSet<(string? Schema, string Table, string Name)>? _acceptedForeignKeyRemovals;
    private HashSet<(string? Schema, string Table)>? _acceptedForeignKeyRemovalOwners;
    private HashSet<(string? Schema, string Table, string Name)>? _acceptedForeignKeyCreations;
    private HashSet<(string? Schema, string Table)>? _invalidatedForeignKeyTables;
    private HashSet<(string? Schema, string Table)>? _invalidatedForeignKeyPrincipalKeys;
    private HashSet<(string? Schema, string Table, string Column)>? _invalidatedForeignKeyColumns;
    private Dictionary<(string? Schema, string Table, string Column), bool>? _projectedForeignKeyColumnNullability;
    private bool _foreignKeyReplacementRowsInvalidated;

    /// <summary>Reads source-bound FK certificates only after relevant declared physical removals.</summary>
    /// <param name="context">The active provider context.</param>
    /// <param name="operations">The immutable operations analyzed in this invocation.</param>
    /// <param name="analyses">Their current-database-qualified live results.</param>
    /// <param name="maximumPayloadBytes">The bounded transport payload.</param>
    /// <param name="commandTimeout">The caller's command timeout.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    private async Task CaptureForeignKeyReplacementProofsAsync(
        DbContext context,
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis[] analyses,
        int maximumPayloadBytes,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        _foreignKeyReplacementProofs = null;
        _acceptedForeignKeyRemovals = null;
        _acceptedForeignKeyRemovalOwners = null;
        _acceptedForeignKeyCreations = null;
        _invalidatedForeignKeyTables = null;
        _invalidatedForeignKeyPrincipalKeys = null;
        _invalidatedForeignKeyColumns = null;
        _projectedForeignKeyColumnNullability = null;
        _foreignKeyReplacementRowsInvalidated = false;

        await CaptureColumnDependenciesAsync(
            context, operations, analyses, maximumPayloadBytes, commandTimeout, cancellationToken);

        HashSet<(string? Schema, string Table, string Name)>? removals = null;
        HashSet<(string? Schema, string Table)>? removalOwners = null;
        List<(SafeMigrationOperation Operation, bool SourceExists)>? candidates = null;
        HashSet<SafeMigrationOperation>? candidateIdentities = null;

        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Intent is DropForeignKeyIntent drop)
            {
                var schema = NormalizeProofSchema(drop.Schema);

                (removals ??= new(PhysicalForeignKeyMemberComparer.Instance))
                    .Add((schema, drop.Table, drop.Name));
                (removalOwners ??= []).Add((schema, drop.Table));
            }
            else if (operation.Intent is EnsureForeignKeyIntent ensure
                && ensure.Definition.Columns.Count <= MySqlProjectedIndexPhysicalShape.MaximumKeyParts
                && TryGetForeignKeyReplacementSource(
                    ensure.Definition, analyses[index], removals, removalOwners, out var sourceExists))
            {
                // WHY: A caller may reuse an immutable operation instance in a
                // stream. Its source certificate is identical, not a second key.
                if ((candidateIdentities ??= new(ReferenceEqualityComparer.Instance)).Add(operation))
                {
                    (candidates ??= []).Add((operation, sourceExists));
                }
            }
        }

        if (candidates is null)
        {
            return;
        }

        var builder = new MySqlSafeMigrationCatalogSqlBuilder(
            _typeMappingSource, context.GetService<ISqlGenerationHelper>());

        var captured = new Dictionary<SafeMigrationOperation, ForeignKeyReplacementProof>(
            ReferenceEqualityComparer.Instance);

        List<SafeMigrationOperation>? rowCandidates = null;

        await SafeMigrationCatalogProbeBatch.ReadAsync(
            context.Database.GetDbConnection(), candidates.Count, maximumPayloadBytes, commandTimeout,
            (command, ordinal) =>
            {
                var candidate = candidates[ordinal];
                var definition = ((EnsureForeignKeyIntent)candidate.Operation.Intent).Definition;
                command.CommandText = builder.BuildForeignKeyReplacementProof(definition, candidate.SourceExists);

                return new SafeMigrationCatalogProbeStatement(1, 0, ordinal);
            },
            async (reader, ordinal, _, token) =>
            {
                if (!await reader.ReadAsync(token))
                {
                    throw new InvalidOperationException("MySQL omitted an owned FK replacement certificate.");
                }

                var eligible = Convert.ToBoolean(reader.GetValue(0), CultureInfo.InvariantCulture);
                var nullableMask = Convert.ToUInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                List<string>? matchingObjectNames = null;

                do
                {
                    if (Convert.ToBoolean(reader.GetValue(0), CultureInfo.InvariantCulture) != eligible
                        || Convert.ToUInt64(reader.GetValue(1), CultureInfo.InvariantCulture) != nullableMask)
                    {
                        throw new InvalidOperationException("MySQL returned inconsistent FK replacement certificates.");
                    }

                    if (!reader.IsDBNull(2))
                    {
                        if (!eligible)
                        {
                            throw new InvalidOperationException("MySQL bound aliases to an ineligible FK replacement.");
                        }

                        (matchingObjectNames ??= []).Add(reader.GetString(2));
                    }
                }
                while (await reader.ReadAsync(token));

                if (eligible)
                {
                    var candidate = candidates[ordinal];
                    var operation = candidate.Operation;
                    captured.Add(operation, new ForeignKeyReplacementProof(
                        nullableMask, RowsSafe: false, matchingObjectNames?.ToArray(), candidate.SourceExists));

                    (rowCandidates ??= []).Add(operation);
                }
            },
            cancellationToken);

        if (rowCandidates is not null)
        {
            await SafeMigrationCatalogProbeBatch.ReadAsync(
                context.Database.GetDbConnection(), rowCandidates.Count, maximumPayloadBytes, commandTimeout,
                (command, ordinal) =>
                {
                    var definition = ((EnsureForeignKeyIntent)rowCandidates[ordinal].Intent).Definition;
                    command.CommandText = builder.BuildForeignKeyReplacementRowProof(definition);

                    return new SafeMigrationCatalogProbeStatement(1, 0, ordinal);
                },
                async (reader, ordinal, _, token) =>
                {
                    if (!await reader.ReadAsync(token))
                    {
                        throw new InvalidOperationException("MySQL omitted a qualified FK replacement row proof.");
                    }

                    var rowsSafe = Convert.ToBoolean(reader.GetValue(0), CultureInfo.InvariantCulture);
                    if (await reader.ReadAsync(token))
                    {
                        throw new InvalidOperationException(
                            "MySQL returned multiple qualified FK replacement row proofs.");
                    }

                    var operation = rowCandidates[ordinal];
                    captured[operation] = captured[operation] with { RowsSafe = rowsSafe };
                },
                cancellationToken);
        }

        _foreignKeyReplacementProofs = captured.Count == 0 ? null : captured;
    }

    /// <summary>Targets only live FK sources whose relevant physical identity was declared for removal.</summary>
    /// <param name="definition">The exact requested FK identity.</param>
    /// <param name="liveAnalysis">The source-prioritized current-database assessment.</param>
    /// <param name="removals">The preceding declared physical names, when any.</param>
    /// <param name="removalOwners">Their physical owners, without changing table comparison.</param>
    /// <param name="sourceExists">Whether the requested physical name must be present in the catalog probe.</param>
    /// <returns>Whether a bounded source certificate is needed after these removals.</returns>
    private bool TryGetForeignKeyReplacementSource(
        ExpectedForeignKeyDefinition definition,
        SafeMigrationProviderAnalysis liveAnalysis,
        HashSet<(string? Schema, string Table, string Name)>? removals,
        HashSet<(string? Schema, string Table)>? removalOwners,
        out bool sourceExists
    )
    {
        var schema = NormalizeProofSchema(definition.Schema);
        var matchedName = liveAnalysis.MatchedObjectName;

        // WHY: BuildEnsureForeignKey prioritizes the requested physical name.
        // Exact Matching always returns that name; a resolved different alias
        // or an unresolved matching set therefore proves requested-name absence.
        // The catalog probe independently confirms this presence boundary.
        sourceExists = liveAnalysis.ObservedState == SafeMigrationObservedState.Different
            || (liveAnalysis.ObservedState == SafeMigrationObservedState.Matching
                && StringComparer.OrdinalIgnoreCase.Equals(matchedName, definition.Name));

        return liveAnalysis.ObservedState switch
        {
            SafeMigrationObservedState.Different
                => removals?.Contains((schema, definition.Table, definition.Name)) == true,
            SafeMigrationObservedState.Missing
                => removalOwners?.Contains((schema, definition.Table)) == true,
            SafeMigrationObservedState.Matching when liveAnalysis.PostconditionSatisfied
                => matchedName is not null
                    ? removals?.Contains((schema, definition.Table, matchedName)) == true
                    : removalOwners?.Contains((schema, definition.Table)) == true,
            _ => false,
        };
    }

    /// <inheritdoc />
    SafeMigrationProviderAnalysis ISafeMigrationProjectedDependencyAnalyzer.ValidateProjectedOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis projectedAnalysis,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (operation.Intent is not EnsureForeignKeyIntent ensure
            || _foreignKeyReplacementRowsInvalidated
            || _foreignKeyReplacementProofs?.TryGetValue(operation, out var proof) != true
            || (projectedAnalysis.ObservedState is not (SafeMigrationObservedState.Missing
                    or SafeMigrationObservedState.Matching)
                && (projectedAnalysis.ObservedState != SafeMigrationObservedState.PrerequisiteMissing
                    || !projectedAnalysis.IsOpaqueProjectionUnknown))
            || !HasAcceptedForeignKeyProofRemoval(ensure.Definition, proof)
            || !ForeignKeyProofIsCurrent(ensure.Definition, columns))
        {
            return projectedAnalysis;
        }

        var definition = ensure.Definition;
        if (definition.OnDelete == ReferentialAction.SetNull || definition.OnUpdate == ReferentialAction.SetNull)
        {
            for (var index = 0; index < definition.Columns.Count; index++)
            {
                var columnKey = (NormalizeProofSchema(definition.Schema), definition.Table, definition.Columns[index]);
                var isNullable = (proof.NullableMask & (1UL << index)) != 0;
                if (_projectedForeignKeyColumnNullability?.TryGetValue(columnKey, out var certifiedNullability) == true)
                {
                    // WHY: Column names are case-insensitive even when table
                    // identities are case-sensitive. A differently cased accepted
                    // transition supersedes the original nullable-bit snapshot.
                    isNullable = certifiedNullability;
                }
                else if (columns.TryGetProjectedColumn(definition.Table, definition.Schema,
                        definition.Columns[index], out var column))
                {
                    isNullable = column.IsNullable;
                }

                if (!isNullable)
                {
                    // WHY: Returning a previously projected Missing or Matching
                    // result would authorize an action despite this independent
                    // physical incompatibility. Required SET NULL remains refused.
                    return new SafeMigrationProviderAnalysis(
                        SafeMigrationObservedState.Different, SafeMigrationRepairCapability.None, false,
                        "projected_foreign_key_set_null_unrepresentable");
                }
            }
        }

        // WHY: Both endpoints, the principal key and the orphan result belong
        // to this exact operation. Only accepted FK removal and value-preserving
        // nullability changes can retain them; width alone is not a row proof.
        var matchingObjectName = ResolveRetainedForeignKeyAlias(definition, proof);
        if (matchingObjectName is not null)
        {
            // WHY: Removing the exact name does not remove another matching
            // physical FK. Returning Missing would misreport runtime's NoOp
            // and incorrectly bind the request to a nonexistent new object.
            return new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.Matching, SafeMigrationRepairCapability.None, true,
                "projected_foreign_key_replacement_matching")
            {
                MatchedObjectName = matchingObjectName,
            };
        }

        if (projectedAnalysis.ObservedState == SafeMigrationObservedState.Matching
            && (_acceptedForeignKeyCreations?.Contains((NormalizeProofSchema(definition.Schema),
                    definition.Table, definition.Name)) == true
                || (projectedAnalysis.MatchedObjectName is { } physicalName
                    && _acceptedForeignKeyCreations?.Contains((NormalizeProofSchema(definition.Schema),
                        definition.Table, physicalName)) == true)))
        {
            // WHY: Only an accepted Apply establishes new physical presence.
            // A previous alias NoOp cannot survive a case-insensitive removal
            // of its real name merely because Core retains the requested alias.
            return projectedAnalysis;
        }

        return new SafeMigrationProviderAnalysis(
            proof.RowsSafe ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.DataBlocked,
            SafeMigrationRepairCapability.None, false,
            proof.RowsSafe
                ? "projected_foreign_key_replacement_safe"
                : "projected_foreign_key_replacement_data_blocked");
    }

    /// <summary>Requires accepted physical removal at the source certificate's exact ownership boundary.</summary>
    /// <param name="definition">The requested current-database foreign-key owner.</param>
    /// <param name="proof">The independently captured original presence or absence certificate.</param>
    /// <returns>Whether an accepted removal permits bounded reuse of the unchanged source.</returns>
    private bool HasAcceptedForeignKeyProofRemoval(
        ExpectedForeignKeyDefinition definition,
        ForeignKeyReplacementProof proof
    )
    {
        var schema = NormalizeProofSchema(definition.Schema);

        // WHY: Different requires removal of the colliding physical name.
        // An originally absent name has no collision to remove; only an
        // accepted FK removal on the same owner can justify recovering its
        // independently qualified endpoint and row facts after neutral invalidation.
        return proof.SourceExists
            ? _acceptedForeignKeyRemovals?.Contains((schema, definition.Table, definition.Name)) == true
            : _acceptedForeignKeyRemovalOwners?.Contains((schema, definition.Table)) == true;
    }

    /// <summary>Resolves a deterministic physical alias that no accepted operation removed.</summary>
    /// <param name="definition">The requested replacement owner.</param>
    /// <param name="proof">The captured complete matching physical identities.</param>
    /// <returns>The surviving actual name, or null when all matching aliases were removed.</returns>
    private string? ResolveRetainedForeignKeyAlias(
        ExpectedForeignKeyDefinition definition,
        ForeignKeyReplacementProof proof
    )
    {
        if (proof.MatchingObjectNames is null)
        {
            return null;
        }

        var schema = NormalizeProofSchema(definition.Schema);
        string? retainedName = null;
        foreach (var name in proof.MatchingObjectNames)
        {
            if (_acceptedForeignKeyRemovals?.Contains((schema, definition.Table, name)) != true
                && (retainedName is null || StringComparer.Ordinal.Compare(name, retainedName) < 0))
            {
                retainedName = name;
            }
        }

        return retainedName;
    }

    /// <summary>Rejects certificates whose physical endpoints, columns or candidate key changed.</summary>
    /// <param name="definition">The exact captured foreign-key endpoints.</param>
    /// <param name="columns">The ordered projected column and table state.</param>
    /// <returns>Whether every endpoint and candidate-key certificate is still current.</returns>
    private bool ForeignKeyProofIsCurrent(
        ExpectedForeignKeyDefinition definition,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        var local = (NormalizeProofSchema(definition.Schema), definition.Table);
        var principal = (NormalizeProofSchema(definition.PrincipalSchema), definition.PrincipalTable);
        if (_invalidatedForeignKeyTables?.Contains(local) == true
            || _invalidatedForeignKeyTables?.Contains(principal) == true
            || _invalidatedForeignKeyPrincipalKeys?.Contains(principal) == true)
        {
            return false;
        }

        if (columns is ISafeMigrationProjectedTableSource tables
            && ((tables.TryGetProjectedTableState(definition.Table, definition.Schema, out var localState)
                    && (localState.HasUnknownStructure || localState.HasDataMutation))
                || (tables.TryGetProjectedTableState(definition.PrincipalTable, definition.PrincipalSchema,
                        out var principalState)
                    && (principalState.HasUnknownStructure || principalState.HasDataMutation))))
        {
            return false;
        }

        for (var index = 0; index < definition.Columns.Count; index++)
        {
            if (_invalidatedForeignKeyColumns?.Contains((local.Item1, local.Item2,
                    definition.Columns[index])) == true
                || _invalidatedForeignKeyColumns?.Contains((principal.Item1, principal.Item2,
                    definition.PrincipalColumns[index])) == true)
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    void ISafeMigrationProjectedDependencyAnalyzer.ObserveAcceptedOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision
    )
    {
        if ((_foreignKeyReplacementProofs is null && _columnSetNullDependencies is null
                && _columnPrimaryKeyDependencies is null)
            || decision.Action is not (SafeMigrationAction.Apply or SafeMigrationAction.Repair))
        {
            return;
        }

        if (analysis.RepairMutatesData || operation.Intent is ModelManagedDataIntent)
        {
            _foreignKeyReplacementRowsInvalidated = true;

            return;
        }

        switch (operation.Intent)
        {
            case DropForeignKeyIntent drop:
                var removedKey = (NormalizeProofSchema(drop.Schema), drop.Table, drop.Name);

                (_acceptedForeignKeyRemovals ??= new(PhysicalForeignKeyMemberComparer.Instance)).Add(removedKey);
                (_acceptedForeignKeyRemovalOwners ??= []).Add((removedKey.Item1, removedKey.Item2));
                _acceptedForeignKeyCreations?.Remove(removedKey);
                break;
            case EnsureForeignKeyIntent ensure when decision.Action == SafeMigrationAction.Apply
                && _foreignKeyReplacementProofs is not null:
                (_acceptedForeignKeyCreations ??= new(PhysicalForeignKeyMemberComparer.Instance))
                    .Add((NormalizeProofSchema(ensure.Definition.Schema),
                        ensure.Definition.Table, ensure.Definition.Name));
                break;
            case AlterColumnIntent alter when alter.OldDefinition is not null
                && IsSameLayoutNullabilityTransition(alter.OldDefinition, alter.Definition):
                if (_foreignKeyReplacementProofs is not null)
                {
                    (_projectedForeignKeyColumnNullability ??= new(PhysicalForeignKeyMemberComparer.Instance))
                        [(NormalizeProofSchema(alter.Schema), alter.Table, alter.Definition.Name)]
                        = alter.Definition.IsNullable;
                }

                break;
            case AlterColumnIntent alter:
                InvalidateForeignKeyColumn(alter.Table, alter.Schema, alter.Definition.Name);
                break;
            case EnsureColumnIntent ensure:
                InvalidateForeignKeyColumn(ensure.Table, ensure.Schema, ensure.Definition.Name);
                break;
            case DropColumnIntent drop:
                InvalidateForeignKeyTable(drop.Table, drop.Schema);
                break;
            case RenameColumnIntent rename:
                InvalidateForeignKeyTable(rename.Table, rename.Schema);
                break;
            case DropTableIntent drop:
                InvalidateForeignKeyTable(drop.Table, drop.Schema);
                break;
            case RenameTableIntent rename:
                InvalidateForeignKeyTable(rename.Name, rename.Schema);
                InvalidateForeignKeyTable(rename.NewName ?? rename.Name, rename.NewSchema ?? rename.Schema);
                break;
            case DropPrimaryKeyIntent drop:
                InvalidateForeignKeyPrincipalKey(drop.Table, drop.Schema);
                break;
            case DropUniqueConstraintIntent drop:
                InvalidateForeignKeyPrincipalKey(drop.Table, drop.Schema);
                break;
            case DropIndexIntent drop:
                InvalidateForeignKeyPrincipalKey(drop.Table, drop.Schema);
                break;
            case RenameIndexIntent rename:
                InvalidateForeignKeyPrincipalKey(rename.Table, rename.Schema);
                break;
        }
    }

    /// <inheritdoc />
    void ISafeMigrationProjectedDependencyAnalyzer.ObserveProviderOperation(MigrationOperation operation)
    {
        if (_foreignKeyReplacementProofs is not null || _columnSetNullDependencies is not null
            || _columnPrimaryKeyDependencies is not null)
        {
            // WHY: Ordinary operations have no accepted, source-bound safe
            // certificate here. DML may invoke unrelated triggers; raw SQL and
            // provider DDL must not revive an earlier replacement row proof.
            _foreignKeyReplacementRowsInvalidated = true;
        }
    }

    /// <summary>Invalidates one affected FK column without copying endpoint inventories.</summary>
    /// <param name="table">The changed physical owner.</param>
    /// <param name="schema">The authored database qualifier, when supplied.</param>
    /// <param name="column">The changed physical column.</param>
    private void InvalidateForeignKeyColumn(string table, string? schema, string column)
        => (_invalidatedForeignKeyColumns ??= new(PhysicalForeignKeyMemberComparer.Instance))
            .Add((NormalizeProofSchema(schema), table, column));

    /// <summary>Invalidates every replacement using a changed physical table.</summary>
    /// <param name="table">The changed physical table.</param>
    /// <param name="schema">The authored database qualifier, when supplied.</param>
    private void InvalidateForeignKeyTable(string table, string? schema)
        => (_invalidatedForeignKeyTables ??= []).Add((NormalizeProofSchema(schema), table));

    /// <summary>Invalidates captured principal candidate keys after their ordered removal or rename.</summary>
    /// <param name="table">The table whose candidate-key inventory changed.</param>
    /// <param name="schema">The authored database qualifier, when supplied.</param>
    private void InvalidateForeignKeyPrincipalKey(string table, string? schema)
        => (_invalidatedForeignKeyPrincipalKeys ??= []).Add((NormalizeProofSchema(schema), table));

    /// <summary>Uses the same verified database identity as ordinary provider analysis.</summary>
    /// <param name="schema">The authored database qualifier, when supplied.</param>
    /// <returns>The verified normalized database identity.</returns>
    private string? NormalizeProofSchema(string? schema)
        => MySqlTableIdentity.NormalizeDatabase(schema, _currentDatabase);

    /// <summary>
    /// Retains catalog nullability, complete physical matching names and the exact target's orphan proof.
    /// </summary>
    /// <param name="NullableMask">The original local-column nullability bits in requested order.</param>
    /// <param name="RowsSafe">Whether the qualified row probe found no orphan.</param>
    /// <param name="MatchingObjectNames">
    /// The owned snapshot of complete matching physical aliases, when present.
    /// </param>
    /// <param name="SourceExists">Whether the original certificate requires the requested physical name.</param>
    private readonly record struct ForeignKeyReplacementProof(
        ulong NullableMask,
        bool RowsSafe,
        string[]? MatchingObjectNames,
        bool SourceExists
    );

    /// <summary>Preserves owner identity while matching physical column and FK names without case.</summary>
    private sealed class PhysicalForeignKeyMemberComparer
        : IEqualityComparer<(string? Schema, string Table, string Name)>
    {
        // WHY: Column and FK names are case-insensitive on MySQL/MariaDB,
        // independently of the server's table-name policy. Folding the owners
        // would merge distinct physical tables on case-sensitive servers.
        /// <summary>Gets the shared allocation-free comparer for lazily captured member inventories.</summary>
        internal static PhysicalForeignKeyMemberComparer Instance { get; } = new();

        /// <inheritdoc />
        public bool Equals(
            (string? Schema, string Table, string Name) x,
            (string? Schema, string Table, string Name) y
        ) => StringComparer.Ordinal.Equals(x.Schema, y.Schema)
            && StringComparer.Ordinal.Equals(x.Table, y.Table)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);

        /// <inheritdoc />
        public int GetHashCode((string? Schema, string Table, string Name) value)
            => HashCode.Combine(
                value.Schema is null ? 0 : StringComparer.Ordinal.GetHashCode(value.Schema),
                StringComparer.Ordinal.GetHashCode(value.Table),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name));
    }
}
