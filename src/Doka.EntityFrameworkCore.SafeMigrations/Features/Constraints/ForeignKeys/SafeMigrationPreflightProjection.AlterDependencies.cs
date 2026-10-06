namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    /// <summary>Reads accepted FK column dependencies, cached until a relevant identity or FK mutation.</summary>
    /// <param name="intent">The exact projected column transition.</param>
    /// <returns>Whether an accepted incoming or outgoing FK references the column.</returns>
    private bool HasProjectedAlterForeignKeyDependency(
        AlterColumnIntent intent
    )
    {
        if (_projectedForeignKeyOwners.Count == 0)
        {
            return false;
        }

        if (_projectedForeignKeyColumns is null)
        {
            // WHY: Only accepted FK owners can contribute dependencies. Cache
            // their normalized columns until FK identities change, not per Alter.
            var columns = new Dictionary<TableKey, HashSet<string>>(new TableKeyComparer(_objectIdentityNormalizer));
            foreach (var owner in _projectedForeignKeyOwners)
            {
                if (_tables.TryGetValue(owner, out var table))
                {
                    foreach (var definition in table.ForeignKeys.Values)
                    {
                        CaptureForeignKeyColumns(columns, definition);
                    }
                }

                if (_prerequisites.TryGetValue(owner, out var prerequisites))
                {
                    foreach (var definition in prerequisites.ForeignKeys.Definitions)
                    {
                        CaptureForeignKeyColumns(columns, definition);
                    }
                }
            }

            _projectedForeignKeyColumns = columns;
        }

        return _projectedForeignKeyColumns.TryGetValue(
                ResolveAlterTable(new TableKey(intent.Table, intent.Schema)), out var referencedColumns)
            && referencedColumns.Contains(intent.Definition.Name);
    }

    /// <summary>Adds both FK endpoints under canonical table identities and normalized column names.</summary>
    /// <param name="dependencies">The dependency index being built from accepted owners.</param>
    /// <param name="definition">The accepted physical FK definition.</param>
    private void CaptureForeignKeyColumns(
        Dictionary<TableKey, HashSet<string>> dependencies,
        ExpectedForeignKeyDefinition definition
    )
    {
        Add(new TableKey(definition.Table, definition.Schema), definition.Columns);
        Add(new TableKey(definition.PrincipalTable, definition.PrincipalSchema), definition.PrincipalColumns);

        void Add(
            TableKey table,
            IReadOnlyList<string> columns
        )
        {
            var canonical = ResolveAlterTable(table);
            if (!dependencies.TryGetValue(canonical, out var names))
            {
                names = new HashSet<string>(new IdentifierComparer(_objectIdentityNormalizer));
                dependencies.Add(canonical, names);
            }

            names.UnionWith(columns);
        }
    }

    /// <summary>Resolves an accepted rename to its canonical original table identity.</summary>
    /// <param name="table">The current logical table identity.</param>
    /// <returns>The original physical identity, or the unchanged identity when no rename was accepted.</returns>
    private TableKey ResolveAlterTable(
        TableKey table
    ) => _renamedTableSources.TryGetValue(table, out var source) ? source : table;

    /// <summary>Refreshes one candidate owner and expires its derived column-dependency index when needed.</summary>
    /// <param name="table">The owner whose accepted FK definitions may have changed.</param>
    /// <param name="schema">The optional owner schema.</param>
    private void RefreshProjectedForeignKeyOwner(
        string table,
        string? schema
    )
    {
        var owner = new TableKey(table, schema);
        if ((_tables.TryGetValue(owner, out var projected)
                && projected.ForeignKeys.Count > 0)
            || (_prerequisites.TryGetValue(owner, out var prerequisites)
                && prerequisites.ForeignKeys.Any(static _ => true)))
        {
            _projectedForeignKeyOwners.Add(owner);
            _projectedForeignKeyColumns = null;
        }
        else
        {
            if (_projectedForeignKeyOwners.Remove(owner))
            {
                _projectedForeignKeyColumns = null;
            }
        }
    }

    /// <summary>
    /// Expires dependencies only for accepted FK or identity changes, retaining unrelated Alter caches.
    /// </summary>
    /// <param name="intent">The accepted operation whose postcondition has already been recorded.</param>
    /// <param name="action">The accepted decision, distinguishing mutation from evidence-only replay.</param>
    private void ObserveProjectedForeignKeyChange(
        SafeMigrationIntent intent,
        SafeMigrationAction action
    )
    {
        if (action == SafeMigrationAction.NoOp
            && intent is not EnsureTableIntent and not EnsureForeignKeyIntent)
        {
            return;
        }

        switch (intent)
        {
            case EnsureTableIntent value:
                RefreshProjectedForeignKeyOwner(value.Definition.Table, value.Definition.Schema);
                break;
            case EnsureForeignKeyIntent value:
                RefreshProjectedForeignKeyOwner(value.Definition.Table, value.Definition.Schema);
                break;
            case DropForeignKeyIntent value:
                RefreshProjectedForeignKeyOwner(value.Table, value.Schema);
                break;
            case DropTableIntent value:
                RefreshProjectedForeignKeyOwner(value.Table, value.Schema);
                break;
            case RenameTableIntent value:
                _projectedForeignKeyColumns = null;
                RefreshProjectedForeignKeyOwner(value.Name, value.Schema);
                RefreshProjectedForeignKeyOwner(value.NewName ?? value.Name, value.NewSchema ?? value.Schema);
                break;
            case DropColumnIntent value:
                _projectedForeignKeyColumns = null;
                RefreshProjectedForeignKeyOwner(value.Table, value.Schema);
                break;
            case RenameColumnIntent value:
                _projectedForeignKeyColumns = null;
                RefreshProjectedForeignKeyOwner(value.Table, value.Schema);
                break;
            case DropSchemaIntent:
                _projectedForeignKeyColumns = null;
                break;
        }
    }

    /// <summary>
    /// Refreshes recognized ordinary-operation identities after their projected postconditions are recorded.
    /// </summary>
    /// <param name="operation">The ordinary operation; opaque operations already clear all cached ownership.</param>
    private void ObserveProjectedForeignKeyChange(
        MigrationOperation operation
    )
    {
        switch (operation)
        {
            case CreateTableOperation value:
                RefreshProjectedForeignKeyOwner(value.Name, value.Schema);
                break;
            case DropTableOperation value:
                RefreshProjectedForeignKeyOwner(value.Name, value.Schema);
                break;
            case RenameTableOperation value:
                _projectedForeignKeyColumns = null;
                RefreshProjectedForeignKeyOwner(value.Name, value.Schema);
                RefreshProjectedForeignKeyOwner(value.NewName ?? value.Name, value.NewSchema ?? value.Schema);
                break;
            case DropColumnOperation value:
                _projectedForeignKeyColumns = null;
                RefreshProjectedForeignKeyOwner(value.Table, value.Schema);
                break;
            case RenameColumnOperation value:
                _projectedForeignKeyColumns = null;
                RefreshProjectedForeignKeyOwner(value.Table, value.Schema);
                break;
        }
    }
}
