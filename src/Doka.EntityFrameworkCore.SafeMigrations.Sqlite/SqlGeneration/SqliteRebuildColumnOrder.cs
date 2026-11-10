namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Preserves retained native ordinals without imposing order on fresh or ordinary EF lifetimes.</summary>
internal sealed class SqliteRebuildColumnOrder
{
    private readonly Dictionary<string, ColumnLifetime> _owners = new(SqliteIdentifierComparer.Instance);

    /// <summary>Captures exact native ordinals and follows only accepted safe structural changes.</summary>
    /// <param name="snapshot">The transaction-owned native catalog before this baseline window.</param>
    /// <param name="operations">The accepted operations for this exact window.</param>
    /// <param name="ownership">The original safe versus ordinary operation identities.</param>
    public SqliteRebuildColumnOrder(
        SqliteCatalogSnapshot snapshot,
        IReadOnlyList<MigrationOperation> operations,
        SqliteGeneratedColumnOwnership ownership
    )
    {
        var changedLifetimes = new HashSet<string>(SqliteIdentifierComparer.Instance);

        // WHY: The accepted structural window is indexed; avoid boxing its interface enumerator.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (SqliteSafeMigrationOperationClassifier.TableName(operation) is not { } owner)
            {
                continue;
            }

            if (ownership.IsProviderOperation(operation)
                || operation is CreateTableOperation or DropTableOperation)
            {
                // WHY: A fresh/recreated or explicitly provider-owned lifetime has its own authored order.
                // A later safe operation cannot borrow the old owner's ordinals after this boundary.
                _ = _owners.Remove(owner);
                _ = changedLifetimes.Add(owner);
                if (operation is RenameTableOperation providerRename)
                {
                    _ = _owners.Remove(providerRename.NewName ?? owner);
                    _ = changedLifetimes.Add(providerRename.NewName ?? owner);
                }

                continue;
            }

            if (!_owners.TryGetValue(owner, out var lifetime)
                && !changedLifetimes.Contains(owner)
                && snapshot.Tables.TryGetValue(owner, out var native))
            {
                // WHY: Allocate ordinal evidence only for touched existing owners, never the whole catalog.
                lifetime = new ColumnLifetime(native.Columns.Values);
                _owners.Add(owner, lifetime);
            }

            if (lifetime is null)
            {
                continue;
            }

            switch (operation)
            {
                case RenameTableOperation rename:
                    _ = _owners.Remove(owner);
                    _ = changedLifetimes.Add(owner);
                    _ = changedLifetimes.Add(rename.NewName ?? owner);
                    _owners[rename.NewName ?? owner] = lifetime;
                    break;
                case RenameColumnOperation rename:
                    if (lifetime.Columns.Remove(rename.Name, out var ordinal))
                    {
                        lifetime.Columns[rename.NewName] = ordinal;
                    }

                    break;
                case DropColumnOperation drop:
                    _ = lifetime.Columns.Remove(drop.Name);
                    break;
                case AddColumnOperation add:
                    lifetime.Columns[add.Name] = lifetime.NextOrdinal++;
                    break;
            }

            lifetime.Rebuilt |= SqliteSafeMigrationOperationClassifier.RequiresTableRebuild(operation);
        }
    }

    /// <summary>Gets whether any retained safe owner requires native reconstruction.</summary>
    public bool Required => _owners.Values.Any(static owner => owner.Rebuilt);

    /// <summary>Reorders private reconstructed declarations without mutating source operations or the model.</summary>
    /// <param name="creators">Exact reconstructed creators resolved by official rename metadata.</param>
    /// <param name="model">The target model whose explicit ordering remains authoritative.</param>
    public void Apply(
        IReadOnlyDictionary<CreateTableOperation, string> creators,
        IModel? model
    )
    {
        foreach (var candidate in creators)
        {
            if (!_owners.TryGetValue(candidate.Value, out var lifetime)
                || !lifetime.Rebuilt)
            {
                continue;
            }

            // WHY: The compiler's creator carries the exact accepted model schema. Explicit main and
            // unqualified SQLite owners share physical lookup semantics but not relational-model keys.
            var table = model
                ?.GetRelationalModel()
                .FindTable(candidate.Value, candidate.Key.Schema);

            if (table?.Columns.Any(static column
                    => column.PropertyMappings.Any(static mapping => mapping.Property.GetColumnOrder() is not null))
                == true)
            {
                // WHY: Explicit destination order is an authored contract, not incidental model enumeration.
                continue;
            }

            var columns = candidate.Key.Columns;
            var ranks = new Dictionary<string, int>(lifetime.Columns, SqliteIdentifierComparer.Instance);
            var next = lifetime.NextOrdinal;

            // WHY: This private declaration list is stable until the separate sort below.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < columns.Count; index++)
            {
                var column = columns[index];
                if (!ranks.ContainsKey(column.Name))
                {
                    ranks.Add(column.Name, next++);
                }
            }

            // WHY: Only EF-created column declarations move. Named copy projections, physical facets,
            // constraints and caller-owned operation objects remain exactly those produced by EF.
            columns.Sort((left, right) => ranks[left.Name]
                .CompareTo(ranks[right.Name]));
        }
    }

    /// <summary>Tracks one surviving native column lifetime; dropped/re-added names receive new ordinals.</summary>
    private sealed class ColumnLifetime
    {
        public ColumnLifetime(
            IEnumerable<SqliteColumnSnapshot> columns
        )
        {
            Columns = new Dictionary<string, int>(SqliteIdentifierComparer.Instance);
            foreach (var column in columns)
            {
                Columns.Add(column.Name, column.Ordinal);
                NextOrdinal = Math.Max(NextOrdinal, column.Ordinal + 1);
            }
        }

        public Dictionary<string, int> Columns { get; }

        public int NextOrdinal { get; set; }

        public bool Rebuilt { get; set; }
    }
}
