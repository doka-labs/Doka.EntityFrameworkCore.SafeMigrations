namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Retains backfill hazards independently of complete table ownership or final schema state.</summary>
internal sealed class MySqlBackfillConstraintCatalog
{
    // WHY: Hazard evidence belongs to captured operation instances, not equal-looking contracts
    // from another capture. Reference identity prevents accidental proof reuse; Rebind explicitly
    // transfers evidence to the corresponding source-bound replacement instances.
    private static readonly MySqlBackfillConstraintCatalog s_empty = new(
        new HashSet<SafeMigrationOperation>(ReferenceEqualityComparer.Instance));

    private readonly HashSet<SafeMigrationOperation> _constrained;

    /// <summary>Retains exact operation identities without cloning their shared hazard evidence.</summary>
    /// <param name="constrained">Exact operation references whose backfills require NULL-free rows.</param>
    private MySqlBackfillConstraintCatalog(
        HashSet<SafeMigrationOperation> constrained
    )
    {
        _constrained = constrained;
    }

    /// <summary>Tests whether this exact operation needs a NULL-free proof for a declared constraint.</summary>
    /// <param name="operation">The operation captured or rendered.</param>
    /// <returns>Whether a retained declaration constrains its backfill.</returns>
    internal bool RequiresNullFreeRows(
        SafeMigrationOperation operation
    ) => _constrained.Contains(operation);

    /// <summary>Transfers complete-stream evidence to exact source-bound analysis operations.</summary>
    /// <param name="originals">The original operation references in source-capture order.</param>
    /// <param name="rebound">Corresponding operations with their physical source identities.</param>
    /// <returns>The same hazard facts keyed by the rebound operation references.</returns>
    internal MySqlBackfillConstraintCatalog Rebind(
        IReadOnlyList<SafeMigrationOperation> originals,
        IReadOnlyList<SafeMigrationOperation> rebound
    )
    {
        if (originals.Count != rebound.Count)
        {
            throw new ArgumentException("A backfill proof rebind requires matching operation counts.");
        }

        var result = new HashSet<SafeMigrationOperation>(ReferenceEqualityComparer.Instance);

        // WHY: Select the rebound operation at the original operation's matching ordinal.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < originals.Count; index++)
        {
            if (_constrained.Contains(originals[index]))
            {
                result.Add(rebound[index]);
            }
        }

        return result.Count == 0
            ? s_empty
            : new MySqlBackfillConstraintCatalog(result);
    }

    /// <summary>Builds a conservative union of declarations across exact table and column rename aliases.</summary>
    /// <param name="operations">The complete safe-operation stream, including later drops.</param>
    /// <param name="currentDatabase">The selected database used to normalize equivalent qualifiers.</param>
    /// <param name="tableComparer">The verified owner equality, or exact identity for connection-free capture.</param>
    /// <returns>Constraint participation for relevant type-alter backfills only.</returns>
    internal static MySqlBackfillConstraintCatalog Create(
        IReadOnlyList<SafeMigrationOperation> operations,
        string? currentDatabase = null,
        StringComparer? tableComparer = null
    )
    {
        if (operations.All(static operation => !MySqlSafeMigrationPlanCapture.RequiresAlterBackfillContract(operation)))
        {
            return s_empty;
        }

        var tables = new Aliases<MySqlTableIdentity>(
            new MySqlTableIdentityComparer(
                tableComparer ?? StringComparer.Ordinal));

        Dictionary<int, TableHazards> hazards = [];

        // WHY: Index this stable list while preserving its order and ownership.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var operationIndex = 0; operationIndex < operations.Count; operationIndex++)
        {
            var operation = operations[operationIndex];

            if (operation.Intent is RenameTableIntent rename)
            {
                tables.Union(
                    MySqlTableIdentity.Create(rename.Name, rename.Schema, currentDatabase),
                    MySqlTableIdentity.Create(
                        rename.NewName ?? rename.Name,
                        rename.NewSchema ?? rename.Schema,
                        currentDatabase));
            }
        }

        // WHY: Index this stable list while preserving its order and ownership.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var operationIndex = 0; operationIndex < operations.Count; operationIndex++)
        {
            var operation = operations[operationIndex];

            if (operation.Intent is RenameColumnIntent rename)
            {
                For(rename.Table, rename.Schema)
                    .Columns
                    .Union(rename.Name, rename.NewName);
            }
        }

        // WHY: Final expected-schema catalogs discard dropped indexes and
        // omit tables without EnsureTable ownership. Backfill must still
        // respect transient declarations. Retaining their alias union is
        // conservative and never pretends to prove a complete table shape.
        // All unions finish before constraint sets retain integer root IDs.
        // WHY: Index this stable list while preserving its order and ownership.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var operationIndex = 0; operationIndex < operations.Count; operationIndex++)
        {
            var operation = operations[operationIndex];

            switch (operation.Intent)
            {
                case EnsureTableIntent table:
                    var tableHazards = For(table.Definition.Table, table.Definition.Schema);
                    tableHazards.AllColumns |= table.Definition.CheckConstraints.Count > 0;
                    if (table.Definition.PrimaryKey is { } tablePrimary)
                    {
                        tableHazards.Add(tablePrimary.Columns);
                    }

                    // WHY: Index this stable list while preserving its order and ownership.
                    // ReSharper disable once ForCanBeConvertedToForeach
                    // ReSharper disable once LoopCanBeConvertedToQuery
                    for (var uniqueIndex = 0; uniqueIndex < table.Definition.UniqueConstraints.Count; uniqueIndex++)
                    {
                        var unique = table.Definition.UniqueConstraints[uniqueIndex];

                        tableHazards.Add(unique.Columns);
                    }

                    break;
                case EnsurePrimaryKeyIntent primary:
                    For(primary.Definition.Table, primary.Definition.Schema)
                        .Add(primary.Definition.Columns);

                    break;
                case EnsureUniqueConstraintIntent unique:
                    For(unique.Definition.Table, unique.Definition.Schema)
                        .Add(unique.Definition.Columns);

                    break;
                case EnsureCheckConstraintIntent check:
                    For(check.Definition.Table, check.Definition.Schema)
                        .AllColumns = true;

                    break;
                case EnsureIndexIntent { Definition.Unique: true } index:
                    var indexHazards = For(index.Definition.Table, index.Definition.Schema);

                    // WHY: Index this stable list while preserving its order and ownership.
                    // ReSharper disable once ForCanBeConvertedToForeach
                    // ReSharper disable once LoopCanBeConvertedToQuery
                    for (var keyIndex = 0; keyIndex < index.Definition.Keys.Count; keyIndex++)
                    {
                        var key = index.Definition.Keys[keyIndex];

                        if (key.Column is { } column)
                        {
                            indexHazards.ConstrainedColumns.Add(indexHazards.Columns.Resolve(column));
                        }
                        else
                        {
                            indexHazards.AllColumns = true;
                        }
                    }

                    break;
            }
        }

        var constrained = new HashSet<SafeMigrationOperation>(ReferenceEqualityComparer.Instance);

        // WHY: Index this stable list while preserving its order and ownership.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var operationIndex = 0; operationIndex < operations.Count; operationIndex++)
        {
            var operation = operations[operationIndex];

            if (!MySqlSafeMigrationPlanCapture.RequiresAlterBackfillContract(operation)
                || operation.Intent is not AlterColumnIntent alter)
            {
                continue;
            }

            var state = For(alter.Table, alter.Schema);
            if (state.AllColumns
                || state.ConstrainedColumns.Contains(state.Columns.Resolve(alter.Definition.Name)))
            {
                constrained.Add(operation);
            }
        }

        return constrained.Count == 0
            ? s_empty
            : new MySqlBackfillConstraintCatalog(constrained);

        TableHazards For(
            string table,
            string? schema
        )
        {
            var identity = tables.Resolve(MySqlTableIdentity.Create(table, schema, currentDatabase));
            if (!hazards.TryGetValue(identity, out var result))
            {
                result = new TableHazards();
                hazards.Add(identity, result);
            }

            return result;
        }
    }

