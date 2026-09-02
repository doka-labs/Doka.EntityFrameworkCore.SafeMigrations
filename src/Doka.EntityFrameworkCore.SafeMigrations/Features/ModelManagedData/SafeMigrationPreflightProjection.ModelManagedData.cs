namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private readonly Dictionary<ModelManagedRowKey, ProjectedModelManagedRow> _modelManagedRows = [];
    private readonly List<AcceptedModelManagedDeleteRow> _acceptedModelManagedDeletes = [];

    private SafeMigrationProviderAnalysis Project(
        ModelManagedDataIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        var canInferMissingRows = CanInferMissingModelManagedRows(intent);
        var states = new SafeMigrationObservedState[intent.RowCount];

        for (var row = 0; row < intent.RowCount; row++)
        {
            if (_modelManagedRows.TryGetValue(ModelManagedRowKey.Create(intent, row), out var projected))
            {
                if (TryClassify(intent, row, projected, out states[row]))
                {
                    continue;
                }

                return ProjectLiveModelManagedDependencies(intent, liveAnalysis);
            }

            if (!canInferMissingRows)
            {
                return ProjectLiveModelManagedDependencies(intent, liveAnalysis);
            }

            states[row] = SafeMigrationObservedState.Missing;
        }

        var state = AggregateModelManagedState(intent, states);

        if (intent is DeleteModelManagedDataIntent deletion
            && liveAnalysis.ObservedState == SafeMigrationObservedState.DataBlocked
            && !CanDischargeDependencies(deletion, liveAnalysis.ModelManagedDataEvidence))
        {
            return liveAnalysis;
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
            || prerequisites.DataMutationVersion != _providerDataMutationVersion)
        {
            return false;
        }

        // A preceding accepted table creation proves an empty relation. This
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

    private SafeMigrationProviderAnalysis ProjectLiveModelManagedDependencies(
        ModelManagedDataIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (intent is not DeleteModelManagedDataIntent deletion
            || liveAnalysis.ObservedState != SafeMigrationObservedState.DataBlocked
            || !CanDischargeDependencies(deletion, liveAnalysis.ModelManagedDataEvidence))
        {
            return liveAnalysis;
        }

        return new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.TransitionReady,
            SafeMigrationRepairCapability.None,
            postconditionSatisfied: false,
            "projected_dependency_handoff")
        {
            ModelManagedDataEvidence = liveAnalysis.ModelManagedDataEvidence,
        };
    }

    private bool CanDischargeDependencies(
        DeleteModelManagedDataIntent intent,
        SafeMigrationModelManagedDataEvidence? evidence
    )
    {
        if (evidence is null
            || evidence.DependencyCounts.Length != intent.ForeignKeys.Count)
        {
            return false;
        }

        for (var foreignKeyOrdinal = 0; foreignKeyOrdinal < intent.ForeignKeys.Count; foreignKeyOrdinal++)
        {
            var foreignKey = intent.ForeignKeys[foreignKeyOrdinal];
            var coveredRows = _acceptedModelManagedDeletes.LongCount(row =>
                MatchesDependency(row, foreignKey, intent));

            if (coveredRows != evidence.DependencyCounts[foreignKeyOrdinal])
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesDependency(
        AcceptedModelManagedDeleteRow deletedRow,
        ExpectedModelManagedDataForeignKeyDefinition foreignKey,
        DeleteModelManagedDataIntent principalDelete
    )
    {
        if (!StringComparer.Ordinal.Equals(deletedRow.Intent.Table, foreignKey.Table)
            || !StringComparer.Ordinal.Equals(deletedRow.Intent.Schema, foreignKey.Schema))
        {
            return false;
        }

        var dependentOrdinals = new int[foreignKey.Columns.Count];
        var principalOrdinals = new int[foreignKey.PrincipalColumns.Count];

        for (var column = 0; column < foreignKey.Columns.Count; column++)
        {
            dependentOrdinals[column] = IndexOf(deletedRow.Intent.Columns, foreignKey.Columns[column]);
            principalOrdinals[column] = IndexOf(principalDelete.Columns, foreignKey.PrincipalColumns[column]);

            if (dependentOrdinals[column] < 0 || principalOrdinals[column] < 0)
            {
                return false;
            }
        }

        for (var principalRow = 0; principalRow < principalDelete.RowCount; principalRow++)
        {
            var matches = true;

            for (var column = 0; column < dependentOrdinals.Length; column++)
            {
                if (SafeMigrationModelManagedValue.AreEqual(
                        deletedRow.Intent.OldValues.GetUnsafeValue(
                            deletedRow.Row,
                            dependentOrdinals[column]),
                        principalDelete.OldValues.GetUnsafeValue(
                            principalRow,
                            principalOrdinals[column])))
                {
                    continue;
                }

                matches = false;
                break;
            }

            if (matches)
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
            var currentKey = ModelManagedRowKey.Create(intent, row);

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
                        || !StringComparer.Ordinal.Equals(candidateKey.Table, intent.Table)
                        || !StringComparer.Ordinal.Equals(candidateKey.Schema, intent.Schema))
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
        SafeMigrationProviderAnalysis analysis
    )
    {
        for (var row = 0; row < intent.RowCount; row++)
        {
            var key = ModelManagedRowKey.Create(intent, row);

            if (intent is DeleteModelManagedDataIntent deletion)
            {
                if (IsSourceRow(deletion, row, analysis.ModelManagedDataEvidence))
                {
                    _acceptedModelManagedDeletes.Add(new AcceptedModelManagedDeleteRow(deletion, row));
                }

                _modelManagedRows[key] = ProjectedModelManagedRow.Absent;
                continue;
            }

            var projected = _modelManagedRows.TryGetValue(key, out var existing) && existing.Exists
                ? existing.Copy()
                : new ProjectedModelManagedRow(exists: true);

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
    }

    private void InvalidateModelManagedDataProjection()
    {
        // Dependency handoff evidence is meaningful only while the row
        // projection that established it remains authoritative. Opaque data
        // mutation invalidates both collections as one proof boundary.
        _modelManagedRows.Clear();
        _acceptedModelManagedDeletes.Clear();
    }

    private bool IsSourceRow(
        DeleteModelManagedDataIntent intent,
        int row,
        SafeMigrationModelManagedDataEvidence? evidence
    )
    {
        if (_modelManagedRows.TryGetValue(ModelManagedRowKey.Create(intent, row), out var projected)
            && projected.Exists
            && Matches(projected, intent.Columns, intent.OldValues, row, out var allKnown)
            && allKnown)
        {
            return true;
        }

        return evidence is not null
            && evidence.RowStates.Length == intent.RowCount
            && evidence.RowStates[row] == SafeMigrationModelManagedRowState.Source;
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
            int row
        )
        {
            using var writer = new CanonicalHashWriter();

            writer.Add(intent.KeyColumns.Count);

            for (var column = 0; column < intent.KeyColumns.Count; column++)
            {
                writer.Add(intent.KeyColumns[column]);
                SafeMigrationModelManagedValue.Write(writer, intent.KeyValues.GetUnsafeValue(row, column));
            }

            return new ModelManagedRowKey(intent.Table, intent.Schema, writer.GetHash());
        }
    }

    private static int ColumnOrdinal(
        IReadOnlyList<string> columns,
        string column
    )
    {
        for (var ordinal = 0; ordinal < columns.Count; ordinal++)
        {
            if (StringComparer.Ordinal.Equals(columns[ordinal], column))
            {
                return ordinal;
            }
        }

        throw new UnreachableException();
    }

    private static int IndexOf(
        IReadOnlyList<string> values,
        string expected
    )
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(values[index], expected))
            {
                return index;
            }
        }

        return -1;
    }

    private sealed record AcceptedModelManagedDeleteRow(
        DeleteModelManagedDataIntent Intent,
        int Row
    );

    private sealed class ProjectedModelManagedRow(
        bool exists
    )
    {
        public static ProjectedModelManagedRow Absent { get; } = new(exists: false);

        public bool Exists { get; } = exists;

        public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

        public ProjectedModelManagedRow Copy()
        {
            var result = new ProjectedModelManagedRow(Exists);

            foreach (var (column, value) in Values)
            {
                result.Values.Add(column, SafeMigrationModelManagedValue.Clone(value));
            }

            return result;
        }
    }
}
