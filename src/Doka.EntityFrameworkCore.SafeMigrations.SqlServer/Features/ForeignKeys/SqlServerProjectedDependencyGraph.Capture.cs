namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerProjectedDependencyGraph
{
    /// <summary>Copies independently captured rename destination occupancy before ordered graph mutations.</summary>
    /// <param name="operations">The operation stream used to bind the immutable catalog names.</param>
    /// <param name="analyses">The corresponding immutable classifications to enrich.</param>
    internal void CaptureRenameTargetPresence(
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis[] analyses
    )
    {
        // WHY: Store rename target evidence in the analysis slot owning that operation.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
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

            analyses[index] = analyses[index]
                .WithRenameTargetExists(
                    !_missingTables.Contains(identity) || _nonTableNames.Contains(target),
                    intermediateExists);
        }
    }

    /// <summary>Reads one FK graph and bounded bindings using the caller's analysis transaction.</summary>
    /// <param name="connection">The open provider connection.</param>
    /// <param name="transaction">The active analysis transaction.</param>
    /// <param name="operations">The stream whose names must resolve using catalog collation.</param>
    /// <param name="commandTimeout">The configured command timeout.</param>
    /// <param name="inlinePrerequisite">The provider's inline storage and candidate-key predicate.</param>
    /// <param name="storageEquals">The provider's inferred physical column comparison.</param>
    /// <param name="inlinePhysicalWidthSupported">The provider's authored inline key-width proof.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="sparsePrerequisite">The catalog-only mixed live/authored endpoint validator.</param>
    /// <returns>A mutable graph private to this analysis scope.</returns>
    public static async Task<SqlServerProjectedDependencyGraph> ReadAsync(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyList<SafeMigrationOperation> operations,
        int? commandTimeout,
        Func<ExpectedTableDefinition, ExpectedForeignKeyDefinition, string> inlinePrerequisite,
        Func<ExpectedColumnDefinition, ExpectedColumnDefinition, bool> storageEquals,
        Func<ExpectedTableDefinition, ExpectedForeignKeyDefinition, bool> inlinePhysicalWidthSupported,
        CancellationToken cancellationToken,
        Func<ExpectedTableDefinition?, ExpectedForeignKeyDefinition, int, string>? sparsePrerequisite = null
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
            + "delete_referential_action, update_referential_action, key_index_id FROM sys.foreign_keys;";

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt32(0);
                var edge = new ForeignKeyEdge(
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    CatalogAction(reader.GetByte(4)),
                    CatalogAction(reader.GetByte(5)),
                    reader.GetInt32(6));

                graph._foreignKeys[id] = edge;
                graph._foreignKeyNames[new ForeignKeyKey(edge.Dependent, reader.GetString(3))] = id;
            }
        }

        var inline = CollectInlineRequests(operations);
        var requests = CollectRequests(operations);

        foreach (var request in inline)
        {
            if (request.IsSparse)
            {
                graph._sparseCaptured.Add(request.ForeignKey);
            }

            if (request.IsStandalone)
            {
                graph._standaloneNames.Add(
                    new SchemaObjectKey(request.ForeignKey.Schema ?? "dbo", request.ForeignKey.Name));
                graph._standaloneObjectNames.Add(request.ForeignKey.Name);
            }

            foreach (var column in request.InitialForeignKey.PrincipalColumns)
            {
                requests.Add(
                    new BindingRequest(
                        request.InitialForeignKey.PrincipalSchema ?? "dbo",
                        request.InitialForeignKey.PrincipalTable,
                        column,
                        null));
            }
        }

        foreach (var operation in operations)
        {
            if (operation.Intent is not RenameTableIntent rename
                || (rename.Schema ?? "dbo") == (rename.NewSchema ?? rename.Schema ?? "dbo"))
            {
                continue;
            }

            foreach (var name in graph._standaloneNames)
            {
                if (name.Schema == (rename.NewSchema ?? rename.Schema ?? "dbo"))
                {
                    // WHY: A live source can own the future FK name before
                    // ALTER SCHEMA transfers it. Bind that owner in the same
                    // bounded catalog batches; unrelated names stay uncaptured.
                    requests.Add(new BindingRequest(rename.Schema ?? "dbo", rename.Name, null, name.Name));
                }
            }
        }

        foreach (var chunk in requests.Chunk(256))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // WHY: Literal binding uses CATALOG_DEFAULT, not a guessed .NET
            // case-folding comparer. A table alias maps to the real object id.
            // Occupancy includes schema-scoped child objects, matching the
            // runtime ObjectExists guard rather than assuming only root names collide.
            var values = string.Join(
                ", ",
                chunk.Select(static request =>
                    "("
                    + Literal(request.Schema)
                    + ", "
                    + Literal(request.Table)
                    + ", "
                    + Literal(request.Column)
                    + ", "
                    + Literal(request.ForeignKey)
                    + ")"));

            command.CommandText = "SELECT requested.schema_name, requested.table_name, requested.column_name, "
                + "requested.fk_name, s.schema_id, t.object_id, c.system_type_id, c.is_nullable, "
                + "c.default_object_id, fk.object_id, "
                + "CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM sys.triggers tr JOIN sys.trigger_events te "
                + "ON te.object_id = tr.object_id WHERE tr.parent_id = t.object_id "
                + "AND tr.is_instead_of_trigger = 1 AND te.type_desc = N'DELETE') THEN 1 ELSE 0 END), "
                + "CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM sys.triggers tr JOIN sys.trigger_events te "
                + "ON te.object_id = tr.object_id WHERE tr.parent_id = t.object_id "
                + "AND tr.is_instead_of_trigger = 1 AND te.type_desc = N'UPDATE') THEN 1 ELSE 0 END), "
                + "ix.index_id, kc.unique_index_id, c.column_id, occupied.object_id, "
                + "named.object_id, named.parent_object_id, named.type "
                + $"FROM (VALUES {values}) requested(schema_name, table_name, column_name, fk_name) "
                + "LEFT JOIN sys.schemas s ON s.name = requested.schema_name COLLATE CATALOG_DEFAULT "
                + "LEFT JOIN sys.tables t ON t.schema_id = s.schema_id "
                + "AND t.name = requested.table_name COLLATE CATALOG_DEFAULT "
                + "LEFT JOIN sys.objects occupied ON occupied.schema_id = s.schema_id "
                + "AND occupied.name = requested.table_name COLLATE CATALOG_DEFAULT "
                + "LEFT JOIN sys.objects named ON named.schema_id = s.schema_id "
                + "AND named.name = requested.fk_name COLLATE CATALOG_DEFAULT "
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
                if (!reader.IsDBNull(6)
                    && !reader.IsDBNull(2))
                {
                    graph._columns[new ColumnKey(tableId, reader.GetString(2))] = new ColumnSafety(
                        reader.GetByte(6) == 189,
                        reader.GetBoolean(7),
                        reader.GetInt32(8) != 0);
                    graph._columnIds[new ColumnKey(tableId, reader.GetString(2))] = reader.GetInt32(14);
                }

                if (!reader.IsDBNull(9)
                    && !reader.IsDBNull(3))
                {
                    graph._foreignKeyNames[new ForeignKeyKey(tableId, reader.GetString(3))] = reader.GetInt32(9);
                }

                if (!reader.IsDBNull(3)
                    && (!reader.IsDBNull(12) || !reader.IsDBNull(13)))
                {
                    graph._candidateKeys.BindName(
                        tableId,
                        reader.GetString(3),
                        reader.IsDBNull(12) ? reader.GetInt32(13) : reader.GetInt32(12));
                }

                if (!reader.IsDBNull(3)
                    && !reader.IsDBNull(16))
                {
                    var name = new SchemaObjectKey(schema, reader.GetString(3));
                    if (graph._standaloneObjectNames.Contains(name.Name))
                    {
                        // WHY: Constraint names are schema-global, unlike index
                        // names. Capture compact physical ownership in this
                        // existing binding batch, including non-constraint
                        // occupants, before a missing child hides the conflict.
                        graph._constraintNames[name] = new ConstraintOccupancy(
                            reader.GetInt32(16),
                            reader.GetInt32(17),
                            reader
                                .GetString(18)
                                .TrimEnd());
                    }
                }
            }
        }

        await graph.ReadSchemaObjectsAsync(command, operations, cancellationToken);
        await graph._candidateKeys.ReadAsync(
            command,
            inline.Select(request => graph.GetTable(
                request.InitialForeignKey.PrincipalTable,
                request.InitialForeignKey.PrincipalSchema)),
            cancellationToken);

        // WHY: A native fallback cannot prove an absent dependent column.
        // Prune it after bindings so a newly added operand consumes only its
        // authored alternatives in the existing bounded metadata transport.
        inline = inline
            .Where(request =>
                !request.IsSparse
                || request.Table is not null
                || graph.HasLiveColumn(
                    request.ForeignKey.Table,
                    request.ForeignKey.Schema,
                    request.ForeignKey.Columns[request.OperandOrdinal]))
            .ToArray();

        // WHY: Advance by accepted batch size while enforcing row and SQL byte limits.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var start = 0; start < inline.Length;)
        {
            var batch = new StringBuilder();
            var count = 0;
            var bytes = 0;
            while (start + count < inline.Length
                   && count < 32)
            {
                var request = inline[start + count];
                var selection = "SELECT "
                    + (start + count).ToString(CultureInfo.InvariantCulture)
                    + ", CONVERT(bit, CASE WHEN "
                    + (request.IsSparse && sparsePrerequisite is not null
                        ? sparsePrerequisite(request.Table, request.InitialForeignKey, request.OperandOrdinal)
                        : request.Table is null
                            ? "1 = 0"
                            : inlinePrerequisite(request.Table, request.InitialForeignKey))
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
                        request.InitialForeignKey.PrincipalTable,
                        request.InitialForeignKey.PrincipalSchema);

                    if (request.IsSparse)
                    {
                        var columnId = graph._columnIds.GetValueOrDefault(
                            new ColumnKey(
                                principal,
                                request.InitialForeignKey.PrincipalColumns[request.OperandOrdinal]));

                        if (columnId > 0)
                        {
                            if (!graph._sparseLiveReady.TryGetValue(request.ForeignKey, out var proofs))
                            {
                                proofs = [];
                                graph._sparseLiveReady.Add(request.ForeignKey, proofs);
                            }

                            proofs.Add(
                                new SparseStorageProof(
                                    principal,
                                    columnId,
                                    request.OperandOrdinal,
                                    request.Table?.Columns[0]));
                        }

                        continue;
                    }

                    var columnIds = request
                        .InitialForeignKey
                        .PrincipalColumns
                        .Select(column => graph._columnIds.GetValueOrDefault(new ColumnKey(principal, column)))
                        .ToArray();

                    if (columnIds.All(static id => id > 0))
                    {
                        // WHY: One FK instance can be reused across authored
                        // child contracts. A later successful capture must
                        // neither certify an incompatible earlier operand nor
                        // replace its independent valid full-definition proof.
                        var key = new InlineProofKey(request.ForeignKey, request.Table!);

                        var proof = new InlineStorageProof(principal, columnIds, request.Table!, request.IsStandalone);

                        graph._liveInlineReady[key] = proof;
                    }
                }
            }

            start += count;
        }

        return graph;
    }

    /// <inheritdoc />
    public bool HasLiveColumn(
        string table,
        string? schema,
        string column
    )
    {
        var identity = GetTable(table, schema);
        var columnId = _columnIds.GetValueOrDefault(new ColumnKey(identity, column));

        return !_unknown
            && identity > 0
            && !_missingTables.Contains(identity)
            && columnId > 0
            && !_invalidatedStorage.Contains(new ColumnIdentity(identity, columnId));
    }

    /// <summary>Reuses captured physical endpoints to exclude unrelated native FK-removal candidates.</summary>
    /// <param name="drop">The exact authored native FK removal.</param>
    /// <param name="source">The column owner whose dependency predicate is being qualified.</param>
    /// <returns>Physical owner relevance, or null when this selective inventory did not bind both owners.</returns>
    internal bool? IsNativeForeignKeyDropRelevant(
        DropForeignKeyIntent drop,
        DropColumnIntent source
    )
    {
        if (!_tables.TryGetValue(new TableKey(drop.Schema ?? "dbo", drop.Table), out var dependent)
            || dependent <= 0
            || !_tables.TryGetValue(new TableKey(source.Schema ?? "dbo", source.Table), out var owner)
            || owner <= 0
            || !_foreignKeyNames.TryGetValue(new ForeignKeyKey(dependent, drop.Name), out var identity)
            || !_foreignKeys.TryGetValue(identity, out var edge))
        {
            return null;
        }

        return edge.Dependent == owner || edge.Principal == owner;
    }

    /// <summary>Returns only a captured native table identity, never a projected or missing owner.</summary>
    internal int? GetNativeTableIdentity(
        string table,
        string? schema
    ) => _tables.TryGetValue(new TableKey(schema ?? "dbo", table), out var identity) && identity > 0 ? identity : null;

    /// <summary>Resolves exact native FK endpoint ids for invocation-local predecessor routing.</summary>
    internal (int Dependent, int Principal)? GetNativeForeignKeyDropEndpoints(
        DropForeignKeyIntent drop
    ) => GetNativeTableIdentity(drop.Table, drop.Schema) is { } owner
        && _foreignKeyNames.TryGetValue(new ForeignKeyKey(owner, drop.Name), out var identity)
        && identity > 0
        && _foreignKeys.TryGetValue(identity, out var edge)
            ? (edge.Dependent, edge.Principal)
            : null;

    /// <summary>Streams existing compact native endpoints without another retained metadata snapshot.</summary>
    internal IEnumerable<(int Dependent, int Principal)> EnumerateNativeForeignKeyEndpoints()
    {
        foreach (var (identity, edge) in _foreignKeys)
        {
            if (identity > 0
                && edge is { Dependent: > 0, Principal: > 0 })
            {
                yield return (edge.Dependent, edge.Principal);
            }
        }
    }

    /// <summary>Proves a native dependent-table removal affects the requested principal owner's FK blockers.</summary>
    /// <param name="drop">The exact explicit dependent-table deletion.</param>
    /// <param name="source">The source column owner whose incoming blockers are being qualified.</param>
    /// <returns>Known native relevance, or null when either physical owner was not bound.</returns>
    internal bool? IsNativeTableDropRelevant(
        DropTableIntent drop,
        DropColumnIntent source
    )
    {
        if (!_tables.TryGetValue(new TableKey(drop.Schema ?? "dbo", drop.Table), out var dependent)
            || dependent <= 0
            || !_tables.TryGetValue(new TableKey(source.Schema ?? "dbo", source.Table), out var owner)
            || owner <= 0)
        {
            return null;
        }

        foreach (var edge in _foreignKeys.Values)
        {
            if (edge.Dependent == dependent
                && edge.Principal == owner)
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public bool RequiresLiveForeignKeyQualification(
        EnsureForeignKeyIntent intent
    )
    {
        var definition = intent.Definition;

        // WHY: Retain failed captures too. Identical authored CLR/null
        // collation facets cannot override a native endpoint mismatch; wholly
        // authored new owners keep their separate projection contract.
        return _sparseCaptured.Contains(definition)
            && GetTable(definition.Table, definition.Schema) > 0
            && GetTable(definition.PrincipalTable, definition.PrincipalSchema) > 0;
    }

    /// <inheritdoc />
    public IReadOnlyList<SafeMigrationLiveCandidateKey>? GetLiveForeignKeyCandidateKeys(
        EnsureForeignKeyIntent intent,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        var foreign = intent.Definition;
        var dependent = GetTable(foreign.Table, foreign.Schema);
        var principal = GetTable(foreign.PrincipalTable, foreign.PrincipalSchema);
        var createdOwner = dependent < 0
            && _createdTables.ContainsKey(dependent)
            && columns is ISafeMigrationProjectedTableSource tables
            && tables.TryGetProjectedTableState(foreign.Table, foreign.Schema, out var state)
            && state.IsNewlyCreated
            && state is { HasDataMutation: false, HasUnknownStructure: false };

        // WHY: An accepted ordinary new owner can acquire columns after its
        // CREATE. Its current empty lifetime is certified by Core, not by a
        // future authoring proposal or the initially missing catalog name.
        if (_unknown
            || (dependent <= 0 && !createdOwner)
            || principal <= 0
            || _missingTables.Contains(dependent)
            || _missingTables.Contains(principal)
            || !_sparseLiveReady.TryGetValue(foreign, out var proofs))
        {
            return null;
        }

        if (foreign.Columns.Count > 32)
        {
            return null;
        }

        Span<int> principalIds = stackalloc int[foreign.Columns.Count];
        // WHY: Match each FK endpoint and storage proof at the same composite-key position.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < foreign.Columns.Count; ordinal++)
        {
            var id = _columnIds.GetValueOrDefault(new ColumnKey(principal, foreign.PrincipalColumns[ordinal]));
            if (id <= 0
                || _invalidatedStorage.Contains(new ColumnIdentity(principal, id)))
            {
                return null;
            }

            var authored = columns.TryGetProjectedColumn(
                foreign.Table,
                foreign.Schema,
                foreign.Columns[ordinal],
                out var current);

            var compatible = false;
            foreach (var proof in proofs)
            {
                if (proof.Principal == principal
                    && proof.PrincipalColumn == id
                    && proof.OperandOrdinal == ordinal
                    && (authored
                        ? proof.DependentColumn is not null
                        && current is not null
                        && SafeMigrationDefinitionEquivalence.Column(proof.DependentColumn, current)
                        : proof.DependentColumn is null
                        && HasLiveColumn(foreign.Table, foreign.Schema, foreign.Columns[ordinal])))
                {
                    compatible = true;
                    break;
                }
            }

            if (!compatible)
            {
                return null;
            }

            principalIds[ordinal] = id;
        }

        // WHY: Existence-only NoOp and rejected proposals retain the earlier
        // accepted definition. Independent operand certificates match that
        // exact shape without a composite Cartesian product; Core separately
        // proves row safety and each surviving owner, column, and key lifetime.

        return _candidateKeys.GetLiveCandidates(principal, foreign.PrincipalColumns, principalIds);
    }

    private async Task ReadSchemaObjectsAsync(
        DbCommand command,
        IReadOnlyList<SafeMigrationOperation> operations,
        CancellationToken cancellationToken
    )
    {
        var schemas = operations
            .Select(static operation => operation.Intent)
            .OfType<DropSchemaIntent>()
            .Select(intent => GetSchema(intent.Name))
            .Where(static id => id > 0)
            .Distinct();

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

    private static ReferentialAction CatalogAction(
        byte action
    ) => action switch
    {
        0 => ReferentialAction.NoAction,
        1 => ReferentialAction.Cascade,
        2 => ReferentialAction.SetNull,
        3 => ReferentialAction.SetDefault,
        _ => throw new InvalidOperationException("SQL Server returned an unknown FK action."),
    };

    private static string Literal(
        string? value
    ) => value is null
        ? "CONVERT(nvarchar(128), NULL)"
        : SqlServerSqlStringLiteral.Render(value);
}
