namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Checks classification stops spilling one temporary table per catalog subquery.</summary>
    /// <remarks>
    /// WHY: MariaDB cannot keep an INFORMATION_SCHEMA materialisation in its HEAP engine because
    /// the views carry TEXT columns, so every catalog subquery of a classification statement landed
    /// on disk. The counter is the mechanical proof of the snapshot: a replayed statement of 491
    /// catalog subqueries produced 711 on-disk temporary tables against the live views and one
    /// against the copy. MySQL keeps the work in memory, so its expectation is that the counter
    /// does not move at all.
    ///
    /// The session counter is read on the connection the analyzer used, because the global counter
    /// is shared with every other test running against the same server.
    /// </remarks>
    [Fact]
    public async Task Analyzer_DoesNotSpillOneTemporaryTablePerCatalogSubquery()
    {
        // Arrange
        const int tables = 32;
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var fixtureSql = new StringBuilder();
        for (var table = 0; table < tables; table++)
        {
            fixtureSql.Append("CREATE TABLE `snap_").Append(table.ToString("D3", CultureInfo.InvariantCulture))
                .Append("` (`id` int NOT NULL, `code` int NOT NULL); ");
        }

        await ExecuteSqlAsync(connectionString, fixtureSql.ToString());

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        for (var table = 0; table < tables; table++)
        {
            builder.EnsureTable(
                ExpectedSnapshotTable("snap_" + table.ToString("D3", CultureInfo.InvariantCulture)),
                SafeMigrationTableMode.StrictDefinition,
                SafeMigrationPolicy.ThrowIfDifferent);
        }

        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var operations = builder.Operations.Cast<SafeMigrationOperation>().ToArray();
        var before = await SessionCounterAsync(context, "Created_tmp_disk_tables");

        // Act
        var analysis = await analyzer.AnalyzeAsync(context, operations, CancellationToken.None);

        // Assert
        var spilled = await SessionCounterAsync(context, "Created_tmp_disk_tables") - before;
        Assert.Equal(tables, analysis.Count);
        Assert.All(
            analysis,
            entry => Assert.Equal(SafeMigrationObservedState.Matching, entry.ObservedState));

        if (Fixture.IsMariaDb)
        {
            Assert.InRange(spilled, 0, tables - 1);
        }
        else
        {
            Assert.Equal(0, spilled);
        }
    }

    /// <summary>Checks the snapshot reaches the same verdicts as the live catalog.</summary>
    /// <remarks>
    /// WHY: The counter test proves the spill is gone, not that the classification is unchanged.
    /// A matching table, a renamed column, a missing column, a surplus column, a present index and
    /// an absent one are classified twice, once through the snapshot and once through the live
    /// views, so a redirection that silently changed a verdict fails here.
    ///
    /// The qualified control uses the connected database. Padding ensures that the unqualified
    /// path crosses the snapshot threshold; command counters prove both creation and actual use.
    /// </remarks>
    [Fact]
    public async Task Analyzer_ReachesTheSameVerdictsWithAndWithoutTheSnapshot()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `verdict_parent` (`id` int NOT NULL,"
            + " CONSTRAINT `pk_verdict_parent` PRIMARY KEY (`id`));"
            + " CREATE TABLE `verdict_match` (`id` int NOT NULL, `code` int NOT NULL,"
            + " `fk_id` int NOT NULL,"
            + " CONSTRAINT `pk_verdict_match` PRIMARY KEY (`id`),"
            + " CONSTRAINT `uq_verdict_match` UNIQUE (`code`),"
            + " CONSTRAINT `ck_verdict_match` CHECK (`code` >= 0),"
            + " INDEX `ix_verdict_match` (`code`),"
            + " CONSTRAINT `fk_verdict_match` FOREIGN KEY (`fk_id`)"
            + " REFERENCES `verdict_parent` (`id`));"
            + " CREATE TABLE `verdict_renamed` (`id` int NOT NULL, `other` int NOT NULL);"
            + " CREATE TABLE `verdict_missing` (`id` int NOT NULL);"
            + " CREATE TABLE `verdict_surplus` (`id` int NOT NULL, `code` int NOT NULL,"
            + " `extra` int NULL);"
            + " CREATE TABLE `snapshot_padding` (`id` int NOT NULL, `code` int NOT NULL);");

        var database = await ScalarStringAsync(connectionString, "SELECT DATABASE();");
        await using var context = CreateContext(connectionString);
        var operations = SnapshotVerdictOperations(context.Database.ProviderName!, schema: null);
        var qualified = SnapshotVerdictOperations(context.Database.ProviderName!, database);
        SafeMigrationObservedState[] expected =
        [
            SafeMigrationObservedState.Matching,
            SafeMigrationObservedState.Different,
            SafeMigrationObservedState.Different,
            SafeMigrationObservedState.Different,
            SafeMigrationObservedState.Matching,
            SafeMigrationObservedState.Missing,
        ];

        // Act
        var throughSnapshot = await ClassifySnapshotAsync(connectionString, operations);
        var throughLiveViews = await ClassifySnapshotAsync(connectionString, qualified);

        // Assert
        Assert.Equal(expected, throughSnapshot.States.Take(expected.Length));
        Assert.Equal(throughLiveViews.States, throughSnapshot.States);
        Assert.All(
            throughSnapshot.States.Skip(expected.Length),
            state => Assert.Equal(SafeMigrationObservedState.Matching, state));

        AssertSnapshotWasUsed(throughSnapshot);
        Assert.Equal(0, throughLiveViews.SnapshotCreates);
        Assert.Equal(0, throughLiveViews.SnapshotClassifiers);
    }

    /// <summary>A qualified neighbor does not disable the snapshot for eligible operations.</summary>
    [Fact]
    public async Task Analyzer_MixedQualifierWindowKeepsEligibleSnapshotAndQualifiedLiveVerdicts()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `snapshot_padding` (`id` int NOT NULL, `code` int NOT NULL);"
            + " CREATE TABLE `snapshot_neighbor` (`id` int NOT NULL, `other` int NOT NULL);");

        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            ExpectedSnapshotTable("snapshot_neighbor", database),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        AddSnapshotPadding(builder);

        // Act
        var result = await ClassifySnapshotAsync(
            connectionString,
            [.. builder.Operations.Cast<SafeMigrationOperation>()]);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, result.States[0]);
        Assert.All(result.States.Skip(1), state => Assert.Equal(SafeMigrationObservedState.Matching, state));
        AssertSnapshotWasUsed(result);
    }

    /// <summary>Creates matching and deliberately different catalog contracts with identical controls.</summary>
    private static SafeMigrationOperation[] SnapshotVerdictOperations(
        string provider,
        string? schema
    )
    {
        var builder = new MigrationBuilder(provider);
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "verdict_match",
                [
                    new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int"),
                    new ExpectedColumnDefinition("code", typeof(int), isNullable: false, storeType: "int"),
                    new ExpectedColumnDefinition("fk_id", typeof(int), isNullable: false, storeType: "int"),
                ],
                schema,
                primaryKey: new ExpectedPrimaryKeyDefinition("pk_verdict_match", "verdict_match", ["id"], schema),
                uniqueConstraints:
                [new ExpectedUniqueConstraintDefinition("uq_verdict_match", "verdict_match", ["code"], schema)],
                checkConstraints:
                [
                    ExpectedCheckConstraintDefinition.FromExpression(
                        "ck_verdict_match",
                        "verdict_match",
                        SqlColumnAndInt("code", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0),
                        schema),
                ],
                foreignKeys:
                [
                    new ExpectedForeignKeyDefinition(
                        "fk_verdict_match", "verdict_match", ["fk_id"], "verdict_parent", ["id"], schema, schema),
                ]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        foreach (var table in new[] { "verdict_renamed", "verdict_missing", "verdict_surplus" })
        {
            builder.EnsureTable(
                ExpectedSnapshotTable(table, schema),
                SafeMigrationTableMode.StrictDefinition,
                SafeMigrationPolicy.ThrowIfDifferent);
        }

        builder.CreateIndexIfNotExists("ix_verdict_match", "verdict_match", ["code"], schema: schema);
        builder.CreateIndexIfNotExists("ix_verdict_absent", "verdict_match", ["id", "code"], schema: schema);
        AddSnapshotPadding(builder, schema);

        return [.. builder.Operations.Cast<SafeMigrationOperation>()];
    }

    /// <summary>Adds real, unresolved operations so snapshot eligibility cannot disappear unnoticed.</summary>
    private static void AddSnapshotPadding(
        MigrationBuilder builder,
        string? schema = null,
        int count = 32
    )
    {
        // WHY: Repeating one immutable matching contract crosses the threshold without requiring
        // dozens of unrelated DDL fixtures; command counters independently prove the path taken.
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            builder.EnsureTable(
                ExpectedSnapshotTable("snapshot_padding", schema),
                SafeMigrationTableMode.StrictDefinition,
                SafeMigrationPolicy.ThrowIfDifferent);
        }
    }

    /// <summary>Checks engine-specific activation without depending on load-sensitive timings.</summary>
    private void AssertSnapshotWasUsed(
        SnapshotClassification result
    )
    {
        if (Fixture.IsMariaDb)
        {
            Assert.True(result.SnapshotCreates > 0);
            Assert.True(result.SnapshotClassifiers > 0);
        }
        else
        {
            Assert.Equal(0, result.SnapshotCreates);
            Assert.Equal(0, result.SnapshotClassifiers);
        }
    }

    /// <summary>Builds the two-column expected definition the snapshot cases share.</summary>
    /// <param name="table">The table name.</param>
    /// <param name="schema">The optional database qualifier.</param>
    /// <returns>The expected definition.</returns>
    private static ExpectedTableDefinition ExpectedSnapshotTable(
        string table,
        string? schema = null
    ) => new(
        table,
        [
            new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int"),
            new ExpectedColumnDefinition("code", typeof(int), isNullable: false, storeType: "int"),
        ],
        schema);

    /// <summary>Classifies operations and captures structural proof of the selected catalog path.</summary>
    /// <param name="connectionString">The fixture database.</param>
    /// <param name="operations">The operations to classify.</param>
    /// <returns>The observed state per operation.</returns>
    private async Task<SnapshotClassification> ClassifySnapshotAsync(
        string connectionString,
        SafeMigrationOperation[] operations
    )
    {
        await using var context = CreateContext(connectionString);
        await using var connection = new CatalogClassificationCountingConnection(new MySqlConnection(connectionString));
        context.Database.SetDbConnection(connection);
        var analysis = await context.GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(context, operations, CancellationToken.None);

        return new SnapshotClassification(
            [.. analysis.Select(static entry => entry.ObservedState)],
            connection.CatalogSnapshotCreateCount,
            connection.CatalogSnapshotClassificationStatementCount);
    }

    /// <summary>Retains only states and counters, never real catalog or row contents.</summary>
    private sealed record SnapshotClassification(
        SafeMigrationObservedState[] States,
        int SnapshotCreates,
        int SnapshotClassifiers);

    /// <summary>Reads a status counter from the connection the analyzer used.</summary>
    /// <param name="context">The context whose connection is inspected.</param>
    /// <param name="counter">The status counter name.</param>
    /// <returns>The counter value for that session.</returns>
    private static async Task<long> SessionCounterAsync(
        DbContext context,
        string counter
    )
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SHOW SESSION STATUS LIKE '{counter}';";
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        return await reader.ReadAsync(CancellationToken.None)
            ? long.Parse(reader.GetString(1), CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"The server reported no '{counter}' status counter.");
    }
}
