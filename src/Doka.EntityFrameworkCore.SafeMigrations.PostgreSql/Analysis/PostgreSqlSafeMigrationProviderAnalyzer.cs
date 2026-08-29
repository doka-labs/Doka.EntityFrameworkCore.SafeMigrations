namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed class PostgreSqlSafeMigrationProviderAnalyzer : ISafeMigrationProviderAnalyzer
{
    // PostgreSQL advisory locks are already local to the current database. A
    // fixed signed bigint therefore avoids coercing the database's unsigned OID
    // into an integer while retaining one package-owned analysis lock domain.
    internal const string AnalysisAdvisoryLockSql = "SELECT pg_catalog.pg_advisory_xact_lock(1397574913::bigint);";

    private readonly IRelationalTypeMappingSource _typeMappingSource;
    private readonly ISqlGenerationHelper _sqlGenerationHelper;

    public PostgreSqlSafeMigrationProviderAnalyzer(
        IRelationalTypeMappingSource typeMappingSource,
        ISqlGenerationHelper sqlGenerationHelper
    )
    {
        ArgumentNullException.ThrowIfNull(typeMappingSource);
        ArgumentNullException.ThrowIfNull(sqlGenerationHelper);

        _typeMappingSource = typeMappingSource;
        _sqlGenerationHelper = sqlGenerationHelper;
    }

    public string ProviderId => "npgsql_postgresql";

    public void ValidateContext(
        DbContext context
    ) => ArgumentNullException.ThrowIfNull(context);

    public async Task<SafeMigrationProviderEnvironment> GetEnvironmentAsync(
        DbContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            return new SafeMigrationProviderEnvironment(ProviderId, "postgresql", connection.ServerVersion);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task<IAsyncDisposable> AcquireAnalysisScopeAsync(
        DbContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        IDbContextTransaction? transaction = null;
        try
        {
            var currentTransaction = context.Database.CurrentTransaction;
            if (currentTransaction is null)
            {
                transaction = await context.Database.BeginTransactionAsync(
                    System.Data.IsolationLevel.RepeatableRead,
                    cancellationToken);
                _ = await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY;", cancellationToken);
            }
            else
            {
                await ValidateCallerOwnedTransactionAsync(currentTransaction, cancellationToken);
            }

            await using var command = context
                .Database
                .GetDbConnection()
                .CreateCommand();

            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = AnalysisAdvisoryLockSql;
            _ = await command.ExecuteScalarAsync(cancellationToken);

            return new AnalysisScope(transaction);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }

            throw;
        }
    }

    private static async Task ValidateCallerOwnedTransactionAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken
    )
    {
        var dbTransaction = transaction.GetDbTransaction();
        if (dbTransaction.IsolationLevel is not (System.Data.IsolationLevel.RepeatableRead
            or System.Data.IsolationLevel.Serializable))
        {
            throw new InvalidOperationException(
                "PostgreSQL SafeMigrations analysis requires a caller-owned transaction "
                + "to use RepeatableRead or Serializable isolation.");
        }

        var connection = dbTransaction.Connection
            ?? throw new InvalidOperationException(
                "The caller-owned PostgreSQL analysis transaction has no active connection.");

        await using var command = connection.CreateCommand();
        command.Transaction = dbTransaction;
        command.CommandText = "SHOW transaction_read_only;";
        var readOnly = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);

        if (!StringComparer.OrdinalIgnoreCase.Equals(readOnly, "on"))
        {
            throw new InvalidOperationException(
                "PostgreSQL SafeMigrations analysis requires a caller-owned transaction to be read-only.");
        }
    }

    public async Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
        DbContext context,
        IReadOnlyList<SafeMigrationOperation> operations,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operations);

        if (operations.Count == 0)
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
            var shortCircuitStates = await FindShortCircuitStatesAsync(connection, operations, cancellationToken);
            var results = new List<SafeMigrationProviderAnalysis>(operations.Count);
            var unsupportedCodes = new string?[operations.Count];
            var ordinal = 0;
            var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
            var trailerBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);
            while (ordinal < operations.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();

                while (ordinal < operations.Count
                       && shortCircuitStates[ordinal] is { } shortCircuitState)
                {
                    results.Add(ShortCircuitAnalysis(shortCircuitState));
                    ordinal++;
                }

                if (ordinal == operations.Count)
                {
                    break;
                }

                await using var command = connection.CreateCommand();
                var parameters = new PostgreSqlCatalogQueryParameters(command);
                var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
                    _typeMappingSource,
                    _sqlGenerationHelper,
                    parameters.AddString);

                var selections = new List<string>(
                    Math.Min(SafeMigrationCatalogQueryLimits.MaximumPostgreSqlOperations, operations.Count - ordinal));

                var sqlBytes = trailerBytes;
                while (ordinal < operations.Count
                       && selections.Count < SafeMigrationCatalogQueryLimits.MaximumPostgreSqlOperations)
                {
                    if (shortCircuitStates[ordinal] is not null)
                    {
                        break;
                    }

                    var operation = operations[ordinal]
                        ?? throw new ArgumentException(
                            "The operation batch cannot contain null entries.",
                            nameof(operations));

                    var checkpoint = parameters.Capture();
                    var plan = builder.Build(operation);
                    unsupportedCodes[ordinal] = plan.UnsupportedCode;
                    var selection = $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, "
                        + $"({plan.StateExpression})::text, "
                        + $"COALESCE(({plan.Postcondition}), FALSE), "
                        + $"COALESCE(({plan.RepairPrecondition}), FALSE)";

                    var selectionBytes = Encoding.UTF8.GetByteCount(selection)
                        + (selections.Count == 0 ? 0 : separatorBytes);

                    var prospectivePayload = sqlBytes + selectionBytes + parameters.Utf8PayloadBytes;
                    if (SafeMigrationCatalogQueryLimits.Exceeded(
                            parameters.Count,
                            prospectivePayload,
                            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes))
                    {
                        var prospectiveParameters = parameters.Count;
                        parameters.Rollback(checkpoint);
                        if (selections.Count == 0)
                        {
                            throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                                ordinal,
                                prospectiveParameters,
                                prospectivePayload);
                        }

                        break;
                    }

                    selections.Add(selection);
                    sqlBytes += selectionBytes;
                    ordinal++;
                }

                command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
                    + SafeMigrationCatalogQueryLimits.Trailer;
                await ReadAnalysisAsync(command, results, unsupportedCodes, cancellationToken);
            }

            if (results.Count != operations.Count)
            {
                throw new InvalidOperationException(
                    "The PostgreSQL SafeMigrations classifier returned an inconsistent row count.");
            }

            return results.AsReadOnly();
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task<SafeMigrationObservedState?[]> FindShortCircuitStatesAsync(
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        CancellationToken cancellationToken
    )
    {
        var states = new SafeMigrationObservedState?[operations.Count];
        var rowsRead = 0;
        var ordinal = 0;
        var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
        var trailerBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);
        while (ordinal < operations.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var command = connection.CreateCommand();
            var parameters = new PostgreSqlCatalogQueryParameters(command);
            var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
                _typeMappingSource,
                _sqlGenerationHelper,
                parameters.AddString);

            var selections = new List<string>(
                Math.Min(SafeMigrationCatalogQueryLimits.MaximumPostgreSqlOperations, operations.Count - ordinal));
            var sqlBytes = trailerBytes;
            while (ordinal < operations.Count
                   && selections.Count < SafeMigrationCatalogQueryLimits.MaximumPostgreSqlOperations)
            {
                var operation = operations[ordinal]
                    ?? throw new ArgumentException(
                        "The operation batch cannot contain null entries.",
                        nameof(operations));

                var checkpoint = parameters.Capture();
                var plan = builder.Build(operation);
                var stateEvaluationGuardBranch = plan.StateEvaluationGuardFailureExpression is null
                    ? string.Empty
                    : $"WHEN NOT COALESCE(({plan.StateEvaluationGuardExpression}), FALSE) THEN "
                    + $"({plan.StateEvaluationGuardFailureExpression}) ";

                var selection = $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, CASE "
                    + $"WHEN NOT COALESCE(({plan.PrerequisiteExpression}), FALSE) "
                    + "THEN 'prerequisite_missing' "
                    + stateEvaluationGuardBranch
                    + "ELSE NULL END";

                var selectionBytes = Encoding.UTF8.GetByteCount(selection)
                    + (selections.Count == 0 ? 0 : separatorBytes);

                var prospectivePayload = sqlBytes + selectionBytes + parameters.Utf8PayloadBytes;
                if (SafeMigrationCatalogQueryLimits.Exceeded(
                        parameters.Count,
                        prospectivePayload,
                        SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes))
                {
                    var prospectiveParameters = parameters.Count;
                    parameters.Rollback(checkpoint);
                    if (selections.Count == 0)
                    {
                        throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                            ordinal,
                            prospectiveParameters,
                            prospectivePayload);
                    }

                    break;
                }

                selections.Add(selection);
                sqlBytes += selectionBytes;
                ordinal++;
            }

            command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
                + SafeMigrationCatalogQueryLimits.Trailer;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var resultOrdinal = reader.GetInt32(0);
                if (resultOrdinal != rowsRead)
                {
                    throw new InvalidOperationException(
                        "The PostgreSQL SafeMigrations prerequisite classifier returned an invalid ordinal.");
                }

                states[resultOrdinal] = reader.IsDBNull(1)
                    ? null
                    : ParseState(reader.GetString(1));
                rowsRead++;
            }
        }

        if (rowsRead != operations.Count)
        {
            throw new InvalidOperationException(
                "The PostgreSQL SafeMigrations prerequisite classifier returned an inconsistent row count.");
        }

        return states;
    }

    private static SafeMigrationProviderAnalysis ShortCircuitAnalysis(
        SafeMigrationObservedState state
    ) => new(
        state,
        SafeMigrationRepairCapability.None,
        false,
        $"classified_{StateCode(state)}");

    private static async Task ReadAnalysisAsync(
        DbCommand command,
        List<SafeMigrationProviderAnalysis> results,
        string?[] unsupportedCodes,
        CancellationToken cancellationToken
    )
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var ordinal = reader.GetInt32(0);
            if (ordinal != results.Count)
            {
                throw new InvalidOperationException(
                    "The PostgreSQL SafeMigrations classifier returned an invalid ordinal.");
            }

            var state = ParseState(reader.GetString(1));
            var repairCapability = reader.GetBoolean(3)
                ? SafeMigrationRepairCapability.Safe
                : SafeMigrationRepairCapability.None;

            var code = state == SafeMigrationObservedState.Unsupported
                ? unsupportedCodes[ordinal] ?? "classified_unsupported"
                : $"classified_{StateCode(state)}";

            results.Add(new SafeMigrationProviderAnalysis(state, repairCapability, reader.GetBoolean(2), code));
        }
    }

    public async Task<IReadOnlyList<SafeMigrationUnexpectedObject>> FindUnexpectedObjectsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operations);

        var expected = SafeMigrationExpectedCatalog.Create(operations);
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
            var findings = new List<SafeMigrationUnexpectedObject>();
            var lookup = new ExpectedTableLookup(expected);
            var seen = new HashSet<(SafeMigrationDatabaseObjectKind Kind, string Schema, string Table, string Name)>();

            var schemaScopes = BuildSchemaScopeBatches(expected);
            foreach (var schemaBatch in schemaScopes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await using var command = connection.CreateCommand();
                var parameters = new PostgreSqlCatalogQueryParameters(command);
                var schemaScope = BuildSchemaScope(schemaBatch, parameters);
                command.CommandText = BuildUnexpectedTableSql(schemaScope);
                await ReadUnexpectedAsync(command, lookup, findings, seen, cancellationToken);
            }

            foreach (var tableBatch in expected.Chunk(SafeMigrationCatalogQueryLimits.MaximumInventoryValues))
            {
                cancellationToken.ThrowIfCancellationRequested();

                await using var command = connection.CreateCommand();
                var parameters = new PostgreSqlCatalogQueryParameters(command);
                var childScope = BuildExpectedTableScope(tableBatch, parameters, "n.nspname", "c.relname");
                var indexScope = BuildExpectedTableScope(tableBatch, parameters, "n.nspname", "tbl.relname");
                command.CommandText = BuildUnexpectedChildObjectSql(childScope, indexScope);
                await ReadUnexpectedAsync(command, lookup, findings, seen, cancellationToken);
            }

            return findings.AsReadOnly();
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task ReadUnexpectedAsync(
        DbCommand command,
        ExpectedTableLookup lookup,
        List<SafeMigrationUnexpectedObject> findings,
        HashSet<(SafeMigrationDatabaseObjectKind Kind, string Schema, string Table, string Name)> seen,
        CancellationToken cancellationToken
    )
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // Unexpected objects are evidence only. They are never folded into
        // the expected catalog and never authorize destructive cleanup.
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = ParseObjectKind(reader.GetString(0));
            var schema = reader.GetString(1);
            var tableName = reader.GetString(2);
            var objectName = reader.GetString(3);
            if (!seen.Add((kind, schema, tableName, objectName)))
            {
                continue;
            }

            var currentSchema = reader.GetString(4);
            var table = lookup.Find(schema, tableName, currentSchema);
            if (table is null)
            {
                if (kind == SafeMigrationDatabaseObjectKind.Table)
                {
                    findings.Add(Unexpected(kind, schema, table: null, objectName));
                }

                continue;
            }

            if (kind == SafeMigrationDatabaseObjectKind.Table
                || IsExpected(table, kind, objectName))
            {
                continue;
            }

            findings.Add(Unexpected(kind, schema, tableName, objectName));
        }
    }

    private static SafeMigrationExpectedTableInventory[][] BuildSchemaScopeBatches(
        IReadOnlyList<SafeMigrationExpectedTableInventory> expected
    )
    {
        var representatives = new List<SafeMigrationExpectedTableInventory>();
        if (expected.FirstOrDefault(static table => table.Schema is null) is { } providerDefault)
        {
            representatives.Add(providerDefault);
        }

        representatives.AddRange(
            expected
                .Where(static table => table.Schema is not null)
                .DistinctBy(static table => table.Schema, StringComparer.Ordinal));

        return representatives
            .Chunk(SafeMigrationCatalogQueryLimits.MaximumInventoryValues)
            .ToArray();
    }

    private static SafeMigrationObservedState ParseState(
        string state
    ) => state switch
    {
        "missing" => SafeMigrationObservedState.Missing,
        "matching" => SafeMigrationObservedState.Matching,
        "different" => SafeMigrationObservedState.Different,
        "unsupported" => SafeMigrationObservedState.Unsupported,
        "data_blocked" => SafeMigrationObservedState.DataBlocked,
        "prerequisite_missing" => SafeMigrationObservedState.PrerequisiteMissing,
        _ => throw new InvalidOperationException("The PostgreSQL SafeMigrations classifier returned an unknown state."),
    };

    private static string StateCode(
        SafeMigrationObservedState state
    ) => state switch
    {
        SafeMigrationObservedState.Missing => "missing",
        SafeMigrationObservedState.Matching => "matching",
        SafeMigrationObservedState.Different => "different",
        SafeMigrationObservedState.Unsupported => "unsupported",
        SafeMigrationObservedState.DataBlocked => "data_blocked",
        SafeMigrationObservedState.PrerequisiteMissing => "prerequisite_missing",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static string BuildSchemaScope(
        IReadOnlyList<SafeMigrationExpectedTableInventory> expected,
        PostgreSqlCatalogQueryParameters parameters
    )
    {
        var conditions = new List<string>();
        if (expected.Any(static table => table.Schema is null))
        {
            conditions.Add("n.nspname = current_schema()");
        }

        conditions.AddRange(
            expected
                .Select(static table => table.Schema)
                .Where(static schema => schema is not null)
                .Distinct(StringComparer.Ordinal)
                .Select(schema => $"n.nspname = {parameters.AddString(schema!)}"));

        return $"({string.Join(" OR ", conditions)})";
    }

    private static string BuildExpectedTableScope(
        IReadOnlyList<SafeMigrationExpectedTableInventory> expected,
        PostgreSqlCatalogQueryParameters parameters,
        string schemaExpression,
        string tableExpression
    )
    {
        var conditions = expected
            .Select(table => "("
                + (table.Schema is null
                    ? $"{schemaExpression} = current_schema()"
                    : $"{schemaExpression} = {parameters.AddString(table.Schema)}")
                + $" AND {tableExpression} = {parameters.AddString(table.Table)})")
            .ToArray();

        return $"({string.Join(" OR ", conditions)})";
    }

    private static bool IsExpected(
        SafeMigrationExpectedTableInventory table,
        SafeMigrationDatabaseObjectKind kind,
        string name
    ) => kind switch
    {
        SafeMigrationDatabaseObjectKind.Column => table.Columns.Contains(name),
        SafeMigrationDatabaseObjectKind.Index => table.Indexes.Contains(name),
        _ => table.Constraints.TryGetValue(name, out var expectedKind) && expectedKind == kind
    };

    private static SafeMigrationUnexpectedObject Unexpected(
        SafeMigrationDatabaseObjectKind kind,
        string schema,
        string? table,
        string name
    ) => new(kind, schema, table, name, $"unexpected_{ObjectCode(kind)}");

    private static SafeMigrationDatabaseObjectKind ParseObjectKind(
        string value
    ) => value switch
    {
        "table" => SafeMigrationDatabaseObjectKind.Table,
        "column" => SafeMigrationDatabaseObjectKind.Column,
        "index" => SafeMigrationDatabaseObjectKind.Index,
        "primary_key" => SafeMigrationDatabaseObjectKind.PrimaryKey,
        "unique_constraint" => SafeMigrationDatabaseObjectKind.UniqueConstraint,
        "check_constraint" => SafeMigrationDatabaseObjectKind.CheckConstraint,
        "foreign_key" => SafeMigrationDatabaseObjectKind.ForeignKey,
        _ => throw new InvalidOperationException(
            "The PostgreSQL unexpected-object inventory returned an unknown object kind."),
    };

    private static string ObjectCode(
        SafeMigrationDatabaseObjectKind kind
    ) => kind switch
    {
        SafeMigrationDatabaseObjectKind.Table => "table",
        SafeMigrationDatabaseObjectKind.Column => "column",
        SafeMigrationDatabaseObjectKind.Index => "index",
        SafeMigrationDatabaseObjectKind.PrimaryKey => "primary_key",
        SafeMigrationDatabaseObjectKind.UniqueConstraint => "unique_constraint",
        SafeMigrationDatabaseObjectKind.CheckConstraint => "check_constraint",
        SafeMigrationDatabaseObjectKind.ForeignKey => "foreign_key",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string BuildUnexpectedTableSql(
        string schemaScope
    ) => $"""
          SELECT 'table', n.nspname, c.relname, c.relname, current_schema()
          FROM pg_catalog.pg_class c
          JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
          WHERE {schemaScope} AND c.relkind IN ('r', 'p')
          ORDER BY 1, 2, 3, 4;
          """;

    private static string BuildUnexpectedChildObjectSql(
        string childScope,
        string indexScope
    ) => $"""
          SELECT 'column', n.nspname, c.relname, a.attname, current_schema()
          FROM pg_catalog.pg_attribute a
          JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
          JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
          WHERE {childScope} AND c.relkind IN ('r', 'p')
            AND a.attnum > 0 AND NOT a.attisdropped
          UNION ALL
          SELECT CASE co.contype
              WHEN 'p' THEN 'primary_key'
              WHEN 'u' THEN 'unique_constraint'
              WHEN 'c' THEN 'check_constraint'
              WHEN 'f' THEN 'foreign_key'
              END,
              n.nspname,
              c.relname,
              co.conname,
              current_schema()
          FROM pg_catalog.pg_constraint co
          JOIN pg_catalog.pg_class c ON c.oid = co.conrelid
          JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
          WHERE {childScope} AND co.contype IN ('p', 'u', 'c', 'f')
          UNION ALL
          SELECT 'index', n.nspname, tbl.relname, idx.relname, current_schema()
          FROM pg_catalog.pg_index i
          JOIN pg_catalog.pg_class idx ON idx.oid = i.indexrelid
          JOIN pg_catalog.pg_class tbl ON tbl.oid = i.indrelid
          JOIN pg_catalog.pg_namespace n ON n.oid = tbl.relnamespace
          WHERE {indexScope}
            AND NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_constraint co WHERE co.conindid = idx.oid)
          ORDER BY 1, 2, 3, 4;
          """;

    private sealed class ExpectedTableLookup
    {
        private readonly Dictionary<(string Schema, string Table), SafeMigrationExpectedTableInventory> _explicit;
        private readonly Dictionary<string, SafeMigrationExpectedTableInventory> _providerDefault;

        public ExpectedTableLookup(
            IReadOnlyList<SafeMigrationExpectedTableInventory> expected
        )
        {
            _explicit = expected
                .Where(static table => table.Schema is not null)
                .ToDictionary(static table => (table.Schema!, table.Table));

            _providerDefault = expected
                .Where(static table => table.Schema is null)
                .ToDictionary(static table => table.Table, StringComparer.Ordinal);
        }

        public SafeMigrationExpectedTableInventory? Find(
            string schema,
            string table,
            string currentSchema
        )
        {
            if (_explicit.TryGetValue((schema, table), out var exact))
            {
                return exact;
            }

            return StringComparer.Ordinal.Equals(schema, currentSchema)
                && _providerDefault.TryGetValue(table, out var providerDefault)
                    ? providerDefault
                    : null;
        }
    }

    private sealed class AnalysisScope : IAsyncDisposable
    {
        private IDbContextTransaction? _transaction;

        public AnalysisScope(
            IDbContextTransaction? transaction
        )
        {
            _transaction = transaction;
        }

        public async ValueTask DisposeAsync()
        {
            if (_transaction is not null)
            {
                await _transaction.DisposeAsync();
                _transaction = null;
            }
        }
    }
}
