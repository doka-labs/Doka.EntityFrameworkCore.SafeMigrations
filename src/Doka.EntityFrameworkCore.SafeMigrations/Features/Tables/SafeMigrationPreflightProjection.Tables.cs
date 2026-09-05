namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private SafeMigrationProviderAnalysis Project(
        EnsureTableIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        var key = new TableKey(intent.Definition.Table, intent.Definition.Schema);
        if (_projectedMissingTables.Contains(key))
        {
            return Analysis(SafeMigrationObservedState.Missing);
        }

        if (IsProjectedTableStructureUnknown(intent.Definition.Table, intent.Definition.Schema))
        {
            return intent.Mode == SafeMigrationTableMode.ConvergenceContainer
                ? Analysis(SafeMigrationObservedState.Matching)
                : StructureStateUnknown();
        }

        if (!TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
            if (_prerequisites.TryGetValue(key, out var prerequisites))
            {
                if (intent.Mode == SafeMigrationTableMode.ConvergenceContainer)
                {
                    return Analysis(SafeMigrationObservedState.Matching);
                }

                // WHY: A matching convergence container is only an existence
                // proof and does not itself stale the provider's live strict
                // analysis. A created or structurally changed table does.
                if (prerequisites.NewlyCreated)
                {
                    return StructureStateUnknown();
                }

                if (!_projectedStructurallyModifiedTables.Contains(key))
                {
                    return liveAnalysis;
                }

                return HasProjectedStrictTableDifference(intent.Definition)
                    ? Analysis(SafeMigrationObservedState.Different)
                    : StructureStateUnknown();
            }

            return liveAnalysis;
        }

        return Analysis(
            intent.Mode == SafeMigrationTableMode.ConvergenceContainer
            || SafeMigrationDefinitionEquivalence.Table(table.Definition, intent.Definition)
                ? SafeMigrationObservedState.Matching
                : SafeMigrationObservedState.Different);
    }

    private SafeMigrationProviderAnalysis Project(
        DropTableIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        var key = new TableKey(intent.Table, intent.Schema);
        if (_projectedMissingTables.Contains(key))
        {
            return Analysis(SafeMigrationObservedState.Missing);
        }

        return TryGet(intent.Table, intent.Schema, out _)
            || _prerequisites.ContainsKey(key)
                ? Analysis(SafeMigrationObservedState.Matching)
                : liveAnalysis;
    }

    private SafeMigrationProviderAnalysis Project(
        RenameTableIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        var source = new TableKey(intent.Name, intent.Schema);
        if (_projectedMissingTables.Contains(source))
        {
            return Analysis(SafeMigrationObservedState.Missing);
        }

        if (!TryGet(intent.Name, intent.Schema, out _)
            && !_prerequisites.ContainsKey(source))
        {
            return liveAnalysis;
        }

        var target = new TableKey(intent.NewName ?? intent.Name, intent.NewSchema ?? intent.Schema);

        return Analysis(
            Contains(intent.NewName ?? intent.Name, intent.NewSchema ?? intent.Schema)
            || _prerequisites.ContainsKey(target)
                ? SafeMigrationObservedState.Different
                : SafeMigrationObservedState.Matching);
    }

    private void Observe(
        EnsureTableIntent intent,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision
    )
    {
        var key = new TableKey(intent.Definition.Table, intent.Definition.Schema);
        if (analysis.ObservedState == SafeMigrationObservedState.Missing)
        {
            _projectedMissingTables.Remove(key);
            _projectedUnknownTableStructures.Remove(key);
            _projectedStructurallyModifiedTables.Remove(key);
            _projectedChangedColumns.Remove(key);
            RemoveProjectedColumnDefinitions(intent.Definition.Table, intent.Definition.Schema);
            foreach (var column in intent.Definition.Columns)
            {
                SetProjectedColumnDefinition(intent.Definition.Table, intent.Definition.Schema, column);
            }

            _tables[key] = new ProjectedTable(
                intent.Definition,
                dataMutationVersion: _providerDataMutationVersion);

            var prerequisites = new ProjectedPrerequisites(
                newlyCreated: true,
                dataMutationVersion: _providerDataMutationVersion);

            foreach (var column in intent.Definition.Columns)
            {
                prerequisites.Columns[column.Name] = ProjectedColumn.From(
                    column,
                    addedToExistingTable: false);
            }

            _prerequisites[key] = prerequisites;
            return;
        }

        if (intent.Mode == SafeMigrationTableMode.StrictDefinition
            && analysis.ObservedState == SafeMigrationObservedState.Matching)
        {
            _projectedMissingTables.Remove(key);
            _projectedUnknownTableStructures.Remove(key);
            RemoveProjectedColumnDefinitions(intent.Definition.Table, intent.Definition.Schema);
            foreach (var column in intent.Definition.Columns)
            {
                SetProjectedColumnDefinition(intent.Definition.Table, intent.Definition.Schema, column);
            }
        }

        if (intent.Mode == SafeMigrationTableMode.ConvergenceContainer
            && analysis.ObservedState == SafeMigrationObservedState.Matching)
        {
            _projectedMissingTables.Remove(key);
            _prerequisites.TryAdd(
                key,
                new ProjectedPrerequisites(
                    newlyCreated: false,
                    // WHY: Matching proves that the table exists; it does not
                    // re-establish row-level safety after provider data DML.
                    dataMutationVersion: 0));
        }
    }

    private void Observe(
        DropTableIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply)
        {
            var key = new TableKey(intent.Table, intent.Schema);
            _tables.Remove(key);
            _prerequisites.Remove(key);
            _projectedDataMutationTables.Remove(key);
            _projectedModelManagedUniqueKeys.Remove(key);
            _projectedUnknownTableStructures.Remove(key);
            _projectedStructurallyModifiedTables.Remove(key);
            _projectedChangedColumns.Remove(key);
            RemoveProjectedColumnDefinitions(intent.Table, intent.Schema);
            _projectedMissingTables.Add(key);
            InvalidateModelManagedDataProjection();
        }
    }

    private void Observe(
        RenameTableIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action != SafeMigrationAction.Apply)
        {
            return;
        }

        var source = new TableKey(intent.Name, intent.Schema);
        if (_prerequisites.TryGetValue(source, out var prerequisites)
            && prerequisites.NewlyCreated
            && _tables.Remove(source, out var table))
        {
            _projectedModelManagedUniqueKeys.Remove(source, out var projectedUniqueKeys);
            InvalidateModelManagedDataProjection();

            var targetTable = intent.NewName ?? intent.Name;
            var targetSchema = intent.NewSchema ?? intent.Schema;
            var target = new TableKey(targetTable, targetSchema);
            _projectedMissingTables.Add(source);
            _projectedMissingTables.Remove(target);

            if (_projectedUnknownTableStructures.Remove(source))
            {
                _projectedUnknownTableStructures.Add(target);
            }

            RenameProjectedTableColumnDefinitions(
                intent.Name,
                intent.Schema,
                targetTable,
                targetSchema);

            if (_projectedDataMutationTables.Remove(source))
            {
                _projectedDataMutationTables.Add(target);
            }

            if (projectedUniqueKeys is not null)
            {
                // WHY: Renaming a table cannot change the uniqueness of rows
                // accepted earlier in this batch. Retain that exact proof so a
                // following unique index does not fail on stale table identity.
                _projectedModelManagedUniqueKeys.Add(target, projectedUniqueKeys);
            }

            _prerequisites.Remove(source);
            _prerequisites[target] = prerequisites;
            table.RenameTable(targetTable, targetSchema);
            foreach (var projection in _tables.Values)
            {
                projection.RenamePrincipalTable(intent.Name, intent.Schema, targetTable, targetSchema);
            }

            _tables[target] = table;
            return;
        }

        // WHY: A rename on an existing table can rewrite provider-owned foreign
        // keys outside the locally projected table. Retaining the pre-batch
        // snapshot would let a later safe operation act on stale identities.
        SetOpaqueProviderPostcondition(mayMutateData: false);
    }

    private sealed partial class ProjectedTable
    {
        public void RenameTable(
            string table,
            string? schema
        )
        {
            var oldTable = _table;
            var oldSchema = _schema;

            PrimaryKey = PrimaryKey is null ? null : Copy(PrimaryKey, table: table, schema: schema);
            ReplaceValues(UniqueConstraints, value => Copy(value, table: table, schema: schema));
            ReplaceValues(CheckConstraints, value => Copy(value, table: table, schema: schema));
            ReplaceValues(
                ForeignKeys,
                value => Copy(
                    value,
                    table: table,
                    schema: schema,
                    principalTable: SameIdentity(value.PrincipalTable, value.PrincipalSchema, oldTable, oldSchema)
                        ? table
                        : value.PrincipalTable,
                    principalSchema: SameIdentity(value.PrincipalTable, value.PrincipalSchema, oldTable, oldSchema)
                        ? schema
                        : value.PrincipalSchema));
            ReplaceValues(Indexes, value => Copy(value, table: table, schema: schema));

            _table = table;
            _schema = schema;
        }

        public void RenamePrincipalTable(
            string oldTable,
            string? oldSchema,
            string newTable,
            string? newSchema
        )
        {
            ReplaceValues(
                ForeignKeys,
                value => SameIdentity(value.PrincipalTable, value.PrincipalSchema, oldTable, oldSchema)
                    ? Copy(value, principalTable: newTable, principalSchema: newSchema)
                    : value);
        }
    }
}
