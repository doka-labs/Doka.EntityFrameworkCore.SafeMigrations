namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<SafeMigrationUnexpectedObject>> FindUnexpectedObjectsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        CancellationToken cancellationToken = default
    )
    {
        ValidateContext(context);
        ArgumentNullException.ThrowIfNull(operations);

        var expected = SafeMigrationExpectedCatalog.Create(operations, static schema => schema ?? "dbo");
        if (expected.Count == 0)
        {
            return [];
        }

        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            var currentSession = HasCurrentAnalysisSession(context, connection, transaction);
            var preamble = currentSession ? _catalogPreamble : null;

            // WHY: EXECUTE AS, permission changes and database collation changes can occur
            // without replacing the connection or transaction. Recheck this cheap stamp before
            // reusing either an identifier verdict or a previous invariant rejection.
            var environment = await ReadCatalogEnvironmentAsync(
                connection,
                transaction,
                context.Database.GetCommandTimeout(),
                cancellationToken);

            if (preamble is not null
                && (!environment.HasReusableIdentity || preamble.Environment != environment))
            {
                ClearCatalogPreamble();
                preamble = null;
            }

            if (!environment.CanSeeDatabaseMetadata)
            {
                if (currentSession && ReferenceEquals(context, _catalogInventoryRejectedContext))
                {
                    // WHY: Preserve the preceding invariant Unsupported report.
                    // This empty optional inventory is not evidence of absence;
                    // direct inventory requests without that rejection still fail.
                    return [];
                }

                throw new InvalidOperationException(
                    "SQL Server catalog inventory requires database metadata visibility.");
            }

            var safeOperations = operations
                .OfType<SafeMigrationOperation>()
                .ToArray();

            var references = SqlServerIdentifierContract.Collect(safeOperations);
            var identifierSafe = preamble is not null && preamble.Covers(context, references, environment)
                ? preamble.IdentifierSafe
                : await ReadIdentifierContractAsync(
                    connection,
                    transaction,
                    references,
                    context.Database.GetCommandTimeout(),
                    cancellationToken);

            if ((!environment.DefaultSchemaIsDbo
                    && safeOperations.Any(static operation => RequiresDefaultSchema(operation.Intent)))
                || !identifierSafe)
            {
                if (currentSession && ReferenceEquals(context, _catalogInventoryRejectedContext))
                {
                    return [];
                }

                throw new InvalidOperationException("SQL Server catalog inventory could not prove object identity.");
            }

            return await ReadUnexpectedObjectsAsync(
                connection,
                transaction,
                expected,
                context.Database.GetCommandTimeout(),
                cancellationToken);
        }
        catch
        {
            ClearCatalogPreamble();

            throw;
        }
        finally
        {
            if (openedHere && !_connectionQuarantined)
            {
                ClearCatalogPreamble();
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>Loads an invocation-owned inventory scope before batching its final immutable reads.</summary>
    /// <param name="connection">The borrowed open analysis connection.</param>
    /// <param name="transaction">The active caller transaction.</param>
    /// <param name="expected">The invocation's expected table inventory.</param>
    /// <param name="commandTimeout">The caller's command timeout.</param>
    /// <param name="cancellationToken">The token that cancels scope loading and reading.</param>
    /// <returns>The complete unexpected-object evidence after owned scope cleanup.</returns>
    internal async Task<IReadOnlyList<SafeMigrationUnexpectedObject>> ReadUnexpectedObjectsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyList<SafeMigrationExpectedTableInventory> expected,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var findings = new List<SafeMigrationUnexpectedObject>();
        var temporaryTable = "#doka_sm_inventory_" + Guid.NewGuid().ToString("N");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        ApplyCommandTimeout(command, commandTimeout);
        command.CommandText = $"SELECT OBJECT_ID(N'tempdb..{temporaryTable}');";
        var occupied = await command.ExecuteScalarAsync(cancellationToken);
        if (occupied is not null and not DBNull)
        {
            throw new InvalidOperationException("SQL Server inventory temporary scope is occupied.");
        }

        var createStatement = $"IF OBJECT_ID(N'tempdb..{temporaryTable}') IS NOT NULL "
            + "THROW 51002, N'doka_sm_unsupported', 1; "
            + $"CREATE TABLE {temporaryTable} (kind int NOT NULL, "
            + "schema_name nvarchar(128) COLLATE CATALOG_DEFAULT NOT NULL, "
            + "table_name nvarchar(128) COLLATE CATALOG_DEFAULT NOT NULL, "
            + "name nvarchar(128) COLLATE CATALOG_DEFAULT NOT NULL);";

        Exception? originalFailure = null;
        try
        {
            command.CommandText = createStatement;
            await command.ExecuteNonQueryAsync(cancellationToken);

            // WHY: Runtime classification uses catalog collation for physical
            // names. The inventory must not turn a proven spelling alias into
            // an unexpected object through ordinal client-side comparisons.
            foreach (var rows in InventoryRows(expected)
                         .Chunk(SafeMigrationCatalogQueryLimits.MaximumInventoryValues))
            {
                cancellationToken.ThrowIfCancellationRequested();

                command.Parameters.Clear();
                var values = new string[rows.Length];
                var valueBytes = 0;
                // WHY: Use the same row ordinal for parameter names and their VALUES entry.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var ordinal = 0; ordinal < rows.Length; ordinal++)
                {
                    var row = rows[ordinal];
                    var suffix = ordinal.ToString(CultureInfo.InvariantCulture);
                    var schemaParameter = "@schema" + suffix;
                    var tableParameter = "@table" + suffix;
                    var nameParameter = "@name" + suffix;
                    AddInventoryParameter(command, schemaParameter, row.Schema);
                    AddInventoryParameter(command, tableParameter, row.Table);
                    AddInventoryParameter(command, nameParameter, row.Name);
                    values[ordinal] =
                        "("
                        + row.Kind.ToString(CultureInfo.InvariantCulture)
                        + ", "
                        + schemaParameter
                        + ", "
                        + tableParameter
                        + ", "
                        + nameParameter
                        + ")";
                    valueBytes += Encoding.UTF8.GetByteCount(row.Schema)
                        + Encoding.UTF8.GetByteCount(row.Table)
                        + Encoding.UTF8.GetByteCount(row.Name);
                }

                command.CommandText = $"INSERT INTO {temporaryTable} VALUES " + string.Join(", ", values) + ";";
                var bytes = Encoding.UTF8.GetByteCount(command.CommandText) + valueBytes;
                if (bytes > SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes)
                {
                    throw SafeMigrationCatalogQueryLimits.OversizedOperation(0, command.Parameters.Count, bytes);
                }

                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            command.Parameters.Clear();
            var tableInventorySql = "SELECT 0, s.name, t.name FROM sys.tables t "
                + "JOIN sys.schemas s ON s.schema_id = t.schema_id "
                + $"WHERE EXISTS (SELECT 1 FROM {temporaryTable} e WHERE e.schema_name = s.name) "
                + $"AND NOT EXISTS (SELECT 1 FROM {temporaryTable} e WHERE e.kind = 0 "
                + "AND e.schema_name = s.name AND e.table_name = t.name) ORDER BY s.name, t.name;";

            var childInventorySql = "WITH expected(schema_name, table_name) AS ("
                + $"SELECT schema_name, table_name FROM {temporaryTable} WHERE kind = 0), "
                + "physical(kind, schema_name, table_name, name) AS ("
                + "SELECT 1, s.name, t.name, c.name FROM expected e "
                + "JOIN sys.schemas s ON s.name = e.schema_name "
                + "JOIN sys.tables t ON t.schema_id = s.schema_id AND t.name = e.table_name "
                + "JOIN sys.columns c ON c.object_id = t.object_id "
                + "UNION ALL SELECT 2, s.name, t.name, i.name FROM expected e "
                + "JOIN sys.schemas s ON s.name = e.schema_name "
                + "JOIN sys.tables t ON t.schema_id = s.schema_id AND t.name = e.table_name "
                + "JOIN sys.indexes i ON i.object_id = t.object_id "
                + "WHERE i.name IS NOT NULL AND i.is_primary_key = 0 AND i.is_unique_constraint = 0 "
                + "UNION ALL SELECT CASE kc.type WHEN 'PK' THEN 3 ELSE 4 END, s.name, t.name, kc.name "
                + "FROM expected e JOIN sys.schemas s ON s.name = e.schema_name "
                + "JOIN sys.tables t ON t.schema_id = s.schema_id AND t.name = e.table_name "
                + "JOIN sys.key_constraints kc ON kc.parent_object_id = t.object_id "
                + "UNION ALL SELECT 5, s.name, t.name, cc.name FROM expected e "
                + "JOIN sys.schemas s ON s.name = e.schema_name "
                + "JOIN sys.tables t ON t.schema_id = s.schema_id AND t.name = e.table_name "
                + "JOIN sys.check_constraints cc ON cc.parent_object_id = t.object_id "
                + "UNION ALL SELECT 6, s.name, t.name, fk.name FROM expected e "
                + "JOIN sys.schemas s ON s.name = e.schema_name "
                + "JOIN sys.tables t ON t.schema_id = s.schema_id AND t.name = e.table_name "
                + "JOIN sys.foreign_keys fk ON fk.parent_object_id = t.object_id) "
                + "SELECT 1, p.kind, p.schema_name, p.table_name, p.name FROM physical p "
                + $"WHERE NOT EXISTS (SELECT 1 FROM {temporaryTable} e WHERE e.kind = p.kind "
                + "AND e.schema_name = p.schema_name AND e.table_name = p.table_name AND e.name = p.name) "
                + "ORDER BY p.kind, p.schema_name, p.table_name, p.name;";

            // WHY: Loading the owned scope is a prerequisite. Only its two
            // immutable final reads share a transport, before owned cleanup.
            await SafeMigrationCatalogProbeBatch.ReadAsync(
                connection,
                2,
                SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes,
                commandTimeout,
                (statement, slot) =>
                {
                    statement.CommandText = slot == 0 ? tableInventorySql : childInventorySql;

                    return new SafeMigrationCatalogProbeStatement(1, 0, slot);
                },
                async (reader, slot, _, token) =>
                {
                    if (reader.FieldCount != (slot == 0 ? 3 : 5))
                    {
                        throw new InvalidOperationException("SQL Server returned an unowned inventory result set.");
                    }

                    while (await reader.ReadAsync(token))
                    {
                        if (reader.GetInt32(0) != slot)
                        {
                            throw new InvalidOperationException("SQL Server returned an unowned inventory object.");
                        }

                        if (slot == 0)
                        {
                            findings.Add(
                                new SafeMigrationUnexpectedObject(
                                    SafeMigrationDatabaseObjectKind.Table,
                                    reader.GetString(1),
                                    table: null,
                                    reader.GetString(2),
                                    "unexpected_table"));

                            continue;
                        }

                        var kind = (SafeMigrationDatabaseObjectKind)reader.GetInt32(1);
                        findings.Add(
                            new SafeMigrationUnexpectedObject(
                                kind,
                                reader.GetString(2),
                                reader.GetString(3),
                                reader.GetString(4),
                                kind switch
                                {
                                    SafeMigrationDatabaseObjectKind.Column => "unexpected_column",
                                    SafeMigrationDatabaseObjectKind.Index => "unexpected_index",
                                    SafeMigrationDatabaseObjectKind.PrimaryKey => "unexpected_primary_key",
                                    SafeMigrationDatabaseObjectKind.UniqueConstraint => "unexpected_unique_constraint",
                                    SafeMigrationDatabaseObjectKind.CheckConstraint => "unexpected_check_constraint",
                                    SafeMigrationDatabaseObjectKind.ForeignKey => "unexpected_foreign_key",
                                    _ => throw new UnreachableException(),
                                }));
                    }
                },
                cancellationToken,
                transaction,
                SqlServerCatalogParameterBindings.MaximumParameters);

            return findings.AsReadOnly();
        }
        catch (Exception exception)
        {
            originalFailure = exception;

            throw;
        }
        finally
        {
            // WHY: Cancellation must not leave invocation-owned temporary
            // state attached to an open caller connection.
            await CleanupTemporaryScopeAsync(connection, transaction, temporaryTable, commandTimeout, originalFailure);
        }
    }

    private static IEnumerable<(int Kind, string Schema, string Table, string Name)> InventoryRows(
        IReadOnlyList<SafeMigrationExpectedTableInventory> expected
    )
    {
        foreach (var table in expected)
        {
            var schema = table.Schema ?? "dbo";
            yield return (0, schema, table.Table, table.Table);
            foreach (var column in table.Columns)
            {
                yield return (1, schema, table.Table, column);
            }

            foreach (var index in table.Indexes)
            {
                yield return (2, schema, table.Table, index);
            }

            foreach (var constraint in table.Constraints)
            {
                yield return ((int)constraint.Value, schema, table.Table, constraint.Key);
            }
        }
    }

    private static void AddInventoryParameter(
        DbCommand command,
        string name,
        string value
    )
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = System.Data.DbType.String;
        parameter.Size = 128;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
