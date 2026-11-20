namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
    private Dictionary<SafeMigrationOperation, ColumnDropConstraintSnapshot>? _columnDropConstraintCandidates;
    private Dictionary<SafeMigrationOperation, int[]>? _computedDropIndexCandidates;
    private long _nativeColumnDropObserverEpoch;

    private Dictionary<(string Schema, string Table),
        List<(SafeMigrationOperation Operation, NativeComputedDropProof Proof)>>? _nativeColumnDropOwnerRoutes;

    private Dictionary<SafeMigrationOperation, NativeComputedDropProof>? _nativeComputedDropRefusals;

    /// <summary>Associates only exact preceding authored index removals with a later source DROP.</summary>
    private void PrepareComputedDropIndexCandidates(
        IReadOnlyList<SafeMigrationOperation> operations
    )
    {
        _computedDropIndexCandidates = null;
        _columnDropConstraintCandidates = null;
        Dictionary<(string Schema, string Table), ComputedDropIndexPrefix>? predecessors = null;

        // WHY: Retain predecessor operation ordinals while advancing the DROP prefix.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            // WHY: Keep prefix mutation separate from conditional later-DROP snapshot admission.
            // ReSharper disable once ConvertIfStatementToSwitchStatement
            if (operations[ordinal].Intent is DropIndexIntent index)
            {
                predecessors ??= [];
                var key = (index.Schema ?? "dbo", index.Table);
                if (!predecessors.TryGetValue(key, out var indexes))
                {
                    indexes = new ComputedDropIndexPrefix();
                    predecessors.Add(key, indexes);
                }

                // WHY: A long mixed stream may remove the same physical name
                // repeatedly. Only its latest exact predecessor can establish
                // current absence; retaining every repetition wastes memory.
                indexes.Observe(index.Name, ordinal);
            }
            else if (operations[ordinal].Intent is DropColumnIntent column
                && predecessors?.TryGetValue((column.Schema ?? "dbo", column.Table), out var indexes) == true)
            {
                (_computedDropIndexCandidates ??= new Dictionary<SafeMigrationOperation, int[]>(
                    ReferenceEqualityComparer.Instance)).TryAdd(operations[ordinal], indexes.Snapshot);
            }
        }
    }

    /// <summary>Separates constraint kinds and exact physical owners before any native equality proof.</summary>
    private static (string Kind, string Schema, string Table, string Name)? ConstraintDropIdentity(
        SafeMigrationIntent intent
    ) => intent switch
    {
        DropPrimaryKeyIntent key => ("PK", key.Schema ?? "dbo", key.Table, key.Name),
        DropUniqueConstraintIntent key => ("UQ", key.Schema ?? "dbo", key.Table, key.Name),
        DropCheckConstraintIntent check => ("C", check.Schema ?? "dbo", check.Table, check.Name),
        DropForeignKeyIntent key => ("F", key.Schema ?? "dbo", key.Table, key.Name),
        DropTableIntent table => ("T", table.Schema ?? "dbo", table.Table, table.Table),
        _ => null,
    };

    /// <summary>Passes only exact earlier typed removals into the existing analysis classification statement.</summary>
    private SafeMigrationOperation[]? GetColumnDropConstraintCandidates(
        SafeMigrationOperation operation
    ) => _columnDropConstraintCandidates?.TryGetValue(operation, out var candidates) == true
        ? candidates.Operations
        : null;

    /// <summary>Routes native FK endpoints before retaining immutable owner-local predecessor prefixes.</summary>
    private void PrepareColumnDropConstraintCandidates(
        IReadOnlyList<SafeMigrationOperation> operations
    )
    {
        _columnDropConstraintCandidates = null;
        if (operations.All(static operation => operation.Intent is not DropColumnIntent)
            || operations.All(static operation => ConstraintDropIdentity(operation.Intent) is null))
        {
            return;
        }

        Dictionary<(string Schema, string Table), DropColumnIntent> owners = [];

        // WHY: The stable indexed snapshot preserves operation and declaration order.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var operationIndex0 = 0; operationIndex0 < operations.Count; operationIndex0++)
        {
            var operation = operations[operationIndex0];

            if (operation.Intent is DropColumnIntent source)
            {
                owners.TryAdd((source.Schema ?? "dbo", source.Table), source);
            }
        }

        Dictionary<int, List<(string Schema, string Table)>>? nativeOwners = null;
        Dictionary<int, HashSet<(string Schema, string Table)>>? deletedDependents = null;
        if (_dependencyGraph is { } graph)
        {
            foreach (var owner in owners.Keys)
            {
                if (graph.GetNativeTableIdentity(owner.Table, owner.Schema) is not { } identity)
                {
                    continue;
                }

                nativeOwners ??= [];
                if (!nativeOwners.TryGetValue(identity, out var names))
                {
                    names = [];
                    nativeOwners.Add(identity, names);
                }

                names.Add(owner);
            }

            if (nativeOwners is not null)
            {
                // WHY: The stable indexed snapshot preserves operation and declaration order.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var operationIndex1 = 0; operationIndex1 < operations.Count; operationIndex1++)
                {
                    var operation = operations[operationIndex1];

                    if (operation.Intent is DropTableIntent table
                        && graph.GetNativeTableIdentity(table.Table, table.Schema) is { } identity)
                    {
                        (deletedDependents ??= []).TryAdd(identity, []);
                    }
                }

                if (deletedDependents is not null)
                {
                    foreach (var (dependent, principal) in graph.EnumerateNativeForeignKeyEndpoints())
                    {
                        if (deletedDependents.TryGetValue(dependent, out var targets)
                            && nativeOwners.TryGetValue(principal, out var names))
                        {
                            targets.UnionWith(names);
                        }
                    }
                }
            }
        }

        Dictionary<(string Schema, string Table), ColumnDropConstraintPrefix> prefixes = [];

        // WHY: Observe only dependency removals preceding the current column DROP.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            var operation = operations[ordinal];
            if (ConstraintDropIdentity(operation.Intent) is { } identity)
            {
                // WHY: FK endpoint routing and table-deletion routing have different lazy native prerequisites.
                // ReSharper disable once ConvertIfStatementToSwitchStatement
                if (operation.Intent is DropForeignKeyIntent foreignKey)
                {
                    if (_dependencyGraph?.GetNativeForeignKeyDropEndpoints(foreignKey) is { } endpoints)
                    {
                        Route(endpoints.Dependent, identity.Kind, identity.Name, ordinal);
                        if (endpoints.Principal != endpoints.Dependent)
                        {
                            Route(endpoints.Principal, identity.Kind, identity.Name, ordinal);
                        }
                    }
                    else if (owners.ContainsKey((identity.Schema, identity.Table)))
                    {
                        Observe((identity.Schema, identity.Table), identity.Kind, identity.Name, ordinal);
                    }
                }
                else if (operation.Intent is DropTableIntent table)
                {
                    if (_dependencyGraph?.GetNativeTableIdentity(table.Table, table.Schema) is { } dependent
                        && deletedDependents?.TryGetValue(dependent, out var targets) == true)
                    {
                        foreach (var target in targets)
                        {
                            Observe(target, identity.Kind, identity.Name, ordinal);
                        }
                    }
                }
                else if (owners.ContainsKey((identity.Schema, identity.Table)))
                {
                    Observe((identity.Schema, identity.Table), identity.Kind, identity.Name, ordinal);
                }
            }
            else if (operation.Intent is DropColumnIntent source
                && prefixes.TryGetValue((source.Schema ?? "dbo", source.Table), out var prefix))
            {
                (_columnDropConstraintCandidates ??=
                    new Dictionary<SafeMigrationOperation, ColumnDropConstraintSnapshot>(
                        ReferenceEqualityComparer.Instance)).TryAdd(operation, prefix.Snapshot(operations));
            }
        }

        return;

        void Route(
            int identity,
            string kind,
            string name,
            int ordinal
        )
        {
            if (nativeOwners?.TryGetValue(identity, out var names) == true)
            {
                foreach (var owner in names)
                {
                    Observe(owner, kind, name, ordinal);
                }
            }
        }

        void Observe(
            (string Schema, string Table) owner,
            string kind,
            string name,
            int ordinal
        )
        {
            if (!prefixes.TryGetValue(owner, out var prefix))
            {
                prefix = new ColumnDropConstraintPrefix();
                prefixes.Add(owner, prefix);
            }

            prefix.Observe(kind, name, ordinal);
        }
    }

    /// <summary>Returns bounded native candidate names without treating their authored wishes as execution.</summary>
    private string[]? GetComputedDropIndexCandidateNames(
        SafeMigrationOperation operation,
        IReadOnlyList<SafeMigrationOperation> operations
    ) => _computedDropIndexCandidates?.TryGetValue(operation, out var candidates) == true
        ? candidates
            .Select(index => ((DropIndexIntent)operations[index].Intent).Name)

            // WHY: Retain this explicit fluent snapshot instead of a collection-spread rewrite.
            // ReSharper disable once UseCollectionExpression
            .ToArray()
        : null;

    /// <summary>Decodes only exact native DROP completeness and retains original dependency refusals.</summary>
    internal void CaptureProjectedComputedDropRefusals(
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis[] results
    )
    {
        _nativeComputedDropRefusals = null;
        _nativeColumnDropOwnerRoutes = null;
        _nativeColumnDropObserverEpoch = 0;

        // WHY: Bind DROP proof transport to the matching original operation and verdict.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            var analysis = results[ordinal];
            if (analysis.Code == SqlServerSafeMigrationCatalogSqlBuilder.EmptyGeneratedDropSetCode)
            {
                if (operations[ordinal].Intent is not DropColumnIntent
                    || analysis.ObservedState != SafeMigrationObservedState.Matching)
                {
                    throw new InvalidOperationException("Invalid SQL Server generated DROP completeness evidence.");
                }

                // WHY: This private classifier transport proves only the
                // generated family after this exact accepted removal. Core
                // separately owns its structural epoch and accepted lifetime.
                results[ordinal] = new SafeMigrationProviderAnalysis(
                    analysis.ObservedState,
                    analysis.RepairCapability,
                    analysis.PostconditionSatisfied,
                    "classified_matching",
                    analysis.OperationalImpact,
                    analysis.Differences)
                {
                    DropLeavesNoGeneratedColumns = true,
                };
            }
            else if (operations[ordinal].Intent is DropColumnIntent
                && analysis.ObservedState == SafeMigrationObservedState.Different)
            {
                List<ComputedDropIndexIdentity>? candidates = null;
                List<ColumnDropConstraintIdentity>? constraints = null;
                if (_computedDropIndexCandidates?.TryGetValue(operations[ordinal], out var predecessors) == true)
                {
                    var source = (DropColumnIntent)operations[ordinal].Intent;
                    foreach (var predecessor in predecessors)
                    {
                        var native = results[predecessor];
                        if (native.ObservedState != SafeMigrationObservedState.Matching
                            || native.MatchedObjectName is null)
                        {
                            continue;
                        }

                        var operation = operations[predecessor];
                        var index = (DropIndexIntent)operation.Intent;
                        var identity = new ExpectedIndexDefinition(
                            index.Name,
                            index.Table,
                            [new ExpectedIndexKeyDefinition(column: source.Name)],
                            index.Schema);

                        (candidates ??= []).Add(new ComputedDropIndexIdentity(operation, identity, native));
                    }
                }

                if (_columnDropConstraintCandidates?.TryGetValue(operations[ordinal], out var priorConstraints) == true)
                {
                    foreach (var predecessor in priorConstraints.Ordinals)
                    {
                        var native = results[predecessor];
                        var candidate = operations[predecessor];
                        var source = (DropColumnIntent)operations[ordinal].Intent;
                        var identity = ConstraintDropIdentity(candidate.Intent)
                            ?? throw new InvalidOperationException("Invalid SQL Server column DROP predecessor.");

                        var unrelated = candidate.Intent switch
                        {
                            DropForeignKeyIntent foreignKey =>
                                _dependencyGraph?.IsNativeForeignKeyDropRelevant(foreignKey, source) == false,
                            DropTableIntent table => _dependencyGraph?.IsNativeTableDropRelevant(table, source) != true,
                            _ => !SameComputedDropOwner(identity.Table, identity.Schema, source),
                        };

                        if (unrelated)
                        {
                            continue;
                        }

                        if (native.ObservedState == SafeMigrationObservedState.Matching
                            || native is
                            {
                                ObservedState: SafeMigrationObservedState.Different,
                                Code: "incoming_foreign_key_key_dependency" or "incoming_foreign_key_dependency",
                            })
                        {
                            (constraints ??= []).Add(new ColumnDropConstraintIdentity(candidate, native));
                        }
                    }
                }

                var sourceOperation = operations[ordinal];
                var proof = new NativeComputedDropProof(
                    analysis.Code,
                    candidates?.ToArray() ?? [],
                    constraints?.ToArray() ?? []);

                if ((_nativeComputedDropRefusals ??= new Dictionary<SafeMigrationOperation, NativeComputedDropProof>(
                        ReferenceEqualityComparer.Instance)).TryAdd(sourceOperation, proof))
                {
                    var source = (DropColumnIntent)sourceOperation.Intent;
                    BindOwner(source.Table, source.Schema, sourceOperation, proof);
                    foreach (var constraint in proof.Constraints)
                    {
                        var identity = ConstraintDropIdentity(constraint.Operation.Intent)
                            ?? throw new InvalidOperationException("A captured native predecessor lost its identity.");

                        BindOwner(identity.Table, identity.Schema, sourceOperation, proof);
                    }
                }

                if (analysis.Code is "generated_column_drop_named_index_dependency"
                    or "column_drop_candidate_dependency")
                {
                    // Conditional authorization remains private. The original
                    // public observation is still an independent live blocker.
                    results[ordinal] = new SafeMigrationProviderAnalysis(
                        analysis.ObservedState,
                        analysis.RepairCapability,
                        analysis.PostconditionSatisfied,
                        analysis.Code == "column_drop_candidate_dependency"
                            ? "column_drop_dependency"
                            : "generated_column_drop_other_dependency",
                        analysis.OperationalImpact,
                        analysis.Differences);
                }
            }
        }

        return;

        void BindOwner(
            string table,
            string? schema,
            SafeMigrationOperation operation,
            NativeComputedDropProof proof
        )
        {
            _nativeColumnDropOwnerRoutes ??= [];
            var owner = (schema ?? "dbo", table);
            if (!_nativeColumnDropOwnerRoutes.TryGetValue(owner, out var routes))
            {
                routes = [];
                _nativeColumnDropOwnerRoutes.Add(owner, routes);
            }

            // WHY: Only references to the existing proof are routed. Each
            // proof is registered contiguously, so its last reference also
            // removes repeated same-owner predecessors without a second set.
            if (routes.Count == 0
                || !ReferenceEquals(routes[^1].Operation, operation))
            {
                routes.Add((operation, proof));
            }
        }
    }

    /// <summary>Retains exact native constraint identities across dependency-removing prefixes.</summary>
    private SafeMigrationProviderAnalysis? QualifyCapturedColumnDropConstraint(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis live,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (ConstraintDropIdentity(operation.Intent) is not { } identity
            || _nativeComputedDropRefusals is null
            || _nativeColumnDropOwnerRoutes is null
            || !_nativeColumnDropOwnerRoutes.TryGetValue((identity.Schema, identity.Table), out var routes)
            || live.IsInvariantUnsupported
            || live.IsOpaqueProjectionUnknown
            || (columns is ISafeMigrationProjectedTableSource tables
                && tables.TryGetProjectedTableState(identity.Table, identity.Schema, out var owner)
                && owner.IsNewlyCreated))
        {
            return null;
        }

        // WHY: Routes are fully registered before qualification; only their proof state may change here.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var routeIndex = 0; routeIndex < routes.Count; routeIndex++)
        {
            var proof = routes[routeIndex].Proof;

            if (proof.Invalidated)
            {
                continue;
            }

            foreach (var constraint in proof.Constraints)
            {
                if (ReferenceEquals(constraint.Operation, operation))
                {
                    var current = constraint.AcceptedRemoval
                        ? new SafeMigrationProviderAnalysis(
                            SafeMigrationObservedState.Missing,
                            SafeMigrationRepairCapability.None,
                            true,
                            "projected_missing")
                        : constraint.Native;

                    return _dependencyGraph?.ValidateProjectedOperation(operation, current, columns) ?? current;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Preserves exact native index identity across SQL Server's dependency-rejecting column removals.
    /// </summary>
    private SafeMigrationProviderAnalysis? QualifyCapturedComputedDropIndex(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis live,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (operation.Intent is not DropIndexIntent drop
            || _nativeComputedDropRefusals is null
            || _nativeColumnDropOwnerRoutes is null
            || !_nativeColumnDropOwnerRoutes.TryGetValue((drop.Schema ?? "dbo", drop.Table), out var routes)
            || live.IsInvariantUnsupported
            || live.IsOpaqueProjectionUnknown
            || (columns is ISafeMigrationProjectedTableSource tables
                && tables.TryGetProjectedTableState(drop.Table, drop.Schema, out var owner)
                && owner.IsNewlyCreated))
        {
            return null;
        }

        // WHY: Routes are fully registered before qualification and retain their original priority.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var routeIndex = 0; routeIndex < routes.Count; routeIndex++)
        {
            var proof = routes[routeIndex].Proof;

            if (proof.Invalidated)
            {
                continue;
            }

            foreach (var index in proof.Indexes)
            {
                if (!ReferenceEquals(index.Operation, operation))
                {
                    continue;
                }

                var current = columns.GetProjectedIndexIdentityAnalysis(
                    index.Identity,
                    index.Native,
                    includeConstraintBackingIndexNames: true);

                if (current is not null)
                {
                    return current.ObservedState is SafeMigrationObservedState.Matching
                        or SafeMigrationObservedState.Missing
                        ? current
                        : null;
                }

                // WHY: A successful SQL Server guarded column DROP refuses
                // every indexed member; unlike engines with implicit index
                // cleanup, it cannot erase this independent native index.
                // Only removal-only, unexpired owner history reaches here.
                return index.Native;
            }
        }

        return null;
    }

    /// <summary>Preserves native dependency blockers after neutral source-presence projection.</summary>
    private SafeMigrationProviderAnalysis QualifyProjectedNativeComputedDrop(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (operation.Intent is not DropColumnIntent drop
            || _nativeComputedDropRefusals is not { } captured
            || !captured.TryGetValue(operation, out var proof)
            || analysis.IsInvariantUnsupported
            || analysis.IsOpaqueProjectionUnknown
            || analysis.ProviderDeferredOriginOrdinal is not null
            || analysis.ModelManagedLocalOriginOrdinal is not null
            || analysis.Code is "projected_check_source_dependency" or "projected_generated_column_drop_dependency"
            || analysis.ObservedState is SafeMigrationObservedState.Missing
                or SafeMigrationObservedState.Unsupported
                or SafeMigrationObservedState.DataBlocked)
        {
            return analysis;
        }

        if (columns is ISafeMigrationProjectedTableSource current
            && current.TryGetProjectedTableState(drop.Table, drop.Schema, out var owner)
            && owner.IsNewlyCreated)
        {
            // An accepted replacement owner cannot inherit the previous
            // native object's dependencies. Current authored generated
            // dependencies were independently checked before this hook.
            return analysis;
        }

        if (proof.SourceWasDropped
            && columns.TryGetProjectedColumn(drop.Table, drop.Schema, drop.Name, out _))
        {
            // WHY: An accepted native DROP removed that physical member and
            // all of its former dependencies. A later owned re-add must not
            // inherit blockers bound to the old column id.
            return analysis;
        }

        var dependency = (columns as ISafeMigrationProjectedTableSource)?.HasProjectedGeneratedColumnDependency(
            drop.Table,
            drop.Schema,
            drop.Name,
            StringComparer.Ordinal);

        // WHY: Native membership uses physical ids above; this exact spelling
        // query may prove complete absence, never infer CI identities in CLR.
        // Absence cannot remove an independent index/default/CHECK/FK blocker.
        var conditional = proof.Code is "generated_column_drop_named_index_dependency"
            or "column_drop_candidate_dependency";

        var acceptedRemovals = proof.Indexes.Length + proof.Constraints.Length > 0;

        // WHY: Retain the non-short-circuit array scan before evaluating each index's live identity.
        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var constraint in proof.Constraints)
        {
            acceptedRemovals &= constraint.AcceptedRemoval;
        }

        var currentIndexesAbsent = true;
        var knownCurrentIndex = false;
        foreach (var index in proof.Indexes)
        {
            acceptedRemovals &= index.AcceptedRemoval;
            var currentIndex = columns.GetProjectedIndexIdentityAnalysis(
                    index.Identity,
                    index.Native,
                    includeConstraintBackingIndexNames: true)
                ?.ObservedState;

            knownCurrentIndex |= currentIndex == SafeMigrationObservedState.Matching;

            // WHY: Native Matching positively bound the authored removal to
            // this different physical spelling. An accepted exact removal in
            // an unexpired owner epoch certifies that same object's absence;
            // it does not claim a CLR case-insensitive identity convention.
            var nativeAliasTombstone = !proof.Invalidated
                && index.AcceptedRemoval
                && currentIndex == SafeMigrationObservedState.PrerequisiteMissing
                && index.Native.MatchedObjectName is { } physicalName
                && !StringComparer.Ordinal.Equals(physicalName, index.Identity.Name);

            currentIndexesAbsent &= currentIndex == SafeMigrationObservedState.Missing || nativeAliasTombstone;
        }

        var removals = acceptedRemovals && currentIndexesAbsent;
        if (conditional
            && acceptedRemovals
            && !knownCurrentIndex
            && (proof.Code == "column_drop_candidate_dependency" || dependency == false)
            && (proof.HasUnprovenIndexMutation
                || proof is { Invalidated: true, InvalidatedOriginOrdinal: not null }))
        {
            // A removed old dependency is not evidence that later DDL
            // uses this source. Preserve the actual mutation's runtime
            // boundary rather than inventing either absence or dependence.
            return new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.PrerequisiteMissing,
                SafeMigrationRepairCapability.None,
                false,
                "projected_structure_state_unknown")
            {
                ProviderDeferredOriginOrdinal = proof.UnprovenIndexOriginOrdinal ?? proof.InvalidatedOriginOrdinal,
            };
        }

        var discharged = !proof.Invalidated
            && ((proof.Code == "generated_column_drop_dependency" && dependency == false)
                || (conditional
                    && removals
                    && (proof.Code == "column_drop_candidate_dependency" || dependency == false)));

        return discharged
            ? new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.Matching,
                SafeMigrationRepairCapability.None,
                false,
                proof.Code == "column_drop_candidate_dependency"
                    ? "projected_column_drop_safe"
                    : "projected_generated_column_drop_safe")
            : new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.Different,
                SafeMigrationRepairCapability.None,
                false,
                proof.Code switch
                {
                    "generated_column_drop_named_index_dependency" => "generated_column_drop_other_dependency",
                    "column_drop_candidate_dependency" => "column_drop_dependency",
                    _ => proof.Code,
                });
    }

    /// <summary>Accepts exact predecessor absence and expires native certificates on new structural blockers.</summary>
    private void ObserveProjectedComputedDropProofs(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis live,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision
    )
    {
        if (_nativeComputedDropRefusals is null
            || (!decision.ShouldExecute
                && (decision.Action != SafeMigrationAction.NoOp
                    || analysis.ObservedState != SafeMigrationObservedState.Missing)))
        {
            return;
        }

        var epoch = unchecked(++_nativeColumnDropObserverEpoch);
        if (epoch == 0)
        {
            foreach (var proof in _nativeComputedDropRefusals.Values)
            {
                proof.LastObservedEpoch = -1;
            }
        }

        switch (operation.Intent)
        {
            case DropSchemaIntent schema:
                if (_nativeColumnDropOwnerRoutes is not null)
                {
                    foreach (var (schemaOwner, routes) in _nativeColumnDropOwnerRoutes)
                    {
                        if (StringComparer.Ordinal.Equals(schemaOwner.Schema, schema.Name))
                        {
                            Visit(routes);
                        }
                    }
                }

                break;
            case EnsureForeignKeyIntent foreignKey:
                Route(foreignKey.Definition.Table, foreignKey.Definition.Schema);
                Route(foreignKey.Definition.PrincipalTable, foreignKey.Definition.PrincipalSchema);
                break;
            case EnsureTableIntent table:
                Route(table.Definition.Table, table.Definition.Schema);

                // WHY: The stable indexed snapshot preserves declaration order without mutating the source.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var foreignKeyIndex0 = 0;
                    foreignKeyIndex0 < table.Definition.ForeignKeys.Count;
                    foreignKeyIndex0++)
                {
                    var foreignKey = table.Definition.ForeignKeys[foreignKeyIndex0];

                    Route(foreignKey.PrincipalTable, foreignKey.PrincipalSchema);
                }

                break;
            default:
                var owner = ComputedDropMutationOwner(operation.Intent);

                // WHY: A resolved mutation owner takes precedence over all syntactic DROP fallback routes.
                // ReSharper disable once ConvertIfStatementToSwitchStatement
                if (owner is { } mutation)
                {
                    Route(mutation.Table, mutation.Schema);
                }
                else if (operation.Intent is DropIndexIntent index)
                {
                    Route(index.Table, index.Schema);
                }
                else if (operation.Intent is DropColumnIntent column)
                {
                    Route(column.Table, column.Schema);
                }
                else if (ConstraintDropIdentity(operation.Intent) is { } identity)
                {
                    Route(identity.Table, identity.Schema);
                }

                break;
        }

        return;

        void Route(
            string table,
            string? schema
        )
        {
            if (_nativeColumnDropOwnerRoutes?.TryGetValue((schema ?? "dbo", table), out var routes) == true)
            {
                Visit(routes);
            }
        }

        void Visit(
            List<(SafeMigrationOperation Operation, NativeComputedDropProof Proof)> routes
        )
        {
            // WHY: This observer changes proof state, never the already registered route slots.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var routeIndex = 0; routeIndex < routes.Count; routeIndex++)
            {
                var (sourceOperation, proof) = routes[routeIndex];

                if (proof.LastObservedEpoch != epoch)
                {
                    proof.LastObservedEpoch = epoch;
                    ObserveProof(sourceOperation, proof);
                }
            }
        }

        void ObserveProof(
            SafeMigrationOperation sourceOperation,
            NativeComputedDropProof proof
        )
        {
            foreach (var constraint in proof.Constraints)
            {
                if (ConstraintDropIdentity(operation.Intent) == ConstraintDropIdentity(constraint.Operation.Intent)
                    && (ReferenceEquals(constraint.Operation, operation)
                        || live.ObservedState == SafeMigrationObservedState.Matching))
                {
                    // WHY: Repeated authored removals may already be absent
                    // through an earlier exact native-matching predecessor.
                    // Only accepted execution or a current tombstone counts.
                    constraint.AcceptedRemoval = decision.ShouldExecute
                        || (decision.Action == SafeMigrationAction.NoOp
                            && analysis.ObservedState == SafeMigrationObservedState.Missing);
                }
            }

            foreach (var index in proof.Indexes)
            {
                if (ReferenceEquals(index.Operation, operation))
                {
                    index.AcceptedRemoval = decision.ShouldExecute
                        || (decision.Action == SafeMigrationAction.NoOp
                            && analysis.ObservedState == SafeMigrationObservedState.Missing);
                }
            }

            if (!decision.ShouldExecute)
            {
                return;
            }

            var source = (DropColumnIntent)sourceOperation.Intent;

            // WHY: Table deletion updates FK ownership before the independent exact source-column observation.
            // ReSharper disable once ConvertIfStatementToSwitchStatement
            if (operation.Intent is DropTableIntent removedOwner)
            {
                foreach (var constraint in proof.Constraints)
                {
                    if (constraint.Operation.Intent is DropForeignKeyIntent removedForeignKey
                        && StringComparer.Ordinal.Equals(removedForeignKey.Table, removedOwner.Table)
                        && StringComparer.Ordinal.Equals(
                            removedForeignKey.Schema ?? "dbo",
                            removedOwner.Schema ?? "dbo"))
                    {
                        // The user's explicit accepted table deletion also
                        // removes its owned outgoing FK, never a sibling FK.
                        constraint.AcceptedRemoval = true;
                    }
                }
            }

            if (operation.Intent is DropColumnIntent removed
                && SameComputedDropOwner(removed.Table, removed.Schema, source)
                && StringComparer.Ordinal.Equals(removed.Name, source.Name))
            {
                proof.SourceWasDropped = true;
            }

            var owner = ComputedDropMutationOwner(operation.Intent);
            if (owner is { } indexOwner
                && SameComputedDropOwner(indexOwner.Table, indexOwner.Schema, source)
                && operation.Intent is EnsureIndexIntent createdIndex)
            {
                var dependsOnSource = ClassifyNewIndexDropDependency(createdIndex.Definition, source);

                // WHY: False removes a proven-independent owner; null records uncertainty without a true-case action.
                // ReSharper disable once ConvertIfStatementToSwitchStatement
                if (dependsOnSource == false)
                {
                    // WHY: Positive native column ids prove this new index
                    // cannot replace the removed source dependency. Names
                    // with unknown catalog identity never use this shortcut.
                    owner = null;
                }
                else if (dependsOnSource is null)
                {
                    proof.HasUnprovenIndexMutation = true;
                    proof.UnprovenIndexOriginOrdinal = _currentProjectedOperationOrdinal;
                }
            }

            if ((owner is { } affected && SameComputedDropOwner(affected.Table, affected.Schema, source))
                || (operation.Intent is DropSchemaIntent schema
                    && StringComparer.Ordinal.Equals(schema.Name, source.Schema ?? "dbo"))
                || (operation.Intent is EnsureForeignKeyIntent foreignKey
                    && SameComputedDropOwner(
                        foreignKey.Definition.PrincipalTable,
                        foreignKey.Definition.PrincipalSchema,
                        source))
                || (operation.Intent is EnsureTableIntent table
                    && table.Definition.ForeignKeys.Any(tableForeignKey => SameComputedDropOwner(
                        tableForeignKey.PrincipalTable,
                        tableForeignKey.PrincipalSchema,
                        source)))
                || proof.Constraints.Any(constraint => ConstraintDropCandidateWasReplaced(
                    constraint.Operation.Intent,
                    operation.Intent)))
            {
                // WHY: A historical native certificate cannot certify a newly
                // created index/default/CHECK/FK or another owner lifetime.
                // The accepted index tombstone query separately catches exact
                // name recreation and shared PK/UNIQUE backing-name occupancy.
                proof.Invalidated = true;
                proof.InvalidatedOriginOrdinal = _currentProjectedOperationOrdinal;
            }
        }
    }

    /// <summary>Separates known index source membership from unknown native identifier equality.</summary>
    private bool? ClassifyNewIndexDropDependency(
        ExpectedIndexDefinition index,
        DropColumnIntent source
    )
    {
        _projectedColumnLayouts.TryGetValue((source.Schema ?? "dbo", source.Table), out var layout);
        var known = true;

        // WHY: The stable indexed snapshot preserves declaration order without mutating the source.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var keyIndex0 = 0; keyIndex0 < index.Keys.Count; keyIndex0++)
        {
            var key = index.Keys[keyIndex0];

            if (key.Column is not { } column)
            {
                known = false;
            }
            else if (References(column) is { } referenced)
            {
                if (referenced)
                {
                    return true;
                }
            }
            else
            {
                known = false;
            }
        }

        // WHY: The stable indexed snapshot preserves declaration order without mutating the source.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var includedIndex0 = 0; includedIndex0 < index.IncludedColumns.Count; includedIndex0++)
        {
            var column = index.IncludedColumns[includedIndex0];

            if (References(column) is not { } referenced)
            {
                known = false;
            }
            else if (referenced)
            {
                return true;
            }
        }

        if (index.Filter is not null
            || index.StructuredFilter is not null)
        {
            // Filter roles outside the bound native key/include inventory
            // need fresh runtime validation, never a spelling-based guess.
            known = false;
        }

        return known
            ? false
            : null;

        bool? References(
            string column
        )
        {
            if (StringComparer.Ordinal.Equals(column, source.Name))
            {
                return true;
            }

            return layout is not null
                && layout.Bindings.TryGetValue(source.Name, out var sourceIdentity)
                && sourceIdentity > 0
                && layout.Bindings.TryGetValue(column, out var memberIdentity)
                && memberIdentity > 0
                    ? memberIdentity == sourceIdentity
                    : null;
        }
    }

    /// <summary>Expires native predecessor identity when the same owned object can be recreated or moved.</summary>
    private static bool ConstraintDropCandidateWasReplaced(
        SafeMigrationIntent candidate,
        SafeMigrationIntent mutation
    )
    {
        if (ConstraintDropIdentity(candidate) is not { } identity)
        {
            return false;
        }

        return mutation switch
        {
            EnsureForeignKeyIntent foreignKey when identity.Kind == "F" => StringComparer.Ordinal.Equals(
                    identity.Table,
                    foreignKey.Definition.Table)
                && StringComparer.Ordinal.Equals(identity.Schema, foreignKey.Definition.Schema ?? "dbo")
                && StringComparer.Ordinal.Equals(identity.Name, foreignKey.Definition.Name),
            RenameTableIntent table => StringComparer.Ordinal.Equals(identity.Table, table.Name)
                && StringComparer.Ordinal.Equals(identity.Schema, table.Schema ?? "dbo"),
            DropSchemaIntent schema => StringComparer.Ordinal.Equals(identity.Schema, schema.Name),
            _ => false,
        };
    }

    private static bool SameComputedDropOwner(
        string table,
        string? schema,
        DropColumnIntent source
    ) => StringComparer.Ordinal.Equals(table, source.Table)
        && StringComparer.Ordinal.Equals(schema ?? "dbo", source.Schema ?? "dbo");

    private static (string Table, string? Schema)? ComputedDropMutationOwner(
        SafeMigrationIntent intent
    ) => intent switch
    {
        EnsureIndexIntent index => (index.Definition.Table, index.Definition.Schema),
        RenameIndexIntent index => (index.Table, index.Schema),
        EnsurePrimaryKeyIntent key => (key.Definition.Table, key.Definition.Schema),
        EnsureUniqueConstraintIntent key => (key.Definition.Table, key.Definition.Schema),
        EnsureForeignKeyIntent key => (key.Definition.Table, key.Definition.Schema),
        EnsureCheckConstraintIntent check => (check.Definition.Table, check.Definition.Schema),
        EnsureColumnIntent column when !SqlServerComputedColumnContract.IsComputed(column.Definition)
            => (column.Table, column.Schema),
        AlterColumnIntent column => (column.Table, column.Schema),
        RenameColumnIntent column => (column.Table, column.Schema),
        DropTableIntent table => (table.Table, table.Schema),
        RenameTableIntent table => (table.Name, table.Schema),
        EnsureTableIntent table => (table.Definition.Table, table.Definition.Schema),
        _ => null,
    };

    private void InvalidateProjectedComputedDropProofs()
    {
        if (_nativeComputedDropRefusals is not null)
        {
            foreach (var proof in _nativeComputedDropRefusals.Values)
            {
                proof.Invalidated = true;
            }
        }
    }

    /// <summary>Preserves native-bound generated dependencies even when their source owner is sparse.</summary>
    private SafeMigrationProviderAnalysis? QualifyProjectedComputedSourceDrop(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis live,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (operation.Intent is not DropColumnIntent drop
            || live.IsInvariantUnsupported
            || live.IsOpaqueProjectionUnknown
            || live.ObservedState is SafeMigrationObservedState.Unsupported or SafeMigrationObservedState.DataBlocked)
        {
            return null;
        }

        columns.TryGetProjectedColumn(drop.Table, drop.Schema, drop.Name, out var source);
        foreach (var (definition, proofs) in _projectedComputedKeys)
        {
            if (!columns.TryGetProjectedColumn(drop.Table, drop.Schema, definition.Name, out var accepted)
                || !ReferenceEquals(accepted, definition))
            {
                continue;
            }

            // WHY: The stable indexed snapshot preserves declaration order without mutating the source.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var proofIndex0 = 0; proofIndex0 < proofs.Count; proofIndex0++)
            {
                var proof = proofs[proofIndex0];

                if (!proof.Matches(columns, drop.Table, drop.Schema))
                {
                    continue;
                }

                var authored = source is not null
                    && proof.Operands.Values.Any(binding => ReferenceEquals(binding, source));

                var native = proof.LiveOperandIdentities is not null
                    && proof.CurrentColumnBindings?.TryGetValue(drop.Name, out var identity) == true
                    && identity > 0
                    && proof.LiveOperandIdentities.Values.Contains(identity);

                if (authored || native)
                {
                    // WHY: Sparse projection need not declare the existing
                    // source. Its positive native column id is an exact member
                    // certificate, not a CLR case-folding or initial-Missing
                    // assumption. Only the still-accepted dependent can veto.
                    return new SafeMigrationProviderAnalysis(
                        SafeMigrationObservedState.Different,
                        SafeMigrationRepairCapability.None,
                        false,
                        "projected_generated_column_drop_dependency");
                }
            }
        }

        return null;
    }

    /// <summary>Retains only one immutable snapshot per unchanged owner-local predecessor prefix.</summary>
    private sealed class ColumnDropConstraintPrefix
    {
        private readonly Dictionary<(string Kind, string Name), int> _latest = [];
        private ColumnDropConstraintSnapshot? _snapshot;

        /// <summary>Gets stable immutable operations and ordinals without a per-DROP array copy.</summary>
        internal ColumnDropConstraintSnapshot Snapshot(
            IReadOnlyList<SafeMigrationOperation> operations
        ) => _snapshot ??= new ColumnDropConstraintSnapshot(
            _latest.Values

                // WHY: Preserve one immutable fluent snapshot until this owner's predecessor prefix changes.
                // ReSharper disable once UseCollectionExpression
                .ToArray(),
            operations);

        /// <summary>Expires only this owner's next snapshot; prior captures keep their exact predecessors.</summary>
        internal void Observe(
            string kind,
            string name,
            int ordinal
        )
        {
            _latest[(kind, name)] = ordinal;
            _snapshot = null;
        }
    }

    /// <summary>Owns immutable ordinal and operation-reference arrays for one exact predecessor prefix.</summary>
    private sealed class ColumnDropConstraintSnapshot
    {
        internal ColumnDropConstraintSnapshot(
            int[] ordinals,
            IReadOnlyList<SafeMigrationOperation> operations
        )
        {
            Ordinals = ordinals;
            Operations = new SafeMigrationOperation[ordinals.Length];

            // WHY: Keep each captured predecessor reference at its selected output ordinal.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < ordinals.Length; index++)
            {
                Operations[index] = operations[ordinals[index]];
            }
        }

        /// <summary>Gets immutable original native result positions.</summary>
        internal int[] Ordinals { get; }

        /// <summary>Gets immutable exact predecessors without copying them per later DROP.</summary>
        internal SafeMigrationOperation[] Operations { get; }
    }

    /// <summary>
    /// Shares invocation-owned immutable ordinal storage until this owner's predecessor prefix changes.
    /// </summary>
    private sealed class ComputedDropIndexPrefix
    {
        private readonly Dictionary<string, int> _latest = new(StringComparer.Ordinal);
        private int[]? _snapshot;

        /// <summary>Gets one stable immutable snapshot without copying it for every unchanged later DROP.</summary>
        internal int[] Snapshot => _snapshot ??= _latest.Values

            // WHY: Preserve one immutable fluent snapshot until this owner's predecessor prefix changes.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        /// <summary>Records a new exact predecessor and expires only the next snapshot, never prior captures.</summary>
        internal void Observe(
            string name,
            int ordinal
        )
        {
            _latest[name] = ordinal;

            // WHY: Previously captured arrays must retain their original
            // operation ordinals. Reusing their mutable backing storage would
            // lend a future removal to an earlier source DROP.
            _snapshot = null;
        }
    }

    private sealed class NativeComputedDropProof
    {
        /// <summary>
        /// Captures an invocation-owned dependency proof without copying immutable predecessor identities.
        /// </summary>
        /// <param name="code">The exact native blocker classification.</param>
        /// <param name="indexes">The verified original index-removal identities.</param>
        /// <param name="constraints">The verified original typed constraint-removal identities.</param>
        internal NativeComputedDropProof(
            string code,
            ComputedDropIndexIdentity[] indexes,
            ColumnDropConstraintIdentity[] constraints
        )
        {
            Code = code;
            Indexes = indexes;
            Constraints = constraints;
        }

        /// <summary>Gets the exact native blocker family certified by this invocation.</summary>
        internal string Code { get; }

        /// <summary>Gets only original matching physical predecessor index identities.</summary>
        internal ComputedDropIndexIdentity[] Indexes { get; }

        /// <summary>Gets exact original typed native constraint-removal predecessors.</summary>
        internal ColumnDropConstraintIdentity[] Constraints { get; }

        /// <summary>
        /// Gets or sets whether an accepted native source removal ended its original member lifetime.
        /// </summary>
        internal bool SourceWasDropped { get; set; }

        /// <summary>Gets or sets whether a later accepted index has unproved native source membership.</summary>
        internal bool HasUnprovenIndexMutation { get; set; }

        /// <summary>Gets or sets the exact accepted index ordinal requiring fresh runtime validation.</summary>
        internal int? UnprovenIndexOriginOrdinal { get; set; }

        /// <summary>Gets or sets the last owner-route visit to avoid double-processing one shared proof.</summary>
        internal long LastObservedEpoch { get; set; }

        /// <summary>Gets or sets whether accepted later DDL expired this immutable native snapshot.</summary>
        internal bool Invalidated { get; set; }

        /// <summary>Gets or sets the exact accepted mutation that expired the native dependency snapshot.</summary>
        internal int? InvalidatedOriginOrdinal { get; set; }
    }

    private sealed class ColumnDropConstraintIdentity
    {
        /// <summary>
        /// Retains an exact predecessor operation together with its independently captured native proof.
        /// </summary>
        /// <param name="operation">The exact authored removal predecessor.</param>
        /// <param name="native">The original permission-qualified native classification.</param>
        internal ColumnDropConstraintIdentity(
            SafeMigrationOperation operation,
            SafeMigrationProviderAnalysis native
        )
        {
            Operation = operation;
            Native = native;
        }

        /// <summary>Gets the exact typed predecessor whose native identity was independently captured.</summary>
        internal SafeMigrationOperation Operation { get; }

        /// <summary>Gets the original native kind and permission-qualified state.</summary>
        internal SafeMigrationProviderAnalysis Native { get; }

        /// <summary>Gets or sets whether exact accepted history currently proves physical absence.</summary>
        internal bool AcceptedRemoval { get; set; }
    }

    private sealed class ComputedDropIndexIdentity
    {
        /// <summary>
        /// Retains one original physical index identity independently from later authored name reuse.
        /// </summary>
        /// <param name="operation">The exact original removal predecessor.</param>
        /// <param name="identity">The immutable native-qualified name query.</param>
        /// <param name="native">The original native index classification.</param>
        internal ComputedDropIndexIdentity(
            SafeMigrationOperation operation,
            ExpectedIndexDefinition identity,
            SafeMigrationProviderAnalysis native
        )
        {
            Operation = operation;
            Identity = identity;
            Native = native;
        }

        /// <summary>Gets the exact original predecessor, not a same-name authored substitute.</summary>
        internal SafeMigrationOperation Operation { get; }

        /// <summary>Gets the native-qualified physical-name query, without claiming the index key shape.</summary>
        internal ExpectedIndexDefinition Identity { get; }

        /// <summary>Gets the genuine original index classification and physical name.</summary>
        internal SafeMigrationProviderAnalysis Native { get; }

        /// <summary>Gets or sets whether this exact predecessor established accepted current absence.</summary>
        internal bool AcceptedRemoval { get; set; }
    }
}
