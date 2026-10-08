namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    private static readonly System.Text.Json.JsonSerializerOptions s_attributionJsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>Records complete catalog and runtime samples for paired column fixtures without timing gates.</summary>
    [Fact]
    [Trait("Category", "Attribution")]
    public async Task ColumnAttribution_PairedPlainAndMixedCatalogRuntimeFixtures()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var results = new List<object>();
        await using var context = CreateContext(connectionString);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        var catalog = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var generator = context.GetService<IMigrationsSqlGenerator>();
        var observedStates = new List<(bool Drift, string State)>();
        var convergedStates = new List<SafeMigrationObservedState>();
        var preservedRows = new List<int>();
        var controlPath = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_POSTGRES_ATTRIBUTION_CONTROL");
        using var controlEvidence = string.IsNullOrWhiteSpace(controlPath) ? null
            : System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(controlPath)));

        if (controlEvidence is not null
            && controlEvidence.RootElement.GetProperty("ServerVersion").GetString() != Fixture.ServerVersion.ToString())
        {
            throw new InvalidOperationException("The PostgreSQL attribution control must use the same engine version.");
        }

        // Act
        foreach (var count in new[] { 1, 8, 16, 64 })
        {
            foreach (var mixed in new[] { false, true })
            {
                foreach (var drift in new[] { false, true })
                {
                    var table = $"attribution_{count}_{mixed}_{drift}";
                    var columns = AttributionColumns(count, mixed);
                    await ExecuteSqlAsync(connectionString, AttributionSetupSql(table, columns, drift));
                    var tableOperation = new SafeMigrationOperation(new EnsureTableIntent(
                        new ExpectedTableDefinition(table, columns), SafeMigrationTableMode.StrictDefinition),
                        SafeMigrationPolicy.ThrowIfDifferent);

                    var repairs = columns
                        .Select(column => (MigrationOperation)new SafeMigrationOperation(
                            new EnsureColumnIntent(table, column), SafeMigrationPolicy.RepairIfSafe))
                        .ToArray();

                    var plan = catalog.Build(tableOperation);
                    var catalogSql = "SELECT (" + plan.RenderStateExpression() + "), (" + plan.Postcondition + ");";
                    var commands = generator.Generate(repairs, context.Model);
                    var arms = AttributionArms(controlEvidence, count, mixed, drift, catalogSql,
                        commands.Select(command => command.CommandText).ToArray());

                    var catalogSamples = new List<object>();
                    var runtimeSamples = new List<object>();

                    for (var sample = -1; sample < 5; sample++)
                    {
                        foreach (var arm in arms)
                        {
                            await using var classification = connection.CreateCommand();
                            classification.CommandText = arm.Catalog;
                            var allocated = GC.GetTotalAllocatedBytes(precise: true);
                            var started = Stopwatch.GetTimestamp();
                            var state = Convert.ToString(
                                await classification.ExecuteScalarAsync(CancellationToken.None),
                                CultureInfo.InvariantCulture) ?? "<null>";

                            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                            var bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
                            if (sample >= 0)
                            {
                                catalogSamples.Add(new
                                {
                                    Arm = arm.Name,
                                    ElapsedMilliseconds = elapsed,
                                    ProcessAllocatedBytes = bytes,
                                    RoundTrips = 1,
                                    State = state,
                                });
                                observedStates.Add((drift, state));
                            }

                            // WHY: Rollback restores identical drift for every sample. Its two transport
                            // calls are setup/teardown, measured separately from the generated commands.
                            await using var transaction =
                                await connection.BeginTransactionAsync(CancellationToken.None);

                            allocated = GC.GetTotalAllocatedBytes(precise: true);
                            started = Stopwatch.GetTimestamp();
                            foreach (var sql in arm.Runtime)
                            {
                                await using var execution = connection.CreateCommand();
                                execution.Transaction = transaction;
                                execution.CommandText = sql;
                                try
                                {
                                    await execution.ExecuteNonQueryAsync(CancellationToken.None);
                                }
                                catch (PostgresException exception)
                                {
                                    var failedOutput = Environment.GetEnvironmentVariable(
                                        "SAFE_MIGRATIONS_POSTGRES_ATTRIBUTION_OUTPUT");

                                    if (!string.IsNullOrWhiteSpace(failedOutput))
                                    {
                                        var failedPath = Path.ChangeExtension(
                                            Path.GetFullPath(failedOutput), "failure.sql");

                                        Directory.CreateDirectory(Path.GetDirectoryName(failedPath)!);
                                        File.WriteAllText(failedPath, "-- Fixture: " + table + "\n"
                                            + AttributionSetupSql(table, columns, drift) + "\n" + sql);
                                    }

                                    throw new InvalidOperationException(
                                        $"PostgreSQL fixture {table}, sample {sample}, arm {arm.Name} failed.",
                                        exception);
                                }
                            }

                            elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                            bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
                            await transaction.RollbackAsync(CancellationToken.None);
                            if (sample >= 0)
                            {
                                runtimeSamples.Add(new
                                {
                                    Arm = arm.Name,
                                    ElapsedMilliseconds = elapsed,
                                    ProcessAllocatedBytes = bytes,
                                    GeneratedCommandRoundTrips = arm.Runtime.Count,
                                    TransactionSetupTeardownRoundTrips = 2,
                                });
                            }
                        }
                    }

                    await using var explain = connection.CreateCommand();
                    explain.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + catalogSql;
                    var explanation = Convert.ToString(await explain.ExecuteScalarAsync(CancellationToken.None),
                        CultureInfo.InvariantCulture) ?? "<null>";

                    results.Add(new
                    {
                        Columns = count,
                        MixedCollations = mixed,
                        Drift = drift,
                        CatalogSql = catalogSql,
                        CatalogUtf8Bytes = Encoding.UTF8.GetByteCount(catalogSql),
                        RuntimeSql = commands.Select(command => command.CommandText).ToArray(),
                        RuntimeUtf8Bytes = commands.Sum(command => Encoding.UTF8.GetByteCount(command.CommandText)),
                        CatalogSamples = catalogSamples,
                        RuntimeSamples = runtimeSamples,
                        ExplainJson = explanation,
                    });
                    await ExecuteOperationsAsync(context, repairs);
                    await ExecuteOperationsAsync(context, repairs);
                    var replay = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
                        context, repairs.Cast<SafeMigrationOperation>().ToArray(), CancellationToken.None);

                    convergedStates.AddRange(replay.Select(result => result.ObservedState));
                    preservedRows.Add(await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM "
                        + PostgreSqlIdentifier(table) + " WHERE " + string.Join(" AND ",
                            columns.Select(column => PostgreSqlIdentifier(column.Name) + " = 'preserved'")) + ";"));
                }
            }
        }

        var output = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_POSTGRES_ATTRIBUTION_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            var fullPath = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                ServerVersion = Fixture.ServerVersion.ToString(),
                AllocationScope = "Process-wide precise totals around asynchronous execution; no timing assertions.",
                ControlEvidence = controlPath,
                Results = results,
            }, s_attributionJsonOptions));
        }

        // Assert
        Assert.Equal(16, results.Count);
        Assert.Equal(80 * (controlEvidence is null ? 1 : 3), observedStates.Count);
        Assert.All(observedStates, result => Assert.Equal(result.Drift ? "different" : "matching", result.State));
        Assert.All(convergedStates, state => Assert.Equal(SafeMigrationObservedState.Matching, state));
        Assert.Equal(356, convergedStates.Count);
        Assert.All(preservedRows, count => Assert.Equal(1, count));
    }

    /// <summary>Brackets the current SQL with an immutable control on the same relation and connection.</summary>
    /// <param name="control">The immutable same-version control evidence, when requested.</param>
    /// <param name="columns">The complete target table width.</param>
    /// <param name="mixed">Whether the fixture mixes visible, qualified and type-owned collations.</param>
    /// <param name="drift">Whether the fixture carries repairable comment drift.</param>
    /// <param name="catalogSql">The current complete catalog classifier.</param>
    /// <param name="runtimeSql">The current ordered complete runtime commands.</param>
    /// <returns>The current arm, optionally bracketed by the identical immutable control.</returns>
    private static (string Name, string Catalog, IReadOnlyList<string> Runtime)[] AttributionArms(
        System.Text.Json.JsonDocument? control,
        int columns,
        bool mixed,
        bool drift,
        string catalogSql,
        IReadOnlyList<string> runtimeSql
    )
    {
        if (control is null)
        {
            return [("current", catalogSql, runtimeSql)];
        }

        var fixture = control.RootElement
            .GetProperty("Results")
            .EnumerateArray()
            .Single(result => result.GetProperty("Columns").GetInt32() == columns
                && result.GetProperty("MixedCollations").GetBoolean() == mixed
                && result.GetProperty("Drift").GetBoolean() == drift);

        var controlCatalog = fixture
            .GetProperty("CatalogSql")
            .GetString()
            ?? throw new InvalidOperationException("The PostgreSQL control has no catalog SQL.");

        var controlRuntime = fixture
            .GetProperty("RuntimeSql")
            .EnumerateArray()
            .Select(value => value.GetString()
                ?? throw new InvalidOperationException("The PostgreSQL control has no runtime SQL."))
            .ToArray();

        return [("control_before", controlCatalog, controlRuntime), ("current", catalogSql, runtimeSql),
            ("control_after", controlCatalog, controlRuntime)];
    }

    /// <summary>Creates the same complete target facets used by the offline generation workload.</summary>
    /// <param name="count">The complete target table width.</param>
    /// <param name="mixed">Whether visible, qualified and type-owned collations are mixed.</param>
    /// <returns>The ordered complete target column definitions.</returns>
    private static ExpectedColumnDefinition[] AttributionColumns(
        int count,
        bool mixed
    ) => Enumerable.Range(0, count)
        .Select(index => new ExpectedColumnDefinition(
            $"value_{index:D2}", typeof(string), false, "character varying(32)", maxLength: 32,
            collation: !mixed || index % 3 == 2
                ? null
                : new SafeMigrationCollationIdentifier(
                    index % 3 == 0 ? "C" : "POSIX", index % 3 == 0 ? null : "pg_catalog"),
            defaultValue: SafeMigrationDefaultValue.Literal("canonical"), comment: "canonical"))
        .ToArray();

    /// <summary>Creates one populated fixture with only a repairable comment difference in drift mode.</summary>
    /// <param name="table">The isolated fixture table name.</param>
    /// <param name="columns">The ordered complete target column definitions.</param>
    /// <param name="drift">Whether only the comment facet differs from the target.</param>
    /// <returns>The complete identical setup SQL for a paired fixture.</returns>
    private static string AttributionSetupSql(
        string table,
        IReadOnlyList<ExpectedColumnDefinition> columns,
        bool drift
    )
    {
        var definitions = columns.Select(column => PostgreSqlIdentifier(column.Name) + " character varying(32)"
            + (column.Collation is null ? string.Empty : " COLLATE "
                + (column.Collation.Schema is null ? string.Empty : PostgreSqlIdentifier(column.Collation.Schema) + ".")
                + PostgreSqlIdentifier(column.Collation.Name)) + " NOT NULL DEFAULT 'canonical'");

        var comments = columns.Select(column => "COMMENT ON COLUMN " + PostgreSqlIdentifier(table) + "."
            + PostgreSqlIdentifier(column.Name) + " IS '" + (drift ? "legacy" : "canonical") + "';");

        return "CREATE TABLE " + PostgreSqlIdentifier(table) + " (" + string.Join(",", definitions) + ");"
            + string.Join(string.Empty, comments) + "INSERT INTO " + PostgreSqlIdentifier(table) + " VALUES ("
            + string.Join(",", columns.Select(_ => "'preserved'")) + ");";
    }
}
