namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Retains accepted safe ownership without lending it to another table or prior physical lifetime.</summary>
internal sealed class SqliteGeneratedColumnOwnership
{
    private readonly HashSet<MigrationOperation> _inputs;
    private readonly HashSet<MigrationOperation>? _providerOperations;
    private readonly HashSet<CreateTableOperation> _safeCreators = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, bool> _lifetimes = new(SqliteIdentifierComparer.Instance);

    /// <summary>Captures accepted input identities and current table lifetimes before native rewriting.</summary>
    /// <param name="operations">The accepted native operations before provider rewriting.</param>
    /// <param name="guardedOperations">The original mixed stream before immutable safe intent conversion.</param>
    public SqliteGeneratedColumnOwnership(
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyList<MigrationOperation> guardedOperations
    )
    {
        _inputs = new HashSet<MigrationOperation>(operations, ReferenceEqualityComparer.Instance);
        foreach (var original in guardedOperations)
        {
            if (original is not SafeMigrationOperation)
            {
                _providerOperations ??= new HashSet<MigrationOperation>(ReferenceEqualityComparer.Instance);
                _ = _providerOperations.Add(original);
            }
        }

        foreach (var operation in operations)
        {
            var safe = !IsProviderOperation(operation);
            switch (operation)
            {
                case DropTableOperation drop:
                    _ = _lifetimes.Remove(drop.Name);
                    break;
                case CreateTableOperation create:
                    _lifetimes[create.Name] = safe;
                    if (safe)
                    {
                        _ = _safeCreators.Add(create);
                    }

                    break;
                case RenameTableOperation rename:
                    var owned = _lifetimes.Remove(rename.Name, out var previous) && previous;
                    _lifetimes[rename.NewName ?? rename.Name] = owned;
                    break;
                default:
                    if (safe && SqliteSafeMigrationOperationClassifier.RequiresTableRebuild(operation)
                        && SqliteSafeMigrationOperationClassifier.TableName(operation) is { } table)
                    {
                        _lifetimes[table] = true;
                    }

                    break;
            }
        }
    }

    /// <summary>Checks whether fresh or rebuilt generated declarations have an accepted safe owner.</summary>
    /// <param name="model">The native target model, when available.</param>
    /// <returns>Whether the accepted safe lifetime owns any generated-column declaration.</returns>
    public bool RequiresCompleteDeclarations(IModel? model)
    {
        foreach (var creator in _safeCreators)
        {
            if (creator.Columns.Any(static column => column.ComputedColumnSql is not null))
            {
                return true;
            }
        }

        if (model is not null)
        {
            foreach (var table in model.GetRelationalModel().Tables)
            {
                if (_lifetimes.TryGetValue(table.Name, out var owned) && owned
                    && table.Columns.Any(static column => column.ComputedColumnSql is not null))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Resolves native rebuild creators through compiler-produced rename metadata, never SQL names.</summary>
    /// <param name="rewritten">The actual operations produced by the official provider's rewrite.</param>
    /// <returns>The exact fresh and rebuilt creators whose generated facets belong to the safe contract.</returns>
    public HashSet<CreateTableOperation> ResolveCreators(IReadOnlyList<MigrationOperation> rewritten)
    {
        Dictionary<string, string>? nativeTargets = null;
        foreach (var operation in rewritten)
        {
            if (operation is RenameTableOperation { NewName: not null } rename && !_inputs.Contains(rename))
            {
                // WHY: The native rewriter supplies the actual temporary-to-final
                // binding. User-authored renames cannot impersonate that proof,
                // and no temporary-name prefix or SQL-text heuristic is needed.
                nativeTargets ??= new Dictionary<string, string>(SqliteIdentifierComparer.Instance);
                nativeTargets[rename.Name] = rename.NewName;
            }
        }

        var creators = new HashSet<CreateTableOperation>(ReferenceEqualityComparer.Instance);
        foreach (var operation in rewritten)
        {
            if (operation is CreateTableOperation creator
                && (_safeCreators.Contains(creator)
                    || (!_inputs.Contains(creator)
                        && nativeTargets?.TryGetValue(creator.Name, out var target) == true
                        && _lifetimes.TryGetValue(target, out var owned) && owned)))
            {
                _ = creators.Add(creator);
            }
        }

        return creators;
    }

    /// <summary>Recognizes original provider objects, including computed ADDs folded into creators.</summary>
    /// <param name="operation">The exact native operation to inspect.</param>
    /// <returns>Whether the operation is an original provider-owned object.</returns>
    public bool IsProviderOperation(MigrationOperation operation) => _providerOperations?.Contains(operation) == true;
}
