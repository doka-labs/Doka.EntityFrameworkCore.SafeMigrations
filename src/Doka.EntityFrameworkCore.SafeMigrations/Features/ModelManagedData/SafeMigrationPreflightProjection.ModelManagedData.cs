namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    // WHY: Real mutation epochs start at zero and only advance. This sentinel
    // cannot accidentally certify completeness against a current epoch.
    private const long RevokedModelManagedDataMutationVersion = -1;

    private readonly Dictionary<ModelManagedRowKey, ProjectedModelManagedRow> _modelManagedRows;
    private Dictionary<TableKey, int>? _modelManagedLocalOrigins;

    /// <summary>Gets or sets the runner ordinal used to attribute table-local proof invalidation.</summary>
    internal int CurrentOperationOrdinal { get; set; } = -1;

    /// <summary>Gets the ordered evidence revision used to attribute deferred seed validation.</summary>
    internal long ModelManagedDataChangeVersion { get; private set; }

    /// <summary>Invalidates data proofs without inventing postconditions for a deferred write.</summary>
    internal void ObserveDeferredModelManagedMutation() => ObserveProviderDataMutation();

    private SafeMigrationProviderAnalysis Project(
        ModelManagedDataIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        foreach (var column in intent.KeyColumns)
        {
            if (IsProjectedColumnMissing(intent.Table, intent.Schema, column))
            {
                return TablePrerequisiteMissing();
            }
        }

        foreach (var column in intent.Columns)
        {
            if (IsProjectedColumnMissing(intent.Table, intent.Schema, column))
            {
                return TablePrerequisiteMissing();
            }
        }

        if (intent is DeleteModelManagedDataIntent deletion)
        {
            if (HasUnmodeledProjectedIncomingForeignKey(deletion))
            {
                return new SafeMigrationProviderAnalysis(
                    SafeMigrationObservedState.Unsupported,
                    SafeMigrationRepairCapability.None,
                    postconditionSatisfied: false,
                    "projected_model_managed_dependency_unmodeled");
            }

            foreach (var foreignKey in deletion.ForeignKeys)
            {
                if (_projectedMissingTables.Contains(new TableKey(foreignKey.Table, foreignKey.Schema)))
                {
                    return TablePrerequisiteMissing();
                }

                foreach (var column in foreignKey.Columns)
                {
                    if (IsProjectedColumnMissing(foreignKey.Table, foreignKey.Schema, column))
                    {
                        return TablePrerequisiteMissing();
                    }
                }
            }
        }

        var canInferMissingRows = CanInferMissingModelManagedRows(intent);

        if (_modelManagedRows.Count == 0 && !canInferMissingRows)
        {
            // WHY: Without row evidence or a completeness proof every row must
            // use live fallback. Avoid scratch storage and a discarded key hash.
            return ProjectLiveModelManagedState(intent, liveAnalysis);
        }

        var states = new SafeMigrationObservedState[intent.RowCount];

        for (var row = 0; row < intent.RowCount; row++)
        {
            if (_modelManagedRows.TryGetValue(
                    ModelManagedRowKey.Create(intent, row, _objectIdentityNormalizer),
                    out var projected))
            {
                if (TryClassify(intent, row, projected, out states[row]))
                {
                    continue;
                }

                return ProjectLiveModelManagedState(intent, liveAnalysis);
            }

            if (!canInferMissingRows)
            {
                return ProjectLiveModelManagedState(intent, liveAnalysis);
            }

            states[row] = SafeMigrationObservedState.Missing;
        }

        var state = AggregateModelManagedState(intent, states);

        if (state != SafeMigrationObservedState.Missing
            && intent is DeleteModelManagedDataIntent { ForeignKeys.Count: > 0 })
        {
            // WHY: Exact parent contents prove neither absence nor stability of
            // incoming rows. Only a guarded absence can bypass dependency checks.
            // Otherwise preserve untouched live conflicts or defer stale counts.
            return ProjectLiveModelManagedState(intent, liveAnalysis);
        }

        if ((state is SafeMigrationObservedState.Missing or SafeMigrationObservedState.TransitionReady)
            && HasProjectedUniqueCollision(intent))
        {
            state = SafeMigrationObservedState.DataBlocked;
        }

        if (state is SafeMigrationObservedState.Missing
            && intent is UpdateModelManagedDataIntent)
        {
            state = SafeMigrationObservedState.PrerequisiteMissing;
        }

        return new SafeMigrationProviderAnalysis(
            state,
            SafeMigrationRepairCapability.None,
            state == SafeMigrationObservedState.Matching
            || (state == SafeMigrationObservedState.Missing && intent is DeleteModelManagedDataIntent),
            $"projected_{ModelManagedStateCode(state)}")
        {
            ModelManagedDataEvidence = liveAnalysis.ModelManagedDataEvidence,
        };
    }

    private bool CanInferMissingModelManagedRows(
        ModelManagedDataIntent intent
    )
    {
        if (!_prerequisites.TryGetValue(new TableKey(intent.Table, intent.Schema), out var prerequisites)
            || !prerequisites.NewlyCreated
            || prerequisites.ModelManagedDataMutationVersion != _providerDataMutationVersion)
        {
            return false;
        }

        // WHY: A preceding accepted table creation proves an empty relation. This
        // proof remains authoritative only while every referenced column is
        // projected and no opaque provider data operation could have populated
        // the table through direct writes or triggers.

        return ContainsAllColumns(prerequisites, intent.KeyColumns)
            && ContainsAllColumns(prerequisites, intent.Columns);
    }

    private static bool ContainsAllColumns(
        ProjectedPrerequisites prerequisites,
        IReadOnlyList<string> columns
    )
    {
        for (var column = 0; column < columns.Count; column++)
        {
            if (!prerequisites.Columns.ContainsKey(columns[column]))
            {
                return false;
            }
        }

        return true;
    }

    private SafeMigrationProviderAnalysis ProjectLiveModelManagedState(
        ModelManagedDataIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (_providerDataMutationVersion == 0
            && !HasUnanalyzedDataChanges(intent.Table, intent.Schema)
            && !HasChangedModelManagedDependencies(intent))
        {
            return liveAnalysis;
        }

        return new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.PrerequisiteMissing,
            SafeMigrationRepairCapability.None,
            postconditionSatisfied: false,
            "projected_model_managed_data_state_unknown")
        {
            // WHY: Neither old positive classifications nor dependency counts
            // survive possible trigger/cascade effects. Runtime guards validate
            // the ordered result without rejecting legitimate seed sequences.
            IsModelManagedProjectionUnknown = true,
            HasStaleGlobalModelManagedData = HasStaleGlobalModelManagedData(intent),
            ModelManagedLocalOriginOrdinal = GetModelManagedLocalOrigin(intent),
        };
    }

    /// <summary>Finds the latest invalidation that actually affects this target or its declared dependencies.</summary>
    private int? GetModelManagedLocalOrigin(ModelManagedDataIntent intent)
    {
        if (_modelManagedLocalOrigins is null)
        {
            return null;
        }

        var ordinal = _modelManagedLocalOrigins.GetValueOrDefault(new TableKey(intent.Table, intent.Schema), -1);

        if (intent is DeleteModelManagedDataIntent deletion)
        {
            foreach (var foreignKey in deletion.ForeignKeys)
            {
                ordinal = Math.Max(ordinal, _modelManagedLocalOrigins.GetValueOrDefault(
                    new TableKey(foreignKey.Table, foreignKey.Schema), -1));
            }
        }

        return ordinal >= 0 ? ordinal : null;
    }

    private void TrackLocalModelManagedOrigin(TableKey table)
    {
        if (CurrentOperationOrdinal < 0)
        {
            return;
        }

        // WHY: An unrelated confined insert must not hide the actual dependency
        // write. Store one ordinal per touched table, allocated only when used,
        // rather than retaining one origin object for every seed operation.
        _modelManagedLocalOrigins ??= new Dictionary<TableKey, int>(_prerequisites.Comparer);
        _modelManagedLocalOrigins[table] = CurrentOperationOrdinal;
    }

    /// <summary>Distinguishes global side effects from later local loss of fresh-table row completeness.</summary>
    private bool HasStaleGlobalModelManagedData(ModelManagedDataIntent intent)
    {
        if (_providerDataMutationVersion == 0)
        {
            return false;
        }

        if (!WasCreatedAfterLastGlobalDataMutation(intent.Table, intent.Schema))
        {
            return true;
        }

        if (intent is DeleteModelManagedDataIntent deletion)
        {
            foreach (var foreignKey in deletion.ForeignKeys)
            {
                if (!WasCreatedAfterLastGlobalDataMutation(foreignKey.Table, foreignKey.Schema))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool WasCreatedAfterLastGlobalDataMutation(string table, string? schema)
    {
        // WHY: A write cannot affect a table that did not exist yet. If a later
        // structural change discards its row proof, attribute uncertainty to
        // that local change rather than an older unrelated global write.

        return _prerequisites.TryGetValue(new TableKey(table, schema), out var prerequisites)
            && prerequisites.NewlyCreated
            && prerequisites.DataMutationVersion == _providerDataMutationVersion;
    }

    /// <summary>Checks whether a delete's incoming-row counts were invalidated by confined child writes.</summary>
    private bool HasChangedModelManagedDependencies(ModelManagedDataIntent intent)
    {
        if (intent is not DeleteModelManagedDataIntent deletion)
        {
            return false;
        }

        // WHY: A confined child insert leaves the parent's contents unchanged
        // but can introduce a dependent row. Freshness covers both sides of the
        // delete contract, even when no global side-effect epoch was advanced.
        foreach (var foreignKey in deletion.ForeignKeys)
        {
            if (HasUnanalyzedDataChanges(foreignKey.Table, foreignKey.Schema))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasProjectedUniqueCollision(
        ModelManagedDataIntent intent
    )
    {
        var uniqueKeys = intent switch
        {
            EnsureModelManagedDataIntent ensure => ensure.UniqueKeys,
            UpdateModelManagedDataIntent update => update.UniqueKeys,
            _ => [],
        };

        var targets = intent switch
        {
            EnsureModelManagedDataIntent ensure => ensure.Values,
            UpdateModelManagedDataIntent update => update.NewValues,
            _ => null,
        };

        if (targets is null)
        {
            return false;
        }

        for (var row = 0; row < intent.RowCount; row++)
        {
            var currentKey = ModelManagedRowKey.Create(intent, row, _objectIdentityNormalizer);

            foreach (var uniqueKey in uniqueKeys)
            {
                var ordinals = uniqueKey.Columns.Select(column => ColumnOrdinal(intent.Columns, column)).ToArray();

                if (ordinals.Any(ordinal => targets.GetUnsafeValue(row, ordinal) is null))
                {
                    continue;
                }

                foreach (var (candidateKey, candidate) in _modelManagedRows)
                {
                    if (!candidate.Exists
                        || candidateKey.Equals(currentKey)
                        || !IdentifierEquals(_objectIdentityNormalizer, candidateKey.Table, intent.Table)
                        || !SameSchema(candidateKey.Schema, intent.Schema))
                    {
                        continue;
                    }

                    if (ordinals.All(ordinal => candidate.Values.TryGetValue(
                                intent.Columns[ordinal],
                                out var candidateValue)
                            && SafeMigrationModelManagedValue.AreEqual(
                                candidateValue,
                                targets.GetUnsafeValue(row, ordinal))))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private void Observe(
        ModelManagedDataIntent intent,
        SafeMigrationDecision decision
    )
    {
        var table = new TableKey(intent.Table, intent.Schema);
        _prerequisites.TryGetValue(table, out var prerequisites);

        if (decision.Action == SafeMigrationAction.Apply)
        {
            // WHY: Triggers can invalidate earlier exact rows as well as generic
            // empty-table proofs. Clear them before recording only the current
            // operation's guarded postconditions; never carry unmodified columns
            // from a pre-write row through this boundary.
            if (!IsConfinedModelManagedInsert(intent, table, prerequisites))
            {
                ObserveProviderDataMutation();
            }

            ModelManagedDataChangeVersion++;
            _projectedDataMutationTables.Add(table);
            TrackLocalModelManagedOrigin(table);
        }

        for (var row = 0; row < intent.RowCount; row++)
        {
            var key = ModelManagedRowKey.Create(intent, row, _objectIdentityNormalizer);

            if (intent is DeleteModelManagedDataIntent)
            {
                _modelManagedRows[key] = ProjectedModelManagedRow.Absent(_objectIdentityNormalizer);
                continue;
            }

            var projected = _modelManagedRows.TryGetValue(key, out var existing) && existing.Exists
                ? existing.Copy()
                : new ProjectedModelManagedRow(exists: true, _objectIdentityNormalizer);

            for (var column = 0; column < intent.KeyColumns.Count; column++)
            {
                projected.Values[intent.KeyColumns[column]] = intent.KeyValues.GetValue(row, column);
            }

            var values = intent switch
            {
                EnsureModelManagedDataIntent ensure => ensure.Values,
                UpdateModelManagedDataIntent update => update.NewValues,
                _ => throw new UnreachableException(),
            };

            for (var column = 0; column < intent.Columns.Count; column++)
            {
                projected.Values[intent.Columns[column]] = values.GetValue(row, column);
            }

            _modelManagedRows[key] = projected;
        }

        if (decision.Action == SafeMigrationAction.Apply)
        {
            ObserveProjectedUniqueKeys(table, intent);
        }
    }

    /// <summary>Proves that an accepted managed insert cannot mutate another table.</summary>
    private bool IsConfinedModelManagedInsert(
        ModelManagedDataIntent intent,
        TableKey key,
        ProjectedPrerequisites? prerequisites
    )
    {
        // WHY: Observe calls this only for Apply. Managed Ensure exposes no
        // repair capability: it inserts missing rows, leaves matches untouched
        // and rejects differing rows. Confinement depends on that insert-only
        // contract; it cannot be reused for a future update/repair path.
        if (intent is not EnsureModelManagedDataIntent
            || prerequisites is not { NewlyCreated: true }
            || !_tables.TryGetValue(key, out var table)
            || _projectedUnknownTableStructures.Contains(key)
            || _projectedUnknownPhysicalKeys.Contains(key)
            || table.CheckConstraints.Count != 0)
        {
            return false;
        }

        // WHY: Fresh-table provenance alone is insufficient: SQL defaults,
        // computed values, checks and index expressions may call user functions.
        // Inspect the current definition so later artifacts revoke the proof.
        // Dictionary value iteration avoids copying the full table definition.
        foreach (var column in table.Columns.Values)
        {
            if (column.DefaultValue.Kind == SafeMigrationDefaultValueKind.Sql
                || column.ComputedColumnSql is not null
                || column.ComputedExpression is not null)
            {
                return false;
            }
        }

        foreach (var index in table.Indexes.Values)
        {
            if (index.Filter is not null
                || index.StructuredFilter is not null
                || index.Method is not null)
            {
                return false;
            }

            for (var ordinal = 0; ordinal < index.Keys.Count; ordinal++)
            {
                var part = index.Keys[ordinal];
                if (part.Column is null
                    || part.OperatorClass is not null)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void ObserveProjectedUniqueKeys(
        TableKey table,
        ModelManagedDataIntent intent
    )
    {
        if (intent is DeleteModelManagedDataIntent)
        {
            return;
        }

        var intentUniqueKeys = intent switch
        {
            EnsureModelManagedDataIntent ensure => ensure.UniqueKeys,
            UpdateModelManagedDataIntent update => update.UniqueKeys,
            _ => throw new UnreachableException(),
        };

        var fingerprints = intentUniqueKeys
            .Select(uniqueKey => ModelManagedUniqueKeyFingerprint(uniqueKey.Columns))
            .ToHashSet(StringComparer.Ordinal);

        if (_projectedModelManagedUniqueKeys.TryGetValue(table, out var provenUniqueKeys))
        {
            // WHY: A candidate key remains proven only when every accepted
            // insert or update batch carried the same target-model metadata.
            // Missing metadata must narrow the proof set, never widen it.
            provenUniqueKeys.IntersectWith(fingerprints);
            return;
        }

        _projectedModelManagedUniqueKeys.Add(table, fingerprints);
    }

    private void InvalidateModelManagedDataProjection()
    {
        // WHY: A structural change can discard exact rows without advancing the
        // data epoch. Losing those rows must not make a populated new table look
        // empty. Iterate discarded evidence, not all tables in a large stream.
        foreach (var key in _modelManagedRows.Keys)
        {
            var table = new TableKey(key.Table, key.Schema);
            if (_projectedDataMutationTables.Contains(table)
                && _prerequisites.TryGetValue(table, out var prerequisites))
            {
                prerequisites.ModelManagedDataMutationVersion = RevokedModelManagedDataMutationVersion;
                TrackLocalModelManagedOrigin(table);
            }
        }

        _modelManagedRows.Clear();
        _projectedModelManagedUniqueKeys.Clear();
        ModelManagedDataChangeVersion++;
    }

    /// <summary>Moves owned row evidence with a proven physical table rename.</summary>
    private void RenameModelManagedTable(
        TableKey source,
        TableKey target
    )
    {
        if (_modelManagedLocalOrigins is not null
            && _modelManagedLocalOrigins.Remove(source, out var origin))
        {
            _modelManagedLocalOrigins[target] = origin;
        }

        List<KeyValuePair<ModelManagedRowKey, ProjectedModelManagedRow>>? moved = null;

        // WHY: A pure rename preserves row values and completeness. Snapshot only
        // matching entries because dictionary enumeration cannot tolerate rekeys.
        foreach (var row in _modelManagedRows)
        {
            if (IdentifierEquals(_objectIdentityNormalizer, row.Key.Table, source.Table)
                && SameSchema(row.Key.Schema, source.Schema))
            {
                (moved ??= []).Add(row);
            }
        }

        if (moved is not null)
        {
            foreach (var row in moved)
            {
                _modelManagedRows.Remove(row.Key);
                _modelManagedRows[row.Key with { Table = target.Table, Schema = target.Schema }] = row.Value;
            }
        }

        if (_projectedModelManagedUniqueKeys.Remove(source, out var uniqueKeys))
        {
            _projectedModelManagedUniqueKeys[target] = uniqueKeys;
        }
    }

    private static bool TryClassify(
        ModelManagedDataIntent intent,
        int row,
        ProjectedModelManagedRow projected,
        out SafeMigrationObservedState state
    )
    {
        if (!projected.Exists)
        {
            state = SafeMigrationObservedState.Missing;
            return true;
        }

        var target = intent switch
        {
            EnsureModelManagedDataIntent ensure => ensure.Values,
            UpdateModelManagedDataIntent update => update.NewValues,
            DeleteModelManagedDataIntent => null,
            _ => throw new UnreachableException(),
        };

        if (target is not null
            && Matches(projected, intent.Columns, target, row, out var targetKnown)
            && targetKnown)
        {
            state = SafeMigrationObservedState.Matching;
            return true;
        }

        var source = intent switch
        {
            UpdateModelManagedDataIntent update => update.OldValues,
            DeleteModelManagedDataIntent delete => delete.OldValues,
            _ => null,
        };

        if (source is not null
            && Matches(projected, intent.Columns, source, row, out var sourceKnown)
            && sourceKnown)
        {
            state = SafeMigrationObservedState.TransitionReady;
            return true;
        }

        var allKnown = intent.Columns.All(projected.Values.ContainsKey);
        state = SafeMigrationObservedState.Different;

        return allKnown;
    }

    private static bool Matches(
        ProjectedModelManagedRow projected,
        IReadOnlyList<string> columns,
        ModelManagedDataMatrix expected,
        int row,
        out bool allKnown
    )
    {
        allKnown = true;

        for (var column = 0; column < columns.Count; column++)
        {
            if (!projected.Values.TryGetValue(columns[column], out var actual))
            {
                allKnown = false;
                return false;
            }

            if (!SafeMigrationModelManagedValue.AreEqual(actual, expected.GetUnsafeValue(row, column)))
            {
                return false;
            }
        }

        return true;
    }

    private static SafeMigrationObservedState AggregateModelManagedState(
        ModelManagedDataIntent intent,
        IReadOnlyList<SafeMigrationObservedState> states
    )
    {
        if (states.Contains(SafeMigrationObservedState.Different))
        {
            return SafeMigrationObservedState.Different;
        }

        if (intent is UpdateModelManagedDataIntent
            && states.Contains(SafeMigrationObservedState.Missing))
        {
            return SafeMigrationObservedState.PrerequisiteMissing;
        }

        if (states.Contains(SafeMigrationObservedState.TransitionReady))
        {
            return SafeMigrationObservedState.TransitionReady;
        }

        if (states.All(static state => state == SafeMigrationObservedState.Matching))
        {
            return SafeMigrationObservedState.Matching;
        }

        return SafeMigrationObservedState.Missing;
    }

    private static string ModelManagedStateCode(
        SafeMigrationObservedState state
    ) => state switch
    {
        SafeMigrationObservedState.Missing => "missing",
        SafeMigrationObservedState.Matching => "matching",
        SafeMigrationObservedState.Different => "different",
        SafeMigrationObservedState.PrerequisiteMissing => "prerequisite_missing",
        SafeMigrationObservedState.DataBlocked => "data_blocked",
        SafeMigrationObservedState.TransitionReady => "transition_ready",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private readonly record struct ModelManagedRowKey(
        string Table,
        string? Schema,
        string KeyFingerprint
    )
    {
        public static ModelManagedRowKey Create(
            ModelManagedDataIntent intent,
            int row,
            ISafeMigrationProviderObjectIdentityNormalizer? objectIdentityNormalizer
        )
        {
            using var writer = new CanonicalHashWriter();

            writer.Add(intent.KeyColumns.Count);

            for (var column = 0; column < intent.KeyColumns.Count; column++)
            {
                writer.Add(NormalizeIdentifier(objectIdentityNormalizer, intent.KeyColumns[column]));
                SafeMigrationModelManagedValue.Write(writer, intent.KeyValues.GetUnsafeValue(row, column));
            }

            return new ModelManagedRowKey(intent.Table, intent.Schema, writer.GetHash());
        }
    }

    private sealed class ModelManagedRowKeyComparer(
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer
    ) : IEqualityComparer<ModelManagedRowKey>
    {
        private readonly TableKeyComparer _tableComparer = new(normalizer);

        public bool Equals(
            ModelManagedRowKey left,
            ModelManagedRowKey right
        ) => _tableComparer.Equals(
                new TableKey(left.Table, left.Schema),
                new TableKey(right.Table, right.Schema))
            && StringComparer.Ordinal.Equals(left.KeyFingerprint, right.KeyFingerprint);

        public int GetHashCode(
            ModelManagedRowKey value
        ) => HashCode.Combine(
            _tableComparer.GetHashCode(new TableKey(value.Table, value.Schema)),
            StringComparer.Ordinal.GetHashCode(value.KeyFingerprint));
    }

    private int ColumnOrdinal(
        IReadOnlyList<string> columns,
        string column
    )
    {
        for (var ordinal = 0; ordinal < columns.Count; ordinal++)
        {
            if (IdentifierEquals(_objectIdentityNormalizer, columns[ordinal], column))
            {
                return ordinal;
            }
        }

        throw new UnreachableException();
    }

    private sealed class ProjectedModelManagedRow(
        bool exists,
        ISafeMigrationProviderObjectIdentityNormalizer? objectIdentityNormalizer
    )
    {
        public static ProjectedModelManagedRow Absent(
            ISafeMigrationProviderObjectIdentityNormalizer? objectIdentityNormalizer
        ) => new(exists: false, objectIdentityNormalizer);

        public bool Exists { get; } = exists;

        public Dictionary<string, object?> Values { get; } =
            new(new IdentifierComparer(objectIdentityNormalizer));

        public ProjectedModelManagedRow Copy()
        {
            var result = new ProjectedModelManagedRow(Exists, objectIdentityNormalizer);

            foreach (var (column, value) in Values)
            {
                result.Values.Add(column, SafeMigrationModelManagedValue.Clone(value));
            }

            return result;
        }
    }
}
