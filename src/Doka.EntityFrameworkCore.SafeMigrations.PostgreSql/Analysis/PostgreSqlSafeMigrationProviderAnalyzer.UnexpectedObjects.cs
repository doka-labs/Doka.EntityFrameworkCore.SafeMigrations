namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationProviderAnalyzer
{
    /// <inheritdoc />
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
            var commandTimeout = context.Database.GetCommandTimeout();
            var findings = await ReadUnexpectedInventoryAsync(connection, expected, commandTimeout, cancellationToken);

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

    /// <summary>Reads complete immutable schema and child inventory chunks through the bounded transport.</summary>
    /// <param name="connection">The open analysis connection.</param>
    /// <param name="expected">The invocation's expected table inventory.</param>
    /// <param name="commandTimeout">The caller's command timeout.</param>
    /// <param name="cancellationToken">The token that cancels construction and reading.</param>
    /// <returns>The complete invocation-local inventory evidence.</returns>
    internal static async Task<List<SafeMigrationUnexpectedObject>> ReadUnexpectedInventoryAsync(
        DbConnection connection,
        IReadOnlyList<SafeMigrationExpectedTableInventory> expected,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var findings = new List<SafeMigrationUnexpectedObject>();
        var lookup = new ExpectedTableLookup(expected);
        var seen = new HashSet<(SafeMigrationDatabaseObjectKind Kind, string Schema, string Table, string Name)>();
        var schemaScopes = BuildSchemaScopeBatches(expected);
        using var chunks = expected
            .Chunk(SafeMigrationCatalogQueryLimits.MaximumInventoryValues)
            .GetEnumerator();
        var pending = new Dictionary<int, SafeMigrationExpectedTableInventory[]>();

        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection,
            schemaScopes.Length
            + ((expected.Count + SafeMigrationCatalogQueryLimits.MaximumInventoryValues - 1)
                / SafeMigrationCatalogQueryLimits.MaximumInventoryValues),
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes,
            commandTimeout,
            (command, slot) =>
            {
                var parameters = new PostgreSqlCatalogQueryParameters(command);
                if (slot < schemaScopes.Length)
                {
                    command.CommandText = BuildUnexpectedTableSql(BuildSchemaScope(schemaScopes[slot], parameters));
                }
                else
                {
                    if (!pending.TryGetValue(slot, out var chunk))
                    {
                        if (!chunks.MoveNext())
                        {
                            throw new InvalidOperationException("PostgreSQL table inventory is incomplete.");
                        }

                        chunk = chunks.Current;
                        pending.Add(slot, chunk);
                    }

                    var childScope = BuildExpectedTableScope(chunk, parameters, "n.nspname", "c.relname");
                    var indexScope = BuildExpectedTableScope(chunk, parameters, "n.nspname", "tbl.relname");
                    command.CommandText = BuildUnexpectedChildObjectSql(childScope, indexScope);
                }

                return new SafeMigrationCatalogProbeStatement(1, parameters.Utf8PayloadBytes, slot);
            },
            async (reader, slot, _, token) =>
            {
                var tablesOnly = slot < schemaScopes.Length;
                var scope = tablesOnly ? schemaScopes[slot] : pending[slot];
                pending.Remove(slot);
                await ReadUnexpectedAsync(
                    reader,
                    lookup,
                    findings,
                    seen,
                    scope,
                    tablesOnly,
                    token);
            },
            cancellationToken);

        return findings;
    }

    /// <summary>Reuses complete provider matching to remove only independently proven semantic aliases.</summary>
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
        var currentSchema = await GetCurrentSchemaAsync(
            context.Database.GetDbConnection(),
            context.Database.GetCommandTimeout(),
            cancellationToken);

        var semanticAliases = new HashSet<int>();
        foreach (var candidates in SafeMigrationSemanticCandidateFactory
                     .Create(operations, findings, currentSchema)
                     .Chunk(SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture))
        {
            var analyses = await AnalyzeAsync(
                context,
                candidates
                    .Select(static candidate => candidate.Operation)
                    .ToArray(),
                cancellationToken);

            // WHY: Map every batched alias response to its corresponding candidate.
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

    /// <summary>Validates scope ownership and deduplicates evidence without authorizing catalog cleanup.</summary>
    private static async Task ReadUnexpectedAsync(
        DbDataReader reader,
        ExpectedTableLookup lookup,
        List<SafeMigrationUnexpectedObject> findings,
        HashSet<(SafeMigrationDatabaseObjectKind Kind, string Schema, string Table, string Name)> seen,
        IReadOnlyList<SafeMigrationExpectedTableInventory> scope,
        bool tablesOnly,
        CancellationToken cancellationToken
    )
    {
        var permittedSchemas = scope
            .Select(static table => table.Schema)
            .ToHashSet(StringComparer.Ordinal);
        var permittedTables = scope
            .Select(static table => (table.Schema, table.Table))
            .ToHashSet();

        // Unexpected objects are evidence only. They are never folded into
        // the expected catalog and never authorize destructive cleanup.
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = ParseObjectKind(reader.GetString(0));
            var schema = reader.GetString(1);
            var tableName = reader.GetString(2);
            var objectName = reader.GetString(3);
            var currentSchema = reader.IsDBNull(4) ? null : reader.GetString(4);
            if (kind == SafeMigrationDatabaseObjectKind.Table != tablesOnly
                || (tablesOnly
                    ? !permittedSchemas.Contains(schema)
                    && !(StringComparer.Ordinal.Equals(schema, currentSchema) && permittedSchemas.Contains(null))
                    : !permittedTables.Contains((schema, tableName))
                    && !(StringComparer.Ordinal.Equals(schema, currentSchema)
                        && permittedTables.Contains((null, tableName)))))
            {
                throw new InvalidOperationException("PostgreSQL returned an unowned inventory object.");
            }

            if (!seen.Add((kind, schema, tableName, objectName)))
            {
                continue;
            }

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

    /// <summary>Builds bounded unique schema scopes while preserving the provider-default identity.</summary>
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

    /// <summary>Registers schema parameters in stable inventory order.</summary>
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

    /// <summary>Restricts child inventory to the exact expected physical table scope.</summary>
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

    /// <summary>Checks authored membership without folding unexpected objects into the expected catalog.</summary>
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

    /// <summary>Creates a diagnostic-only unexpected object with its stable provider code.</summary>
    private static SafeMigrationUnexpectedObject Unexpected(
        SafeMigrationDatabaseObjectKind kind,
        string schema,
        string? table,
        string name
    ) => new(kind, schema, table, name, UnexpectedObjectCode(kind));

    /// <summary>Rejects unrecognized server inventory kinds instead of inventing an object identity.</summary>
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

    /// <summary>Maps each known inventory kind to its existing diagnostic code.</summary>
    private static string UnexpectedObjectCode(
        SafeMigrationDatabaseObjectKind kind
    ) => kind switch
    {
        SafeMigrationDatabaseObjectKind.Table => "unexpected_table",
        SafeMigrationDatabaseObjectKind.Column => "unexpected_column",
        SafeMigrationDatabaseObjectKind.Index => "unexpected_index",
        SafeMigrationDatabaseObjectKind.PrimaryKey => "unexpected_primary_key",
        SafeMigrationDatabaseObjectKind.UniqueConstraint => "unexpected_unique_constraint",
        SafeMigrationDatabaseObjectKind.CheckConstraint => "unexpected_check_constraint",
        SafeMigrationDatabaseObjectKind.ForeignKey => "unexpected_foreign_key",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Reads ordinary and partitioned tables only within the bounded schema predicate.</summary>
    private static string BuildUnexpectedTableSql(
        string schemaScope
    ) => $"""
          SELECT 'table', n.nspname, c.relname, c.relname, current_schema()
          FROM pg_catalog.pg_class c
          JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
          WHERE {schemaScope} AND c.relkind IN ('r', 'p')
          ORDER BY 1, 2, 3, 4;
          """;

    /// <summary>Reads column, constraint and independent-index identities for bounded expected owners.</summary>
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

    /// <summary>Keeps explicit schemas distinct from an invocation's effective provider-default schema.</summary>
    private sealed class ExpectedTableLookup
    {
        private readonly Dictionary<(string Schema, string Table), SafeMigrationExpectedTableInventory> _explicit;
        private readonly Dictionary<string, SafeMigrationExpectedTableInventory> _providerDefault;

        /// <summary>Captures separate explicit and provider-default expected-table maps.</summary>
        /// <param name="expected">The immutable expected inventory for this invocation.</param>
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

        /// <summary>Resolves an explicit identity before considering the provider-default schema.</summary>
        /// <param name="schema">The exact physical schema.</param>
        /// <param name="table">The exact physical table name.</param>
        /// <param name="currentSchema">The observed current schema, or null when the search path has none.</param>
        /// <returns>The matching expected table, or null for an unexpected owner.</returns>
        public SafeMigrationExpectedTableInventory? Find(
            string schema,
            string table,
            string? currentSchema
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
}
