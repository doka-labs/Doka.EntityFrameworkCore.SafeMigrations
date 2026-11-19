namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the ColumnAttribution PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlColumnAttributionIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlColumnAttributionIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Preserves each native column fixture, rollback control and committed idempotent repair.</summary>
    /// <param name="count">The exact number of required varchar columns in this isolated fixture.</param>
    /// <param name="mixed">Whether visible, qualified and type-owned collations are mixed.</param>
    /// <param name="drift">Whether comments initially differ from the canonical target.</param>
    [Theory]
    [InlineData(1, false, false)]
    [InlineData(1, false, true)]
    [InlineData(1, true, false)]
    [InlineData(1, true, true)]
    [InlineData(8, false, false)]
    [InlineData(8, false, true)]
    [InlineData(8, true, false)]
    [InlineData(8, true, true)]
    [InlineData(16, false, false)]
    [InlineData(16, false, true)]
    [InlineData(16, true, false)]
    [InlineData(16, true, true)]
    [InlineData(64, false, false)]
    [InlineData(64, false, true)]
    [InlineData(64, true, false)]
    [InlineData(64, true, true)]
    [Trait("Category", "Attribution")]
    public async Task ColumnAttribution_PairedPlainAndMixedCatalogRuntimeFixtures(
        int count,
        bool mixed,
        bool drift
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        var catalog = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var generator = context.GetService<IMigrationsSqlGenerator>();
        var table = $"attribution_{count}_{mixed}_{drift}";
        var columns = AttributionColumns(count, mixed);
        await PrepareAttributionFixtureAsync(
            connectionString,
            count,
            mixed,
            drift,
            CancellationToken.None);

        var tableOperation = new SafeMigrationOperation(
            new EnsureTableIntent(
                new ExpectedTableDefinition(table, columns),
                SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        var repairs = columns
            .Select(MigrationOperation (column) => new SafeMigrationOperation(
                new EnsureColumnIntent(table, column),
                SafeMigrationPolicy.RepairIfSafe))
            .ToArray();

        var plan = catalog.Build(tableOperation);
        var catalogSql = "SELECT (" + plan.RenderStateExpression() + "), (" + plan.Postcondition + ");";
        var commands = generator.Generate(repairs, context.Model);
        var controlPath = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_POSTGRES_ATTRIBUTION_CONTROL");
        using var controlEvidence = string.IsNullOrWhiteSpace(controlPath)
            ? null
            : JsonDocument.Parse(await File.ReadAllTextAsync(Path.GetFullPath(controlPath), CancellationToken.None));

        if (controlEvidence is not null
            && controlEvidence
                .RootElement
                .GetProperty("ServerVersion")
                .GetString()
            != Fixture.ServerVersion.ToString())
        {
            throw new InvalidOperationException("The PostgreSQL attribution control must use the same engine version.");
        }

        var arms = AttributionArms(
            controlEvidence,
            count,
            mixed,
            drift,
            catalogSql,
            commands
                .Select(command => command.CommandText)
                .ToArray());

        var observedStates = new List<string>();

        // Act
        foreach (var arm in arms)
        {
            await using var classification = connection.CreateCommand();
            classification.CommandText = arm.Catalog;
            observedStates.Add(Convert.ToString(
                    await classification.ExecuteScalarAsync(CancellationToken.None),
                    CultureInfo.InvariantCulture)
                ?? "<null>");

            // WHY: Each control must see the same initial drift; native rollback owns the reset,
            // not custom repeated timing or an artificial test deadline.
            await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
            foreach (var sql in arm.Runtime)
            {
                await using var execution = connection.CreateCommand();
                execution.Transaction = transaction;
                execution.CommandText = sql;
                await execution.ExecuteNonQueryAsync(CancellationToken.None);
            }

            await transaction.RollbackAsync(CancellationToken.None);
        }

        await ExecuteOperationsAsync(context, repairs, CancellationToken.None);
        await ExecuteOperationsAsync(context, repairs, CancellationToken.None);
        var replay = await context
            .GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(
                context,
                repairs
                    .Cast<SafeMigrationOperation>()
                    .ToArray(),
                CancellationToken.None);

        var preservedRows = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM "
            + PostgreSqlIdentifier(table)
            + " WHERE "
            + string.Join(
                " AND ",
                columns.Select(column => PostgreSqlIdentifier(column.Name) + " = 'preserved'"))
            + ";",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(controlEvidence is null
                ? 1
                : 3,
            observedStates.Count);

        Assert.All(observedStates,
            state => Assert.Equal(drift
                    ? "different"
                    : "matching",
                state));

        Assert.Equal(count, replay.Count);
        Assert.All(replay, result => Assert.Equal(SafeMigrationObservedState.Matching, result.ObservedState));
        Assert.Equal(1, preservedRows);
    }

    /// <summary>Recreates the original preceding catalog independently for every explicit theory row.</summary>
    /// <param name="connectionString">The current theory row's isolated database.</param>
    /// <param name="targetCount">The exact current target width.</param>
    /// <param name="targetMixed">Whether the current fixture mixes collation ownership.</param>
    /// <param name="targetDrift">Whether the current fixture retains its initial comment difference.</param>
    /// <param name="cancellationToken">Cancels forward fixture creation.</param>
    private static async Task PrepareAttributionFixtureAsync(
        string connectionString,
        int targetCount,
        bool targetMixed,
        bool targetDrift,
        CancellationToken cancellationToken
    )
    {
        foreach (var count in new[] { 1, 8, 16, 64 })
        {
            foreach (var mixed in new[] { false, true })
            {
                foreach (var drift in new[] { false, true })
                {
                    var current = count == targetCount && mixed == targetMixed && drift == targetDrift;

                    // WHY: Earlier loop fixtures had already committed canonical comments. Preserve that
                    // catalog context without making an independent theory row depend on earlier execution.
                    await ExecuteSqlAsync(
                        connectionString,
                        AttributionSetupSql($"attribution_{count}_{mixed}_{drift}",
                            AttributionColumns(count, mixed),
                            current && targetDrift),
                        cancellationToken);

                    if (current)
                    {
                        return;
                    }
                }
            }
        }

        throw new InvalidOperationException("The PostgreSQL attribution case is not declared by its fixture matrix.");
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
        JsonDocument? control,
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

        var fixture = control
            .RootElement
            .GetProperty("Results")
            .EnumerateArray()
            .Single(result => result
                    .GetProperty("Columns")
                    .GetInt32()
                == columns
                && result
                    .GetProperty("MixedCollations")
                    .GetBoolean()
                == mixed
                && result
                    .GetProperty("Drift")
                    .GetBoolean()
                == drift);

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

        return
        [
            ("control_before", controlCatalog, controlRuntime), ("current", catalogSql, runtimeSql),
            ("control_after", controlCatalog, controlRuntime),
        ];
    }

    /// <summary>Creates the same complete target facets used by the offline generation workload.</summary>
    /// <param name="count">The complete target table width.</param>
    /// <param name="mixed">Whether visible, qualified and type-owned collations are mixed.</param>
    /// <returns>The ordered complete target column definitions.</returns>
    private static ExpectedColumnDefinition[] AttributionColumns(
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

        // WHY: Retain explicit fluent materialization and its independently owned result snapshot.
        // ReSharper disable once UseCollectionExpression
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
        var definitions = columns.Select(column => PostgreSqlIdentifier(column.Name)
            + " character varying(32)"
            + (column.Collation is null
                ? string.Empty
                : " COLLATE "
                + (column.Collation.Schema is null
                    ? string.Empty
                    : PostgreSqlIdentifier(column.Collation.Schema) + ".")
                + PostgreSqlIdentifier(column.Collation.Name))
            + " NOT NULL DEFAULT 'canonical'");

        var comments = columns.Select(column => "COMMENT ON COLUMN "
            + PostgreSqlIdentifier(table)
            + "."
            + PostgreSqlIdentifier(column.Name)
            + " IS '"
            + (drift
                ? "legacy"
                : "canonical")
            + "';");

        return $"""
            CREATE TABLE {PostgreSqlIdentifier(table)} ({string.Join(",", definitions)});{string.Join(
                string.Empty, comments)}INSERT
            INTO {PostgreSqlIdentifier(table)} VALUES ({string.Join(",", columns.Select(_ => "'preserved'"))});
            """;
    }
}
