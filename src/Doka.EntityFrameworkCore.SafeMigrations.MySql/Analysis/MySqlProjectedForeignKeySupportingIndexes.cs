namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Retains ordered child-index collision evidence without inferring complete table ownership.</summary>
internal sealed class MySqlProjectedForeignKeySupportingIndexes
{
    private readonly Func<string?, string?> _normalizeSchema;
    private readonly Func<string, string> _normalizeTable;
    private readonly bool _isMariaDb;
    private readonly Dictionary<(string? Schema, string Table), TableIndexes> _tables = [];
    private bool _opaqueBoundary;

    /// <summary>Initializes the provider's physical schema-identity normalization.</summary>
    /// <param name="normalizeSchema">Normalizes the current database and its equivalent qualifiers.</param>
    /// <param name="isMariaDb">Whether the captured engine permits descending child supporting keys.</param>
    /// <param name="normalizeTable">The caller-qualified table identity normalization, or identity when absent.</param>
    internal MySqlProjectedForeignKeySupportingIndexes(
        Func<string?, string?> normalizeSchema,
        bool isMariaDb,
        Func<string, string>? normalizeTable = null
    )
    {
        ArgumentNullException.ThrowIfNull(normalizeSchema);

        _normalizeSchema = normalizeSchema;
        _normalizeTable = normalizeTable ?? (static table => table);
        _isMariaDb = isMariaDb;
    }

    /// <summary>Captures complete aliases for one immutable requested child-column prefix.</summary>
    /// <param name="definition">The requested foreign-key definition independently queried.</param>
    /// <param name="localNameOccupied">Whether an index currently occupies the requested FK symbol.</param>
    /// <param name="coveringIndexAliases">Every physical index independently proved to cover its child columns.</param>
    /// <param name="unsafeIndexAliases">Complete physical aliases with unsafe multi-valued child-row coverage.</param>
    internal void Capture(
        ExpectedForeignKeyDefinition definition,
        bool localNameOccupied,
        IEnumerable<string> coveringIndexAliases,
        IEnumerable<string>? unsafeIndexAliases = null
    )
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(coveringIndexAliases);

        var state = GetOrAdd(definition.Table, definition.Schema);

        if (!state.CapturedRequests.TryGetValue(definition.Name, out var requests))
        {
            requests = [];
            state.CapturedRequests.Add(definition.Name, requests);
        }

        if (!requests.Any(columns => SameColumns(columns, definition.Columns)))
        {
            requests.Add(definition.Columns);
        }

        if (localNameOccupied)
        {
            state.GetOrAdd(definition.Name);
        }

        foreach (var alias in coveringIndexAliases)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(alias);

