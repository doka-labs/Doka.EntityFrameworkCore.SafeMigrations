namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
    private readonly Dictionary<(string Schema, string Table), ColumnLayoutState?> _projectedColumnLayouts = [];

    private void ResetProjectedColumnLayouts() => _projectedColumnLayouts.Clear();

    /// <summary>Reads compact layout aggregates and requested physical identities in bounded metadata groups.</summary>
    /// <param name="connection">The open, identity-validated analysis connection.</param>
    /// <param name="transaction">The active caller transaction, when present.</param>
    /// <param name="operations">The ordered safe-operation stream.</param>
    /// <param name="commandTimeout">The active context timeout.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task completing after the immutable baseline has been captured.</returns>
    private async Task ReadProjectedColumnLayoutsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyList<SafeMigrationOperation> operations,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        if (!operations.Any(static operation => operation.Intent is EnsureColumnIntent))
        {
            return;
        }

        var requested = new Dictionary<(string Schema, string Table), HashSet<string>>();
        foreach (var column in operations.Select(static operation => operation.Intent).OfType<EnsureColumnIntent>())
        {
            RegisterColumnLayoutRequest(requested, column.Schema, column.Table, column.Definition.Name);
        }

        // WHY: A later column target can be absent in the immutable catalog
        // because an earlier rename creates its identity. Read that source,
        // not every unrelated table in a large initial migration.
        for (var ordinal = operations.Count - 1; ordinal >= 0; ordinal--)
        {
            if (operations[ordinal].Intent is RenameTableIntent table
                && requested.TryGetValue(
                    (table.NewSchema ?? table.Schema ?? "dbo", table.NewName ?? table.Name), out var targetNames))
            {
                RegisterColumnLayoutRequest(requested, table.Schema, table.Name, null);
                requested[(table.Schema ?? "dbo", table.Name)].UnionWith(targetNames);
            }
        }

        foreach (var operation in operations)
        {
            switch (operation.Intent)
            {
                case DropColumnIntent column when requested.ContainsKey((column.Schema ?? "dbo", column.Table)):
                    RegisterColumnLayoutRequest(requested, column.Schema, column.Table, column.Name);
                    break;
                case RenameColumnIntent column when requested.ContainsKey((column.Schema ?? "dbo", column.Table)):
                    RegisterColumnLayoutRequest(requested, column.Schema, column.Table, column.Name);
                    RegisterColumnLayoutRequest(requested, column.Schema, column.Table, column.NewName);
                    break;
                case EnsurePrimaryKeyIntent primary when requested.ContainsKey(
                    (primary.Definition.Schema ?? "dbo", primary.Definition.Table)):
                    foreach (var name in primary.Definition.Columns)
                    {
                        RegisterColumnLayoutRequest(requested,
                            primary.Definition.Schema, primary.Definition.Table, name);
                    }

                    break;
            }
        }

        foreach (var key in requested.Keys)
        {
            _projectedColumnLayouts[key] = null;
        }

        // WHY: Aggregates stay server-side; even a 1,024-column table allocates
        // only one layout record plus bindings for names used by this stream.
        foreach (var chunk in requested.Keys.Chunk(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            ApplyCommandTimeout(command, commandTimeout);
            command.CommandText = "SELECT requested.[schema],requested.[table],"
                + "CONVERT(int,layout.column_count),CONVERT(int,layout.fixed_bytes),layout.bit_columns,"
                + "layout.variable_columns,CONVERT(int,layout.clustered_variable_extra),"
                + "CONVERT(bit,CASE WHEN layout.unknown_columns=0 "
                + "AND t.max_column_id_used=layout.column_count THEN 1 ELSE 0 END) FROM (VALUES "
                + string.Join(",", chunk.Select(static key => "(" + KeyLiteral(key.Schema) + ","
                    + KeyLiteral(key.Table) + ")")) + ") AS requested([schema],[table]) "
                + "JOIN sys.schemas AS s ON s.name=requested.[schema] COLLATE CATALOG_DEFAULT "
                + "JOIN sys.tables AS t ON t.schema_id=s.schema_id "
                + "AND t.name=requested.[table] COLLATE CATALOG_DEFAULT "
                + "CROSS APPLY (" + SqlServerSafeMigrationCatalogSqlBuilder.BuildColumnLayoutCatalogQuery(
                    "t.object_id") + ") AS layout;";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                CaptureProjectedColumnLayout(reader);
            }
        }

        var bindings = requested.SelectMany(static pair => pair.Value.Select(name =>
            (pair.Key.Schema, pair.Key.Table, Name: name))).ToArray();

        foreach (var chunk in bindings.Chunk(SafeMigrationCatalogQueryLimits.MaximumInventoryValues))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            ApplyCommandTimeout(command, commandTimeout);
            command.CommandText = "SELECT requested.[schema],requested.[table],requested.[name],c.column_id,"
                + "CASE WHEN ty.name IN(N'varchar',N'nvarchar',N'varbinary') THEN CONVERT(int,c.max_length) "
                + "ELSE 0 END,CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM sys.index_columns ic JOIN sys.indexes i "
                + "ON i.object_id=ic.object_id AND i.index_id=ic.index_id WHERE i.type=1 AND ic.key_ordinal>0 "
                + "AND ic.object_id=c.object_id AND ic.column_id=c.column_id) THEN 1 ELSE 0 END) "
                + "FROM (VALUES " + string.Join(",", chunk.Select(static item =>
                    "(" + KeyLiteral(item.Schema) + "," + KeyLiteral(item.Table) + "," + KeyLiteral(item.Name) + ")"))
                + ") AS requested([schema],[table],[name]) JOIN sys.schemas AS s "
                + "ON s.name=requested.[schema] COLLATE CATALOG_DEFAULT JOIN sys.tables AS t "
                + "ON t.schema_id=s.schema_id AND t.name=requested.[table] COLLATE CATALOG_DEFAULT "
                + "JOIN sys.columns AS c ON c.object_id=t.object_id "
                + "AND c.name=requested.[name] COLLATE CATALOG_DEFAULT "
                + "JOIN sys.types ty ON ty.user_type_id=c.user_type_id;";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                CaptureProjectedColumnLayoutBinding(reader);
            }
        }

        await ReadColumnLayoutRowPresenceAsync(connection, transaction, operations, commandTimeout, cancellationToken);
    }

    /// <summary>Captures the current compact catalog layout row for ordered allocation validation.</summary>
    /// <param name="reader">The reader positioned at the captured layout row.</param>
    internal void CaptureProjectedColumnLayout(
        DbDataReader reader
    )
    {
        _projectedColumnLayouts[(reader.GetString(0), reader.GetString(1))] = new ColumnLayoutState(
            reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5),
            reader.GetBoolean(7), reader.GetInt32(6));
    }

    /// <summary>Captures the current requested physical column identity and clustered storage binding.</summary>
    /// <param name="reader">The reader positioned at the captured binding row.</param>
    internal void CaptureProjectedColumnLayoutBinding(
        DbDataReader reader
    )
    {
        if (_projectedColumnLayouts.TryGetValue((reader.GetString(0), reader.GetString(1)), out var layout)
            && layout is not null)
        {
            layout.Bindings[reader.GetString(2)] = reader.GetInt32(3);
            if (reader.GetInt32(4) != 0)
            {
                layout.VariableWidths[reader.GetInt32(3)] = reader.GetInt32(4);
                if (reader.GetBoolean(5))
                {
                    layout.ClusteredVariables.Add(reader.GetInt32(3));
                }
            }
        }
    }

    private async Task ReadColumnLayoutRowPresenceAsync(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyList<SafeMigrationOperation> operations,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var prospective = new Dictionary<(string Schema, string Table), ColumnLayoutState>();
        var materialized = new HashSet<(string Schema, string Table)>();
        var clusteredMutations = operations.Select(static operation => operation.Intent)
            .OfType<EnsurePrimaryKeyIntent>()
            .Select(static primary => (primary.Definition.Schema ?? "dbo", primary.Definition.Table)).ToHashSet();

        var renamedSources = new Dictionary<(string Schema, string Table), (string Schema, string Table)>();
        foreach (var table in operations.Select(static operation => operation.Intent).OfType<RenameTableIntent>())
        {
            var source = (table.Schema ?? "dbo", table.Name);
            renamedSources[(table.NewSchema ?? table.Schema ?? "dbo", table.NewName ?? table.Name)]
                = renamedSources.GetValueOrDefault(source, source);
        }

        foreach (var column in operations.Select(static operation => operation.Intent).OfType<EnsureColumnIntent>())
        {
            var key = (column.Schema ?? "dbo", column.Table);
            key = renamedSources.GetValueOrDefault(key, key);
            if (!_projectedColumnLayouts.TryGetValue(key, out var live) || live is null || !live.IsKnown
                || live.Bindings.ContainsKey(column.Definition.Name)
                || !_catalogSqlBuilder.TryGetColumnStorageLayout(column.Definition, out var storage))
            {
                continue;
            }

            if (!prospective.TryGetValue(key, out var candidate))
            {
                candidate = live.CopyAllocation();
                prospective.Add(key, candidate);
            }

            candidate.Add(column.Definition.Name, storage);
            if (SqlServerSafeMigrationCatalogSqlBuilder.ColumnAdditionMaterializesRows(column.Definition, storage))
            {
                materialized.Add(key);
            }
        }

        var needed = prospective.Where(pair => materialized.Contains(pair.Key)
            && (pair.Value.RowCapacityUnproven()
                || clusteredMutations.Contains(pair.Key) && pair.Value.HasVariableColumns))
            .Select(static pair => pair.Key);

        // WHY: Row presence is read only for near-ceiling materialization.
        // A TOP(1) witness does not scan contents and fits the existing bounded
        // catalog statement groups even for large migration streams.
        foreach (var chunk in needed.Chunk(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            ApplyCommandTimeout(command, commandTimeout);
            command.CommandText = string.Join(" ", chunk.Select(key =>
            {
                var qualified = _sqlGenerationHelper.DelimitIdentifier(key.Table, key.Schema);
                var prefix = "SELECT " + KeyLiteral(key.Schema) + "," + KeyLiteral(key.Table) + ",";
                var select = prefix + "CONVERT(bit,CASE WHEN EXISTS(SELECT TOP(1)1 FROM "
                    + qualified + ") THEN 1 ELSE 0 END);";

                // WHY: VIEW DEFINITION does not grant SELECT. Keep unauthorized
                // table binding inside the guarded dynamic branch and retain
                // unknown row presence for a structured fail-closed decision.

                return "IF HAS_PERMS_BY_NAME(" + KeyLiteral(qualified) + ",N'OBJECT',N'SELECT')=1 "
                    + "EXEC sys.sp_executesql " + KeyLiteral(select) + "; ELSE "
                    + prefix + "CONVERT(bit,NULL);";
            }));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            do
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (_projectedColumnLayouts[(reader.GetString(0), reader.GetString(1))] is { } layout)
                    {
                        layout.HasRows = reader.IsDBNull(2) ? null : reader.GetBoolean(2);
                    }
                }
            }
            while (await reader.NextResultAsync(cancellationToken));
        }
    }

    private static void RegisterColumnLayoutRequest(
        Dictionary<(string Schema, string Table), HashSet<string>> requested,
        string? schema,
        string table,
        string? name
    )
    {
        var key = (schema ?? "dbo", table);
        if (!requested.TryGetValue(key, out var names))
        {
            names = new HashSet<string>(StringComparer.Ordinal);
            requested.Add(key, names);
        }

        if (name is not null)
        {
            names.Add(name);
        }
    }

    private SafeMigrationProviderAnalysis QualifyProjectedColumnLayoutOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis
    )
    {
        if (operation.Intent is not EnsureColumnIntent column || analysis.IsInvariantUnsupported)
        {
            return analysis;
        }

        var structureUnknown = analysis.IsOpaqueProjectionUnknown
            && analysis.ObservedState == SafeMigrationObservedState.PrerequisiteMissing
            && analysis.Code == "projected_structure_state_unknown";
        if (!structureUnknown && (analysis.IsOpaqueProjectionUnknown
                || analysis.ObservedState != SafeMigrationObservedState.Missing))
        {
            return analysis;
        }

        if (!_projectedColumnLayouts.TryGetValue((column.Schema ?? "dbo", column.Table), out var layout)
            || layout is null || !_catalogSqlBuilder.TryGetColumnStorageLayout(column.Definition, out var addition))
        {
            return structureUnknown ? analysis : ColumnLayoutFailure("column_layout_unproven");
        }

        if (layout.Bindings.ContainsKey(column.Definition.Name))
        {
            return analysis;
        }

        var code = layout.AdditionFailure(addition);

        if (code is not null)
        {
            return ColumnLayoutFailure(code);
        }

        // WHY: A captured allocation failure remains a rejection after an
        // accepted structural change. Available capacity never proves that an
        // unknown target is missing or otherwise safe to execute. Raw SQL is
        // deferred by Core before this qualifier can consume stale evidence.
        if (structureUnknown)
        {
            return analysis;
        }

        return SqlServerSafeMigrationCatalogSqlBuilder.ColumnAdditionMaterializesRows(column.Definition, addition)
            && (!layout.RowCapacityKnown || layout.RowCapacityUnproven(addition)) && layout.HasRows != false
                ? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.DataBlocked,
                    SafeMigrationRepairCapability.None, false, "column_row_layout_unproven")
                : analysis;
    }

    private void ObserveProjectedColumnLayoutOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision
    )
    {
        if (_projectedColumnLayouts.Count == 0 || !decision.ShouldExecute)
        {
            return;
        }

        switch (operation.Intent)
        {
            case EnsureTableIntent table when analysis.ObservedState == SafeMigrationObservedState.Missing
                && _projectedColumnLayouts.ContainsKey((table.Definition.Schema ?? "dbo", table.Definition.Table)):
                var created = new ColumnLayoutState(0, 0, 0, 0, true) { HasRows = false };
                foreach (var column in table.Definition.Columns)
                {
                    if (_catalogSqlBuilder.TryGetColumnStorageLayout(column, out var storage))
                    {
                        created.Add(column.Name, storage);
                    }
                    else
                    {
                        created.IsKnown = false;
                    }
                }

                if (table.Definition.PrimaryKey is { } primaryKey)
                {
                    created.ReserveClusteredKey(primaryKey.Columns);
                }

                _projectedColumnLayouts[(table.Definition.Schema ?? "dbo", table.Definition.Table)] = created;
                break;
            case EnsureColumnIntent column:
                if (_projectedColumnLayouts.TryGetValue((column.Schema ?? "dbo", column.Table), out var layout)
                    && layout is not null)
                {
                    if (analysis.ObservedState == SafeMigrationObservedState.Missing
                        && _catalogSqlBuilder.TryGetColumnStorageLayout(column.Definition, out var storage))
                    {
                        layout.Add(column.Definition.Name, storage);
                    }
                    else
                    {
                        layout.IsKnown = false;
                    }
                }

                break;
            case DropColumnIntent column:
                DropProjectedLayoutColumn(column.Schema, column.Table, column.Name);
                break;
            case RenameColumnIntent column:
                RenameProjectedLayoutColumn(column.Schema, column.Table, column.Name, column.NewName);
                break;
            case AlterColumnIntent column:
                InvalidateProjectedColumnLayout(column.Schema, column.Table);
                break;
            case DropTableIntent table:
                _projectedColumnLayouts[(table.Schema ?? "dbo", table.Table)] = null;
                break;
            case RenameTableIntent table:
                TransferProjectedColumnLayout(table.Schema, table.Name,
                    table.NewSchema ?? table.Schema, table.NewName ?? table.Name);
                break;
            case ModelManagedDataIntent data:
                if (_projectedColumnLayouts.TryGetValue((data.Schema ?? "dbo", data.Table), out var seeded)
                    && seeded is not null)
                {
                    // WHY: Updates/deletes do not prove complete emptiness;
                    // inserts can invalidate a previously empty-row witness.
                    seeded.HasRows = null;
                }

                break;
            case EnsurePrimaryKeyIntent primary:
                ReserveProjectedClusteredKey(primary.Definition.Schema, primary.Definition.Table,
                    primary.Definition.Columns);
                break;
            case EnsureIndexIntent index when operation["SqlServer:Clustered"] is true:
                InvalidateProjectedClusteredRowProof(index.Definition.Schema, index.Definition.Table);
                break;
        }
    }

    private void ObserveProviderColumnLayoutOperation(
        MigrationOperation operation
    )
    {
        if (_projectedColumnLayouts.Count == 0)
        {
            return;
        }

        switch (operation)
        {
            case RenameTableOperation table:
                TransferProjectedColumnLayout(table.Schema, table.Name,
                    table.NewSchema ?? table.Schema, table.NewName ?? table.Name);
                break;
            case RenameColumnOperation column:
                RenameProjectedLayoutColumn(column.Schema, column.Table, column.Name, column.NewName);
                break;
            case DropColumnOperation column:
                DropProjectedLayoutColumn(column.Schema, column.Table, column.Name);
                break;
            case DropTableOperation table:
                _projectedColumnLayouts[(table.Schema ?? "dbo", table.Name)] = null;
                break;
            case AddColumnOperation column:
                InvalidateProjectedColumnLayout(column.Schema, column.Table);
                break;
            case AlterColumnOperation column:
                InvalidateProjectedColumnLayout(column.Schema, column.Table);
                break;
            case CreateTableOperation table:
                _projectedColumnLayouts[(table.Schema ?? "dbo", table.Name)] = null;
                break;
            case SqlOperation:
                foreach (var layout in _projectedColumnLayouts.Values)
                {
                    if (layout is not null)
                    {
                        layout.IsKnown = false;
                    }
                }

                break;
            case InsertDataOperation data:
                InvalidateProjectedLayoutRows(data.Schema, data.Table);
                break;
            case UpdateDataOperation data:
                InvalidateProjectedLayoutRows(data.Schema, data.Table);
                break;
            case DeleteDataOperation data:
                InvalidateProjectedLayoutRows(data.Schema, data.Table);
                break;
            case AddPrimaryKeyOperation primary:
                ReserveProjectedClusteredKey(primary.Schema, primary.Table, primary.Columns);
                break;
            case CreateIndexOperation index when index["SqlServer:Clustered"] is true:
                InvalidateProjectedClusteredRowProof(index.Schema, index.Table);
                break;
        }
    }

    private void InvalidateProjectedClusteredRowProof(
        string? schema,
        string table
    )
    {
        if (_projectedColumnLayouts.TryGetValue((schema ?? "dbo", table), out var layout) && layout is not null
            && layout.HasVariableColumns)
        {
            // WHY: A newly clustered variable key cannot use ROW_OVERFLOW_DATA.
            // The old root-based bound therefore cannot authorize a later row
            // extension without a fresh exact non-overflowable storage proof.
            layout.RowCapacityKnown = false;
        }
    }

    private void ReserveProjectedClusteredKey(
        string? schema,
        string table,
        IReadOnlyList<string> columns
    )
    {
        if (_projectedColumnLayouts.TryGetValue((schema ?? "dbo", table), out var layout) && layout is not null)
        {
            layout.ReserveClusteredKey(columns);
        }
    }

    private void InvalidateProjectedLayoutRows(
        string? schema,
        string table
    )
    {
        if (_projectedColumnLayouts.TryGetValue((schema ?? "dbo", table), out var layout) && layout is not null)
        {
            layout.HasRows = null;
        }
    }

    private void InvalidateProjectedColumnLayout(
        string? schema,
        string table
    )
    {
        if (_projectedColumnLayouts.TryGetValue((schema ?? "dbo", table), out var layout) && layout is not null)
        {
            layout.IsKnown = false;
        }
    }

    private void TransferProjectedColumnLayout(
        string? schema,
        string name,
        string? newSchema,
        string newName
    )
    {
        var key = (schema ?? "dbo", name);
        _projectedColumnLayouts.TryGetValue(key, out var layout);
        _projectedColumnLayouts[key] = null;
        _projectedColumnLayouts[(newSchema ?? "dbo", newName)] = layout;
    }

    private void RenameProjectedLayoutColumn(
        string? schema,
        string table,
        string name,
        string newName
    )
    {
        if (_projectedColumnLayouts.TryGetValue((schema ?? "dbo", table), out var layout) && layout is not null
            && layout.Bindings.Remove(name, out var identity))
        {
            layout.Bindings[newName] = identity;
        }
    }

    private void DropProjectedLayoutColumn(
        string? schema,
        string table,
        string name
    )
    {
        if (!_projectedColumnLayouts.TryGetValue((schema ?? "dbo", table), out var layout) || layout is null)
        {
            return;
        }

        if (!layout.Bindings.TryGetValue(name, out var identity))
        {
            layout.IsKnown = false;

            return;
        }

        foreach (var alias in layout.Bindings.Where(pair => pair.Value == identity).Select(static pair => pair.Key)
            .ToArray())
        {
            layout.Bindings.Remove(alias);
        }

        layout.VisibleColumns--;
        layout.IsKnown = false;
        // WHY: DROP COLUMN removes the visible column, not necessarily its
        // fixed storage, packed-bit slot, null bitmap bit, or offset metadata.
        // The runtime lineage guard therefore rejects every new allocation,
        // even when retained-width arithmetic would fit. Only a proven fresh
        // table resets that lineage; existing matching columns remain valid.
    }

    private static SafeMigrationProviderAnalysis ColumnLayoutFailure(
        string code
    ) => new(SafeMigrationObservedState.Unsupported, SafeMigrationRepairCapability.None, false, code);

    private sealed class ColumnLayoutState(
        int columns,
        int fixedBytes,
        int bits,
        int variables,
        bool known,
        int clusteredVariableExtra = 0
    )
    {
        private int _allocatedColumns = columns;
        private long _fixedBytes = fixedBytes;
        private int _bits = bits;
        private int _variables = variables;
        private int _nextIdentity = -1;
        private long _clusteredVariableExtra = clusteredVariableExtra;

        internal int VisibleColumns { get; set; } = columns;

        internal bool IsKnown { get; set; } = known;

        internal bool? HasRows { get; set; }

        internal bool RowCapacityKnown { get; set; } = true;

        internal bool HasVariableColumns => _variables > 0;

        internal Dictionary<string, int> Bindings { get; } = new(StringComparer.Ordinal);

        internal Dictionary<int, int> VariableWidths { get; } = [];

        internal HashSet<int> ClusteredVariables { get; } = [];

        internal ColumnLayoutState CopyAllocation() => new(
            _allocatedColumns, checked((int)_fixedBytes), _bits, _variables, IsKnown,
            checked((int)_clusteredVariableExtra))
        {
            VisibleColumns = this.VisibleColumns,
            HasRows = this.HasRows,
            RowCapacityKnown = this.RowCapacityKnown,
        };

        internal void ReserveClusteredKey(
            IReadOnlyList<string> columns
        )
        {
            foreach (var name in columns)
            {
                if (!Bindings.TryGetValue(name, out var identity))
                {
                    RowCapacityKnown = false;

                    continue;
                }

                if (VariableWidths.TryGetValue(identity, out var maximumBytes) && ClusteredVariables.Add(identity))
                {
                    _clusteredVariableExtra += Math.Max(0, maximumBytes - 24);
                }
            }
        }

        internal bool RowCapacityUnproven(
            SqlServerSafeMigrationCatalogSqlBuilder.ColumnStorageLayout addition = default
        )
        {
            var variables = _variables + (addition.IsVariable ? 1 : 0);
            var addedColumns = addition == default ? 0 : 1;
            var bytes = _fixedBytes + addition.FixedBytes + (_bits + (addition.IsBit ? 1 : 0) + 7) / 8
                + 6 + (_allocatedColumns + addedColumns + 7) / 8 + 2 + 26 * variables + _clusteredVariableExtra;

            return variables > 0 && bytes > 8060;
        }

        internal string? AdditionFailure(
            SqlServerSafeMigrationCatalogSqlBuilder.ColumnStorageLayout addition
        )
        {
            if (VisibleColumns >= 1024)
            {
                return "column_limit";
            }

            if (!IsKnown)
            {
                return "column_layout_unproven";
            }

            var rowBytes = _fixedBytes + addition.FixedBytes + (_bits + (addition.IsBit ? 1 : 0) + 7) / 8
                + 6 + (_allocatedColumns + 8) / 8;

            return rowBytes > 8060 ? "column_fixed_row_limit" : null;
        }

        internal void Add(
            string name,
            SqlServerSafeMigrationCatalogSqlBuilder.ColumnStorageLayout storage
        )
        {
            if (Bindings.ContainsKey(name))
            {
                return;
            }

            var identity = _nextIdentity--;
            Bindings[name] = identity;
            if (storage.IsVariable)
            {
                VariableWidths[identity] = storage.MaximumVariableBytes;
            }

            VisibleColumns++;
            _allocatedColumns++;
            _fixedBytes += storage.FixedBytes;
            _bits += storage.IsBit ? 1 : 0;
            _variables += storage.IsVariable ? 1 : 0;
        }
    }
}