    /// <summary>Contains only constraint participation, never an inferred physical table definition.</summary>
    private sealed class TableHazards
    {
        // MySQL column identifiers are case-insensitive on every platform;
        // table/database identities retain their separate normalization rules.
        internal Aliases<string> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<int> ConstrainedColumns { get; } = [];
        internal bool AllColumns { get; set; }

        /// <summary>Retains constraint participation after all column aliases have been joined.</summary>
        /// <param name="columns">The columns participating in one declared key.</param>
        internal void Add(
            IReadOnlyList<string> columns
        )
        {
            // WHY: Index this stable list while preserving its order and ownership.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
            {
                var column = columns[columnIndex];

                ConstrainedColumns.Add(Columns.Resolve(column));
            }
        }
    }

    /// <summary>Bounds alias resolution with path compression and union by size.</summary>
    /// <typeparam name="T">The exact table or column identity represented by each alias.</typeparam>
    private sealed class Aliases<T>
        where T : notnull
    {
        private readonly Dictionary<T, int> _identities;
        private readonly List<int> _parents;
        private readonly List<int> _sizes;

        /// <summary>Initializes identity storage before its corresponding parent and size slots.</summary>
        /// <param name="comparer">The equality rules for this specific identifier domain.</param>
        internal Aliases(
            IEqualityComparer<T> comparer
        )
        {
            _identities = new Dictionary<T, int>(comparer);
            _parents = [];
            _sizes = [];
        }

        /// <summary>Creates an unseen identity or resolves its root while compressing the traversed path.</summary>
        /// <param name="value">The alias whose current root is required.</param>
        /// <returns>The integer root, stable for retained constraints after all unions finish.</returns>
        internal int Resolve(
            T value
        )
        {
            if (!_identities.TryGetValue(value, out var index))
            {
                index = _parents.Count;
                _identities.Add(value, index);
                _parents.Add(index);
                _sizes.Add(1);
            }

            while (_parents[index] != index)
            {
                _parents[index] = _parents[_parents[index]];
                index = _parents[index];
            }

            return index;
        }

        /// <summary>Joins two alias groups before consumers retain their integer root identities.</summary>
        /// <param name="left">The first alias, created when not already present.</param>
        /// <param name="right">The second alias, created when not already present.</param>
        internal void Union(
            T left,
            T right
        )
        {
            var leftRoot = Resolve(left);
            var rightRoot = Resolve(right);
            if (leftRoot == rightRoot)
            {
                return;
            }

            if (_sizes[leftRoot] < _sizes[rightRoot])
            {
                (leftRoot, rightRoot) = (rightRoot, leftRoot);
            }

            _parents[rightRoot] = leftRoot;
            _sizes[leftRoot] += _sizes[rightRoot];
        }
    }
}
