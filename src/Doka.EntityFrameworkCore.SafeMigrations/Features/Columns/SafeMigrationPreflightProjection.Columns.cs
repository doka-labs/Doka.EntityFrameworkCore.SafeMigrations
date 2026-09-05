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
            return AnalyzeAlterColumn(projectedDefinition, intent);
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
            return HasUnanalyzedDataChanges(intent.Table, intent.Schema)
                && liveAnalysis.ObservedState == SafeMigrationObservedState.Different
                && intent.OldDefinition?.IsNullable == true
                && !intent.Definition.IsNullable
                    ? DataStateUnknown()
                    : liveAnalysis;
        }

        var analysis = AnalyzeAlterColumn(table, intent);

        return HasUnanalyzedDataChanges(table.Table, table.Schema)
            && analysis.ObservedState == SafeMigrationObservedState.Different
            && intent.OldDefinition?.IsNullable == true
            && !intent.Definition.IsNullable
                ? DataStateUnknown()
                : analysis;
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
        SafeMigrationDecision decision
    )
    {
        var key = new TableKey(intent.Table, intent.Schema);
        if (_prerequisites.TryGetValue(key, out var prerequisites)
            && decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.NoOp or SafeMigrationAction.Repair)
        {
            prerequisites.Columns[intent.Definition.Name] = ProjectedColumn.From(
                intent.Definition,
                addedToExistingTable: decision.Action == SafeMigrationAction.Apply && !prerequisites.NewlyCreated);
        }

        if (decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.NoOp or SafeMigrationAction.Repair)
        {
            SetProjectedColumnDefinition(intent.Table, intent.Schema, intent.Definition);
        }

        if (decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.Repair
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.AddColumn(intent.Definition);
        }

        if (decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.Repair)
        {
            MarkProjectedColumnChanged(intent.Table, intent.Schema, intent.Definition.Name);
        }
    }

    private void Observe(
        AlterColumnIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Repair
            && _prerequisites.TryGetValue(new TableKey(intent.Table, intent.Schema), out var prerequisites))
        {
            prerequisites.Columns[intent.Definition.Name] = ProjectedColumn.From(
                intent.Definition,
                addedToExistingTable: false);
        }

        if (decision.Action is SafeMigrationAction.NoOp or SafeMigrationAction.Repair)
        {
            SetProjectedColumnDefinition(intent.Table, intent.Schema, intent.Definition);
        }

        if (decision.Action == SafeMigrationAction.Repair
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.Columns[intent.Definition.Name] = intent.Definition;
        }

        if (decision.Action == SafeMigrationAction.Repair)
        {
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
            RemoveDroppedIndexes(intent.Table, intent.Schema);
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
        SetOpaqueProviderPostcondition(mayMutateData: false);
    }

    private static SafeMigrationProviderAnalysis AnalyzeAlterColumn(
        ProjectedTable table,
        AlterColumnIntent intent
    )
    {
        if (!table.Columns.TryGetValue(intent.Definition.Name, out var actual))
        {
            return Analysis(SafeMigrationObservedState.Missing);
        }

        return AnalyzeAlterColumn(actual, intent);
    }

    private static SafeMigrationProviderAnalysis AnalyzeAlterColumn(
        ExpectedColumnDefinition actual,
        AlterColumnIntent intent
    )
    {
        ArgumentNullException.ThrowIfNull(actual);

        if (SafeMigrationDefinitionEquivalence.Column(actual, intent.Definition))
        {
            return Analysis(SafeMigrationObservedState.Matching);
        }

        var repair = intent.OldDefinition is not null
            && SafeMigrationDefinitionEquivalence.Column(actual, intent.OldDefinition)
            && SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(intent.OldDefinition, intent.Definition)
                ? SafeMigrationRepairCapability.Safe
                : SafeMigrationRepairCapability.None;

        return Analysis(SafeMigrationObservedState.Different, repair);
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
