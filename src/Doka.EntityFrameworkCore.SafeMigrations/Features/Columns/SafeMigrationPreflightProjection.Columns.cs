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
            !table.Columns.ContainsKey(intent.Name) ? SafeMigrationObservedState.Missing :
            table.Columns.ContainsKey(intent.NewName) ? SafeMigrationObservedState.Different :
            SafeMigrationObservedState.Matching)
        : liveAnalysis;

    private void Observe(
        EnsureColumnIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply
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
        if (decision.Action != SafeMigrationAction.Apply
            || !TryGet(intent.Table, intent.Schema, out var table))
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
                value => value.ComputedColumnSql is null
                    ? value
                    : Copy(value, computedColumnSql: RenameIdentifier(value.ComputedColumnSql, source, target)));

            PrimaryKey = PrimaryKey is null
                ? null
                : Copy(PrimaryKey, columns: Rename(PrimaryKey.Columns, source, target));

            ReplaceValues(UniqueConstraints, value => Copy(value, columns: Rename(value.Columns, source, target)));
            ReplaceValues(CheckConstraints, value => Copy(value, sql: RenameIdentifier(value.Sql, source, target)));
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
                    filter: value.Filter is null ? null : RenameIdentifier(value.Filter, source, target)));
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
