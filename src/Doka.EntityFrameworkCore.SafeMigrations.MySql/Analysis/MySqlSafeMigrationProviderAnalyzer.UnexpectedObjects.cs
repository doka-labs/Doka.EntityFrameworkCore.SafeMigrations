namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    public async Task<IReadOnlyList<SafeMigrationUnexpectedObject>> FindUnexpectedObjectsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operations);

        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var commandTimeout = context.Database.GetCommandTimeout();
            _currentDatabase = await ReadCurrentDatabaseAsync(connection, commandTimeout, cancellationToken);

            var expected = new Dictionary<string, SafeMigrationExpectedTableInventory>(StringComparer.Ordinal);
            foreach (var table in SafeMigrationExpectedCatalog.Create(
                         operations,
                         schema => MySqlTableIdentity.NormalizeDatabase(schema, _currentDatabase)))
            {
                if (table.Schema is not null
                    && !StringComparer.Ordinal.Equals(table.Schema, _currentDatabase))
                {
                    continue;
                }

                if (!expected.TryAdd(table.Table, table))
                {
                    throw new InvalidOperationException(
                        "The migration contains multiple expected table definitions for one MySQL database object.");
                }
            }

            var serverVersion = MySqlServerVersion.AutoDetect(
                connection,
                MySqlServerVersionCompatibilityMode.AllowUnsupported);

            var maximumPayloadBytes = await GetMaximumPayloadBytesAsync(connection, commandTimeout, cancellationToken);
            expected = await CanonicalizeUnexpectedInventoryOwnersAsync(
                connection,
                expected,
                maximumPayloadBytes,
                commandTimeout,
                cancellationToken);

            var findings = await ReadUnexpectedInventoryAsync(
                connection,
                expected,
                serverVersion.IsMariaDb,
                maximumPayloadBytes,
                commandTimeout,
                cancellationToken);

            findings = await RemoveRequiredColumnAddRecoveryChecksAsync(
                context,
                operations,
                findings,
                serverVersion.IsMariaDb,
                maximumPayloadBytes,
                commandTimeout,
                cancellationToken);

            return await RemoveSemanticAliasesAsync(context, operations, findings, cancellationToken);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>Reads immutable inventory scopes as complete 512-table statement slots.</summary>
    /// <param name="connection">The open analysis connection.</param>
    /// <param name="expected">The invocation's expected table inventory.</param>
    /// <param name="isMariaDb">Whether implicit JSON checks require MariaDB catalog qualification.</param>
    /// <param name="maximumPayloadBytes">The qualified packet and shared payload bound.</param>
    /// <param name="commandTimeout">The caller's command timeout.</param>
    /// <param name="cancellationToken">The token that cancels construction and reading.</param>
    /// <returns>The complete invocation-local inventory evidence.</returns>
    internal static async Task<List<SafeMigrationUnexpectedObject>> ReadUnexpectedInventoryAsync(
        DbConnection connection,
        Dictionary<string, SafeMigrationExpectedTableInventory> expected,
        bool isMariaDb,
        int maximumPayloadBytes,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var findings = new List<SafeMigrationUnexpectedObject>();
        var seen = new HashSet<(SafeMigrationDatabaseObjectKind Kind, string Table, string Name)>();
        var tables = expected
            .Keys
            .Order(StringComparer.Ordinal)
            .ToArray();

        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection,
            1
            + ((tables.Length + SafeMigrationCatalogQueryLimits.MaximumInventoryValues - 1)
                / SafeMigrationCatalogQueryLimits.MaximumInventoryValues),
            maximumPayloadBytes,
            commandTimeout,
            (command, slot) =>
            {
                if (slot == 0)
                {
                    command.CommandText = BuildUnexpectedTableSql();

                    return new SafeMigrationCatalogProbeStatement(1, 0, slot);
                }

                var parameters = new MySqlCatalogQueryParameterizer(command);
                var tableScope = string.Join(
                    ", ",
                    tables
                        .Skip((slot - 1) * SafeMigrationCatalogQueryLimits.MaximumInventoryValues)
                        .Take(SafeMigrationCatalogQueryLimits.MaximumInventoryValues)
                        .Select(parameters.AddString));

                command.CommandText = BuildUnexpectedChildObjectSql(tableScope, isMariaDb);

                return new SafeMigrationCatalogProbeStatement(1, parameters.Utf8PayloadBytes, slot);
            },
            (reader, slot, _, token) => ReadUnexpectedAsync(
                reader,
                expected,
                findings,
                seen,
                slot == 0
                    ? null
                    : tables
                        .Skip((slot - 1) * SafeMigrationCatalogQueryLimits.MaximumInventoryValues)
                        .Take(SafeMigrationCatalogQueryLimits.MaximumInventoryValues)
                        .ToHashSet(StringComparer.Ordinal),
                token),
            cancellationToken);

        return findings;
    }

    private async Task<IReadOnlyList<SafeMigrationUnexpectedObject>> RemoveSemanticAliasesAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        List<SafeMigrationUnexpectedObject> findings,
        CancellationToken cancellationToken
    )
    {
        if (findings.Count == 0)
        {
            return findings.AsReadOnly();
        }

        // Reuse the provider's complete catalog comparator instead of
        // maintaining a weaker second definition of semantic equivalence in
        // the inventory path. Candidates are consumed in bounded windows so a
        // large legacy catalog cannot materialize a cross-product in memory.
        var semanticAliases = new HashSet<int>();
        foreach (var candidates in SafeMigrationSemanticCandidateFactory
                     .Create(operations, findings, projectUniqueIndexesAsUniqueConstraints: true)
                     .Chunk(SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture))
        {
            var analyses = await AnalyzeAsync(
                context,
                candidates
                    .Select(static candidate => candidate.Operation)
                    .ToArray(),
                cancellationToken);

            // WHY: Pair each alias candidate with the analysis returned at the same ordinal.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < candidates.Length; index++)
            {
                if (analyses[index].ObservedState == SafeMigrationObservedState.Matching)
                {
                    semanticAliases.Add(candidates[index].UnexpectedObjectIndex);
                }
            }
        }

        if (semanticAliases.Count == 0)
        {
            return findings.AsReadOnly();
        }

        return findings
            .Where((_, index) => !semanticAliases.Contains(index))
            .ToArray();
    }

    private static async Task ReadUnexpectedAsync(
        DbDataReader reader,
        Dictionary<string, SafeMigrationExpectedTableInventory> expected,
        List<SafeMigrationUnexpectedObject> findings,
        HashSet<(SafeMigrationDatabaseObjectKind Kind, string Table, string Name)> seen,
        HashSet<string>? permittedTables,
        CancellationToken cancellationToken
    )
    {
        // Unexpected objects are evidence only. They are never folded into
        // the expected catalog and never authorize destructive cleanup.
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = ParseObjectKind(reader.GetString(0));
            var tableName = reader.GetString(1);
            var objectName = reader.GetString(2);
            var providerGeneratedJsonCheck = reader.GetBoolean(3);
            if (permittedTables is null
                    ? kind != SafeMigrationDatabaseObjectKind.Table
                    : kind == SafeMigrationDatabaseObjectKind.Table || !permittedTables.Contains(tableName))
            {
                throw new InvalidOperationException("MySQL returned an unowned inventory object.");
            }

            if (!seen.Add((kind, tableName, objectName)))
            {
                continue;
            }

            if (!expected.TryGetValue(tableName, out var table))
            {
                if (kind == SafeMigrationDatabaseObjectKind.Table)
                {
                    findings.Add(Unexpected(kind, table: null, objectName));
                }

                continue;
            }

            if (kind == SafeMigrationDatabaseObjectKind.Table
                || IsExpected(table, kind, objectName, providerGeneratedJsonCheck))
            {
                continue;
            }

            findings.Add(Unexpected(kind, tableName, objectName));
        }
    }

    private static bool IsExpected(
        SafeMigrationExpectedTableInventory table,
        SafeMigrationDatabaseObjectKind kind,
        string name,
        bool providerGeneratedJsonCheck
    ) => kind switch
    {
        SafeMigrationDatabaseObjectKind.Column => table.Columns.Contains(name),
        SafeMigrationDatabaseObjectKind.CheckConstraint when providerGeneratedJsonCheck
            && table.ColumnStoreTypes.TryGetValue(name, out var storeType)
            && StringComparer.OrdinalIgnoreCase.Equals(storeType?.Trim(), "json") => true,
        SafeMigrationDatabaseObjectKind.Index => table.Indexes.Contains(name),
        SafeMigrationDatabaseObjectKind.PrimaryKey => table.Constraints.Values.Contains(
            SafeMigrationDatabaseObjectKind.PrimaryKey),
        SafeMigrationDatabaseObjectKind.UniqueConstraint when table.UniqueIndexes.Contains(name) => true,
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

    private static string BuildUnexpectedTableSql() => """
                                                       SELECT 'table', t.TABLE_NAME, t.TABLE_NAME, FALSE
                                                       FROM INFORMATION_SCHEMA.TABLES t
                                                       WHERE t.TABLE_SCHEMA = DATABASE() AND t.TABLE_TYPE = 'BASE TABLE'
                                                       ORDER BY 1, 2, 3;
                                                       """;

    private static string BuildUnexpectedChildObjectSql(
        string tableScope,
        bool isMariaDb
    ) => $"""
          SELECT 'column', c.TABLE_NAME, c.COLUMN_NAME, FALSE
          FROM INFORMATION_SCHEMA.COLUMNS c
          WHERE c.TABLE_SCHEMA = DATABASE()
            AND c.TABLE_NAME IN ({tableScope})
          UNION ALL
          SELECT CASE tc.CONSTRAINT_TYPE
              WHEN 'PRIMARY KEY' THEN 'primary_key'
              WHEN 'UNIQUE' THEN 'unique_constraint'
              WHEN 'CHECK' THEN 'check_constraint'
              WHEN 'FOREIGN KEY' THEN 'foreign_key'
              END,
              tc.TABLE_NAME,
              tc.CONSTRAINT_NAME,
              {(isMariaDb ? MariaDbImplicitJsonCheckInventoryExpression : "FALSE")}
          FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
          {(isMariaDb ? MariaDbImplicitJsonCheckInventoryJoins : string.Empty)}
          WHERE tc.CONSTRAINT_SCHEMA = DATABASE()
            AND tc.TABLE_NAME IN ({tableScope})
            AND tc.CONSTRAINT_TYPE IN ('PRIMARY KEY', 'UNIQUE', 'CHECK', 'FOREIGN KEY')
          UNION ALL
          SELECT 'index', s.TABLE_NAME, s.INDEX_NAME, FALSE
          FROM INFORMATION_SCHEMA.STATISTICS s
          WHERE s.TABLE_SCHEMA = DATABASE()
            AND s.TABLE_NAME IN ({tableScope})
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
