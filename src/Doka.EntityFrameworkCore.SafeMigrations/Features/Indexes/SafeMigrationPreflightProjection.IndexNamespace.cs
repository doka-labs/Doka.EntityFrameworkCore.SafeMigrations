namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    /// <summary>Reads current relation-name occupancy when the index namespace spans owner tables.</summary>
    /// <param name="intent">The index rename's verified owner schema and destination name.</param>
    /// <returns>Current presence/removal, or null when immutable destination evidence remains authoritative.</returns>
    private bool? ProjectSchemaScopedIndexDestination(RenameIndexIntent intent)
    {
        if (_objectIdentityNormalizer?.IndexUsesSchemaScopedIdentity != true)
        {
            return null;
        }

        var comparer = _objectIdentityNormalizer.IdentifierComparer;
        var indexComparer = _objectIdentityNormalizer.IndexIdentifierComparer;
        var targetSchema = _objectIdentityNormalizer.NormalizeSchema(intent.Schema);
        var keyBackingNames = _objectIdentityNormalizer.KeyConstraintsUseSchemaScopedBackingIndexNames;

        // WHY: Renames are rare and schema-global providers can reuse a relation
        // name under another owner. Inspect current accepted maps without a
        // second per-operation catalog or a cache whose invalidation can drift.
        // Current presence always wins over an older removal tombstone.
        foreach (var pair in _tables)
        {
            if (!SameIndexSchema(pair.Key, targetSchema) || _projectedMissingTables.Contains(pair.Key))
            {
                continue;
            }

            var table = pair.Value;
            if (comparer.Equals(pair.Key.Table, intent.NewName)
                || table.Indexes.ContainsKey(intent.NewName)
                || (keyBackingNames
                    && ((table.PrimaryKey is not null
                            && indexComparer.Equals(table.PrimaryKey.Name, intent.NewName))
                        || table.UniqueConstraints.ContainsKey(intent.NewName))))
            {
                return true;
            }
        }

        foreach (var pair in _prerequisites)
        {
            if (!SameIndexSchema(pair.Key, targetSchema) || _projectedMissingTables.Contains(pair.Key))
            {
                continue;
            }

            var prerequisites = pair.Value;
            if (comparer.Equals(pair.Key.Table, intent.NewName)
                || (prerequisites.Indexes.TryGetPhysicalState(intent.NewName, out var indexPresent) && indexPresent)
                || (keyBackingNames
                    && ((prerequisites.PrimaryKeyPhysicalName is { } primaryName
                            && indexComparer.Equals(primaryName, intent.NewName))
                        || (prerequisites.UniqueConstraints.TryGetPhysicalState(intent.NewName, out var uniquePresent)
                            && uniquePresent))))
            {
                return true;
            }
        }

        foreach (var missing in _droppedPhysicalKeys)
        {
            if (SameIndexSchema(new TableKey(missing.Table, missing.Schema), targetSchema)
                && indexComparer.Equals(missing.Name, intent.NewName))
            {
                return false;
            }
        }

        foreach (var missing in _projectedMissingTables)
        {
            if (SameIndexSchema(missing, targetSchema) && comparer.Equals(missing.Table, intent.NewName))
            {
                return false;
            }
        }

        return null;
    }

    /// <summary>Compares verified effective schemas without broadening object or member identity.</summary>
    private bool SameIndexSchema(TableKey owner, string? schema) => StringComparer.Ordinal.Equals(
        _objectIdentityNormalizer is null ? owner.Schema : _objectIdentityNormalizer.NormalizeSchema(owner.Schema),
        schema);

    /// <summary>Retains known owned index removals before a physical table lifetime is discarded.</summary>
    /// <param name="owner">The actual table removed by an accepted drop.</param>
    private void CaptureDroppedTableIndexNames(TableKey owner)
    {
        if (_objectIdentityNormalizer?.IndexUsesSchemaScopedIdentity != true)
        {
            return;
        }

        var keyBackingNames = _objectIdentityNormalizer.KeyConstraintsUseSchemaScopedBackingIndexNames;
        if (_tables.TryGetValue(owner, out var table))
        {
            foreach (var pair in table.Indexes)
            {
                _droppedPhysicalKeys.Add(new IndexKey(owner.Table, owner.Schema, pair.Key));
            }

            if (keyBackingNames)
            {
                if (table.PrimaryKey is not null)
                {
                    _droppedPhysicalKeys.Add(new IndexKey(owner.Table, owner.Schema, table.PrimaryKey.Name));
                }

                foreach (var pair in table.UniqueConstraints)
                {
                    _droppedPhysicalKeys.Add(new IndexKey(owner.Table, owner.Schema, pair.Key));
                }
            }
        }

        if (_prerequisites.TryGetValue(owner, out var prerequisites))
        {
            prerequisites.Indexes.CaptureIndexTombstones(_droppedPhysicalKeys, owner);

            if (keyBackingNames)
            {
                if (prerequisites.PrimaryKeyPhysicalName is { } primaryName)
                {
                    _droppedPhysicalKeys.Add(new IndexKey(owner.Table, owner.Schema, primaryName));
                }

                // WHY: Unbound table-matched shapes are stored separately. Only
                // independently created or catalog-bound physical members enter
                // this set, so their actual backing names survive owner removal.
                prerequisites.UniqueConstraints.CaptureIndexTombstones(_droppedPhysicalKeys, owner);
            }
        }
    }
}
