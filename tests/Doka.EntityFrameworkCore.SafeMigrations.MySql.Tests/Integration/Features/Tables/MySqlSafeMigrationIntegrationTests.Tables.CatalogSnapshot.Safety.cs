namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>
    /// Catalog TEXT columns remain snapshot-compatible regardless of the session temporary-table engine.
    /// </summary>
    [Theory]
    [InlineData("MEMORY")]
    [InlineData("InnoDB")]
    public async Task Analyzer_SnapshotUsesInnoDbIndependentlyOfSessionTemporaryEngine(
        string temporaryEngine
    )
    {
        if (!Fixture.IsMariaDb)
        {
            return;
        }

        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `snapshot_padding` (`id` int NOT NULL, `code` int NOT NULL);");

        // WHY: Session settings belong only to this test connection and must not leak
        // into another test through a pooled session after the context is disposed.
        var settings = new MySqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
        };

        await using var context = CreateContext(settings.ConnectionString);
        await using var connection = new CatalogClassificationCountingConnection(
            new MySqlConnection(settings.ConnectionString));

        context.Database.SetDbConnection(connection);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await ExecuteSnapshotCommandAsync(
            connection,
            $"SET SESSION default_tmp_storage_engine = '{temporaryEngine}';");

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AddSnapshotPadding(builder);
        var operations = builder.Operations.Cast<SafeMigrationOperation>().ToArray();

        // Act
        var analysis = await context.GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(context, operations, CancellationToken.None);

        // Assert
        Assert.Equal(32, analysis.Count);
        Assert.All(analysis, entry => Assert.Equal(SafeMigrationObservedState.Matching, entry.ObservedState));
        Assert.True(connection.CatalogSnapshotCreateCount > 0);
        Assert.True(connection.CatalogSnapshotClassificationStatementCount > 0);
    }

    /// <summary>Catalog-looking CHECK literals retain their value in both positive and negative row probes.</summary>
    [Theory]
    [InlineData("INFORMATION_SCHEMA.COLUMNS", SafeMigrationObservedState.Missing)]
    [InlineData("different", SafeMigrationObservedState.DataBlocked)]
    [InlineData("`__doka_sm_cat_columns`", SafeMigrationObservedState.DataBlocked)]
    public async Task Analyzer_SnapshotPreservesCatalogLookingCheckLiterals(
        string rowValue,
        SafeMigrationObservedState expected
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `snapshot_check` (`value` varchar(64) NOT NULL);"
            + $" INSERT INTO `snapshot_check` (`value`) VALUES ('{rowValue}');"
            + " CREATE TABLE `snapshot_padding` (`id` int NOT NULL, `code` int NOT NULL);");

        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        await using var context = CreateContext(connectionString);
        var operation = SnapshotCheckOperations(context.Database.ProviderName!, schema: null);
        var qualified = SnapshotCheckOperations(context.Database.ProviderName!, database);

        // Act
        var snapshot = await ClassifySnapshotAsync(connectionString, operation);
        var live = await ClassifySnapshotAsync(connectionString, qualified);

        // Assert
        Assert.Equal(expected, snapshot.States[0]);
        Assert.Equal(live.States, snapshot.States);
        AssertSnapshotWasUsed(snapshot);
        Assert.Equal(0, live.SnapshotCreates);
        Assert.Equal(0, live.SnapshotClassifiers);
    }

    /// <summary>An incoming FK from another database remains visible to unqualified managed deletes.</summary>
    [Theory]
    [InlineData(false, SafeMigrationObservedState.TransitionReady)]
    [InlineData(true, SafeMigrationObservedState.Unsupported)]
    public async Task Analyzer_SnapshotPreservesCrossDatabaseIncomingDependencies(
        bool hasIncomingDependency,
        SafeMigrationObservedState expected
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var childConnectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `snapshot_parent` (`id` int NOT NULL PRIMARY KEY);"
            + " INSERT INTO `snapshot_parent` (`id`) VALUES (1);"
            + " CREATE TABLE `snapshot_padding` (`id` int NOT NULL, `code` int NOT NULL);");

        if (hasIncomingDependency)
        {
            await ExecuteSqlAsync(
                childConnectionString,
                "CREATE TABLE `snapshot_child` (`id` int NOT NULL PRIMARY KEY, `parent_id` int NOT NULL,"
                + " CONSTRAINT `fk_snapshot_child_parent` FOREIGN KEY (`parent_id`)"
                + $" REFERENCES `{database}`.`snapshot_parent` (`id`));"
                + " INSERT INTO `snapshot_child` (`id`, `parent_id`) VALUES (1, 1);");
        }

        await using var context = CreateContext(connectionString);
        var operation = SnapshotDeleteOperations(context.Database.ProviderName!, schema: null);
        var qualified = SnapshotDeleteOperations(context.Database.ProviderName!, database);

        try
        {
            // Act
            var snapshot = await ClassifySnapshotAsync(connectionString, operation);
            var live = await ClassifySnapshotAsync(connectionString, qualified);

            // Assert
            Assert.Equal(expected, snapshot.States[0]);
            Assert.Equal(live.States, snapshot.States);
            AssertSnapshotWasUsed(snapshot);
            Assert.Equal(0, live.SnapshotCreates);
            Assert.Equal(0, live.SnapshotClassifiers);
            Assert.Equal(1, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `snapshot_parent`;"));
        }
        finally
        {
            // WHY: The fixture disposes databases independently; release the cross-database
            // dependency first so cleanup does not depend on their enumeration order.
            await ExecuteSqlAsync(childConnectionString, "DROP TABLE IF EXISTS `snapshot_child`;");
        }
    }

    /// <summary>Lacking an optimization-only grant does not prevent otherwise valid read-only analysis.</summary>
    [Fact]
    public async Task Analyzer_SnapshotPermissionDenialFallsBackToLiveCatalog()
    {
        // Arrange
        var rootConnectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            rootConnectionString,
            "CREATE TABLE `snapshot_padding` (`id` int NOT NULL, `code` int NOT NULL);");

        var connectionString = await Fixture.CreateLeastPrivilegeConnectionStringAsync(
            rootConnectionString,
            CancellationToken.None);

        var settings = new MySqlConnectionStringBuilder(connectionString);
        await ExecuteSqlAsync(
            rootConnectionString,
            $"REVOKE CREATE TEMPORARY TABLES ON `{settings.Database}`.* FROM `{settings.UserID}`@'%';");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        // WHY: Crossing the 512-operation capture boundary proves a denied optimization
        // is not retried for every window of the same otherwise valid analysis call.
        AddSnapshotPadding(builder, count: 600);

        // Act
        var result = await ClassifySnapshotAsync(
            connectionString,
            [.. builder.Operations.Cast<SafeMigrationOperation>()]);

        // Assert
        Assert.All(result.States, state => Assert.Equal(SafeMigrationObservedState.Matching, state));
        Assert.Equal(600, result.States.Length);
        Assert.Equal(0, result.SnapshotClassifiers);
        if (Fixture.IsMariaDb)
        {
            Assert.Equal(1, result.SnapshotCreates);
        }
        else
        {
            Assert.Equal(0, result.SnapshotCreates);
        }
    }

    /// <summary>Read-only transaction ownership survives the snapshot attempt and live fallback.</summary>
    [Fact]
    public async Task Analyzer_SnapshotPreservesCallerReadOnlyTransaction()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `snapshot_padding` (`id` int NOT NULL, `code` int NOT NULL);");

        await using var context = CreateContext(connectionString);
        await using var connection = new CatalogClassificationCountingConnection(new MySqlConnection(connectionString));
        context.Database.SetDbConnection(connection);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await ExecuteSnapshotCommandAsync(connection, "START TRANSACTION READ ONLY;");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AddSnapshotPadding(builder);
        var operations = builder.Operations.Cast<SafeMigrationOperation>().ToArray();

        try
        {
            // Act
            var analysis = await context.GetService<ISafeMigrationProviderAnalyzer>()
                .AnalyzeAsync(context, operations, CancellationToken.None);

            var mutationError = await Record.ExceptionAsync(() => ExecuteSnapshotCommandAsync(
                connection,
                "INSERT INTO `snapshot_padding` (`id`, `code`) VALUES (1, 1);"));

            // Assert
            Assert.All(analysis, entry => Assert.Equal(SafeMigrationObservedState.Matching, entry.ObservedState));
            Assert.Equal(1792, Assert.IsType<MySqlException>(mutationError).Number);
            Assert.Equal(0, connection.CatalogSnapshotClassificationStatementCount);
            Assert.Equal(0, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `snapshot_padding`;"));
        }
        finally
        {
            await ExecuteSnapshotCommandAsync(connection, "ROLLBACK;");
        }
    }

    /// <summary>A reset-disabled pool never receives snapshot-owned session state.</summary>
    [Fact]
    public async Task Analyzer_ResetDisabledPooledConnectionUsesLiveCatalogWithoutSnapshotDdl()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `snapshot_padding` (`id` int NOT NULL, `code` int NOT NULL);");

        var settings = new MySqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            ConnectionReset = false,
        };

        await using var context = CreateContext(settings.ConnectionString);
        await using var physicalConnection = new MySqlConnection(settings.ConnectionString);
        await using var connection = new CatalogClassificationCountingConnection(physicalConnection);
        context.Database.SetDbConnection(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AddSnapshotPadding(builder);
        var operations = builder.Operations.Cast<SafeMigrationOperation>().ToArray();

        try
        {
            // Act
            var analysis = await context.GetService<ISafeMigrationProviderAnalyzer>()
                .AnalyzeAsync(context, operations, CancellationToken.None);

            // Assert
            Assert.Equal(32, analysis.Count);
            Assert.All(analysis, entry => Assert.Equal(SafeMigrationObservedState.Matching, entry.ObservedState));
            Assert.True(connection.ClassificationStatementCount > 0);
            Assert.Equal(0, connection.CatalogSnapshotCreateCount);
            Assert.Equal(0, connection.CatalogSnapshotClassificationStatementCount);
        }
        finally
        {
            // WHY: The dedicated test database owns this pool; release its idle session
            // before the fixture tears down databases and the server container.
            MySqlConnection.ClearPool(physicalConnection);
        }
    }

    /// <summary>Reset-disabled nonpooled sessions remain eligible because close destroys their state.</summary>
    [Fact]
    public async Task Analyzer_ResetDisabledNonpooledConnectionStillUsesSnapshot()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `snapshot_padding` (`id` int NOT NULL, `code` int NOT NULL);");

        var settings = new MySqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            ConnectionReset = false,
        };

        await using var context = CreateContext(settings.ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AddSnapshotPadding(builder);

        // Act
        var result = await ClassifySnapshotAsync(
            settings.ConnectionString,
            [.. builder.Operations.Cast<SafeMigrationOperation>()]);

        // Assert
        Assert.Equal(32, result.States.Length);
        Assert.All(result.States, state => Assert.Equal(SafeMigrationObservedState.Matching, state));
        AssertSnapshotWasUsed(result);
    }

    /// <summary>Builds a supported CHECK whose string literal resembles an actual catalog relation.</summary>
    private static SafeMigrationOperation[] SnapshotCheckOperations(
        string provider,
        string? schema
    )
    {
        var builder = new MigrationBuilder(provider);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_snapshot_check",
                "snapshot_check",
                SqlBinary(
                    SqlColumn("value"),
                    SafeMigrationSqlBinaryOperator.Equal,
                    SafeMigrationSql.Literal("INFORMATION_SCHEMA.COLUMNS")),
                schema),
            SafeMigrationPolicy.ThrowIfDifferent);

        AddSnapshotPadding(builder, schema);

        return [.. builder.Operations.Cast<SafeMigrationOperation>()];
    }

    /// <summary>Builds an unmodeled-dependency delete and matching operations that activate the snapshot.</summary>
    private static SafeMigrationOperation[] SnapshotDeleteOperations(
        string provider,
        string? schema
    )
    {
        var builder = new MigrationBuilder(provider);
        builder.DeleteModelManagedDataFromModel(
            "snapshot_parent",
            ["id"],
            ["int"],
            new object?[,] { { 1 } },
            ["id"],
            ["int"],
            new object?[,] { { 1 } },
            schema);

        AddSnapshotPadding(builder, schema);

        return [.. builder.Operations.Cast<SafeMigrationOperation>()];
    }

    /// <summary>Executes test-owned transaction commands on the exact analyzer connection.</summary>
    private static async Task ExecuteSnapshotCommandAsync(
        DbConnection connection,
        string sql
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
