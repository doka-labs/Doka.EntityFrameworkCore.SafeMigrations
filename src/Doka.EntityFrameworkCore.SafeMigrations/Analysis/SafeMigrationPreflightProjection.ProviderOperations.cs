namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private void ObserveProviderDataMutation() =>
        // EF's typed data operations cannot change schema prerequisites, but
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

        _tables.Remove(key);
        RemoveDroppedIndexes(operation.Name, operation.Schema);

        foreach (var column in operation.Columns)
        {
            prerequisites.Columns[column.Name] = ProjectedColumn.From(
                column,
                addedToExistingTable: false);
        }

        _prerequisites[key] = prerequisites;
    }

    private void ObserveProviderPostcondition(
        AddColumnOperation operation
    )
    {
        _tables.Remove(new TableKey(operation.Table, operation.Schema));

        var prerequisites = GetOrCreateProviderPrerequisites(operation.Table, operation.Schema);

        prerequisites.Columns[operation.Name] = ProjectedColumn.From(
            operation,
            addedToExistingTable: !prerequisites.NewlyCreated);
    }

    private void ObserveProviderPostcondition(
        AlterColumnOperation operation
    )
    {
        _tables.Remove(new TableKey(operation.Table, operation.Schema));

        var prerequisites = GetOrCreateProviderPrerequisites(operation.Table, operation.Schema);

        // Altering an existing column proves presence but not how pre-existing
        // rows populate a later unique key. Keep that safety fact conservative.
        prerequisites.Columns[operation.Name] = ProjectedColumn.From(
            operation,
            addedToExistingTable: false);
    }

    private void ObserveProviderPostcondition(
        AlterTableOperation operation
    )
    {
        // AlterTableOperation changes table metadata but cannot add columns or
        // recreate a dropped index. Discard the complete table definition while
        // retaining those narrower postconditions for later ordered operations.
        _tables.Remove(new TableKey(operation.Name, operation.Schema));
    }

    private void ObserveProviderPostcondition(
        DropColumnOperation operation
    )
    {
        // A provider drop can also remove dependent indexes or constraints.
        // Discard complete shapes because those cascade effects are provider-owned.
        _tables.Clear();
        _droppedIndexes.Clear();

        if (_prerequisites.TryGetValue(new TableKey(operation.Table, operation.Schema), out var prerequisites))
        {
            prerequisites.Columns.Remove(operation.Name);
        }
    }

    private void ObserveProviderPostcondition(
        RenameColumnOperation operation
    )
    {
        // Renames can rewrite local expressions and referencing foreign keys.
        // Compact prerequisites remain movable; complete shapes do not.
        _tables.Clear();
        _droppedIndexes.Clear();

        var prerequisites = GetOrCreateProviderPrerequisites(operation.Table, operation.Schema);
        var column = prerequisites.Columns.Remove(operation.Name, out var projected)
            ? projected
            : ProjectedColumn.Unknown;

        prerequisites.Columns[operation.NewName] = column;
    }

    private void ObserveProviderPostcondition(
        DropIndexOperation operation
    )
    {
        if (operation.Table is null)
        {
            // Some providers identify an index without its owning table. The
            // generic projection cannot bind that drop to one safe target, so
            // discard complete index knowledge instead of inventing ownership.
            _tables.Clear();
            _droppedIndexes.Clear();
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
        _tables.Clear();
        _prerequisites.Remove(new TableKey(operation.Name, operation.Schema));
        RemoveDroppedIndexes(operation.Name, operation.Schema);
    }

    private void ObserveProviderPostcondition(
        RenameTableOperation operation
    )
    {
        _tables.Clear();
        _droppedIndexes.Clear();

        var source = new TableKey(operation.Name, operation.Schema);
        if (!_prerequisites.Remove(source, out var prerequisites))
        {
            return;
        }

        _prerequisites[
            new TableKey(operation.NewName ?? operation.Name, operation.NewSchema ?? operation.Schema)] = prerequisites;
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
            // The table already existed when this prerequisite was first
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
}
