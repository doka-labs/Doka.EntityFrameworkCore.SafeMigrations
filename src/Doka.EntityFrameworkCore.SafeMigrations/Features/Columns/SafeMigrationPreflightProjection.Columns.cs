namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private SafeMigrationProviderAnalysis Project(
        EnsureColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (TryGetProjectedColumnDefinition(
                intent.Table,
                intent.Schema,
                intent.Definition.Name,
                out var projectedDefinition))
        {
            // WHY: Provider repair evidence describes the historical live
            // definition. Once an earlier operation changes the projected
            // definition, only exact target equality remains proven.
            return Analysis(
                SafeMigrationDefinitionEquivalence.Column(projectedDefinition, intent.Definition)
                    ? SafeMigrationObservedState.Matching
                    : SafeMigrationObservedState.Different);
        }

        if (IsProjectedColumnMissing(intent.Table, intent.Schema, intent.Definition.Name))
        {
            return Analysis(SafeMigrationObservedState.Missing);
        }

        if (IsProjectedColumnUnknown(intent.Table, intent.Schema, intent.Definition.Name))
        {
            return StructureStateUnknown();
        }

        if (IsProjectedTableStructureUnknown(intent.Table, intent.Schema))
        {
            // WHY: A drop or provider-certified rename can change dependent
            // definitions. Only absence of an unrelated, safely additive
            // column survives that structural uncertainty.
            return liveAnalysis.ObservedState == SafeMigrationObservedState.Missing
                && SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(intent.Definition)
                    ? liveAnalysis
                    : StructureStateUnknown();
        }

        if (!TryGet(intent.Table, intent.Schema, out var table))
        {
            var unprojectedAnalysis = SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(intent.Definition)
                ? liveAnalysis
                : InvalidateDataDependentMissing(intent.Table, intent.Schema, liveAnalysis);

            return InvalidateStaleLiveDataProof(intent.Table, intent.Schema, unprojectedAnalysis);
        }

        var analysis = AnalyzeDefinition(
            table.Columns,
            intent.Definition.Name,
            intent.Definition,
            SafeMigrationDefinitionEquivalence.Column);

        analysis = SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(intent.Definition)
            ? analysis
            : InvalidateDataDependentMissing(table.Table, table.Schema, analysis);

        return InvalidateStaleLiveDataProof(table.Table, table.Schema, analysis);
    }

    private SafeMigrationProviderAnalysis Project(
        AlterColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (TryGetProjectedColumnDefinition(
                intent.Table,
                intent.Schema,
                intent.Definition.Name,
                out var projectedDefinition))
        {
            return AnalyzeAlterColumn(projectedDefinition, intent, liveAnalysis);
        }

        if (IsProjectedColumnMissing(intent.Table, intent.Schema, intent.Definition.Name))
        {
            return Analysis(SafeMigrationObservedState.Missing);
        }

        if (IsProjectedColumnUnknown(intent.Table, intent.Schema, intent.Definition.Name))
        {
            return StructureStateUnknown();
        }

        if (!TryGet(intent.Table, intent.Schema, out var table))
        {
            var key = new TableKey(intent.Table, intent.Schema);
            if (liveAnalysis.CanReuseAfterUnrelatedColumnDrops
                && liveAnalysis.ObservedState == SafeMigrationObservedState.Different
                && liveAnalysis.RepairCapability == SafeMigrationRepairCapability.Safe
                && intent.OldDefinition is not null
                && SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(intent.OldDefinition, intent.Definition)
                && _projectedColumnDropOnlyTables?.Contains(key) == true
                && (!_prerequisites.TryGetValue(key, out var sourcePrerequisites)
                    || !sourcePrerequisites.NewlyCreated)
                && !_projectedUnknownPhysicalKeys.Contains(key)
                && !HasProjectedAlterForeignKeyDependency(intent))
            {
                return HasUnanalyzedDataChanges(intent.Table, intent.Schema)
                    ? DataStateUnknown()
                    : liveAnalysis;
            }

            if (_projectedColumnAnalyzer is not null
                && intent.OldDefinition is not null
                && liveAnalysis.RepairCapability == SafeMigrationRepairCapability.Safe
                && liveAnalysis.ObservedState == SafeMigrationObservedState.Different)
            {
                return AnalyzeAlterColumn(intent.OldDefinition, intent, liveAnalysis);
            }

            if (_projectedStructurallyModifiedTables.Contains(new TableKey(intent.Table, intent.Schema))
                || IsProjectedTableStructureUnknown(intent.Table, intent.Schema))
            {
                return StructureStateUnknown();
            }

            return HasUnanalyzedDataChanges(intent.Table, intent.Schema)
                && liveAnalysis.ObservedState == SafeMigrationObservedState.Different
                && intent.OldDefinition?.IsNullable == true
                && !intent.Definition.IsNullable
                    ? DataStateUnknown()
                    : InvalidateStaleLiveDataProof(intent.Table, intent.Schema, liveAnalysis);
        }

        return table.Columns.TryGetValue(intent.Definition.Name, out var actual)
            ? AnalyzeAlterColumn(actual, intent, liveAnalysis)
            : Analysis(SafeMigrationObservedState.Missing);
    }

    private SafeMigrationProviderAnalysis Project(
        DropColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (TryGetProjectedColumnDefinition(intent.Table, intent.Schema, intent.Name, out _))
        {
            return Analysis(SafeMigrationObservedState.Matching);
        }

        if (IsProjectedColumnMissing(intent.Table, intent.Schema, intent.Name))
        {
            return Analysis(SafeMigrationObservedState.Missing);
        }

        if (IsProjectedColumnUnknown(intent.Table, intent.Schema, intent.Name))
        {
            return StructureStateUnknown();
        }

        return TryGet(intent.Table, intent.Schema, out var table)
            ? Analysis(
                table.Columns.ContainsKey(intent.Name)
                    ? SafeMigrationObservedState.Matching
                    : SafeMigrationObservedState.Missing)
            : liveAnalysis;
    }

    private SafeMigrationProviderAnalysis Project(
        RenameColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (IsProjectedColumnMissing(intent.Table, intent.Schema, intent.NewName)
            && liveAnalysis.ObservedState is SafeMigrationObservedState.Matching
                or SafeMigrationObservedState.Different)
        {
            // WHY: Rename analysis can be Different only because both names
            // exist in the immutable live snapshot. An accepted earlier drop of
            // the target removes that conflict and proves the rename is now applicable.

            return Analysis(SafeMigrationObservedState.Matching);
        }

        if (IsProjectedColumnMissing(intent.Table, intent.Schema, intent.Name))
        {
            return Analysis(SafeMigrationObservedState.Missing);
        }

        if (IsProjectedColumnUnknown(intent.Table, intent.Schema, intent.Name)
            || IsProjectedColumnUnknown(intent.Table, intent.Schema, intent.NewName))
        {
            return StructureStateUnknown();
        }

        if (TryGetProjectedColumnDefinition(intent.Table, intent.Schema, intent.Name, out _))
        {
            return Analysis(
                TryGetProjectedColumnDefinition(intent.Table, intent.Schema, intent.NewName, out _)
                && !IsProjectedColumnMissing(intent.Table, intent.Schema, intent.NewName)
                    ? SafeMigrationObservedState.Different
                    : SafeMigrationObservedState.Matching);
        }

        return TryGet(intent.Table, intent.Schema, out var table)
            ? Analysis(
                !table.Columns.ContainsKey(intent.Name)
                    ? SafeMigrationObservedState.Missing
                    : table.Columns.ContainsKey(intent.NewName)
                        ? SafeMigrationObservedState.Different
                        : SafeMigrationObservedState.Matching)
            : liveAnalysis;
    }

    private void Observe(
        EnsureColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.NoOp or SafeMigrationAction.Repair)
        {
            var prerequisites = GetOrCreateProviderPrerequisites(intent.Table, intent.Schema);

            prerequisites.Columns[intent.Definition.Name] = ProjectedColumn.From(
                intent.Definition,
                addedToExistingTable: decision.Action == SafeMigrationAction.Apply && !prerequisites.NewlyCreated);

            if (decision.Action == SafeMigrationAction.Apply
                && liveAnalysis.ObservedState == SafeMigrationObservedState.Missing
                && !SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(intent.Definition))
            {
                // WHY: Both providers classify this otherwise unsafe add as
                // Missing only after proving that the existing table has no
                // rows. Retain that bounded proof for later constraints.
                prerequisites.EmptyTableProofVersion = _providerDataMutationVersion;
            }
        }

        if (decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.NoOp or SafeMigrationAction.Repair)
        {
            ObserveAcceptedColumnDefinition(intent.Table, intent.Schema, intent.Definition, liveAnalysis, decision);
        }

        if (decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.Repair
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.AddColumn(intent.Definition);
        }

        if (decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.Repair)
        {
            if (!liveAnalysis.RepairPreservesPhysicalKeys)
            {
                InvalidateAcceptedIndexesForColumn(intent.Table, intent.Schema, intent.Definition.Name);
            }

            MarkProjectedColumnChanged(intent.Table, intent.Schema, intent.Definition.Name);
        }
    }

    private void Observe(
        AlterColumnIntent intent,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action is SafeMigrationAction.NoOp or SafeMigrationAction.Repair)
        {
            var prerequisites = GetOrCreateProviderPrerequisites(intent.Table, intent.Schema);

            prerequisites.Columns[intent.Definition.Name] = ProjectedColumn.From(
                intent.Definition,
                addedToExistingTable: false);
        }

        if (decision.Action is SafeMigrationAction.NoOp or SafeMigrationAction.Repair)
        {
            ObserveAcceptedColumnDefinition(intent.Table, intent.Schema, intent.Definition, analysis, decision);
        }

        if (decision.Action == SafeMigrationAction.Repair
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.Columns[intent.Definition.Name] = intent.Definition;
        }

        if (decision.Action == SafeMigrationAction.Repair)
        {
            if (!analysis.RepairPreservesPhysicalKeys)
            {
                InvalidateAcceptedIndexesForColumn(intent.Table, intent.Schema, intent.Definition.Name);
            }

            MarkProjectedColumnChanged(intent.Table, intent.Schema, intent.Definition.Name);
        }
    }

    private void Observe(
        DropColumnIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply)
        {
            // WHY: The provider may remove local indexes or constraints together
            // with the column. Their exact post-state must remain unknown.
            InvalidateModelManagedDataProjection();
            SetProjectedTableStructureUnknown(intent.Table, intent.Schema);
            RemoveDroppedPhysicalKeys(intent.Table, intent.Schema);
            SetProjectedColumnMissing(intent.Table, intent.Schema, intent.Name);
        }

        if (decision.Action == SafeMigrationAction.Apply
            && _prerequisites.TryGetValue(new TableKey(intent.Table, intent.Schema), out var prerequisites))
        {
            prerequisites.Columns.Remove(intent.Name);
        }
    }

    private void Observe(
        RenameColumnIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action != SafeMigrationAction.Apply)
        {
            return;
        }

        var key = new TableKey(intent.Table, intent.Schema);
        if (_prerequisites.TryGetValue(key, out var prerequisites)
            && prerequisites.NewlyCreated
            && _tables.TryGetValue(key, out var table))
        {
            InvalidateModelManagedDataProjection();
            RenameProjectedColumnDefinition(
                intent.Table,
                intent.Schema,
                intent.Name,
                intent.NewName);

            if (prerequisites.Columns.Remove(intent.Name, out var prerequisite))
            {
                prerequisites.Columns[intent.NewName] = prerequisite;
            }

            table.RenameColumn(intent.Name, intent.NewName);
            foreach (var projection in _tables.Values)
            {
                projection.RenamePrincipalColumn(table.Table, table.Schema, intent.Name, intent.NewName);
            }

            return;
        }

        // WHY: A rename on an existing table can rewrite provider-owned indexes,
        // expressions, and foreign keys outside the locally projected table.
        // Later safe operations must not trust the catalog snapshot captured
        // before the ordered migration started.
        if (_providerOperationProjection?.PreservesUnrelatedColumnAbsence(intent) == true)
        {
            InvalidateModelManagedDataProjection();
            SetProjectedTableStructureUnknown(intent.Table, intent.Schema);
            RemoveProjectedColumnDefinitions(intent.Table, intent.Schema);
            RemoveDroppedPhysicalKeys(intent.Table, intent.Schema);
            RenameProjectedColumnDefinition(
                intent.Table,
                intent.Schema,
                intent.Name,
                intent.NewName);

            return;
        }

        SetOpaqueProviderPostcondition(mayMutateData: false);
    }

    private SafeMigrationProviderAnalysis AnalyzeAlterColumn(
        ExpectedColumnDefinition actual,
        AlterColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        ArgumentNullException.ThrowIfNull(actual);

        if (SafeMigrationDefinitionEquivalence.Column(actual, intent.Definition))
        {
            return Analysis(SafeMigrationObservedState.Matching);
        }

        if (intent.OldDefinition is null
            || !SafeMigrationDefinitionEquivalence.Column(actual, intent.OldDefinition))
        {
            return Analysis(SafeMigrationObservedState.Different);
        }

        var key = new TableKey(intent.Table, intent.Schema);
        if (IsProjectedTableStructureUnknown(intent.Table, intent.Schema)
            || _projectedUnknownPhysicalKeys.Contains(key))
        {
            return StructureStateUnknown();
        }

        var provesEmpty = _prerequisites.TryGetValue(key, out var prerequisites)
            && ProvesTableEmpty(intent.Table, intent.Schema, prerequisites);
        var dataChanged = HasUnanalyzedDataChanges(intent.Table, intent.Schema);
        var tightensNullability = actual.IsNullable && !intent.Definition.IsNullable;

        // WHY: An exact old shape proves no facts about existing rows. A
        // projected definition must never erase a provider's blocking NULL or
        // length evidence, nor reuse that evidence after accepted DML.
        if (!provesEmpty
            && (liveAnalysis.RequiresLiveDataProof
                || tightensNullability))
        {
            if (dataChanged)
            {
                return DataStateUnknown();
            }

            if (liveAnalysis.ObservedState == SafeMigrationObservedState.DataBlocked)
            {
                return liveAnalysis;
            }
        }

        var repair = SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(actual, intent.Definition)
            ? SafeMigrationRepairCapability.Safe
            : SafeMigrationRepairCapability.None;

        if (tightensNullability
            && !provesEmpty
            && liveAnalysis.RepairCapability != SafeMigrationRepairCapability.Safe)
        {
            repair = SafeMigrationRepairCapability.None;
        }

        var analysis = Analysis(SafeMigrationObservedState.Different, repair);
        var canReuseLiveProof = !_projectedStructurallyModifiedTables.Contains(key)
            && !IsProjectedTableStructureUnknown(intent.Table, intent.Schema)
            && (!liveAnalysis.RequiresLiveDataProof || !dataChanged);

        _tables.TryGetValue(key, out var table);

        if (canReuseLiveProof
            && liveAnalysis.ObservedState == SafeMigrationObservedState.Different
            && liveAnalysis.RepairCapability == SafeMigrationRepairCapability.Safe)
        {
            analysis = liveAnalysis;
        }

        return _projectedColumnAnalyzer?.ValidateProjectedAlterColumn(
                intent,
                actual,
                new SafeMigrationProjectedAlterColumnContext(
                    (ISafeMigrationProjectedAlterTable?)table ?? new ProjectedAlterTableView(this, key),
                    HasCompleteTable: table is not null,
                    ProvesEmpty: provesEmpty,
                    HasForeignKeyDependency: HasProjectedAlterForeignKeyDependency(intent),
                    CanReuseLiveProof: canReuseLiveProof,
                    HasDataMutation: dataChanged,
                    PreservesLiveValueDomain: !dataChanged
                        && TryGetProjectedColumnState(intent.Table, intent.Schema, actual.Name, out var state)
                        && state.PreservesLiveValueDomain,
                    CanReuseCreationCharacterSet: table?.CanReuseCreationCharacterSet == true),
                liveAnalysis,
                analysis)
            ?? analysis;
    }

    private void ObserveAcceptedColumnDefinition(
        string table,
        string? schema,
        ExpectedColumnDefinition definition,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision
    )
    {
        var hasPrevious = TryGetProjectedColumnState(table, schema, definition.Name, out var previous);
        var preservesDomain = decision.Action switch
        {
            SafeMigrationAction.NoOp => hasPrevious && previous.PreservesLiveValueDomain,
            SafeMigrationAction.Repair => analysis.RepairPreservesLiveValueDomain
                && !analysis.RepairMutatesData
                && (!hasPrevious || previous.PreservesLiveValueDomain),
            _ => false,
        };

        // WHY: A later certificate cannot erase an earlier uncertified conversion.
        // Row epochs are checked separately because triggers can mutate another table.
        SetProjectedColumnDefinition(table, schema, definition, preservesDomain);
    }

    /// <summary>Reads accepted facts for an incomplete table without inventing missing live definitions.</summary>
    private sealed class ProjectedAlterTableView(
        SafeMigrationPreflightProjection projection,
        TableKey key
    ) : ISafeMigrationProjectedAlterTable
    {
        /// <inheritdoc />
        public IEnumerable<ExpectedColumnDefinition> Columns =>
            projection._projectedColumnStates.TryGetValue(key, out var columns)
                ? columns.Values.Where(static value => value.Definition is not null)
                    .Select(static value => value.Definition!)
                : [];

        /// <inheritdoc />
        public ExpectedPrimaryKeyDefinition? PrimaryKey =>
            projection._prerequisites.TryGetValue(key, out var prerequisites) ? prerequisites.PrimaryKey : null;

        /// <inheritdoc />
        public IEnumerable<ExpectedUniqueConstraintDefinition> UniqueConstraints =>
            projection._prerequisites.TryGetValue(key, out var prerequisites)
                ? prerequisites.UniqueConstraints.Definitions
                : [];

        /// <inheritdoc />
        public IEnumerable<ExpectedIndexDefinition> Indexes =>
            projection._prerequisites.TryGetValue(key, out var prerequisites)
                ? prerequisites.Indexes.Definitions
                : [];

        /// <inheritdoc />
        public bool TryGetProjectedColumn(
            string table,
            string? schema,
            string column,
            [NotNullWhen(true)] out ExpectedColumnDefinition? definition
        ) => projection.TryGetProjectedColumnDefinition(key.Table, key.Schema, column, out definition);
    }

    private sealed partial class ProjectedTable
    {
        public void AddColumn(
            ExpectedColumnDefinition definition
        )
        {
            if (!Columns.ContainsKey(definition.Name))
            {
                _columnOrder.Add(definition.Name);
            }

            Columns[definition.Name] = definition;
        }

        public void RemoveColumn(
            string name
        )
        {
            if (Columns.Remove(name))
            {
                _columnOrder.Remove(name);
            }
        }

        public void RenameColumn(
            string source,
            string target
        )
        {
            if (!Columns.Remove(source, out var column))
            {
                return;
            }

            Columns[target] = Copy(column, name: target);
            var ordinal = _columnOrder.IndexOf(source);
            if (ordinal >= 0)
            {
                _columnOrder[ordinal] = target;
            }

            ReplaceValues(
                Columns,
                value => value.ComputedColumnSql is not null
                    ? Copy(
                        value,
                        computedExpression: SafeMigrationSql.OpaqueAfterRename(value.ComputedColumnSql),
                        replaceComputed: true)
                    : value.ComputedExpression is not null
                        ? Copy(
                            value,
                            computedExpression: SafeMigrationSqlExpressionInspector.RenameIdentifier(
                                value.ComputedExpression,
                                source,
                                target),
                            replaceComputed: true)
                        : value);

            PrimaryKey = PrimaryKey is null
                ? null
                : Copy(PrimaryKey, columns: Rename(PrimaryKey.Columns, source, target));

            ReplaceValues(UniqueConstraints, value => Copy(value, columns: Rename(value.Columns, source, target)));
            ReplaceValues(
                CheckConstraints,
                value => value.Sql is not null
                    ? Copy(value, expression: SafeMigrationSql.OpaqueAfterRename(value.Sql), replaceExpression: true)
                    : Copy(
                        value,
                        expression: SafeMigrationSqlExpressionInspector.RenameIdentifier(
                            value.Expression!,
                            source,
                            target),
                        replaceExpression: true));
            ReplaceValues(
                ForeignKeys,
                value => Copy(
                    value,
                    columns: Rename(value.Columns, source, target),
                    principalColumns: SameIdentity(value.PrincipalTable, value.PrincipalSchema, _table, _schema)
                        ? Rename(value.PrincipalColumns, source, target)
                        : value.PrincipalColumns));

            ReplaceValues(
                Indexes,
                value => Copy(
                    value,
                    keys: value.Keys.Select(key => Copy(key, source, target)),
                    structuredFilter: value.Filter is not null
                        ? SafeMigrationSql.OpaqueAfterRename(value.Filter)
                        : value.StructuredFilter is null
                            ? null
                            : SafeMigrationSqlExpressionInspector.RenameIdentifier(
                                value.StructuredFilter,
                                source,
                                target),
                    replaceFilter: value.Filter is not null || value.StructuredFilter is not null));
        }

        public void RenamePrincipalColumn(
            string principalTable,
            string? principalSchema,
            string source,
            string target
        )
        {
            ReplaceValues(
                ForeignKeys,
                value => SameIdentity(value.PrincipalTable, value.PrincipalSchema, principalTable, principalSchema)
                    ? Copy(value, principalColumns: Rename(value.PrincipalColumns, source, target))
                    : value);
        }
    }
}
