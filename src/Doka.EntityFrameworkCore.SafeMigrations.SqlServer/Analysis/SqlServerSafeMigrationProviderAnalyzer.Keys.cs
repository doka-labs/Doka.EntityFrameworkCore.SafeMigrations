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
    private async Task ReadProjectedKeySnapshotAsync(
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
        for (var offset = 0; offset < tables.Length; offset += 32)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = Math.Min(32, tables.Length - offset);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            ApplyCommandTimeout(command, commandTimeout);
            command.CommandText = BuildProjectedKeySnapshotSql(tables, rowsRequired, offset, count);

            var columnShapes = new Dictionary<string, SqlServerProjectedKeyColumn>?[count];
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var ordinal = reader.GetInt32(0);
                    if ((uint)ordinal >= (uint)count || columnShapes[ordinal] is not null)
                    {
                        throw new InvalidOperationException(
                            "SQL Server returned an invalid projected key table ordinal.");
                    }

                    var shapes = new Dictionary<string, SqlServerProjectedKeyColumn>(StringComparer.Ordinal);
                    columnShapes[ordinal] = shapes;
                    result.Add(tables[offset + ordinal], new SqlServerProjectedKeyTable(
                        reader.GetInt32(1) == 1, reader.GetInt32(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3), shapes,
                        reader.GetInt32(4) == 1));
                }
            }

            if (columnShapes.Any(static shapes => shapes is null))
            {
                throw new InvalidOperationException("SQL Server omitted a projected key catalog result.");
            }

            var requestedColumns = new List<string>(SafeMigrationCatalogQueryLimits.MaximumInventoryValues);
            for (var ordinal = 0; ordinal < count; ordinal++)
            {
                var table = tables[offset + ordinal];
                var qualified = _sqlGenerationHelper.DelimitIdentifier(table.Table, table.Schema);
                var tableId = $"OBJECT_ID({KeyLiteral(qualified)}, N'U')";
                foreach (var column in requested[table])
                {
                    requestedColumns.Add($"({ordinal}, {tableId}, {KeyLiteral(column)})");
                    if (requestedColumns.Count == SafeMigrationCatalogQueryLimits.MaximumInventoryValues)
                    {
                        await ReadProjectedKeyColumnsAsync(
                            connection, transaction, commandTimeout, requestedColumns, columnShapes, cancellationToken);
                        requestedColumns.Clear();
                    }
                }
            }

            if (requestedColumns.Count != 0)
            {
                await ReadProjectedKeyColumnsAsync(
                    connection, transaction, commandTimeout, requestedColumns, columnShapes, cancellationToken);
            }
        }

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
            + "physical_supported int NOT NULL); "
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
                .Append(tableId).Append(" AND type = 'PK'), ").Append(physical).Append(");\n");

        }

        sql.Append("SELECT ordinal, CASE WHEN object_id IS NULL THEN 0 ELSE 1 END, ")
            .Append("row_count, primary_key_name, physical_supported ")
            .Append("FROM @doka_key_tables ORDER BY ordinal;");

        var text = sql.ToString();
        if (Encoding.UTF8.GetByteCount(text) > SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes)
        {
            throw new NotSupportedException("SQL Server projected key catalog payload exceeds its bounded SQL budget.");
        }

        return text;
    }

    private static async Task ReadProjectedKeyColumnsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        int? commandTimeout,
        List<string> requested,
        Dictionary<string, SqlServerProjectedKeyColumn>?[] columns,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        ApplyCommandTimeout(command, commandTimeout);
        command.CommandText = "SELECT requested.ordinal, requested.name, ty.name, "
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

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var ordinal = reader.GetInt32(0);
            if ((uint)ordinal >= (uint)columns.Length || columns[ordinal] is not { } shapes)
            {
                throw new InvalidOperationException("SQL Server returned an invalid projected key column ordinal.");
            }

            shapes.Add(reader.GetString(1), new SqlServerProjectedKeyColumn(
                reader.GetString(2), reader.GetInt32(3), reader.GetBoolean(4),
                reader.GetInt32(5) == 1, reader.GetInt32(6) == 1,
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt32(8) == 1));
        }
    }

    private static string KeyLiteral(string value) => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
