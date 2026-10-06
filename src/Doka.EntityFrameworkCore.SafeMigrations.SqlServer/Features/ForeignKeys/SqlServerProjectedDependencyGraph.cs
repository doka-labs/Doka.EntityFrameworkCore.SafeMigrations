namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>Tracks accepted physical FK dependencies without snapshotting complete table definitions.</summary>
internal sealed class SqlServerProjectedDependencyGraph : ISafeMigrationProjectedDependencyAnalyzer
{
    private readonly Dictionary<TableKey, int> _tables = [];
    private readonly HashSet<int> _missingTables = [];
    private readonly HashSet<TableKey> _nonTableNames = [];
    private readonly HashSet<int> _renameSafeTables = [];
    private readonly Dictionary<string, int> _schemas = new(StringComparer.Ordinal);
    private readonly HashSet<int> _missingSchemas = [];
    private readonly HashSet<int> _schemasWithOtherObjects = [];
    private readonly Dictionary<int, int> _tableSchemas = [];
    private readonly Dictionary<int, ForeignKeyEdge> _foreignKeys = [];
    private readonly Dictionary<ForeignKeyKey, int> _foreignKeyNames = [];
    private readonly Dictionary<ColumnKey, ColumnSafety> _columns = [];
    private readonly Dictionary<ColumnKey, int> _columnIds = [];
    private readonly HashSet<ColumnIdentity> _invalidatedStorage = [];
    private readonly Dictionary<int, TriggerSafety> _triggers = [];
    private readonly Dictionary<int, ExpectedTableDefinition> _createdTables = [];
    private readonly Dictionary<ExpectedForeignKeyDefinition, InlineStorageProof> _liveInlineReady = [];
    private readonly SqlServerProjectedCandidateKeys _candidateKeys = new();
    private readonly Func<ExpectedTableDefinition, ExpectedForeignKeyDefinition, bool> _inlinePhysicalWidthSupported;
    private Func<ExpectedColumnDefinition, ExpectedColumnDefinition, bool> _storageEquals = static (left, right)
        => left.StoreType == right.StoreType && left.ClrType == right.ClrType
            && left.IsUnicode == right.IsUnicode && left.MaxLength == right.MaxLength
            && left.IsFixedLength == right.IsFixedLength && left.IsRowVersion == right.IsRowVersion
            && left.Precision == right.Precision && left.Scale == right.Scale
            && Equals(left.Collation, right.Collation);

    private int _nextTableId = -1;
    private int _nextForeignKeyId = -1;
    private int _nextSchemaId = -1;
    private bool _unknown;

    /// <summary>Creates an ordered graph using the provider's authored inline key-width proof.</summary>
    /// <param name="inlinePhysicalWidthSupported">The provider's bounded physical width and arity validator.</param>
    public SqlServerProjectedDependencyGraph(
        Func<ExpectedTableDefinition, ExpectedForeignKeyDefinition, bool> inlinePhysicalWidthSupported
    )
    {
        _inlinePhysicalWidthSupported = inlinePhysicalWidthSupported;
    }

    /// <summary>Copies independently captured rename destination occupancy before ordered graph mutations.</summary>
    /// <param name="operations">The operation stream used to bind the immutable catalog names.</param>
    /// <param name="analyses">The corresponding immutable classifications to enrich.</param>
    internal void CaptureRenameTargetPresence(
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis[] analyses
    )
    {
        for (var index = 0; index < operations.Count; index++)
        {
            if (operations[index].Intent is not RenameTableIntent rename)
            {
                continue;
            }

            var target = new TableKey(rename.NewSchema ?? rename.Schema ?? "dbo", rename.NewName ?? rename.Name);
            if (!_tables.TryGetValue(target, out var identity))
            {
                continue;
            }

            // WHY: Rename identities are already bound using catalog collation.
            // Reuse that snapshot without another query, and retain non-table
            // occupants rather than mistaking them for a free schema name.
            bool? intermediateExists = null;
            if ((rename.NewName ?? rename.Name) != rename.Name
                && (rename.NewSchema ?? rename.Schema ?? "dbo") != (rename.Schema ?? "dbo"))
            {
                var intermediate = new TableKey(rename.Schema ?? "dbo", rename.NewName ?? rename.Name);
                if (_tables.TryGetValue(intermediate, out var intermediateIdentity))
                {
                    intermediateExists = !_missingTables.Contains(intermediateIdentity)
                        || _nonTableNames.Contains(intermediate);
                }
            }

            analyses[index] = analyses[index].WithRenameTargetExists(
                !_missingTables.Contains(identity) || _nonTableNames.Contains(target), intermediateExists);
        }
    }

    /// <summary>Determines whether an operation stream needs dependency metadata.</summary>
    /// <param name="operations">The safe operation stream.</param>
    /// <returns>Whether structural dependency or schema validation is necessary.</returns>
    public static bool IsRequired(
        IReadOnlyList<SafeMigrationOperation> operations
    )
        => operations.Any(static operation => operation.Intent is EnsureTableIntent or DropTableIntent
            or RenameTableIntent or EnsureForeignKeyIntent or DropForeignKeyIntent
            or EnsureSchemaIntent or DropSchemaIntent);

