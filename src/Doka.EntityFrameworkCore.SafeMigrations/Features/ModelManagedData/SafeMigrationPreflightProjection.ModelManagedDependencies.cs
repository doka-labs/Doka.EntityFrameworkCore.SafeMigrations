namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    /// <summary>Detects accepted incoming keys that the source-frozen delete cannot guard.</summary>
    private bool HasUnmodeledProjectedIncomingForeignKey(DeleteModelManagedDataIntent intent)
    {
        if (_projectedForeignKeyOwners.Count == 0)
        {
            return false;
        }

        var target = ResolveAlterTable(new TableKey(intent.Table, intent.Schema));

        // WHY: Creating an FK changes the delete contract without changing row
        // contents. A pre-batch TransitionReady cannot certify this new topology.
        // Reuse tracked owners instead of scanning every projected table.
        foreach (var owner in _projectedForeignKeyOwners)
        {
            if (_tables.TryGetValue(owner, out var table))
            {
                foreach (var definition in table.ForeignKeys.Values)
                {
                    if (IsUnmodeledIncomingForeignKey(intent, target, definition))
                    {
                        return true;
                    }
                }
            }

            if (_prerequisites.TryGetValue(owner, out var prerequisites))
            {
                foreach (var definition in prerequisites.ForeignKeys.Definitions)
                {
                    if (IsUnmodeledIncomingForeignKey(intent, target, definition))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private bool IsUnmodeledIncomingForeignKey(
        DeleteModelManagedDataIntent intent,
        TableKey target,
        ExpectedForeignKeyDefinition definition
    )
    {
        var principal = ResolveAlterTable(new TableKey(definition.PrincipalTable, definition.PrincipalSchema));

        if (!_prerequisites.Comparer.Equals(principal, target))
        {
            return false;
        }

        var dependent = ResolveAlterTable(new TableKey(definition.Table, definition.Schema));

        foreach (var modeled in intent.ForeignKeys)
        {
            var modeledOwner = ResolveAlterTable(new TableKey(modeled.Table, modeled.Schema));

            if (!_prerequisites.Comparer.Equals(dependent, modeledOwner)
                || definition.Columns.Count != modeled.Columns.Count)
            {
                continue;
            }

            var matching = true;

            // WHY: Runtime guards compare the source-frozen FK columns by
            // ordinal, even when a reordered pair list is semantically similar.
            // Preflight must not accept a shape that those guards reject.
            for (var column = 0; column < definition.Columns.Count; column++)
            {
                if (!IdentifierEquals(_objectIdentityNormalizer, definition.Columns[column], modeled.Columns[column])
                    || !IdentifierEquals(_objectIdentityNormalizer,
                        definition.PrincipalColumns[column], modeled.PrincipalColumns[column]))
                {
                    matching = false;
                    break;
                }
            }

            if (matching)
            {
                return false;
            }
        }

        return true;
    }
}
