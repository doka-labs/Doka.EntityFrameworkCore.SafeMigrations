namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private SafeMigrationProviderAnalysis Project(
        EnsureColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => TryGet(intent.Table, intent.Schema, out var table)
        ? AnalyzeDefinition(
            table.Columns,
            intent.Definition.Name,
            intent.Definition,
            SafeMigrationDefinitionEquivalence.Column)
        : liveAnalysis;

    private SafeMigrationProviderAnalysis Project(
        AlterColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => TryGet(intent.Table, intent.Schema, out var table) ? AnalyzeAlterColumn(table, intent) : liveAnalysis;

    private SafeMigrationProviderAnalysis Project(
        DropColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => TryGet(intent.Table, intent.Schema, out var table)
        ? Analysis(
            table.Columns.ContainsKey(intent.Name)
                ? SafeMigrationObservedState.Matching
                : SafeMigrationObservedState.Missing)
        : liveAnalysis;

    private SafeMigrationProviderAnalysis Project(
        RenameColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => TryGet(intent.Table, intent.Schema, out var table)
        ? Analysis(
            !table.Columns.ContainsKey(intent.Name)
                ? SafeMigrationObservedState.Missing
                : table.Columns.ContainsKey(intent.NewName)
                    ? SafeMigrationObservedState.Different
                    : SafeMigrationObservedState.Matching)
        : liveAnalysis;

    private void Observe(
        EnsureColumnIntent intent,
        SafeMigrationDecision decision
    )
    {
        var key = new TableKey(intent.Table, intent.Schema);
        if (_prerequisites.TryGetValue(key, out var prerequisites)
            && decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.NoOp or SafeMigrationAction.Repair)
        {
            prerequisites.Columns[intent.Definition.Name] = new ProjectedColumn(
                intent.Definition,
                AddedToExistingTable: decision.Action == SafeMigrationAction.Apply && !prerequisites.NewlyCreated);
        }

        if (decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.Repair
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.AddColumn(intent.Definition);
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
            prerequisites.Columns[intent.Definition.Name] = new ProjectedColumn(
                intent.Definition,
                AddedToExistingTable: false);
        }

        if (decision.Action == SafeMigrationAction.Repair
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.Columns[intent.Definition.Name] = intent.Definition;
        }
    }

    private void Observe(
        DropColumnIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply
            && _prerequisites.TryGetValue(new TableKey(intent.Table, intent.Schema), out var prerequisites))
        {
            prerequisites.Columns.Remove(intent.Name);
        }

        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.RemoveColumn(intent.Name);
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

        if (_prerequisites.TryGetValue(new TableKey(intent.Table, intent.Schema), out var prerequisites)
            && prerequisites.Columns.Remove(intent.Name, out var prerequisite))
        {
            prerequisites.Columns[intent.NewName] = new ProjectedColumn(
                prerequisite.Definition,
                prerequisite.AddedToExistingTable);
        }

        if (!TryGet(intent.Table, intent.Schema, out var table))
        {
            return;
        }

        table.RenameColumn(intent.Name, intent.NewName);
        foreach (var projection in _tables.Values)
        {
            projection.RenamePrincipalColumn(table.Table, table.Schema, intent.Name, intent.NewName);
        }
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
