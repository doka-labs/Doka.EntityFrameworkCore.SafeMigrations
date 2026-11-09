namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Certifies exact FK removals subsumed by their owner's later atomic table retirement.</summary>
/// <remarks>This is structural evidence, not permission to bypass live policy or artifact classification.</remarks>
internal sealed class SqliteTableRetirementContract
{
    private static readonly SqliteTableRetirementContract s_empty = new(null, null, null, null);
    private readonly HashSet<string>? _changedLifetimes;
    private readonly HashSet<SafeMigrationOperation>? _foreignKeys;
    private readonly HashSet<string>? _retiredOwners;
    private readonly HashSet<SafeMigrationOperation>? _tableDrops;

    private SqliteTableRetirementContract(
        HashSet<SafeMigrationOperation>? foreignKeys,
        HashSet<string>? retiredOwners,
        HashSet<string>? changedLifetimes,
        HashSet<SafeMigrationOperation>? tableDrops
    )
    {
        _foreignKeys = foreignKeys;
        _retiredOwners = retiredOwners;
        _changedLifetimes = changedLifetimes;
        _tableDrops = tableDrops;
    }

    /// <summary>Captures reference-bound certificates without retaining the caller's structural window.</summary>
    /// <typeparam name="TTable">The target-model description; only its owner identity is consulted.</typeparam>
    /// <param name="modeledTables">The immutable target-model owner index.</param>
    /// <param name="operations">The exact current atomic structural window, not cumulative migration history.</param>
    /// <returns>Certificates limited to FK operations whose unchanged owner is explicitly retired later.</returns>
    public static SqliteTableRetirementContract Capture<TTable>(
        IReadOnlyDictionary<string, TTable> modeledTables,
        IReadOnlyList<MigrationOperation> operations
    )
    {
        if (!operations.Any(static operation => operation is SafeMigrationOperation { Intent: DropForeignKeyIntent }))
        {
            return s_empty;
        }

        HashSet<string>? changedOwners = null;
        HashSet<string>? changedLifetimes = null;
        // WHY: Inspect the stable indexed window while collecting owner changes and structural boundaries.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (!SqliteSafeMigrationOperationClassifier.CanParticipateInStructuralBatch(operation))
            {
                // WHY: Later DDL across SQL or DML cannot discharge work in an earlier independently committed batch.
                return s_empty;
            }

            if (operation is SafeMigrationOperation { Intent: DropForeignKeyIntent or DropTableIntent }
                or DropTableOperation)
            {
                continue;
            }

            if (SqliteSafeMigrationOperationClassifier.TableName(operation) is { } owner)
            {
                changedOwners ??= new HashSet<string>(SqliteIdentifierComparer.Instance);
                _ = changedOwners.Add(owner);
            }

            var renamedTarget = operation switch
            {
                SafeMigrationOperation { Intent: RenameTableIntent rename } => rename.NewName ?? rename.Name,
                RenameTableOperation rename => rename.NewName ?? rename.Name,
                _ => null,
            };

            if (renamedTarget is not null)
            {
                changedOwners ??= new HashSet<string>(SqliteIdentifierComparer.Instance);
                _ = changedOwners.Add(renamedTarget);
            }

            if (operation is CreateTableOperation or RenameTableOperation
                or SafeMigrationOperation { Intent: EnsureTableIntent or RenameTableIntent })
            {
                changedLifetimes ??= new HashSet<string>(SqliteIdentifierComparer.Instance);
                if (SqliteSafeMigrationOperationClassifier.TableName(operation) is { } changed)
                {
                    _ = changedLifetimes.Add(changed);
                }

                if (renamedTarget is not null)
                {
                    _ = changedLifetimes.Add(renamedTarget);
                }
            }
        }

        HashSet<string>? retiredOwners = null;
        HashSet<SafeMigrationOperation>? foreignKeys = null;