            var index = state.GetOrAdd(alias);
            if (!index.CapturedPrefixes.Any(columns => SameColumns(columns, definition.Columns)))
            {
                index.CapturedPrefixes.Add(definition.Columns);
            }
        }

        foreach (var alias in unsafeIndexAliases ?? [])
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(alias);

            state.GetOrAdd(alias);
            state.AddUnsafePrefix(alias, definition.Columns);
        }
    }

    /// <summary>Tests whether the current physical namespace avoids an automatic-index name collision.</summary>
    /// <param name="definition">The foreign key being considered at this ordered stream position.</param>
    /// <returns>True only when the captured name is free or a surviving complete covering index exists.</returns>
    internal bool CanEnsure(
        ExpectedForeignKeyDefinition definition
    )
    {
        ArgumentNullException.ThrowIfNull(definition);

        var state = FindEligibleOwner(definition);
        if (state is null)
        {
            return false;
        }

        return !state.Indexes.ContainsKey(definition.Name)
            || state.Indexes.Values.Any(index => index.Covers(definition.Columns, _isMariaDb));
    }

    /// <summary>Tests whether valid current facts prove an occupied symbol without child-key coverage.</summary>
    /// <param name="definition">The captured foreign-key request considered at this stream position.</param>
    /// <returns>True only for a known collision, never for absent or invalidated owner evidence.</returns>
    internal bool HasKnownNameCollision(
        ExpectedForeignKeyDefinition definition
    )
    {
        ArgumentNullException.ThrowIfNull(definition);

        return FindEligibleOwner(definition) is not null && !CanEnsure(definition);
    }

    /// <summary>Tests whether a captured unsafe lifetime is alive, reused or no longer verifiable.</summary>
    /// <param name="definition">The requested foreign key whose ordinary leading columns were captured.</param>
    /// <returns>True unless ordered physical lifetime evidence has removed every relevant unsafe alias.</returns>
    internal bool HasUnsafeMultiValuedIndex(
        ExpectedForeignKeyDefinition definition
    )
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!_tables.TryGetValue(Owner(definition.Table, definition.Schema), out var state))
        {
            return _opaqueBoundary;
        }

        if (state.Unknown
            || state.UnsafeOpaqueBoundary
            || _opaqueBoundary && !state.Fresh && !state.Dropped)
        {
            return true;
        }

        return state.UnsafeAliasHistory.Any(alias => state.Indexes.ContainsKey(alias.Key)
            && (state.UnsafeUnknownAliases.Contains(alias.Key)
                || alias.Value.Any(prefix => prefix.Count >= definition.Columns.Count
                    && prefix.Take(definition.Columns.Count)
                        .SequenceEqual(definition.Columns, StringComparer.OrdinalIgnoreCase))));
    }

    /// <summary>Updates facts only after the planner has genuinely accepted an ordered mutation.</summary>
    /// <param name="operation">The accepted safe intent.</param>
    /// <param name="projectedAnalysis">The state actually used by the planner after preceding operations.</param>
    /// <param name="decision">The planner's action; no-ops, rejections and deferred actions mint no proof.</param>
    internal void ObserveAcceptedOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis projectedAnalysis,
        SafeMigrationDecision decision
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(projectedAnalysis);
        ArgumentNullException.ThrowIfNull(decision);

        if (!decision.ShouldExecute)
        {
            return;
        }

        var creates = decision.Action == SafeMigrationAction.Apply
            && projectedAnalysis.ObservedState == SafeMigrationObservedState.Missing;

        switch (operation.Intent)
        {
            case EnsureIndexIntent index:
                ReplaceIndex(index.Definition.Table, index.Definition.Schema, index.Definition.Name,
                    creates ? index.Definition.Keys : null, creates ? index.Definition : null);
                break;
            case DropIndexIntent index:
                DropIndex(index.Table, index.Schema, index.Name);
                break;
            case RenameIndexIntent index:
                RenameIndex(index.Table, index.Schema, index.Name, index.NewName);
                break;
            case EnsurePrimaryKeyIntent primary:
                ReplaceColumns(primary.Definition.Table, primary.Definition.Schema, "PRIMARY",
                    creates ? primary.Definition.Columns : null);
                break;
            case DropPrimaryKeyIntent primary:
                DropIndex(primary.Table, primary.Schema, "PRIMARY");
                break;
            case EnsureUniqueConstraintIntent unique:
                ReplaceColumns(unique.Definition.Table, unique.Definition.Schema, unique.Definition.Name,
                    creates ? unique.Definition.Columns : null);
                break;
            case DropUniqueConstraintIntent unique:
                DropIndex(unique.Table, unique.Schema, unique.Name);
                break;
            case EnsureForeignKeyIntent foreignKey when creates:
                ObserveAutomaticIndex(foreignKey.Definition);
                break;
            case EnsureTableIntent table when creates
                && !projectedAnalysis.RequiresLiveDataProof:
                CreateTable(table.Definition);
                break;
            case EnsureTableIntent table:
                InvalidateTable(table.Definition.Table, table.Definition.Schema);
                break;
            case DropTableIntent table:
                DropTable(table.Table, table.Schema);
                break;
            case RenameTableIntent table:
                RenameTable(table.Name, table.Schema,
                    table.NewName ?? table.Name, table.NewSchema ?? table.Schema);
                break;
            case DropColumnIntent column:
                InvalidateColumn(column.Table, column.Schema, column.Name);
                break;
            case RenameColumnIntent column:
                InvalidateColumn(column.Table, column.Schema, column.Name);
                InvalidateColumn(column.Table, column.Schema, column.NewName);
                break;
            case AlterColumnIntent column when column.OldDefinition is { } source
                && MySqlSafeMigrationProviderAnalyzer.IsSameLayoutNullabilityTransition(source, column.Definition):
                // WHY: An accepted nullability-only transition preserves the
                // exact child-key layout and its physical index lifetimes.
                // Reuse the analyzer's metadata proof; row safety stays there.
                break;
            case AlterColumnIntent column:
                InvalidateColumn(column.Table, column.Schema, column.Definition.Name);
                break;
            case EnsureColumnIntent column when decision.Action == SafeMigrationAction.Repair
                && !projectedAnalysis.RepairPreservesPhysicalKeys:
                InvalidateColumn(column.Table, column.Schema, column.Definition.Name);
                break;
        }
    }

    /// <summary>Preserves exact known provider transitions while treating uncaptured DDL as a proof boundary.</summary>
    /// <param name="operation">The provider operation observed in original migration order.</param>
    internal void ObserveProviderOperation(
        MigrationOperation operation
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        switch (operation)
        {
            case DropIndexOperation { Table: { } indexTable } index:
                DropIndex(indexTable, index.Schema, index.Name);
                break;
            case RenameIndexOperation { Table: { } renameIndexTable } index:
                RenameIndex(renameIndexTable, index.Schema, index.Name, index.NewName);
                break;
            case DropPrimaryKeyOperation primary:
                DropIndex(primary.Table, primary.Schema, "PRIMARY");
                break;
            case DropUniqueConstraintOperation unique:
                DropIndex(unique.Table, unique.Schema, unique.Name);
                break;
            case DropTableOperation table:
                DropTable(table.Name, table.Schema);
                break;
            case RenameTableOperation table:
                RenameTable(table.Name, table.Schema,
                    table.NewName ?? table.Name, table.NewSchema ?? table.Schema);
                break;
            case DropColumnOperation column:
                InvalidateColumn(column.Table, column.Schema, column.Name);
                break;
            case RenameColumnOperation column:
                InvalidateColumn(column.Table, column.Schema, column.Name);
                InvalidateColumn(column.Table, column.Schema, column.NewName);
                break;
            case AlterColumnOperation column:
                InvalidateColumn(column.Table, column.Schema, column.Name);
                break;
            case CreateIndexOperation index:
                InvalidateTable(index.Table, index.Schema);
                break;
            case AddPrimaryKeyOperation primary:
                InvalidateTable(primary.Table, primary.Schema);
                break;
            case AddUniqueConstraintOperation unique:
                InvalidateTable(unique.Table, unique.Schema);
                break;
            case CreateTableOperation table:
                InvalidateTable(table.Name, table.Schema);
                break;
            case AddForeignKeyOperation foreignKey:
                InvalidateTable(foreignKey.Table, foreignKey.Schema);
                break;
            case AddColumnOperation or DropForeignKeyOperation
                or AddCheckConstraintOperation or DropCheckConstraintOperation
                or InsertDataOperation or UpdateDataOperation or DeleteDataOperation
                or EnsureSchemaOperation or AlterDatabaseOperation:
                break;
            default:
                _opaqueBoundary = true;
                foreach (var table in _tables.Values)
                {
                    table.UnsafeOpaqueBoundary = true;
                }

                break;
        }
    }

    /// <summary>Applies caller-qualified identities before ordinal owner lookup.</summary>
    private (string? Schema, string Table) Owner(
        string table,
        string? schema
    ) => (_normalizeSchema(schema), _normalizeTable(table));

    /// <summary>Requires a captured request or complete fresh owner before interpreting its namespace.</summary>
    private TableIndexes? FindEligibleOwner(
        ExpectedForeignKeyDefinition definition
    )
    {
        if (_opaqueBoundary
            || !_tables.TryGetValue(Owner(definition.Table, definition.Schema), out var state)
            || state.Unknown
            || state.Dropped
            || !state.Fresh
            && (!state.CapturedRequests.TryGetValue(definition.Name, out var requests)
                || !requests.Any(columns => SameColumns(columns, definition.Columns))))
        {
            return null;
        }

        return state;
    }

    /// <summary>Retains sparse facts for an exact physical owner.</summary>
    private TableIndexes GetOrAdd(
        string table,
        string? schema
    )
    {
        var owner = Owner(table, schema);
        if (!_tables.TryGetValue(owner, out var state))
        {
            state = new TableIndexes();
            _tables.Add(owner, state);
        }

        return state;
    }

    /// <summary>Starts a complete fresh namespace only for a plain accepted table creation.</summary>
    private void CreateTable(
        ExpectedTableDefinition definition
    )
    {
        _tables[Owner(definition.Table, definition.Schema)] = new TableIndexes { Fresh = true };
        if (definition.PrimaryKey is { } primary)
        {
            ReplaceColumns(definition.Table, definition.Schema, "PRIMARY", primary.Columns);
        }

        foreach (var unique in definition.UniqueConstraints)
        {
            ReplaceColumns(definition.Table, definition.Schema, unique.Name, unique.Columns);
        }

        foreach (var foreignKey in definition.ForeignKeys)
        {
            ObserveAutomaticIndex(foreignKey);
        }
    }

    /// <summary>Ends every old index lifetime while retaining a precise absent-owner proof after opaque SQL.</summary>
    private void DropTable(
        string table,
        string? schema
    ) => _tables[Owner(table, schema)] = new TableIndexes { Dropped = true };

    /// <summary>Retains a newly created automatic index only when no explicit covering index already exists.</summary>
    private void ObserveAutomaticIndex(
        ExpectedForeignKeyDefinition definition
    )
    {
        var state = GetOrAdd(definition.Table, definition.Schema);
        if (!state.Unknown
            && !state.Indexes.ContainsKey(definition.Name)
            && !state.Indexes.Values.Any(index => index.Covers(definition.Columns, _isMariaDb)))
        {
            state.GetOrAdd(definition.Name).CapturedPrefixes.Add(definition.Columns);
        }
    }

    /// <summary>Represents primary and unique keys using their full ordinary native key parts.</summary>
    private void ReplaceColumns(
        string table,
        string? schema,
        string name,
        IReadOnlyList<string>? columns
    )
    {
        var keys = columns?
            .Select(static column => new ExpectedIndexKeyDefinition(column: column))
            .ToArray();

        ReplaceIndex(table, schema, name, keys, definition: null);
    }

    /// <summary>Replaces the lifetime and revokes aliases which native automatic-index pruning can retire.</summary>
    private void ReplaceIndex(
        string table,
        string? schema,
        string name,
        IReadOnlyList<ExpectedIndexKeyDefinition>? keys,
        ExpectedIndexDefinition? definition
    )
    {
        var state = GetOrAdd(table, schema);
        var eligible = keys is not null
            && (definition is null || IsEligible(definition));

        if (eligible)
        {
            // WHY: MySQL may silently retire an automatically created child index
            // after another covering index is added. STATISTICS does not certify
            // which aliases were automatic, so retaining their old coverage could
            // resurrect a nonexistent index after the replacement is dropped.
            foreach (var index in state.Indexes)
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(index.Key, "PRIMARY"))
                {
                    index.Value.CapturedPrefixes.RemoveAll(prefix => Covers(keys!, prefix, _isMariaDb));
                }
            }
        }

        state.Indexes[name] = new PhysicalIndex { Keys = eligible ? keys : null };
    }

    /// <summary>Removes precisely the physical index lifetime addressed by this ordered drop.</summary>
    private void DropIndex(
        string table,
        string? schema,
        string name
    )
    {
        if (_tables.TryGetValue(Owner(table, schema), out var state))
        {
            state.Indexes.Remove(name);
        }
    }

    /// <summary>Moves an existing physical index rather than copying an alias which could later resurrect.</summary>
    private void RenameIndex(
        string table,
        string? schema,
        string name,
        string newName
    )
    {
        var state = GetOrAdd(table, schema);
        if (!state.Indexes.Remove(name, out var index))
        {
            index = new PhysicalIndex();
        }

        if (state.UnsafeAliasHistory.TryGetValue(name, out var unsafePrefixes))
        {
            foreach (var prefix in unsafePrefixes)
            {
                state.AddUnsafePrefix(newName, prefix);
            }
        }

        if (state.UnsafeUnknownAliases.Contains(name))
        {
            state.UnsafeUnknownAliases.Add(newName);
        }

        state.Indexes[newName] = index;
    }

    /// <summary>Moves a physical table lifetime and discards any former destination-owner facts.</summary>
    private void RenameTable(
        string table,
        string? schema,
        string newTable,
        string? newSchema
    )
    {
        var source = Owner(table, schema);
        var destination = Owner(newTable, newSchema);
        if (_tables.Remove(source, out var state))
        {
            _tables[destination] = state;
        }
        else
        {
            _tables.Remove(destination);
        }
    }

    /// <summary>Retains name occupancy but revokes coverage whose physical column lifetime changed.</summary>
    private void InvalidateColumn(
        string table,
        string? schema,
        string column
    )
    {
        if (!_tables.TryGetValue(Owner(table, schema), out var state))
        {
            return;
        }

        foreach (var index in state.Indexes.Values)
        {
            index.CapturedPrefixes.RemoveAll(prefix => prefix.Contains(column, StringComparer.OrdinalIgnoreCase));
            if (index.Keys?.Any(key => StringComparer.OrdinalIgnoreCase.Equals(key.Column, column)) == true)
            {
                index.Keys = null;
            }
        }

        foreach (var alias in state.UnsafeAliasHistory.Keys)
        {
            if (state.Indexes.ContainsKey(alias))
            {
                state.UnsafeUnknownAliases.Add(alias);
            }
        }
    }

    /// <summary>Fails closed when an uncaptured table mutation could create or remove physical indexes.</summary>
    private void InvalidateTable(
        string table,
        string? schema
    ) => GetOrAdd(table, schema).Unknown = true;

    /// <summary>Requires a representable ordinary BTREE shape before accepted creation can mint coverage.</summary>
    private static bool IsEligible(
        ExpectedIndexDefinition definition
    ) => definition.Filter is null
        && definition.StructuredFilter is null
        && definition.IncludedColumns.Count == 0
        && definition.NullsDistinct is null
        && (definition.Method is null || StringComparer.OrdinalIgnoreCase.Equals(definition.Method, "BTREE"))
        && definition.Keys.All(static key => key.Column is not null
            && key.NullOrder == SafeMigrationIndexNullOrder.ProviderDefault
            && key.Collation is null
            && key.OperatorClass is null);

    /// <summary>Checks the requested full-column ordered left prefix without requiring an exact index length.</summary>
    private static bool Covers(
        IReadOnlyList<ExpectedIndexKeyDefinition> keys,
        IReadOnlyList<string> columns,
        bool isMariaDb
    )
    {
        if (keys.Count < columns.Count)
        {
            return false;
        }

        // WHY: Compare actual and expected key facets at the same ordered key position.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < columns.Count; ordinal++)
        {
            if (keys[ordinal].PrefixLength is not null
                || !isMariaDb && keys[ordinal].SortOrder == SafeMigrationIndexSortOrder.Descending
                || !StringComparer.OrdinalIgnoreCase.Equals(keys[ordinal].Column, columns[ordinal]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Matches a captured request's immutable child-column contract.</summary>
    private static bool SameColumns(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second
    ) => first.Count == second.Count && first.SequenceEqual(second, StringComparer.OrdinalIgnoreCase);

    /// <summary>Contains sparse physical occupancy and complete prefix certificates for one table lifetime.</summary>
    private sealed class TableIndexes
    {
        /// <summary>Gets index symbols in the native case-insensitive identifier domain.</summary>
        internal Dictionary<string, PhysicalIndex> Indexes { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets deduplicated immutable requests by symbol without scanning unrelated foreign keys.</summary>
        internal Dictionary<string, List<IReadOnlyList<string>>> CapturedRequests { get; }
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets whether a complete new table namespace was genuinely created.</summary>
        internal bool Fresh { get; init; }

        /// <summary>Gets whether a precise table drop ended the old physical namespace.</summary>
        internal bool Dropped { get; init; }

        /// <summary>Gets whether uncaptured DDL invalidated this table's sparse facts.</summary>
        internal bool Unknown { get; set; }

        /// <summary>Gets whether opaque SQL postdates this owner lifetime's complete unsafe-index proof.</summary>
        internal bool UnsafeOpaqueBoundary { get; set; }

        /// <summary>Gets unsafe aliases retained across drops so name reuse cannot certify a new lifetime.</summary>
        internal Dictionary<string, List<IReadOnlyList<string>>> UnsafeAliasHistory { get; }
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets unsafe aliases whose complete shape was invalidated by a member transition.</summary>
        internal HashSet<string> UnsafeUnknownAliases { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Retains complete unsafe prefixes independently of ordinary supporting coverage.</summary>
        internal void AddUnsafePrefix(
            string alias,
            IReadOnlyList<string> columns
        )
        {
            if (!UnsafeAliasHistory.TryGetValue(alias, out var prefixes))
            {
                prefixes = [];
                UnsafeAliasHistory.Add(alias, prefixes);
            }

            // WHY: Ordinary coverage cannot waive an MVI's missing child-row
            // records. Retain its alias history even after a precise drop so
            // reoccupation cannot silently inherit the old clean-lifetime proof.
            if (!prefixes.Any(prefix => SameColumns(prefix, columns)))
            {
                prefixes.Add(columns);
            }
        }

        /// <summary>Retains an occupied index symbol even when its complete shape is unknown.</summary>
        internal PhysicalIndex GetOrAdd(
            string name
        )
        {
            if (!Indexes.TryGetValue(name, out var index))
            {
                index = new PhysicalIndex();
                Indexes.Add(name, index);
            }

            return index;
        }
    }

    /// <summary>Tracks one lifetime, with captured certificates separate from explicitly created shapes.</summary>
    private sealed class PhysicalIndex
    {
        /// <summary>Gets independently captured complete native prefix certificates.</summary>
        internal List<IReadOnlyList<string>> CapturedPrefixes { get; } = [];

        /// <summary>Gets the ordinary shape minted only by accepted explicit key creation.</summary>
        internal IReadOnlyList<ExpectedIndexKeyDefinition>? Keys { get; set; }

        /// <summary>Combines only surviving captured prefixes and genuinely accepted explicit key shapes.</summary>
        internal bool Covers(
            IReadOnlyList<string> columns,
            bool isMariaDb
        ) => Keys is not null && MySqlProjectedForeignKeySupportingIndexes.Covers(Keys, columns, isMariaDb)
            || CapturedPrefixes.Any(prefix => prefix.Count >= columns.Count
                && prefix.Take(columns.Count).SequenceEqual(columns, StringComparer.OrdinalIgnoreCase));
    }
}
