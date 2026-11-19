namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Benchmarks;

public partial class PostgreSqlLiveColumnAttributionBenchmarks
{
    /// <summary>
    /// Reconstructs the original loop's catalog state without depending on BenchmarkDotNet case order.
    /// </summary>
    /// <param name="cancellationToken">Cancels forward creation of source-controlled fixture tables.</param>
    private async Task PrepareFixtureAsync(
        CancellationToken cancellationToken
    )
    {
        var database = _database ?? throw new InvalidOperationException("The native database owner is missing.");
        foreach (var count in new[] { 1, 8, 16, 64 })
        {
            foreach (var mixed in new[] { false, true })
            {
                foreach (var drift in new[] { false, true })
                {
                    var current = count == ColumnCount && mixed == MixedCollations && drift == Drift;
                    var columns = current
                        ? _columns
                        : Columns(count, mixed);

                    var table = $"attribution_{count}_{mixed}_{drift}";

                    // WHY: Earlier fixtures in the original native loop had already committed their repairs.
                    // Recreate that exact preceding catalog while keeping every BDN case independently owned.
                    await database.ExecuteAsync(SetupSql(table, columns, current && Drift), cancellationToken);
                    if (current)
                    {
                        return;
                    }
                }
            }
        }

        throw new InvalidOperationException("The native column benchmark selected an undeclared fixture.");
    }

    /// <summary>Executes every real generated command once using the supplied native transaction owner.</summary>
    /// <param name="transaction">The iteration owner, or null for final committed verification.</param>
    /// <param name="cancellationToken">Cancels forward native execution.</param>
    private async Task ExecuteCommandsAsync(
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken
    )
    {
        foreach (var generated in _commands)
        {
            var command = Connection.CreateCommand();
            Exception? failure = null;
            try
            {
                command.Transaction = transaction;
                command.CommandText = generated.CommandText;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                failure = exception;

                throw;
            }
            finally
            {
                await SafeMigrationAnalysisCleanup.DisposeAsync(
                    command,
                    failure,
                    "SafeMigrations.PostgreSqlLiveColumnRuntimeCommandCleanupException");
            }
        }
    }

    /// <summary>Verifies the selected initial matching or comment-drift state outside measured execution.</summary>
    /// <param name="cancellationToken">Cancels forward validation.</param>
    private async Task VerifyInitialAsync(
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        MaintenanceAttribution.Require(
            await ReadCatalogStateAsync(cancellationToken)
            == (Drift
                ? "different"
                : "matching"),
            "The native column fixture does not have the selected initial classification.");

        await VerifyPreservedRowsAsync(cancellationToken);
    }

    /// <summary>Checks the original populated row without introducing a measured validation query.</summary>
    /// <param name="cancellationToken">Cancels forward data validation.</param>
    private async Task VerifyPreservedRowsAsync(
        CancellationToken cancellationToken
    )
    {
        var sql = $"SELECT COUNT(*) FROM {Identifier(_table)} WHERE "
            + string.Join(" AND ", _columns.Select(column => Identifier(column.Name) + " = 'preserved'"))
            + ";";

        MaintenanceAttribution.Require(
            Convert.ToInt32(await ReadScalarAsync(sql, cancellationToken), CultureInfo.InvariantCulture) == 1,
            "Native runtime changed the fixture's preserved row.");
    }

