namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed partial class SqliteSafeMigrationCatalogIntegrationTests
{
    /// <summary>Alias windows share one catalog without hiding structurally different physical indexes.</summary>
    /// <param name="aliasCount">The number of semantically matching physical indexes spanning the bounded windows.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(1025)]
    public async Task UnexpectedObjectInventory_AliasWindowsCaptureCatalogOnce(int aliasCount)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        var sql = new StringBuilder("CREATE TABLE inventory_windows (Code TEXT, Legacy TEXT); ");
        for (var index = 0; index < aliasCount; index++)
        {
            sql.Append("CREATE INDEX physical_alias_")
                .Append(index.ToString(CultureInfo.InvariantCulture))
                .Append(" ON inventory_windows (Code); ");
        }

        sql.Append("CREATE INDEX physical_drift ON inventory_windows (Legacy);");
        await ExecuteSqlAsync(connection, sql.ToString());
        await using var context = CreateContext(connection);
        var operations = CreateInventoryOperations(context, "inventory_windows");
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var counter = new CatalogReadCounter(connection);

        // Act
        var inventory = await analyzer.FindUnexpectedObjectsAsync(context, operations, CancellationToken.None);

        // Assert
        var finding = Assert.Single(inventory);

        Assert.Equal(SafeMigrationDatabaseObjectKind.Index, finding.ObjectKind);
        Assert.Equal("physical_drift", finding.Name);
        Assert.Equal("inventory_windows", finding.Table);
        Assert.Equal(1, counter.Count);
    }

    /// <summary>An independent inventory invocation must observe DDL performed after the earlier snapshot.</summary>
    [Fact]
    public async Task UnexpectedObjectInventory_RefreshesCatalogBetweenInvocations()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE inventory_fresh (Code TEXT, Legacy TEXT); "
            + "CREATE INDEX physical_index ON inventory_fresh (Code);");
        await using var context = CreateContext(connection);
        var operations = CreateInventoryOperations(context, "inventory_fresh");
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var counter = new CatalogReadCounter(connection);

        // Act
        var before = await analyzer.FindUnexpectedObjectsAsync(context, operations, CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            "DROP INDEX physical_index; CREATE INDEX physical_index ON inventory_fresh (Legacy);");
        var after = await analyzer.FindUnexpectedObjectsAsync(context, operations, CancellationToken.None);

        // Assert
        Assert.Empty(before);
        Assert.Equal("physical_index", Assert.Single(after).Name);
        Assert.Equal(2, counter.Count);
    }

    /// <summary>Rolled-back catalog changes cannot leak from one caller-owned transaction into the next.</summary>
    [Fact]
    public async Task UnexpectedObjectInventory_RefreshesCatalogAfterTransactionRollback()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE inventory_transactions (Code TEXT, Legacy TEXT);");
        await using var context = CreateContext(connection);
        var operations = CreateInventoryOperations(context, "inventory_transactions");
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var counter = new CatalogReadCounter(connection);
        IReadOnlyList<SafeMigrationUnexpectedObject> insideTransaction;

        // Act
        await using (var transaction = await connection.BeginTransactionAsync(CancellationToken.None))
        {
            await using var contextTransaction = context.Database.UseTransaction(transaction);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "CREATE INDEX transaction_index ON inventory_transactions (Legacy);";
            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
            insideTransaction = await analyzer.FindUnexpectedObjectsAsync(context, operations, CancellationToken.None);
            await transaction.RollbackAsync(CancellationToken.None);
        }

        var afterRollback = await analyzer.FindUnexpectedObjectsAsync(context, operations, CancellationToken.None);

        // Assert
        Assert.Equal("transaction_index", Assert.Single(insideTransaction).Name);
        Assert.Empty(afterRollback);
        Assert.Equal(2, counter.Count);
    }

    /// <summary>Sharing the stateless analyzer service cannot reuse another connection's catalog.</summary>
    [Fact]
    public async Task UnexpectedObjectInventory_RefreshesCatalogForEachConnection()
    {
        // Arrange
        await using var firstConnection = await OpenConnectionAsync();
        await using var secondConnection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            firstConnection,
            "CREATE TABLE inventory_connections (Code TEXT, Legacy TEXT); "
            + "CREATE INDEX physical_index ON inventory_connections (Code);");
        await ExecuteSqlAsync(
            secondConnection,
            "CREATE TABLE inventory_connections (Code TEXT, Legacy TEXT); "
            + "CREATE INDEX physical_index ON inventory_connections (Legacy);");
        await using var firstContext = CreateContext(firstConnection);
        await using var secondContext = CreateContext(secondConnection);
        var operations = CreateInventoryOperations(firstContext, "inventory_connections");
        var analyzer = firstContext.GetService<ISafeMigrationProviderAnalyzer>();
        var firstCounter = new CatalogReadCounter(firstConnection);
        var secondCounter = new CatalogReadCounter(secondConnection);

        // Act
        var first = await analyzer.FindUnexpectedObjectsAsync(firstContext, operations, CancellationToken.None);
        var second = await analyzer.FindUnexpectedObjectsAsync(secondContext, operations, CancellationToken.None);

        // Assert
        Assert.Empty(first);
        Assert.Equal("physical_index", Assert.Single(second).Name);
        Assert.Equal(1, firstCounter.Count);
        Assert.Equal(1, secondCounter.Count);
    }

    /// <summary>Snapshot reuse in inventory does not retain row-safety proofs between independent analyses.</summary>
    [Fact]
    public async Task Analyzer_RefreshesDataProofsBetweenInvocations()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE inventory_data (Code TEXT NOT NULL); INSERT INTO inventory_data VALUES ('value');");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("expected_unique", "inventory_data", ["Code"], unique: true);
        var operations = builder.Operations.OfType<SafeMigrationOperation>().ToArray();
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var counter = new CatalogReadCounter(connection);

        // Act
        var before = await analyzer.AnalyzeAsync(context, operations, CancellationToken.None);
        await ExecuteSqlAsync(connection, "INSERT INTO inventory_data VALUES ('value');");
        var after = await analyzer.AnalyzeAsync(context, operations, CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, Assert.Single(before).ObservedState);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, Assert.Single(after).ObservedState);
        Assert.Equal(2, counter.Count);
    }

    /// <summary>Cancellation still stops alias-window evaluation without another catalog capture.</summary>
    [Fact]
    public async Task UnexpectedObjectInventory_CancellationDuringCaptureRemainsObservable()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE inventory_cancelled (Code TEXT, Legacy TEXT); "
            + "CREATE INDEX physical_index ON inventory_cancelled (Code);");
        await using var context = CreateContext(connection);
        var operations = CreateInventoryOperations(context, "inventory_cancelled");
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        using var cancellation = new CancellationTokenSource();
        var counter = new CatalogReadCounter(connection, cancellation.Cancel);

        // Act
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => analyzer.FindUnexpectedObjectsAsync(context, operations, cancellation.Token));

        // Assert
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, counter.Count);
    }

    /// <summary>Creates one expected index shape with both physical columns in the inventory contract.</summary>
    private static List<MigrationOperation> CreateInventoryOperations(
        DbContext context,
        string tableName
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            tableName,
            table => new
            {
                Code = table.Column<string>(type: "TEXT", nullable: true),
                Legacy = table.Column<string>(type: "TEXT", nullable: true),
            },
            mode: SafeMigrationTableMode.ConvergenceContainer);
        builder.CreateIndexIfNotExists("expected_index", tableName, ["Code"]);

        return builder.Operations;
    }

    /// <summary>Counts real version queries, executed once at the start of every catalog capture.</summary>
    private sealed class CatalogReadCounter
    {
        /// <summary>Observes a real catalog command while returning the unchanged native engine version.</summary>
        public CatalogReadCounter(
            SqliteConnection connection,
            Action? onRead = null
        )
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sqlite_version();";
            var version = (string)(command.ExecuteScalar()
                ?? throw new InvalidOperationException("The SQLite engine version was not returned."));

            // WHY: Inventory uses direct ADO.NET commands, so EF interceptors
            // do not observe it. Replacing only this scalar function counts
            // captures without retaining SQL, changing catalog values or
            // adding a production observability seam.
            connection.CreateFunction(
                "sqlite_version",
                () =>
                {
                    Count++;
                    onRead?.Invoke();

                    return version;
                });
        }

        /// <summary>Gets the number of complete catalog captures started on this connection.</summary>
        public int Count { get; private set; }
    }
}
