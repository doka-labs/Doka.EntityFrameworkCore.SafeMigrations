namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private void ObserveProviderDataMutation() =>
        // WHY: EF's typed data operations cannot change schema prerequisites, but
        // triggers can change rows beyond the named table. Preserve structural
        // facts while invalidating every data-dependent proof that existed
        // before this operation. A monotonic version keeps this O(1) even for
        // migrations with many seed rows and projected tables.
        _providerDataMutationVersion++;

    private void ObserveProviderPostcondition(
        CreateTableOperation operation
    )
    {
        var key = new TableKey(operation.Name, operation.Schema);
        var prerequisites = new ProjectedPrerequisites(
            newlyCreated: true,
            dataMutationVersion: _providerDataMutationVersion);

        try
        {
            _tables[key] = new ProjectedTable(
                SafeMigrationExpectedDefinitionFactory.From(operation),
                dataMutationVersion: _providerDataMutationVersion);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            // WHY: The existence postcondition remains valid, but strict shape
            // comparison must fail closed when any provider facet is opaque.
            _tables.Remove(key);
        }

        RemoveProjectedColumnDefinitions(operation.Name, operation.Schema);
        RemoveDroppedIndexes(operation.Name, operation.Schema);
        _projectedMissingTables.Remove(key);
        _projectedUnknownTableStructures.Remove(key);
        _projectedStructurallyModifiedTables.Remove(key);
        _projectedChangedColumns.Remove(key);

        foreach (var column in operation.Columns)
        {
            prerequisites.Columns[column.Name] = ProjectedColumn.From(
                column,
                addedToExistingTable: false);

            CaptureProjectedProviderColumnDefinition(
                operation.Name,
                operation.Schema,
                column);
        }

        _prerequisites[key] = prerequisites;
    }

    private void ObserveProviderPostcondition(
        AddColumnOperation operation
    )
    {
        var key = new TableKey(operation.Table, operation.Schema);

        _tables.Remove(key);
        _projectedMissingTables.Remove(key);
        CaptureProjectedProviderColumnDefinition(operation.Table, operation.Schema, operation);

        var prerequisites = GetOrCreateProviderPrerequisites(operation.Table, operation.Schema);

        prerequisites.Columns[operation.Name] = ProjectedColumn.From(
            operation,
            addedToExistingTable: !prerequisites.NewlyCreated);

        MarkProjectedColumnChanged(operation.Table, operation.Schema, operation.Name);
    }

    private void ObserveProviderPostcondition(
        AlterColumnOperation operation
    )
    {
        var key = new TableKey(operation.Table, operation.Schema);

        _tables.Remove(key);
        _projectedMissingTables.Remove(key);
        CaptureProjectedProviderColumnDefinition(operation.Table, operation.Schema, operation);

        var prerequisites = GetOrCreateProviderPrerequisites(operation.Table, operation.Schema);

        // WHY: Altering an existing column proves presence but not how pre-existing
        // rows populate a later unique key. Keep that safety fact conservative.
        prerequisites.Columns[operation.Name] = ProjectedColumn.From(
            operation,
            addedToExistingTable: false);

        MarkProjectedColumnChanged(operation.Table, operation.Schema, operation.Name);
    }

    private void ObserveProviderPostcondition(
        AlterTableOperation operation
    )
    {
        // WHY: AlterTableOperation can carry provider annotations that the generic
        // projection cannot reconstruct. Preserve exact column and dropped-index
        // facts, but never reuse a historical complete-table observation.
        GetOrCreateProviderPrerequisites(operation.Name, operation.Schema);
        SetProjectedTableStructureUnknown(operation.Name, operation.Schema);
    }

    private void ObserveProviderPostcondition(
        DropColumnOperation operation
    )
    {
        // WHY: A provider drop can also remove local indexes or constraints. Mark
        // that table's aggregate structure unknown while retaining exact facts
        // for unrelated tables and the removed column itself.
        SetProjectedTableStructureUnknown(operation.Table, operation.Schema);
        RemoveDroppedIndexes(operation.Table, operation.Schema);
        InvalidateModelManagedDataProjection();

        var prerequisites = GetOrCreateProviderPrerequisites(operation.Table, operation.Schema);

        prerequisites.Columns.Remove(operation.Name);

        SetProjectedColumnMissing(operation.Table, operation.Schema, operation.Name);
    }

    private void ObserveProviderPostcondition(
        RenameColumnOperation operation
    )
    {
        var key = new TableKey(operation.Table, operation.Schema);
        if (_prerequisites.TryGetValue(key, out var prerequisites)
            && prerequisites.NewlyCreated
            && _tables.TryGetValue(key, out var table))
        {
            InvalidateModelManagedDataProjection();

            var column = prerequisites.Columns.Remove(operation.Name, out var projected)
                ? projected
                : ProjectedColumn.Unknown;

            prerequisites.Columns[operation.NewName] = column;
            RenameProjectedColumnDefinition(
                operation.Table,
                operation.Schema,
                operation.Name,
                operation.NewName);
            table.RenameColumn(operation.Name, operation.NewName);

            foreach (var projection in _tables.Values)
            {
                projection.RenamePrincipalColumn(
                    operation.Table,
                    operation.Schema,
                    operation.Name,
                    operation.NewName);
            }

            return;
        }

        // WHY: A column rename can rewrite local expressions and foreign keys in
        // other tables. No bounded table-local projection can prove every
        // provider-owned side effect, so later safe operations fail closed.
        SetOpaqueProviderPostcondition(mayMutateData: false);
    }

    private void ObserveProviderPostcondition(
        DropIndexOperation operation
    )
    {
        if (operation.Table is null)
        {
            // WHY: Some providers identify an index without its owning table. The
            // generic projection cannot bind that drop to one safe target, so
            // discard complete index knowledge instead of inventing ownership.
            SetOpaqueProviderPostcondition(mayMutateData: false);
            return;
        }

        var key = new IndexKey(operation.Table, operation.Schema, operation.Name);

        _droppedIndexes.Add(key);
        if (_tables.TryGetValue(new TableKey(operation.Table, operation.Schema), out var table))
        {
            table.Indexes.Remove(operation.Name);
        }
    }

    private void ObserveProviderPostcondition(
        DropTableOperation operation
    )
    {
        var key = new TableKey(operation.Name, operation.Schema);

        _tables.Remove(key);
        _prerequisites.Remove(key);
        _projectedMissingTables.Add(key);
        _projectedUnknownTableStructures.Remove(key);
        _projectedStructurallyModifiedTables.Remove(key);
        _projectedChangedColumns.Remove(key);
        RemoveProjectedColumnDefinitions(operation.Name, operation.Schema);
        RemoveDroppedIndexes(operation.Name, operation.Schema);
        InvalidateModelManagedDataProjection();
    }

    private void ObserveProviderPostcondition(
        RenameTableOperation operation
    )
    {
        var source = new TableKey(operation.Name, operation.Schema);
        if (_prerequisites.TryGetValue(source, out var prerequisites)
            && prerequisites.NewlyCreated
            && _tables.Remove(source, out var table))
        {
            InvalidateModelManagedDataProjection();

            var targetTable = operation.NewName ?? operation.Name;
            var targetSchema = operation.NewSchema ?? operation.Schema;
            var target = new TableKey(targetTable, targetSchema);

            _projectedMissingTables.Add(source);
            _projectedMissingTables.Remove(target);
            RenameProjectedTableColumnDefinitions(
                operation.Name,
                operation.Schema,
                targetTable,
                targetSchema);

            _prerequisites.Remove(source);
            _prerequisites[target] = prerequisites;
            table.RenameTable(targetTable, targetSchema);

            foreach (var projection in _tables.Values)
            {
                projection.RenamePrincipalTable(
                    operation.Name,
                    operation.Schema,
                    targetTable,
                    targetSchema);
            }

            _tables[target] = table;
            return;
        }

        // WHY: A table rename can rewrite foreign keys in any referencing table.
        // Without a complete database-wide projection, retaining narrower
        // facts would make pre-batch catalog evidence appear current.
        SetOpaqueProviderPostcondition(mayMutateData: false);
    }

    private ProjectedPrerequisites GetOrCreateProviderPrerequisites(
        string table,
        string? schema
    )
    {
        var key = new TableKey(table, schema);
        if (_prerequisites.TryGetValue(key, out var prerequisites))
        {
            return prerequisites;
        }

        prerequisites = new ProjectedPrerequisites(
            newlyCreated: false,
            // WHY: The table already existed when this prerequisite was first
            // observed. A preceding data operation may therefore have changed
            // its rows, even when the structural provider operation is newer.
            dataMutationVersion: 0);
        _prerequisites.Add(key, prerequisites);

        return prerequisites;
    }

    private void RemoveDroppedIndexes(
        string table,
        string? schema
    ) => _droppedIndexes.RemoveWhere(key => StringComparer.Ordinal.Equals(key.Table, table)
        && StringComparer.Ordinal.Equals(key.Schema, schema));

    private void CaptureProjectedProviderColumnDefinition(
        string table,
        string? schema,
        ColumnOperation operation
    )
    {
        try
        {
            SetProjectedColumnDefinition(
                table,
                schema,
                SafeMigrationExpectedDefinitionFactory.From(operation));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            // WHY: Ordinary EF operations are provider-owned and must remain
            // executable even when an annotation cannot be snapshotted. Only a
            // later safe operation is blocked from trusting an incomplete shape.
            SetProjectedColumnUnknown(table, schema, operation.Name);
        }
    }

    private void SetOpaqueProviderPostcondition(
        bool mayMutateData
    )
    {
        if (mayMutateData)
        {
            ObserveProviderDataMutation();
        }

        _tables.Clear();
        _prerequisites.Clear();
        _droppedIndexes.Clear();
        _projectedColumnDefinitions.Clear();
        _projectedMissingColumns.Clear();
        _projectedUnknownColumns.Clear();
        _projectedMissingTables.Clear();
        _projectedUnknownTableStructures.Clear();
        _projectedStructurallyModifiedTables.Clear();
        _projectedChangedColumns.Clear();
        _hasOpaqueProviderPostcondition = true;
        InvalidateModelManagedDataProjection();
    }
}