        // WHY: A reverse scan proves a later DROP without rescanning the window for each FK or retaining its list.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = operations.Count - 1; index >= 0; index--)
        {
            var operation = operations[index];
            var droppedOwner = operation switch
            {
                SafeMigrationOperation { Intent: DropTableIntent drop } safe
                    when IsMain(drop.Schema) && !safe.GetAnnotations().Any() => drop.Table,
                DropTableOperation drop when IsMain(drop.Schema) && !drop.GetAnnotations().Any() => drop.Name,
                _ => null,
            };

            if (droppedOwner is not null)
            {
                retiredOwners ??= new HashSet<string>(SqliteIdentifierComparer.Instance);
                _ = retiredOwners.Add(droppedOwner);
            }

            var renamedOwner = operation switch
            {
                SafeMigrationOperation { Intent: RenameTableIntent rename } =>
                    (Source: rename.Name, Target: rename.NewName ?? rename.Name),
                RenameTableOperation rename => (Source: rename.Name, Target: rename.NewName ?? rename.Name),
                _ => (Source: (string?)null, Target: (string?)null),
            };

            if (renamedOwner.Source is not null
                && renamedOwner.Target is not null
                && retiredOwners?.Contains(renamedOwner.Target) == true)
            {
                // WHY: Follow future deletion wishes backwards only for refusal evidence. A renamed old
                // neighbor cannot hide its live artifacts, but a retained principal rename is independent.
                _ = retiredOwners.Add(renamedOwner.Source);
            }

            if (operation is not SafeMigrationOperation { Intent: DropForeignKeyIntent foreignKey } safeForeignKey
                || !IsMain(foreignKey.Schema)
                || safeForeignKey.GetAnnotations().Any()
                || modeledTables.ContainsKey(foreignKey.Table)
                || changedOwners?.Contains(foreignKey.Table) == true
                || retiredOwners?.Contains(foreignKey.Table) != true)
            {
                continue;
            }

            foreignKeys ??= new HashSet<SafeMigrationOperation>(ReferenceEqualityComparer.Instance);
            _ = foreignKeys.Add(safeForeignKey);
        }

        if (foreignKeys is null)
        {
            return s_empty;
        }

        var firstRemovals = new Dictionary<string, int>(SqliteIdentifierComparer.Instance);
        HashSet<SafeMigrationOperation>? tableDrops = null;

        // WHY: Keep only exact terminal DROPs after their owner's certified FK removal, never earlier namesakes.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < operations.Count; index++)
        {
            if (operations[index] is SafeMigrationOperation { Intent: DropForeignKeyIntent foreignKey } key
                && foreignKeys.Contains(key))
            {
                _ = firstRemovals.TryAdd(foreignKey.Table, index);
            }

            if (operations[index] is SafeMigrationOperation { Intent: DropTableIntent table } drop
                && IsMain(table.Schema)
                && !drop.GetAnnotations().Any()
                && firstRemovals.TryGetValue(table.Table, out var firstRemoval)
                && index > firstRemoval)
            {
                tableDrops ??= new HashSet<SafeMigrationOperation>(ReferenceEqualityComparer.Instance);
                _ = tableDrops.Add(drop);
            }
        }

        return new SqliteTableRetirementContract(foreignKeys, retiredOwners, changedLifetimes, tableDrops);
    }

    /// <summary>Determines whether this exact operation belongs to the certified retiring owner lifetime.</summary>
    /// <param name="operation">The same operation reference captured from the current structural window.</param>
    /// <returns>Whether its physical FK rebuild can be subsumed after independent live guards accept it.</returns>
    public bool Contains(
        SafeMigrationOperation operation
    ) => _foreignKeys?.Contains(operation) == true;

    /// <summary>Identifies potential retiring owner lineages for refusal checks, never FK-elision permission.</summary>
    public bool Retires(
        string table
    ) => _retiredOwners?.Contains(table) == true;

    /// <summary>Identifies native owners whose changed lifetime cannot lend artifact evidence to retirement.</summary>
    public bool ChangesLifetime(
        string table
    ) => _changedLifetimes?.Contains(table) == true;

    /// <summary>Identifies exact terminal table removals whose intrinsic native owner remains unchanged.</summary>
    public bool ContainsTableDrop(
        SafeMigrationOperation operation
    ) => _tableDrops?.Contains(operation) == true;

    private static bool IsMain(
        string? schema
    ) => schema is null || SqliteIdentifierComparer.Instance.Equals(schema, "main");
}
