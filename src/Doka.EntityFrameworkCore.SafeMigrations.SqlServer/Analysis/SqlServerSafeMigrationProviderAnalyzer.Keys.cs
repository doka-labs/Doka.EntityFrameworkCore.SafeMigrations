namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
    private Dictionary<(string Schema, string Table), SqlServerProjectedKeyTable> _projectedKeyTables
        = new Dictionary<(string Schema, string Table), SqlServerProjectedKeyTable>();

    bool ISafeMigrationProjectedKeyAnalyzer.SharesUniqueConstraintAndIndexIdentity => false;

    SafeMigrationProviderAnalysis ISafeMigrationProjectedKeyAnalyzer.ValidateProjectedIndex(
        EnsureIndexIntent intent,
        ISafeMigrationProjectedColumnSource columns,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis
    )
        => ValidateProjectedKey(intent, intent.Definition.Table, intent.Definition.Schema,
            columns, liveAnalysis, projectedAnalysis);

    SafeMigrationProviderAnalysis ISafeMigrationProjectedKeyAnalyzer.ValidateProjectedPrimaryKey(
        EnsurePrimaryKeyIntent intent,
        ISafeMigrationProjectedColumnSource columns,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis
    )
        => ValidateProjectedKey(intent, intent.Definition.Table, intent.Definition.Schema,
            columns, liveAnalysis, projectedAnalysis);

    SafeMigrationProviderAnalysis ISafeMigrationProjectedKeyAnalyzer.ValidateProjectedUniqueConstraint(
        EnsureUniqueConstraintIntent intent,
        ISafeMigrationProjectedColumnSource columns,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis
    )
        => ValidateProjectedKey(intent, intent.Definition.Table, intent.Definition.Schema,
            columns, liveAnalysis, projectedAnalysis);

    private SafeMigrationProviderAnalysis ValidateProjectedKey(
        SafeMigrationIntent intent,
        string table,
        string? schema,
        ISafeMigrationProjectedColumnSource columns,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis
    )
    {
        _projectedKeyTables.TryGetValue((schema ?? "dbo", table), out var snapshot);

        return SqlServerProjectedKeyShape.Validate(
            intent, columns, liveAnalysis, projectedAnalysis, snapshot, _typeMappingSource,
            ProjectedSeedKeyAnalysis(intent, columns),
            ProjectedIndexFilterAnalysis(intent, columns, snapshot, liveAnalysis),
            intent is EnsureIndexIntent index
                ? SqlServerSafeMigrationCatalogSqlBuilder.GetStructuredIndexFilter(index.Definition) : null);
    }

    /// <summary>Captures requested live key facets once per analysis, in bounded table groups.</summary>
    /// <param name="connection">The already-open analysis connection.</param>
    /// <param name="transaction">The caller's active analysis transaction.</param>
    /// <param name="operations">The immutable operation stream.</param>
    /// <param name="commandTimeout">The active context command timeout.</param>
    /// <param name="cancellationToken">The analysis cancellation token.</param>
    /// <returns>A task completing after every bounded result has been captured.</returns>
    internal async Task ReadProjectedKeySnapshotAsync(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyList<SafeMigrationOperation> operations,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var requested = new Dictionary<(string Schema, string Table), HashSet<string>>();
        var rowsRequired = new HashSet<(string Schema, string Table)>();
        foreach (var operation in operations)
        {
            if (operation.Intent is EnsureIndexIntent index
                && (index.Definition.Keys.Count > 32
                    || index.Definition.Keys.Any(static key => key.Column is null))
                || operation.Intent is EnsurePrimaryKeyIntent primary && primary.Definition.Columns.Count > 32
                || operation.Intent is EnsureUniqueConstraintIntent unique && unique.Definition.Columns.Count > 32)
            {
                // WHY: Statically impossible keys never need row or metadata
                // probes, and cannot inflate the bounded snapshot payload.
                continue;
            }

            var table = operation.Intent switch
            {
                EnsureIndexIntent value => (value.Definition.Schema ?? "dbo", value.Definition.Table),
                EnsurePrimaryKeyIntent value => (value.Definition.Schema ?? "dbo", value.Definition.Table),
                EnsureUniqueConstraintIntent value => (value.Definition.Schema ?? "dbo", value.Definition.Table),
                _ => ((string, string)?)null,
            };

            if (table is null)
            {
                continue;
            }

            if (!requested.TryGetValue(table.Value, out var columns))
            {
                columns = new HashSet<string>(StringComparer.Ordinal);
                requested.Add(table.Value, columns);
            }

            if (operation.Intent is EnsurePrimaryKeyIntent or EnsureUniqueConstraintIntent
                || operation.Intent is EnsureIndexIntent { Definition.Unique: true, })
            {
                rowsRequired.Add(table.Value);
            }

            switch (operation.Intent)
            {
                case EnsureIndexIntent value:
                    foreach (var key in value.Definition.Keys)
                    {
                        if (key.Column is not null)
                        {
                            columns.Add(key.Column);
                        }
                    }

                    columns.UnionWith(value.Definition.IncludedColumns);
                    if (SqlServerSafeMigrationCatalogSqlBuilder.GetStructuredIndexFilter(value.Definition) is { } filter
                        && SqlServerSafeMigrationCatalogSqlBuilder.IsSupportedIndexFilter(filter))
                    {
                        columns.UnionWith(SqlServerSafeMigrationCatalogSqlBuilder.GetIndexFilterComparisons(filter)
                            .Select(static comparison => comparison.Column));
                    }

                    break;
                case EnsurePrimaryKeyIntent value:
                    columns.UnionWith(value.Definition.Columns);
                    break;
                case EnsureUniqueConstraintIntent value:
                    columns.UnionWith(value.Definition.Columns);
                    break;
            }
        }

        var result = new Dictionary<(string Schema, string Table), SqlServerProjectedKeyTable>(requested.Count);
        var tables = requested.Keys.ToArray();
        var columnShapes = new Dictionary<string, SqlServerProjectedKeyColumn>?[tables.Length];

        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection, (tables.Length + SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement - 1)
                / SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement,
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes, commandTimeout,
            (command, slot) =>
            {
                var offset = slot * SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement;
                command.CommandText = BuildProjectedKeySnapshotSql(tables, rowsRequired, offset,
                    Math.Min(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement, tables.Length - offset));

                return new SafeMigrationCatalogProbeStatement(1, 0, offset);
            },
            async (reader, slot, _, token) =>
            {
                var offset = slot * SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement;
                var count = Math.Min(
                    SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement, tables.Length - offset);
                var received = 0;
                while (await reader.ReadAsync(token))
                {
                    var ordinal = reader.GetInt32(0);
                    if ((uint)ordinal >= (uint)count || columnShapes[offset + ordinal] is not null
                        || !StringComparer.Ordinal.Equals(reader.GetString(5), tables[offset + ordinal].Schema)
                        || !StringComparer.Ordinal.Equals(reader.GetString(6), tables[offset + ordinal].Table))
                    {
                        throw new InvalidOperationException(
                            "SQL Server returned an invalid projected key table ordinal.");
                    }

                    var shapes = new Dictionary<string, SqlServerProjectedKeyColumn>(StringComparer.Ordinal);
                    columnShapes[offset + ordinal] = shapes;
                    result.Add(tables[offset + ordinal], new SqlServerProjectedKeyTable(
                        reader.GetInt32(1) == 1, reader.GetInt32(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3), shapes,
                        reader.GetInt32(4) == 1));
                    received++;
                }

                if (received != count)
                {
                    throw new InvalidOperationException("SQL Server omitted a projected key catalog result.");
                }
            },
            cancellationToken, transaction, SqlServerCatalogParameterBindings.MaximumParameters);

        // WHY: Every table header is captured before column facets. Chunk slots
        // transport whole 512-value statements, not individual column operations.
        var bindings = tables.SelectMany((table, ordinal) => requested[table].Select(name => (ordinal, name)));
        using var chunks = bindings.Chunk(SafeMigrationCatalogQueryLimits.MaximumInventoryValues).GetEnumerator();
        var pending = new Dictionary<int, (int Ordinal, string Name)[]>();
        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection,
            (requested.Values.Sum(static names => names.Count)
                + SafeMigrationCatalogQueryLimits.MaximumInventoryValues - 1)
                / SafeMigrationCatalogQueryLimits.MaximumInventoryValues,
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes, commandTimeout,
            (command, slot) =>
            {
                if (!pending.TryGetValue(slot, out var chunk))
                {
                    if (!chunks.MoveNext())
                    {
                        throw new InvalidOperationException("SQL Server projected key column inventory is incomplete.");
                    }

                    chunk = chunks.Current;
                    pending.Add(slot, chunk);
                }

                command.CommandText = BuildProjectedKeyColumnsSql(chunk.Select(binding =>
                {
                    var table = tables[binding.Ordinal];
                    var qualified = _sqlGenerationHelper.DelimitIdentifier(table.Table, table.Schema);

                    return $"({binding.Ordinal}, OBJECT_ID({KeyLiteral(qualified)}, N'U'), {KeyLiteral(binding.Name)})";
                }));

                return new SafeMigrationCatalogProbeStatement(1, 0, slot);
            },
            async (reader, slot, _, token) =>
            {
                var permitted = pending[slot].ToHashSet();
                pending.Remove(slot);

                while (await reader.ReadAsync(token))
                {
                    var ordinal = reader.GetInt32(0);
                    var name = reader.GetString(1);
                    if (!permitted.Remove((ordinal, name)) || columnShapes[ordinal] is not { } shapes)
                    {
                        throw new InvalidOperationException(
                            "SQL Server returned an invalid projected key column ordinal.");
                    }

                    shapes.Add(name, new SqlServerProjectedKeyColumn(
                        reader.GetString(2), reader.GetInt32(3), reader.GetBoolean(4),
                        reader.GetInt32(5) == 1, reader.GetInt32(6) == 1,
                        reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt32(8) == 1));
                }
            },
            cancellationToken, transaction, SqlServerCatalogParameterBindings.MaximumParameters);

        // WHY: DbContext analysis is scoped and cannot run concurrently on one
        // context. Replacing the completed snapshot prevents partially captured
        // metadata from becoming a projection proof after cancellation.
        await ReadProjectedSeedKeyProofsAsync(
            connection, transaction, operations, commandTimeout, cancellationToken);

        _projectedKeyTables = result;
    }

    private string BuildProjectedKeySnapshotSql(
        (string Schema, string Table)[] tables,
        HashSet<(string Schema, string Table)> rowsRequired,
        int offset,
        int count
    )
    {
        var sql = new StringBuilder("DECLARE @doka_key_tables TABLE (ordinal int NOT NULL, "
            + "object_id int NULL, row_count int NOT NULL, primary_key_name nvarchar(128) NULL, "
            + "physical_supported int NOT NULL, schema_name nvarchar(128) NOT NULL, "
            + "table_name nvarchar(128) NOT NULL); "
            + "DECLARE @doka_key_rows int;\n");

        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var table = tables[offset + ordinal];
            var qualified = _sqlGenerationHelper.DelimitIdentifier(table.Table, table.Schema);
            var tableId = $"OBJECT_ID({KeyLiteral(qualified)}, N'U')";
            var physical = _catalogSqlBuilder.BuildPhysicalTableSupportExpression(table.Table, table.Schema);
            if (rowsRequired.Contains(table))
            {
                var rowProbe = "SELECT @count = COUNT(*) FROM "
                    + $"(SELECT TOP (2) 1 AS value FROM {qualified}) [doka_rows];";

                // WHY: Memory-optimized and other unsupported physical tables
                // can reject ordinary transactional reads before classification.
                sql.Append("SET @doka_key_rows = CASE WHEN ").Append(tableId)
                    .Append(" IS NULL THEN 0 ELSE -1 END; IF (").Append(physical).Append(") = 1 AND ").Append(tableId)
                    .Append(" IS NOT NULL EXEC sys.sp_executesql ").Append(KeyLiteral(rowProbe))
                    .Append(", N'@count int OUTPUT', @count = @doka_key_rows OUTPUT;\n");
            }
            else
            {
                // WHY: Ordinary non-unique index validation uses metadata only.
                // An unnecessary SELECT probe would require extra permissions
                // and add data access to an otherwise metadata-only contract.
                sql.Append("SET @doka_key_rows = -1;\n");
            }

            sql.Append("INSERT @doka_key_tables VALUES (").Append(ordinal).Append(", ")
                .Append(tableId).Append(", @doka_key_rows, ")
                .Append("(SELECT name FROM sys.key_constraints WHERE parent_object_id = ")
                .Append(tableId).Append(" AND type = 'PK'), ").Append(physical).Append(", ")
                .Append(KeyLiteral(table.Schema)).Append(", ").Append(KeyLiteral(table.Table)).Append(");\n");

        }

        sql.Append("SELECT ordinal, CASE WHEN object_id IS NULL THEN 0 ELSE 1 END, ")
            .Append("row_count, primary_key_name, physical_supported, schema_name, table_name ")
            .Append("FROM @doka_key_tables ORDER BY ordinal;");

        var text = sql.ToString();
        if (Encoding.UTF8.GetByteCount(text) > SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes)
        {
            throw new NotSupportedException("SQL Server projected key catalog payload exceeds its bounded SQL budget.");
        }

        return text;
    }

    private static string BuildProjectedKeyColumnsSql(
        IEnumerable<string> requested
    ) => "SELECT requested.ordinal, requested.name, ty.name, "
            + "CONVERT(int, c.max_length), c.is_nullable, "
            + "CASE WHEN c.is_computed = 0 AND c.max_length > 0 AND ty.is_user_defined = 0 "
            + "AND ty.name NOT IN (N'xml', N'text', N'ntext', N'image', "
            + "N'sql_variant', N'geography', N'geometry', N'json') "
            + "THEN 1 ELSE 0 END, CASE WHEN c.is_computed = 0 AND ty.is_user_defined = 0 "
            + "AND ty.name NOT IN (N'text', N'ntext', N'image') THEN 1 ELSE 0 END, c.collation_name, "
            + "CASE WHEN c.collation_name = CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), N'Collation')) "
            + "THEN 1 ELSE 0 END FROM (VALUES " + string.Join(", ", requested)
            + ") requested(ordinal, object_id, name) JOIN sys.columns c ON c.object_id = requested.object_id "
            + "AND c.name COLLATE CATALOG_DEFAULT = requested.name COLLATE CATALOG_DEFAULT "
            + "JOIN sys.types ty ON ty.user_type_id = c.user_type_id;";

    private static string KeyLiteral(string value) => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
