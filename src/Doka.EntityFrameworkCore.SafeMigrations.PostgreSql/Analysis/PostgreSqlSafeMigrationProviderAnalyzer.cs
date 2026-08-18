namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed class PostgreSqlSafeMigrationProviderAnalyzer : ISafeMigrationProviderAnalyzer
{
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
            await using var command = connection.CreateCommand();
            var parameters = new PostgreSqlCatalogQueryParameters(command);
            var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
                _typeMappingSource,
                _sqlGenerationHelper,
                parameters.AddString);

            var selections = new List<string>(operations.Count);
            for (var ordinal = 0; ordinal < operations.Count; ordinal++)
            {
                var operation = operations[ordinal]
                    ?? throw new ArgumentException(
                        "The operation batch cannot contain null entries.",
                        nameof(operations));

                var plan = builder.Build(operation);
                selections.Add(
                    $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, "
                    + $"({plan.StateExpression})::text, "
                    + $"COALESCE(({plan.Postcondition}), FALSE), "
                    + $"COALESCE(({plan.RepairPrecondition}), FALSE)");
            }

            command.CommandText = string.Join("\nUNION ALL\n", selections) + "\nORDER BY 1;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var results = new List<SafeMigrationProviderAnalysis>(operations.Count);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetInt32(0) != results.Count)
                {
                    throw new InvalidOperationException(
                        "The PostgreSQL SafeMigrations classifier returned an invalid ordinal.");
                }

                var state = ParseState(reader.GetString(1));
                var repairCapability = reader.GetBoolean(3)
                    ? SafeMigrationRepairCapability.Safe
                    : SafeMigrationRepairCapability.None;

                results.Add(
                    new SafeMigrationProviderAnalysis(
                        state,
                        repairCapability,
                        reader.GetBoolean(2),
                        $"classified_{StateCode(state)}"));
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
            await using var command = connection.CreateCommand();
            var parameters = new PostgreSqlCatalogQueryParameters(command);
            var scope = BuildSchemaScope(expected, parameters);
            command.CommandText = BuildUnexpectedObjectSql(scope);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var findings = new List<SafeMigrationUnexpectedObject>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var kind = ParseObjectKind(reader.GetString(0));
                var schema = reader.GetString(1);
                var tableName = reader.GetString(2);
                var objectName = reader.GetString(3);
                var currentSchema = reader.GetString(4);
                var table = FindExpected(expected, schema, tableName, currentSchema);
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

    private static SafeMigrationObservedState ParseState(
        string state
    ) => state switch
    {
        "missing" => SafeMigrationObservedState.Missing,
        "matching" => SafeMigrationObservedState.Matching,
        "different" => SafeMigrationObservedState.Different,
        "unsupported" => SafeMigrationObservedState.Unsupported,
        "data_blocked" => SafeMigrationObservedState.DataBlocked,
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

    private static SafeMigrationExpectedTableInventory? FindExpected(
        IReadOnlyList<SafeMigrationExpectedTableInventory> expected,
        string schema,
        string table,
        string currentSchema
    )
    {
        var exact = expected.FirstOrDefault(value =>
            StringComparer.Ordinal.Equals(value.Schema, schema) && StringComparer.Ordinal.Equals(value.Table, table));

        return exact
            ?? expected.FirstOrDefault(value =>
                value.Schema is null
                && StringComparer.Ordinal.Equals(schema, currentSchema)
                && StringComparer.Ordinal.Equals(value.Table, table));
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

    private static string BuildUnexpectedObjectSql(
        string scope
    ) => $"""
          SELECT 'table', n.nspname, c.relname, c.relname, current_schema()
          FROM pg_catalog.pg_class c
          JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
          WHERE {scope} AND c.relkind IN ('r', 'p')
          UNION ALL
          SELECT 'column', n.nspname, c.relname, a.attname, current_schema()
          FROM pg_catalog.pg_attribute a
          JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
          JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
          WHERE {scope} AND c.relkind IN ('r', 'p')
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
          WHERE {scope} AND co.contype IN ('p', 'u', 'c', 'f')
          UNION ALL
          SELECT 'index', n.nspname, tbl.relname, idx.relname, current_schema()
          FROM pg_catalog.pg_index i
          JOIN pg_catalog.pg_class idx ON idx.oid = i.indexrelid
          JOIN pg_catalog.pg_class tbl ON tbl.oid = i.indrelid
          JOIN pg_catalog.pg_namespace n ON n.oid = tbl.relnamespace
          WHERE {scope}
            AND NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_constraint co WHERE co.conindid = idx.oid)
          ORDER BY 1, 2, 3, 4;
          """;
}
