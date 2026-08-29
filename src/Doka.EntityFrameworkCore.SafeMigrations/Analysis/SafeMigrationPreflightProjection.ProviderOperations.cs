namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private void ObserveProviderPostcondition(
        CreateTableOperation operation
    )
    {
        var key = new TableKey(operation.Name, operation.Schema);
        var prerequisites = new ProjectedPrerequisites(newlyCreated: true);

        _tables.Remove(key);

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
        DropColumnOperation operation
    )
    {
        // A provider drop can also remove dependent indexes or constraints.
        // Discard complete shapes because those cascade effects are provider-owned.
        _tables.Clear();

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

        var prerequisites = GetOrCreateProviderPrerequisites(operation.Table, operation.Schema);
        var column = prerequisites.Columns.Remove(operation.Name, out var projected)
            ? projected
            : ProjectedColumn.Unknown;

        prerequisites.Columns[operation.NewName] = column;
    }

    private void ObserveProviderPostcondition(
        DropTableOperation operation
    )
    {
        _tables.Clear();
        _prerequisites.Remove(new TableKey(operation.Name, operation.Schema));
    }

    private void ObserveProviderPostcondition(
        RenameTableOperation operation
    )
    {
        _tables.Clear();

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

        prerequisites = new ProjectedPrerequisites(newlyCreated: false);
        _prerequisites.Add(key, prerequisites);

        return prerequisites;
    }
}