    /// <summary>Captures exact SQL, payload sizes and the native plan, never custom statistical samples.</summary>
    /// <param name="cancellationToken">Cancels optional structural-evidence collection.</param>
    private async Task WriteStructuralEvidenceAsync(
        CancellationToken cancellationToken
    )
    {
        var output = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_ATTRIBUTION_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        var explanation = await ReadScalarAsync(
            "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + _catalogSql,
            cancellationToken);

        MaintenanceAttribution.Require(!string.IsNullOrWhiteSpace(explanation), "Native EXPLAIN returned no plan.");

        var runtime = _commands
            .Select(static command => command.CommandText)

            // WHY: Persist the complete immutable command sequence, not a deferred mutable observation.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        await MaintenanceAttribution.WriteAsync(
            Path.Combine(
                output,
                $"postgresql-live-columns-{ColumnCount}-{MixedCollations}-{Drift}-{Guid.NewGuid():N}.json"),
            "Native PostgreSQL SQL/plan evidence; durations and allocations are measured only by BenchmarkDotNet",
            [
                new
                {
                    ServerVersionNumber = _database?.ServerVersionNumber
                        ?? throw new InvalidOperationException("Native structural evidence has no database owner."),
                    Columns = ColumnCount,
                    MixedCollations,
                    Drift,
                    CatalogSql = _catalogSql,
                    CatalogUtf8Bytes = Encoding.UTF8.GetByteCount(_catalogSql),
                    RuntimeSql = runtime,
                    RuntimeUtf8Bytes = runtime.Sum(static sql => Encoding.UTF8.GetByteCount(sql)),
                    GeneratedCommandRoundTrips = runtime.Length,
                    TransactionSetupTeardownRoundTrips = 2,
                    ExplainJson = explanation,
                },
            ],
            cancellationToken);
    }

    /// <summary>Retains the original complete varchar/default/comment and collation fixture contracts.</summary>
    /// <param name="count">The exact complete-table column cardinality.</param>
    /// <param name="mixed">Whether visible, qualified and type-owned collations are mixed.</param>
    /// <returns>The complete ordered required-column target contract.</returns>
    private static ExpectedColumnDefinition[] Columns(
        int count,
        bool mixed
    ) => Enumerable
        .Range(0, count)
        .Select(index => new ExpectedColumnDefinition(
            $"value_{index:D2}",
            typeof(string),
            false,
            "character varying(32)",
            maxLength: 32,
            collation: !mixed || index % 3 == 2
                ? null
                : new SafeMigrationCollationIdentifier(
                    index % 3 == 0
                        ? "C"
                        : "POSIX",
                    index % 3 == 0
                        ? null
                        : "pg_catalog"),
            defaultValue: SafeMigrationDefaultValue.Literal("canonical"),
            comment: "canonical"))

        // WHY: Retain stable ordered column identities for both catalog and runtime fixtures.
        // ReSharper disable once UseCollectionExpression
        .ToArray();

    /// <summary>Creates the same populated native fixture with optional repairable comment drift.</summary>
    /// <param name="table">The fixed source-controlled fixture table name.</param>
    /// <param name="columns">The complete ordered required-column target contract.</param>
    /// <param name="drift">Whether the initial comment is legacy rather than canonical.</param>
    /// <returns>The complete fixture setup SQL.</returns>
    private static string SetupSql(
        string table,
        IReadOnlyList<ExpectedColumnDefinition> columns,
        bool drift
    )
    {
        var definitions = columns.Select(column => Identifier(column.Name)
            + " character varying(32)"
            + (column.Collation is null
                ? string.Empty
                : " COLLATE "
                + (column.Collation.Schema is null
                    ? string.Empty
                    : Identifier(column.Collation.Schema) + ".")
                + Identifier(column.Collation.Name))
            + " NOT NULL DEFAULT 'canonical'");

        var comments = columns.Select(column => $"COMMENT ON COLUMN {Identifier(table)}.{Identifier(column.Name)} "
            + $"IS '{(drift ? "legacy" : "canonical")}';");

        return $"""
            CREATE TABLE {Identifier(table)} ({string.Join(",", definitions)});
            {string.Join(string.Empty, comments)}
            INSERT INTO {Identifier(table)} VALUES ({string.Join(",", columns.Select(_ => "'preserved'"))});
            """;
    }

    /// <summary>Delimits only source-controlled fixture identifiers, preserving PostgreSQL case semantics.</summary>
    /// <param name="value">The complete source-controlled identifier part.</param>
    /// <returns>The correctly escaped PostgreSQL delimited identifier.</returns>
    private static string Identifier(
        string value
    ) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
