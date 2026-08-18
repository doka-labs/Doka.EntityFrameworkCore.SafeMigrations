namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private SafeMigrationProviderAnalysis Project(
        EnsureTableIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (!TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
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
    ) => TryGet(intent.Table, intent.Schema, out _) ? Analysis(SafeMigrationObservedState.Matching) : liveAnalysis;

    private SafeMigrationProviderAnalysis Project(
        RenameTableIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (!TryGet(intent.Name, intent.Schema, out _))
        {
            return liveAnalysis;
        }

        return Analysis(
            Contains(intent.NewName ?? intent.Name, intent.NewSchema ?? intent.Schema)
                ? SafeMigrationObservedState.Different
                : SafeMigrationObservedState.Matching);
    }

    private void Observe(
        EnsureTableIntent intent,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision
    )
    {
        if (analysis.ObservedState == SafeMigrationObservedState.Missing)
        {
            _tables[new TableKey(intent.Definition.Table, intent.Definition.Schema)] =
                new ProjectedTable(intent.Definition);
        }
    }

    private void Observe(
        DropTableIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply)
        {
            _tables.Remove(new TableKey(intent.Table, intent.Schema));
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
        if (!_tables.Remove(source, out var table))
        {
            return;
        }

        var targetTable = intent.NewName ?? intent.Name;
        var targetSchema = intent.NewSchema ?? intent.Schema;
        table.RenameTable(targetTable, targetSchema);
        foreach (var projection in _tables.Values)
        {
            projection.RenamePrincipalTable(intent.Name, intent.Schema, targetTable, targetSchema);
        }

        _tables[new TableKey(targetTable, targetSchema)] = table;
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