    /// <summary>Reads one FK graph and bounded bindings using the caller's analysis transaction.</summary>
    /// <param name="connection">The open provider connection.</param>
    /// <param name="transaction">The active analysis transaction.</param>
    /// <param name="operations">The stream whose names must resolve using catalog collation.</param>
    /// <param name="commandTimeout">The configured command timeout.</param>
    /// <param name="inlinePrerequisite">The provider's inline storage and candidate-key predicate.</param>
    /// <param name="storageEquals">The provider's inferred physical column comparison.</param>
    /// <param name="inlinePhysicalWidthSupported">The provider's authored inline key-width proof.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A mutable graph private to this analysis scope.</returns>
    public static async Task<SqlServerProjectedDependencyGraph> ReadAsync(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyList<SafeMigrationOperation> operations,
        int? commandTimeout,
        Func<ExpectedTableDefinition, ExpectedForeignKeyDefinition, string> inlinePrerequisite,
        Func<ExpectedColumnDefinition, ExpectedColumnDefinition, bool> storageEquals,
        Func<ExpectedTableDefinition, ExpectedForeignKeyDefinition, bool> inlinePhysicalWidthSupported,
        CancellationToken cancellationToken
    )
    {
        var graph = new SqlServerProjectedDependencyGraph(inlinePhysicalWidthSupported)
        {
            _storageEquals = storageEquals
        };

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (commandTimeout is { } timeout)
        {
            command.CommandTimeout = timeout;
        }

        // WHY: Cascade topology is global, but one compact edge per FK is
        // sufficient. Unrelated column/table metadata is never materialized.
        command.CommandText = "SELECT object_id, parent_object_id, referenced_object_id, name, "
            + "delete_referential_action, update_referential_action FROM sys.foreign_keys;";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt32(0);
                var edge = new ForeignKeyEdge(reader.GetInt32(1), reader.GetInt32(2),
                    CatalogAction(reader.GetByte(4)), CatalogAction(reader.GetByte(5)));

                graph._foreignKeys[id] = edge;
                graph._foreignKeyNames[new ForeignKeyKey(edge.Dependent, reader.GetString(3))] = id;
            }
        }

        var inline = CollectInlineRequests(operations);
        var requests = CollectRequests(operations);

        foreach (var request in inline)
        {
            foreach (var column in request.InitialForeignKey.PrincipalColumns)
            {
                requests.Add(new BindingRequest(request.InitialForeignKey.PrincipalSchema ?? "dbo",
                    request.InitialForeignKey.PrincipalTable, column, null));
            }
        }

        foreach (var chunk in requests.Chunk(256))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // WHY: Literal binding uses CATALOG_DEFAULT, not a guessed .NET
            // case-folding comparer. A table alias maps to the real object id.
            // Occupancy includes schema-scoped child objects, matching the
            // runtime ObjectExists guard rather than assuming only root names collide.
            var values = string.Join(", ", chunk.Select(static request => "("
                + Literal(request.Schema) + ", " + Literal(request.Table) + ", "
                + Literal(request.Column) + ", " + Literal(request.ForeignKey) + ")"));

            command.CommandText = "SELECT requested.schema_name, requested.table_name, requested.column_name, "
                + "requested.fk_name, s.schema_id, t.object_id, c.system_type_id, c.is_nullable, "
                + "c.default_object_id, fk.object_id, "
                + "CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM sys.triggers tr JOIN sys.trigger_events te "
                + "ON te.object_id = tr.object_id WHERE tr.parent_id = t.object_id "
                + "AND tr.is_instead_of_trigger = 1 AND te.type_desc = N'DELETE') THEN 1 ELSE 0 END), "
                + "CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM sys.triggers tr JOIN sys.trigger_events te "
                + "ON te.object_id = tr.object_id WHERE tr.parent_id = t.object_id "
                + "AND tr.is_instead_of_trigger = 1 AND te.type_desc = N'UPDATE') THEN 1 ELSE 0 END), "
                + "ix.index_id, kc.unique_index_id, c.column_id, occupied.object_id "
                + $"FROM (VALUES {values}) requested(schema_name, table_name, column_name, fk_name) "
                + "LEFT JOIN sys.schemas s ON s.name = requested.schema_name COLLATE CATALOG_DEFAULT "
                + "LEFT JOIN sys.tables t ON t.schema_id = s.schema_id "
                + "AND t.name = requested.table_name COLLATE CATALOG_DEFAULT "
                + "LEFT JOIN sys.objects occupied ON occupied.schema_id = s.schema_id "
                + "AND occupied.name = requested.table_name COLLATE CATALOG_DEFAULT "
                + "LEFT JOIN sys.columns c ON c.object_id = t.object_id "
                + "AND c.name = requested.column_name COLLATE CATALOG_DEFAULT "
                + "LEFT JOIN sys.foreign_keys fk ON fk.parent_object_id = t.object_id "
                + "AND fk.name = requested.fk_name COLLATE CATALOG_DEFAULT "
                + "LEFT JOIN sys.indexes ix ON ix.object_id = t.object_id "
                + "AND ix.name = requested.fk_name COLLATE CATALOG_DEFAULT "
                + "LEFT JOIN sys.key_constraints kc ON kc.parent_object_id = t.object_id "
                + "AND kc.name = requested.fk_name COLLATE CATALOG_DEFAULT;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var schema = reader.GetString(0);
                var table = reader.GetString(1);
                var tableId = reader.IsDBNull(5) ? graph.GetTable(table, schema) : reader.GetInt32(5);

                graph._tables[new TableKey(schema, table)] = tableId;
                if (reader.IsDBNull(5))
                {
                    graph._missingTables.Add(tableId);
                    if (!reader.IsDBNull(15))
                    {
                        graph._nonTableNames.Add(new TableKey(schema, table));
                    }
                }

                graph._schemas[schema] = reader.IsDBNull(4) ? graph.GetSchema(schema) : reader.GetInt32(4);
                graph._tableSchemas[tableId] = graph._schemas[schema];
                if (reader.IsDBNull(4))
                {
                    graph._missingSchemas.Add(graph._schemas[schema]);
                }

                graph._triggers[tableId] = new TriggerSafety(reader.GetBoolean(10), reader.GetBoolean(11));
                if (!reader.IsDBNull(6) && !reader.IsDBNull(2))
                {
                    graph._columns[new ColumnKey(tableId, reader.GetString(2))] =
                        new ColumnSafety(reader.GetByte(6) == 189, reader.GetBoolean(7), reader.GetInt32(8) != 0);
                    graph._columnIds[new ColumnKey(tableId, reader.GetString(2))] = reader.GetInt32(14);
                }

                if (!reader.IsDBNull(9) && !reader.IsDBNull(3))
                {
                    graph._foreignKeyNames[new ForeignKeyKey(tableId, reader.GetString(3))] = reader.GetInt32(9);
                }

                if (!reader.IsDBNull(3) && (!reader.IsDBNull(12) || !reader.IsDBNull(13)))
                {
                    graph._candidateKeys.BindName(tableId, reader.GetString(3),
                        reader.IsDBNull(12) ? reader.GetInt32(13) : reader.GetInt32(12));
                }
            }
        }

        await graph.ReadSchemaObjectsAsync(command, operations, cancellationToken);
        await graph._candidateKeys.ReadAsync(command,
            inline.Select(request => graph.GetTable(request.InitialForeignKey.PrincipalTable,
                request.InitialForeignKey.PrincipalSchema)), cancellationToken);

        for (var start = 0; start < inline.Length;)
        {
            var batch = new StringBuilder();
            var count = 0;
            var bytes = 0;
            while (start + count < inline.Length && count < 32)
            {
                var request = inline[start + count];
                var selection = "SELECT " + (start + count).ToString(CultureInfo.InvariantCulture)
                    + ", CONVERT(bit, CASE WHEN " + inlinePrerequisite(request.Table, request.InitialForeignKey)
                    + " THEN 1 ELSE 0 END)";

                var addedBytes = Encoding.UTF8.GetByteCount(selection) + (count == 0 ? 0 : 11);

                if (addedBytes > SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes)
                {
                    throw SafeMigrationCatalogQueryLimits.OversizedOperation(start + count, 0, addedBytes);
                }

                if (bytes + addedBytes > SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes)
                {
                    break;
                }

                if (count > 0)
                {
                    batch.Append(" UNION ALL ");
                }

                batch.Append(selection);
                bytes += addedBytes;
                count++;
            }

            command.CommandText = batch.ToString();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetBoolean(1))
                {
                    var request = inline[reader.GetInt32(0)];

                    var principal = graph.GetTable(
                        request.InitialForeignKey.PrincipalTable, request.InitialForeignKey.PrincipalSchema);

                    var columnIds = request.InitialForeignKey.PrincipalColumns
                        .Select(column => graph._columnIds.GetValueOrDefault(new ColumnKey(principal, column)))
                        .ToArray();

                    if (columnIds.All(static id => id > 0))
                    {
                        graph._liveInlineReady[request.ForeignKey] = new InlineStorageProof(principal, columnIds);
                    }
                }
            }

            start += count;
        }

        return graph;
    }

    /// <summary>Recovers only complete provider graph proofs after an accepted ordinary-table rename.</summary>
    /// <param name="operation">The operation following an opaque generic provider postcondition.</param>
    /// <param name="liveAnalysis">The immutable provider assessment before ordered mutations.</param>
    /// <param name="columns">The surviving accepted authored column contracts.</param>
    /// <returns>A complete ordered assessment, or null when the graph cannot prove it.</returns>
    internal SafeMigrationProviderAnalysis? ValidateOpaqueProviderPostcondition(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (_unknown || liveAnalysis.IsInvariantUnsupported
            || liveAnalysis.ObservedState is SafeMigrationObservedState.Unsupported
                or SafeMigrationObservedState.DataBlocked)
        {
            return null;
        }

        switch (operation.Intent)
        {
            case EnsureSchemaIntent or DropSchemaIntent:
                return ValidateSchemaOperation(operation.Intent, liveAnalysis);
            case EnsureTableIntent table when liveAnalysis.ObservedState
                is SafeMigrationObservedState.Missing or SafeMigrationObservedState.PrerequisiteMissing:
                var key = new TableKey(table.Definition.Schema ?? "dbo", table.Definition.Table);

                if (!_missingTables.Contains(GetTable(table.Definition.Table, table.Definition.Schema))
                    || _nonTableNames.Contains(key))
                {
                    return null;
                }

                if (!SchemaExists(table.Definition.Schema))
                {
                    return Rejected("projected_schema_missing");
                }

                var assessment = ValidateForeignKeys(
                    table.Definition.ForeignKeys, liveAnalysis, columns, table.Definition);

                // WHY: Core cannot retain a complete existing-table model after
                // a rename. This graph instead binds each surviving parent key
                // and storage proof to its physical ids; no stale name or absent
                // row proof is promoted merely because the original table was missing.
                return assessment.ObservedState == SafeMigrationObservedState.PrerequisiteMissing
                        && assessment.Code is "classified_prerequisite_missing" or "inline_foreign_key_prerequisite"
                        && HasAllInlinePrerequisites(table.Definition, columns)
                    ? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
                        SafeMigrationRepairCapability.None, false, "projected_inline_prerequisites_ready")
                    : assessment;
            case RenameTableIntent table:
                return liveAnalysis.ObservedState == SafeMigrationObservedState.Different
                    ? null : ValidateOrderedRename(table);
            default:
                return null;
        }
    }

    /// <inheritdoc />
    public SafeMigrationProviderAnalysis ValidateProjectedOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis projectedAnalysis,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (operation.Intent is EnsureSchemaIntent or DropSchemaIntent)
        {
            return ValidateSchemaOperation(operation.Intent, projectedAnalysis);
        }

        var incomingOnlyDrop = operation.Intent is DropTableIntent
            && projectedAnalysis.ObservedState == SafeMigrationObservedState.Different
            && projectedAnalysis.Code == "incoming_foreign_key_dependency";

        if (projectedAnalysis.ObservedState is SafeMigrationObservedState.Unsupported
            or SafeMigrationObservedState.DataBlocked
            || projectedAnalysis.ObservedState == SafeMigrationObservedState.Different && !incomingOnlyDrop)
        {
            return projectedAnalysis;
        }

        if (_unknown && (operation.Intent is EnsureForeignKeyIntent or DropTableIntent
            || operation.Intent is EnsureTableIntent { Definition.ForeignKeys.Count: > 0 }))
        {
            return Rejected("projected_dependency_state_unknown", unknown: true);
        }

        switch (operation.Intent)
        {
            case EnsureTableIntent table when _missingTables.Contains(
                GetTable(table.Definition.Table, table.Definition.Schema)):
                if (!SchemaExists(table.Definition.Schema))
                {
                    return Rejected("projected_schema_missing");
                }

                var tableAnalysis = ValidateForeignKeys(
                    table.Definition.ForeignKeys, projectedAnalysis, columns, table.Definition);

                return tableAnalysis.ObservedState == SafeMigrationObservedState.PrerequisiteMissing
                        && tableAnalysis.Code is "classified_prerequisite_missing" or "inline_foreign_key_prerequisite"
                        && HasAllInlinePrerequisites(table.Definition, columns)
                    ? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
                        SafeMigrationRepairCapability.None, false, "projected_inline_prerequisites_ready")
                    : tableAnalysis;
            case EnsureForeignKeyIntent foreignKey
                when projectedAnalysis.ObservedState != SafeMigrationObservedState.Matching:
                return ValidateForeignKeys([foreignKey.Definition], projectedAnalysis, columns);
            case DropTableIntent table:
                var tableId = GetTable(table.Table, table.Schema);
                if (_foreignKeys.Values.Any(edge => edge.Principal == tableId && edge.Dependent != tableId))
                {
                    return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
                        SafeMigrationRepairCapability.None, false, "projected_incoming_foreign_key");
                }

                if (incomingOnlyDrop)
                {
                    // WHY: Only this provider classification proves every non-FK
                    // blocker absent. Reconcile the immutable FK snapshot with
                    // accepted removals without weakening other DROP contracts.
                    return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
                        SafeMigrationRepairCapability.None, true, "projected_incoming_foreign_keys_removed");
                }

                break;
            case RenameTableIntent table:
                if (!SchemaExists(table.NewSchema ?? table.Schema))
                {
                    return Rejected("projected_schema_missing");
                }

                if (projectedAnalysis.ObservedState == SafeMigrationObservedState.PrerequisiteMissing
                    && projectedAnalysis.Code == "classified_prerequisite_missing"
                    && !_missingTables.Contains(GetTable(table.Name, table.Schema))
                    && _missingTables.Contains(GetTable(table.NewName ?? table.Name, table.NewSchema ?? table.Schema)))
                {
                    return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
                        SafeMigrationRepairCapability.None, false, "projected_schema_transfer_ready");
                }

                break;
        }

        return projectedAnalysis;
    }

    /// <inheritdoc />
    public void ObserveAcceptedOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action is not (SafeMigrationAction.Apply or SafeMigrationAction.Repair))
        {
            return;
        }

        switch (operation.Intent)
        {
            case EnsureSchemaIntent schema:
                _missingSchemas.Remove(GetSchema(schema.Name));
                break;
            case DropSchemaIntent schema:
                _missingSchemas.Add(GetSchema(schema.Name));
                break;
            case EnsureTableIntent table:
                var tableId = GetTable(table.Definition.Table, table.Definition.Schema);

                _missingTables.Remove(tableId);
                _tableSchemas[tableId] = GetSchema(table.Definition.Schema ?? "dbo");
                _createdTables[tableId] = table.Definition;
                if (table.Definition.PrimaryKey is { } primaryKey)
                {
                    AddCandidateKey(tableId, primaryKey.Name, primaryKey.Columns);
                }

                foreach (var unique in table.Definition.UniqueConstraints)
                {
                    AddCandidateKey(tableId, unique.Name, unique.Columns);
                }

                foreach (var foreignKey in table.Definition.ForeignKeys)
                {
                    Add(foreignKey);
                }

                break;
            case EnsureForeignKeyIntent foreignKey:
                Add(foreignKey.Definition);
                break;
            case DropForeignKeyIntent foreignKey:
                RemoveForeignKey(foreignKey.Table, foreignKey.Schema, foreignKey.Name);
                break;
            case DropTableIntent table:
                RemoveTable(table.Table, table.Schema);
                break;
            case RenameTableIntent table:
                _renameSafeTables.Add(GetTable(table.Name, table.Schema));
                RenameTable(table.Name, table.Schema, table.NewName ?? table.Name, table.NewSchema ?? table.Schema);
                break;
            case RenameColumnIntent column:
                RenameColumn(column.Table, column.Schema, column.Name, column.NewName);
                break;
            case DropColumnIntent column:
                RemoveColumn(column.Table, column.Schema, column.Name);
                break;
            case EnsureColumnIntent column:
                InvalidateColumnStorage(column.Table, column.Schema, column.Definition.Name);
                break;
            case AlterColumnIntent column:
                InvalidateColumnStorage(column.Table, column.Schema, column.Definition.Name);
                break;
            case EnsurePrimaryKeyIntent key:
                AddCandidateKey(
                    GetTable(key.Definition.Table, key.Definition.Schema), key.Definition.Name, key.Definition.Columns);
                break;
            case EnsureUniqueConstraintIntent key:
                AddCandidateKey(
                    GetTable(key.Definition.Table, key.Definition.Schema), key.Definition.Name, key.Definition.Columns);
                break;
            case DropPrimaryKeyIntent key:
                _candidateKeys.Remove(GetTable(key.Table, key.Schema), key.Name);
                break;
            case DropUniqueConstraintIntent key:
                _candidateKeys.Remove(GetTable(key.Table, key.Schema), key.Name);
                break;
            case EnsureIndexIntent index:
                AddCandidateIndex(index.Definition);
                break;
            case DropIndexIntent index:
                _candidateKeys.Remove(GetTable(index.Table, index.Schema), index.Name);
                break;
            case RenameIndexIntent index:
                _candidateKeys.Rename(GetTable(index.Table, index.Schema), index.Name, index.NewName);
                break;
        }
    }

    /// <inheritdoc />
    public void ObserveProviderOperation(
        MigrationOperation operation
    )
    {
        switch (operation)
        {
            case DropForeignKeyOperation foreignKey:
                RemoveForeignKey(foreignKey.Table, foreignKey.Schema, foreignKey.Name);
                break;
            case DropTableOperation table:
                RemoveTable(table.Name, table.Schema);
                break;
            case RenameTableOperation table:
                RenameTable(table.Name, table.Schema, table.NewName ?? table.Name, table.NewSchema ?? table.Schema);
                break;
            case EnsureSchemaOperation schema:
                _missingSchemas.Remove(GetSchema(schema.Name));
                break;
            case DropSchemaOperation schema:
                _missingSchemas.Add(GetSchema(schema.Name));
                break;
            case AddPrimaryKeyOperation key:
                AddCandidateKey(GetTable(key.Table, key.Schema), key.Name, key.Columns);
                break;
            case AddUniqueConstraintOperation key:
                AddCandidateKey(GetTable(key.Table, key.Schema), key.Name, key.Columns);
                break;
            case DropPrimaryKeyOperation key:
                _candidateKeys.Remove(GetTable(key.Table, key.Schema), key.Name);
                break;
            case DropUniqueConstraintOperation key:
                _candidateKeys.Remove(GetTable(key.Table, key.Schema), key.Name);
                break;
            case CreateIndexOperation index when index.IsUnique && index.Filter is null:
                AddCandidateKey(GetTable(index.Table, index.Schema), index.Name, index.Columns);
                break;
            case DropIndexOperation index when index.Table is not null:
                _candidateKeys.Remove(GetTable(index.Table, index.Schema), index.Name);
                break;
            case RenameIndexOperation index when index.Table is not null:
                _candidateKeys.Rename(GetTable(index.Table, index.Schema), index.Name, index.NewName);
                break;
            case RenameColumnOperation column:
                RenameColumn(column.Table, column.Schema, column.Name, column.NewName);
                break;
            case DropColumnOperation column:
                RemoveColumn(column.Table, column.Schema, column.Name);
                break;
            case AddColumnOperation column:
                InvalidateColumnStorage(column.Table, column.Schema, column.Name);
                break;
            case AlterColumnOperation column:
                InvalidateColumnStorage(column.Table, column.Schema, column.Name);
                break;
            case CreateIndexOperation or AddCheckConstraintOperation or DropCheckConstraintOperation
                or InsertDataOperation or UpdateDataOperation or DeleteDataOperation:
                break;
            default:
                // WHY: Ordinary SQL or an unproven provider effect can change
                // FKs and triggers. Retaining the original graph would authorize
                // a later DROP or cascade based on evidence that no longer holds.
                _unknown = true;
                break;
        }
    }

    /// <summary>Checks an event-aware graph with capped path counts.</summary>
    /// <param name="foreignKeys">The live and candidate FK edges.</param>
    /// <returns>Whether each DELETE/UPDATE action tree visits each physical table at most once.</returns>
    internal static bool HasValidTopology(
        IEnumerable<ForeignKeyEdge> foreignKeys
    )
    {
        var adjacency = new Dictionary<ActionNode, List<ActionNode>>();
        foreach (var edge in foreignKeys)
        {
            AddAction(edge.Principal, edge.Dependent, edge.OnDelete, isDelete: true);
            AddAction(edge.Principal, edge.Dependent, edge.OnUpdate, isDelete: false);
        }

        var seen = new HashSet<int>();
        var queue = new Queue<ActionNode>();
        foreach (var root in adjacency.Keys)
        {
            seen.Clear();
            queue.Clear();
            seen.Add(root.Table);
            queue.Enqueue(root);
            while (queue.TryDequeue(out var node))
            {
                if (!adjacency.TryGetValue(node, out var children))
                {
                    continue;
                }

                foreach (var child in children)
                {
                    if (!seen.Add(child.Table))
                    {
                        return false;
                    }

                    queue.Enqueue(child);
                }
            }
        }

        return true;

        void AddAction(
            int principal,
            int dependent,
            ReferentialAction action,
            bool isDelete
        )
        {
            if (!SqlServerForeignKeySafety.IsCascading(action))
            {
                return;
            }

            var source = new ActionNode(principal, isDelete);
            if (!adjacency.TryGetValue(source, out var targets))
            {
                targets = [];
                adjacency.Add(source, targets);
            }

            targets.Add(new ActionNode(dependent, isDelete && action == ReferentialAction.Cascade));
        }
    }

    /// <summary>Represents one FK's compact physical cascading contract.</summary>
    /// <param name="Dependent">The dependent physical table id.</param>
    /// <param name="Principal">The principal physical table id.</param>
    /// <param name="OnDelete">The dependent action caused by deletion.</param>
    /// <param name="OnUpdate">The dependent action caused by update.</param>
    internal readonly record struct ForeignKeyEdge(
        int Dependent,
        int Principal,
        ReferentialAction OnDelete,
        ReferentialAction OnUpdate
    );

    private SafeMigrationProviderAnalysis ValidateForeignKeys(
        IReadOnlyList<ExpectedForeignKeyDefinition> foreignKeys,
        SafeMigrationProviderAnalysis analysis,
        ISafeMigrationProjectedColumnSource columns,
        ExpectedTableDefinition? inlineTable = null
    )
    {
        var additions = new List<ForeignKeyEdge>(foreignKeys.Count);
        if (inlineTable is not null && !HasAllInlinePrerequisites(inlineTable, columns))
        {
            return Rejected("inline_foreign_key_prerequisite");
        }

        foreach (var foreignKey in foreignKeys)
        {
            var dependentId = GetTable(foreignKey.Table, foreignKey.Schema);
            var principalId = GetTable(foreignKey.PrincipalTable, foreignKey.PrincipalSchema);
            if (_triggers.TryGetValue(dependentId, out var triggers)
                && ((foreignKey.OnDelete == ReferentialAction.Cascade && triggers.Delete)
                    || ((SqlServerForeignKeySafety.IsCascading(foreignKey.OnUpdate)
                            || foreignKey.OnDelete is ReferentialAction.SetNull or ReferentialAction.SetDefault)
                        && triggers.Update)))
            {
                return Rejected("foreign_key_instead_of_trigger");
            }

            for (var ordinal = 0; ordinal < foreignKey.Columns.Count; ordinal++)
            {
                var dependentColumn = ReadColumn(foreignKey.Table, foreignKey.Schema,
                    foreignKey.Columns[ordinal], columns, inlineTable);

                var principalColumn = ReadColumn(foreignKey.PrincipalTable, foreignKey.PrincipalSchema,
                    foreignKey.PrincipalColumns[ordinal], columns, inlineTable);

                if (SqlServerForeignKeySafety.HasCascadingAction(foreignKey)
                    && (dependentColumn?.RowVersion == true || principalColumn?.RowVersion == true))
                {
                    return Rejected("foreign_key_rowversion_action");
                }

                if ((foreignKey.OnDelete == ReferentialAction.SetNull
                        || foreignKey.OnUpdate == ReferentialAction.SetNull)
                    && dependentColumn?.Nullable == false)
                {
                    return Rejected("foreign_key_set_null_required_column");
                }

                if ((foreignKey.OnDelete == ReferentialAction.SetDefault
                        || foreignKey.OnUpdate == ReferentialAction.SetDefault)
                    && dependentColumn is { Nullable: false, HasDefault: false })
                {
                    return Rejected("foreign_key_set_default_missing_default");
                }
            }

            if (!_foreignKeyNames.TryGetValue(new ForeignKeyKey(dependentId, foreignKey.Name), out var existingId)
                || !_foreignKeys.ContainsKey(existingId))
            {
                additions.Add(new ForeignKeyEdge(dependentId, principalId, foreignKey.OnDelete, foreignKey.OnUpdate));
            }
        }

        var topologyValid = !additions.Any(static edge => SqlServerForeignKeySafety.IsCascading(edge.OnDelete)
                    || SqlServerForeignKeySafety.IsCascading(edge.OnUpdate))
                || HasValidTopology(_foreignKeys.Values.Concat(additions));

        if (!topologyValid)
        {
            return Rejected("foreign_key_cascade_topology");
        }

        // WHY: The immutable catalog may reject a cascade that becomes
        // valid after an accepted FK drop. This provider-specific code is
        // emitted only after storage and orphan prerequisites are proven;
        // unrelated missing prerequisites must never become an Apply.

        return analysis.ObservedState == SafeMigrationObservedState.PrerequisiteMissing
                && analysis.Code == "foreign_key_cascade_topology"
            ? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
                SafeMigrationRepairCapability.None, false, "projected_foreign_key_topology_ready")
            : analysis;
    }

    private bool HasAllInlinePrerequisites(
        ExpectedTableDefinition table,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        var dependentId = GetTable(table.Table, table.Schema);
        // WHY: SQL Server caps FK keys at 32 columns. A fixed stack buffer
        // avoids allocating requested id vectors for every inline assessment.
        Span<int> columnIds = stackalloc int[32];

        foreach (var foreignKey in table.ForeignKeys)
        {
            var principalId = GetTable(foreignKey.PrincipalTable, foreignKey.PrincipalSchema);
            if (foreignKey.PrincipalColumns.Count > columnIds.Length
                || !_inlinePhysicalWidthSupported(table, foreignKey))
            {
                return false;
            }

            ResolveColumnIds(principalId, foreignKey.PrincipalColumns, columnIds);

            if (principalId != dependentId && (_missingTables.Contains(principalId)
                || !_candidateKeys.Contains(principalId, foreignKey.PrincipalColumns,
                    columnIds[..foreignKey.PrincipalColumns.Count])))
            {
                return false;
            }

            var principal = principalId == dependentId ? table : _createdTables.GetValueOrDefault(principalId);
            if (principalId == dependentId
                && !(table.PrimaryKey?.Columns.SequenceEqual(foreignKey.PrincipalColumns) == true
                    || table.UniqueConstraints.Any(key => key.Columns.SequenceEqual(foreignKey.PrincipalColumns))))
            {
                return false;
            }

            for (var ordinal = 0; ordinal < foreignKey.Columns.Count; ordinal++)
            {
                var dependentColumn = table.Columns.First(column => column.Name == foreignKey.Columns[ordinal]);
                var name = foreignKey.PrincipalColumns[ordinal];
                var principalColumn = principal?.Columns.FirstOrDefault(column => column.Name == name);

                if (principalId != dependentId
                    && columns.TryGetProjectedColumn(foreignKey.PrincipalTable, foreignKey.PrincipalSchema, name,
                        out var projectedColumn))
                {
                    principalColumn = projectedColumn;
                }

                if (principalColumn is not null)
                {
                    if (!_storageEquals(dependentColumn, principalColumn))
                    {
                        return false;
                    }

                    continue;
                }

                // WHY: A Boolean snapshot is not ordered evidence. Keep its
                // physical table/column identity and require the same surviving
                // candidate key; renames preserve ids, drops/replacements do not.
                if (!_liveInlineReady.TryGetValue(foreignKey, out var proof)
                    || proof.Principal != principalId
                    || columnIds[ordinal] <= 0
                    || proof.ColumnIds[ordinal] != columnIds[ordinal]
                    || _invalidatedStorage.Contains(new ColumnIdentity(principalId, columnIds[ordinal])))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private ColumnSafety? ReadColumn(
        string table,
        string? schema,
        string column,
        ISafeMigrationProjectedColumnSource columns,
        ExpectedTableDefinition? inlineTable
    )
    {
        ExpectedColumnDefinition? definition = null;
        if (inlineTable?.Table == table && (inlineTable.Schema ?? "dbo") == (schema ?? "dbo"))
        {
            definition = inlineTable.Columns.FirstOrDefault(value => value.Name == column);
        }

        if (definition is null)
        {
            columns.TryGetProjectedColumn(table, schema, column, out definition);
        }

        if (definition is not null)
        {
            return new ColumnSafety(definition.IsRowVersion
                    || definition.StoreType is { } type
                        && (type.Equals("rowversion", StringComparison.OrdinalIgnoreCase)
                        || type.Equals("timestamp", StringComparison.OrdinalIgnoreCase)),
                definition.IsNullable, definition.DefaultValue.Kind != SafeMigrationDefaultValueKind.None);
        }

        return _columns.TryGetValue(new ColumnKey(GetTable(table, schema), column), out var live) ? live : null;
    }

    private void Add(
        ExpectedForeignKeyDefinition definition
    )
    {
        var dependent = GetTable(definition.Table, definition.Schema);
        var key = new ForeignKeyKey(dependent, definition.Name);
        if (!_foreignKeyNames.TryGetValue(key, out var id))
        {
            id = _nextForeignKeyId--;
            _foreignKeyNames[key] = id;
        }

        _foreignKeys[id] = new ForeignKeyEdge(dependent,
            GetTable(definition.PrincipalTable, definition.PrincipalSchema), definition.OnDelete, definition.OnUpdate);
    }

    private void RemoveForeignKey(
        string table,
        string? schema,
        string name
    )
    {
        if (_foreignKeyNames.TryGetValue(new ForeignKeyKey(GetTable(table, schema), name), out var id))
        {
            _foreignKeys.Remove(id);
        }
    }

    private void RemoveTable(
        string table,
        string? schema
    )
    {
        var id = GetTable(table, schema);

        _missingTables.Add(id);
        _createdTables.Remove(id);
        _renameSafeTables.Remove(id);
        _triggers.Remove(id);
        _candidateKeys.RemoveTable(id);
        foreach (var key in _columns.Keys.Where(key => key.Table == id).ToArray())
        {
            _columns.Remove(key);
            _columnIds.Remove(key);
        }

        foreach (var pair in _foreignKeys.Where(pair => pair.Value.Dependent == id).ToArray())
        {
            _foreignKeys.Remove(pair.Key);
        }
    }

    private void AddCandidateIndex(
        ExpectedIndexDefinition index
    )
    {
        if (index.Unique && index.Filter is null && index.StructuredFilter is null
            && index.Keys.All(static key => key.Column is not null))
        {
            AddCandidateKey(GetTable(index.Table, index.Schema), index.Name,
                index.Keys.Select(static key => key.Column!).ToArray());
        }
    }

    private void AddCandidateKey(
        int table,
        string name,
        IReadOnlyList<string> columns
    )
    {
        Span<int> columnIds = stackalloc int[32];

        if (columns.Count > columnIds.Length)
        {
            return;
        }

        ResolveColumnIds(table, columns, columnIds);
        _candidateKeys.Add(table, name, columns, columnIds[..columns.Count]);
    }

    private void ResolveColumnIds(
        int table,
        IReadOnlyList<string> columns,
        Span<int> columnIds
    )
    {
        for (var ordinal = 0; ordinal < columns.Count; ordinal++)
        {
            columnIds[ordinal] = _columnIds.GetValueOrDefault(new ColumnKey(table, columns[ordinal]));
        }
    }

    private void RenameColumn(
        string table,
        string? schema,
        string name,
        string newName
    )
    {
        var id = GetTable(table, schema);
        var source = new ColumnKey(id, name);
        var target = new ColumnKey(id, newName);

        if (_columns.Remove(source, out var safety))
        {
            _columns[target] = safety;
        }

        var columnId = _columnIds.GetValueOrDefault(source);

        if (_columnIds.Remove(source))
        {
            _columnIds[target] = columnId;
        }

        _candidateKeys.RenameColumn(id, name, newName, columnId);
    }

    private void RemoveColumn(
        string table,
        string? schema,
        string name
    )
    {
        var id = GetTable(table, schema);
        var key = new ColumnKey(id, name);
        var columnId = _columnIds.GetValueOrDefault(key);

        _columns.Remove(key);
        _columnIds.Remove(key);
        _candidateKeys.RemoveColumn(id, name, columnId);
    }

    private void InvalidateColumnStorage(
        string table,
        string? schema,
        string name
    )
    {
        var id = GetTable(table, schema);

        if (_columnIds.TryGetValue(new ColumnKey(id, name), out var columnId))
        {
            _invalidatedStorage.Add(new ColumnIdentity(id, columnId));
        }
    }

    private void RenameTable(
        string table,
        string? schema,
        string newTable,
        string? newSchema
    )
    {
        var id = GetTable(table, schema);
        if (table == newTable && (schema ?? "dbo") == (newSchema ?? "dbo"))
        {
            return;
        }

        _tables[new TableKey(newSchema ?? "dbo", newTable)] = id;
        _tableSchemas[id] = GetSchema(newSchema ?? "dbo");
        var missingSourceId = _nextTableId--;

        _tables[new TableKey(schema ?? "dbo", table)] = missingSourceId;
        _missingTables.Add(missingSourceId);
    }

    private SafeMigrationProviderAnalysis? ValidateOrderedRename(
        RenameTableIntent intent
    )
    {
        var source = GetTable(intent.Name, intent.Schema);
        var targetName = new TableKey(intent.NewSchema ?? intent.Schema ?? "dbo", intent.NewName ?? intent.Name);
        var target = GetTable(targetName.Table, targetName.Schema);
        if (!_renameSafeTables.Contains(source) || _missingTables.Contains(source))
        {
            return null;
        }

        if (!SchemaExists(targetName.Schema))
        {
            return Rejected("projected_schema_missing");
        }

        if (target != source && (!_missingTables.Contains(target) || _nonTableNames.Contains(targetName)))
        {
            return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
                SafeMigrationRepairCapability.None, false, "projected_rename_target_occupied");
        }

        if (targetName.Table != intent.Name
            && targetName.Schema != (intent.Schema ?? "dbo"))
        {
            var intermediate = new TableKey(intent.Schema ?? "dbo", targetName.Table);
            if (!_tables.TryGetValue(intermediate, out var intermediateId)
                || !_missingTables.Contains(intermediateId)
                || _nonTableNames.Contains(intermediate))
            {
                return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
                    SafeMigrationRepairCapability.None, false, "projected_rename_target_occupied");
            }
        }

        // WHY: Only an accepted prior rename carries this proof that the same
        // ordinary physical table has no unsafe textual dependencies. Catalog
        // absence under the later source name alone cannot authorize a rename.
        return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None, target == source, "projected_rename_source_ready");
    }

    private int GetTable(
        string table,
        string? schema
    )
    {
        var key = new TableKey(schema ?? "dbo", table);
        if (!_tables.TryGetValue(key, out var id))
        {
            id = _nextTableId--;
            _tables[key] = id;
        }

        return id;
    }

    private int GetSchema(
        string schema
    )
    {
        if (!_schemas.TryGetValue(schema, out var id))
        {
            id = _nextSchemaId--;
            _schemas[schema] = id;
        }

        return id;
    }

    private bool SchemaExists(
        string? schema
    )
        => !_missingSchemas.Contains(GetSchema(schema ?? "dbo"));

    private SafeMigrationProviderAnalysis ValidateSchemaOperation(
        SafeMigrationIntent intent,
        SafeMigrationProviderAnalysis analysis
    )
    {
        if (analysis.ObservedState is SafeMigrationObservedState.Unsupported or SafeMigrationObservedState.DataBlocked)
        {
            return analysis;
        }

        if (_unknown)
        {
            return Rejected("projected_schema_state_unknown", unknown: true);
        }

        var name = intent is EnsureSchemaIntent ensure ? ensure.Name : ((DropSchemaIntent)intent).Name;
        var schema = GetSchema(name);
        var exists = !_missingSchemas.Contains(schema);

        if (intent is DropSchemaIntent && exists
            && (_schemasWithOtherObjects.Contains(schema)
                || _tableSchemas.Any(pair => pair.Value == schema && !_missingTables.Contains(pair.Key))))
        {
            return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
                SafeMigrationRepairCapability.None, false, "projected_schema_not_empty");
        }

        // WHY: Schema existence is ordered state, not the immutable analyzer
        // snapshot. Every non-table object remains a blocker; table drops and
        // transfers change emptiness only after their decisions were accepted.

        return new SafeMigrationProviderAnalysis(
            exists ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, intent is EnsureSchemaIntent ? exists : !exists,
            exists ? "projected_schema_present" : "projected_schema_absent");
    }

    private async Task ReadSchemaObjectsAsync(
        DbCommand command,
        IReadOnlyList<SafeMigrationOperation> operations,
        CancellationToken cancellationToken
    )
    {
        var schemas = operations.Select(static operation => operation.Intent)
            .OfType<DropSchemaIntent>().Select(intent => GetSchema(intent.Name))
            .Where(static id => id > 0).Distinct();

        foreach (var chunk in schemas.Chunk(256))
        {
            var ids = string.Join(", ", chunk.Select(static id => id.ToString(CultureInfo.InvariantCulture)));

            // WHY: A schema DROP needs all resident tables, not just requested
            // ones. Other top-level objects/types stay conservatively present;
            // table-owned constraints and triggers disappear with their table.
            command.CommandText = "SELECT s.schema_id, CONVERT(bit, CASE WHEN "
                + "EXISTS (SELECT 1 FROM sys.objects o WHERE o.schema_id = s.schema_id "
                + "AND o.type <> N'U' AND o.parent_object_id = 0) "
                + "OR EXISTS (SELECT 1 FROM sys.types t WHERE t.schema_id = s.schema_id AND t.is_user_defined = 1) "
                + "OR EXISTS (SELECT 1 FROM sys.xml_schema_collections x WHERE x.schema_id = s.schema_id) "
                + "THEN 1 ELSE 0 END) FROM sys.schemas s "
                + $"WHERE s.schema_id IN ({ids}); "
                + $"SELECT object_id, schema_id FROM sys.tables WHERE schema_id IN ({ids});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetBoolean(1))
                {
                    _schemasWithOtherObjects.Add(reader.GetInt32(0));
                }
            }

            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                _tableSchemas[reader.GetInt32(0)] = reader.GetInt32(1);
            }
        }
    }

    private static SafeMigrationProviderAnalysis Rejected(
        string code,
        bool unknown = false
    )
        => new(SafeMigrationObservedState.PrerequisiteMissing, SafeMigrationRepairCapability.None, false, code)
        {
            IsOpaqueProjectionUnknown = unknown,
        };

    private static ReferentialAction CatalogAction(
        byte action
    )
        => action switch
        {
            0 => ReferentialAction.NoAction,
            1 => ReferentialAction.Cascade,
            2 => ReferentialAction.SetNull,
            3 => ReferentialAction.SetDefault,
            _ => throw new InvalidOperationException("SQL Server returned an unknown FK action."),
        };

    private static HashSet<BindingRequest> CollectRequests(
        IReadOnlyList<SafeMigrationOperation> operations
    )
    {
        var result = new HashSet<BindingRequest>();
        foreach (var operation in operations)
        {
            switch (operation.Intent)
            {
                case EnsureSchemaIntent schema:
                    result.Add(new BindingRequest(schema.Name, string.Empty, null, null));
                    break;
                case DropSchemaIntent schema:
                    result.Add(new BindingRequest(schema.Name, string.Empty, null, null));
                    break;
                case EnsureTableIntent table:
                    result.Add(new BindingRequest(
                        table.Definition.Schema ?? "dbo", table.Definition.Table, null, null));
                    foreach (var foreignKey in table.Definition.ForeignKeys)
                    {
                        AddForeignKey(foreignKey);
                    }

                    break;
                case DropTableIntent table:
                    result.Add(new BindingRequest(table.Schema ?? "dbo", table.Table, null, null));
                    break;
                case RenameTableIntent table:
                    result.Add(new BindingRequest(table.Schema ?? "dbo", table.Name, null, null));
                    result.Add(new BindingRequest(
                        table.NewSchema ?? table.Schema ?? "dbo", table.NewName ?? table.Name, null, null));
                    if ((table.NewName ?? table.Name) != table.Name
                        && (table.NewSchema ?? table.Schema ?? "dbo") != (table.Schema ?? "dbo"))
                    {
                        result.Add(new BindingRequest(table.Schema ?? "dbo", table.NewName ?? table.Name, null, null));
                    }

                    break;
                case EnsureForeignKeyIntent foreignKey:
                    AddForeignKey(foreignKey.Definition);
                    break;
                case DropForeignKeyIntent foreignKey:
                    result.Add(new BindingRequest(foreignKey.Schema ?? "dbo", foreignKey.Table, null, foreignKey.Name));
                    break;
                case DropPrimaryKeyIntent key:
                    result.Add(new BindingRequest(key.Schema ?? "dbo", key.Table, null, key.Name));
                    break;
                case DropUniqueConstraintIntent key:
                    result.Add(new BindingRequest(key.Schema ?? "dbo", key.Table, null, key.Name));
                    break;
                case DropIndexIntent index:
                    result.Add(new BindingRequest(index.Schema ?? "dbo", index.Table, null, index.Name));
                    break;
                case RenameIndexIntent index:
                    result.Add(new BindingRequest(index.Schema ?? "dbo", index.Table, null, index.Name));
                    break;
                case RenameColumnIntent column:
                    result.Add(new BindingRequest(column.Schema ?? "dbo", column.Table, column.Name, null));
                    break;
                case DropColumnIntent column:
                    result.Add(new BindingRequest(column.Schema ?? "dbo", column.Table, column.Name, null));
                    break;
            }
        }

        return result;

        void AddForeignKey(ExpectedForeignKeyDefinition definition)
        {
            for (var ordinal = 0; ordinal < definition.Columns.Count; ordinal++)
            {
                result.Add(new BindingRequest(
                    definition.Schema ?? "dbo", definition.Table, definition.Columns[ordinal], definition.Name));
                result.Add(new BindingRequest(
                    definition.PrincipalSchema ?? "dbo", definition.PrincipalTable,
                    definition.PrincipalColumns[ordinal], null));
            }
        }
    }

    private static InlineRequest[] CollectInlineRequests(
        IReadOnlyList<SafeMigrationOperation> operations
    )
    {
        var result = new List<InlineRequest>();
        var renames = new List<SafeMigrationIntent>();

        foreach (var operation in operations)
        {
            if (operation.Intent is RenameTableIntent or RenameColumnIntent)
            {
                renames.Add(operation.Intent);
            }

            if (operation.Intent is not EnsureTableIntent table)
            {
                continue;
            }

            foreach (var foreignKey in table.Definition.ForeignKeys)
            {
                var principalTable = foreignKey.PrincipalTable;
                var principalSchema = foreignKey.PrincipalSchema ?? "dbo";
                var principalColumns = foreignKey.PrincipalColumns.ToArray();

                // WHY: A later reference to an accepted rename target needs
                // metadata from its initial source. This is only a hypothesis
                // until projection proves the same physical ids reached that
                // target; a rejected rename can never activate this proof.
                for (var previous = renames.Count - 1; previous >= 0; previous--)
                {
                    switch (renames[previous])
                    {
                        case RenameTableIntent rename when (rename.NewName ?? rename.Name) == principalTable
                            && (rename.NewSchema ?? rename.Schema ?? "dbo") == principalSchema:
                            principalTable = rename.Name;
                            principalSchema = rename.Schema ?? "dbo";
                            break;
                        case RenameColumnIntent rename when rename.Table == principalTable
                            && (rename.Schema ?? "dbo") == principalSchema:
                            for (var column = 0; column < principalColumns.Length; column++)
                            {
                                if (principalColumns[column] == rename.NewName)
                                {
                                    principalColumns[column] = rename.Name;
                                }
                            }

                            break;
                    }
                }

                var initial = new ExpectedForeignKeyDefinition(foreignKey.Name, foreignKey.Table, foreignKey.Columns,
                    principalTable, principalColumns, foreignKey.Schema, principalSchema,
                    onUpdate: foreignKey.OnUpdate, onDelete: foreignKey.OnDelete);

                result.Add(new InlineRequest(table.Definition, foreignKey, initial));
            }
        }

        return result.ToArray();
    }

    private static string Literal(
        string? value
    )
        => value is null
            ? "CONVERT(nvarchar(128), NULL)"
            : "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private readonly record struct BindingRequest(
        string Schema,
        string Table,
        string? Column,
        string? ForeignKey
    );

    private readonly record struct InlineRequest(
        ExpectedTableDefinition Table,
        ExpectedForeignKeyDefinition ForeignKey,
        ExpectedForeignKeyDefinition InitialForeignKey
    );

    private readonly record struct InlineStorageProof(
        int Principal,
        IReadOnlyList<int> ColumnIds
    );

    private readonly record struct ColumnIdentity(
        int Table,
        int Column
    );

    private readonly record struct TableKey(
        string Schema,
        string Table
    );

    private readonly record struct ForeignKeyKey(
        int Table,
        string Name
    );

    private readonly record struct ColumnKey(
        int Table,
        string Column
    );

    private readonly record struct ColumnSafety(
        bool RowVersion,
        bool Nullable,
        bool HasDefault
    );

    private readonly record struct TriggerSafety(
        bool Delete,
        bool Update
    );

    private readonly record struct ActionNode(
        int Table,
        bool Delete
    );
}
