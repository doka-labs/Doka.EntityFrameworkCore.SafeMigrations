namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed class MySqlSafeMigrationProviderAnalyzer : ISafeMigrationProviderAnalyzer
{
    private const string StatePrefix = "SET @doka_sm_state = (";
    private const string RepairPreconditionPrefix = "SET @doka_sm_repair_ok = (";
    private const string PostconditionPrefix = "SET @doka_sm_observed_postcondition = (";
    private readonly IMigrationsSqlGenerator _sqlGenerator;

    public MySqlSafeMigrationProviderAnalyzer(
        IMigrationsSqlGenerator sqlGenerator
    )
    {
        ArgumentNullException.ThrowIfNull(sqlGenerator);

        _sqlGenerator = sqlGenerator;
    }

    public string ProviderId => "doka_mysql";

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
            var serverVersion = MySqlServerVersion.AutoDetect(
                connection,
                MySqlServerVersionCompatibilityMode.AllowUnsupported);

            return new SafeMigrationProviderEnvironment(
                ProviderId,
                serverVersion.IsMariaDb ? "mariadb" : "mysql",
                connection.ServerVersion);
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
            var parameterizer = new MySqlCatalogQueryParameterizer(command);
            var selections = new List<string>(operations.Count);

            // One UNION ALL query classifies the complete batch against one
            // catalog observation and avoids a round trip per operation.
            for (var ordinal = 0; ordinal < operations.Count; ordinal++)
            {
                var operation = operations[ordinal]
                    ?? throw new ArgumentException(
                        "The operation batch cannot contain null entries.",
                        nameof(operations));

                var commands = _sqlGenerator.Generate([operation], context.Model);
                var stateExpression = parameterizer.Parameterize(ExtractExpression(commands, StatePrefix));
                var postcondition = parameterizer.Parameterize(ExtractExpression(commands, PostconditionPrefix));
                var repairPrecondition = parameterizer.Parameterize(
                    ExtractExpression(commands, RepairPreconditionPrefix));

                selections.Add(
                    $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, "
                    + $"({stateExpression}), "
                    + $"COALESCE(({postcondition}), FALSE), "
                    + $"COALESCE(({repairPrecondition}), FALSE)");
            }

            command.CommandText = string.Join("\nUNION ALL\n", selections) + "\nORDER BY 1;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var results = new List<SafeMigrationProviderAnalysis>(operations.Count);

            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetInt32(0) != results.Count)
                {
                    throw new InvalidOperationException(
                        "The MySQL SafeMigrations classifier returned an invalid ordinal.");
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
                    "The MySQL SafeMigrations classifier returned an inconsistent row count.");
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

        var expected = SafeMigrationExpectedCatalog
            .Create(operations)
            .Where(static table => table.Schema is null)
            .ToDictionary(static table => table.Table, StringComparer.Ordinal);

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
            command.CommandText = UnexpectedObjectSql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var findings = new List<SafeMigrationUnexpectedObject>();

            // Unexpected objects are evidence only. They are never folded into
            // the expected catalog and never authorize destructive cleanup.
            while (await reader.ReadAsync(cancellationToken))
            {
                var kind = ParseObjectKind(reader.GetString(0));
                var tableName = reader.GetString(1);
                var objectName = reader.GetString(2);

                if (!expected.TryGetValue(tableName, out var table))
                {
                    if (kind == SafeMigrationDatabaseObjectKind.Table)
                    {
                        findings.Add(Unexpected(kind, table: null, objectName));
                    }

                    continue;
                }

                if (kind == SafeMigrationDatabaseObjectKind.Table
                    || IsExpected(table, kind, objectName))
                {
                    continue;
                }

                findings.Add(Unexpected(kind, tableName, objectName));
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

    private static string ExtractExpression(
        IReadOnlyList<MigrationCommand> commands,
        string prefix
    )
    {
        var commandText = commands
            .Select(static command => command.CommandText)
            .Single(command => command.StartsWith(prefix, StringComparison.Ordinal));

        const string suffix = ");";

        return commandText.EndsWith(suffix, StringComparison.Ordinal)
            ? commandText[prefix.Length..^suffix.Length]
            : throw new InvalidOperationException(
                "The MySQL SafeMigrations classifier command has an invalid boundary.");
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
        _ => throw new InvalidOperationException("The MySQL SafeMigrations classifier returned an unknown state."),
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

    private static bool IsExpected(
        SafeMigrationExpectedTableInventory table,
        SafeMigrationDatabaseObjectKind kind,
        string name
    ) => kind switch
    {
        SafeMigrationDatabaseObjectKind.Column => table.Columns.Contains(name),
        SafeMigrationDatabaseObjectKind.Index => table.Indexes.Contains(name),
        SafeMigrationDatabaseObjectKind.PrimaryKey => table.Constraints.Values.Contains(
            SafeMigrationDatabaseObjectKind.PrimaryKey),
        _ => table.Constraints.TryGetValue(name, out var expectedKind) && expectedKind == kind
    };

    private static SafeMigrationUnexpectedObject Unexpected(
        SafeMigrationDatabaseObjectKind kind,
        string? table,
        string name
    ) => new(kind, schema: null, table, name, $"unexpected_{ObjectCode(kind)}");

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
            "The MySQL unexpected-object inventory returned an unknown object kind."),
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

    private const string UnexpectedObjectSql = """
                                               SELECT 'table', t.TABLE_NAME, t.TABLE_NAME
                                               FROM INFORMATION_SCHEMA.TABLES t
                                               WHERE t.TABLE_SCHEMA = DATABASE() AND t.TABLE_TYPE = 'BASE TABLE'
                                               UNION ALL
                                               SELECT 'column', c.TABLE_NAME, c.COLUMN_NAME
                                               FROM INFORMATION_SCHEMA.COLUMNS c
                                               WHERE c.TABLE_SCHEMA = DATABASE()
                                               UNION ALL
                                               SELECT CASE tc.CONSTRAINT_TYPE
                                                   WHEN 'PRIMARY KEY' THEN 'primary_key'
                                                   WHEN 'UNIQUE' THEN 'unique_constraint'
                                                   WHEN 'CHECK' THEN 'check_constraint'
                                                   WHEN 'FOREIGN KEY' THEN 'foreign_key'
                                                   END,
                                                   tc.TABLE_NAME,
                                                   tc.CONSTRAINT_NAME
                                               FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                                               WHERE tc.CONSTRAINT_SCHEMA = DATABASE()
                                                 AND tc.CONSTRAINT_TYPE IN ('PRIMARY KEY', 'UNIQUE', 'CHECK', 'FOREIGN KEY')
                                               UNION ALL
                                               SELECT 'index', s.TABLE_NAME, s.INDEX_NAME
                                               FROM INFORMATION_SCHEMA.STATISTICS s
                                               WHERE s.TABLE_SCHEMA = DATABASE()
                                                 AND s.SEQ_IN_INDEX = 1
                                                 AND s.INDEX_NAME <> 'PRIMARY'
                                                 AND NOT EXISTS (
                                                     SELECT 1
                                                     FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                                                     WHERE tc.CONSTRAINT_SCHEMA = s.TABLE_SCHEMA
                                                       AND tc.TABLE_NAME = s.TABLE_NAME
                                                       AND tc.CONSTRAINT_NAME = s.INDEX_NAME)
                                               ORDER BY 1, 2, 3;
                                               """;
}
