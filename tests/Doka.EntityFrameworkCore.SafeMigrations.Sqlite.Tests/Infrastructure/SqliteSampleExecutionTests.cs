namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Executes the actual linked sample scenario rather than a separately maintained migration copy.</summary>
public sealed class SqliteSampleExecutionTests : IDisposable
{
    private readonly ServiceProvider _services;

    /// <summary>Owns this case's provider without adding another shape to EF's global provider cache.</summary>
    public SqliteSampleExecutionTests()
    {
        var registrations = new ServiceCollection();
        _ = registrations
            .AddEntityFrameworkSqlite()
            .AddSqliteSafeMigrations();

        _services = registrations.BuildServiceProvider(validateScopes: true);
    }

    /// <inheritdoc />
    public void Dispose() => _services.Dispose();

    /// <summary>The callable entrypoint publishes the exact ordered streams used by the executable summary.</summary>
    [Fact]
    public void Run_ValidWriter_ProducesExactStreamsAndOutput()
    {
        // Arrange
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        SafeMigrationOperationKind[] initialKinds =
        [
            SafeMigrationOperationKind.EnsureTable,
            SafeMigrationOperationKind.EnsureColumn,
            SafeMigrationOperationKind.EnsureColumn,
            SafeMigrationOperationKind.EnsureColumn,
            SafeMigrationOperationKind.EnsurePrimaryKey,
            SafeMigrationOperationKind.EnsureUniqueConstraint,
            SafeMigrationOperationKind.EnsureTable,
            SafeMigrationOperationKind.EnsureColumn,
            SafeMigrationOperationKind.EnsureColumn,
            SafeMigrationOperationKind.EnsureColumn,
            SafeMigrationOperationKind.EnsurePrimaryKey,
            SafeMigrationOperationKind.EnsureForeignKey,
            SafeMigrationOperationKind.EnsureCheckConstraint,
            SafeMigrationOperationKind.EnsureIndex,
        ];

        SafeMigrationOperationKind[] maintenanceKinds =
        [
            SafeMigrationOperationKind.EnsureSchema,
            SafeMigrationOperationKind.RenameTable,
            SafeMigrationOperationKind.RenameColumn,
            SafeMigrationOperationKind.RenameIndex,
            SafeMigrationOperationKind.AlterColumn,
        ];

        // Act
        var scenario = SampleMigrationUsage.Run(output);

        // Assert

        Assert.Equal(initialKinds,
            scenario.InitialOperations.Select(operation =>
                Assert.IsType<SafeMigrationOperation>(operation)
                    .Intent.Kind));

        Assert.Equal(maintenanceKinds,
            scenario.MaintenanceOperations.Select(operation =>
                Assert.IsType<SafeMigrationOperation>(operation)
                    .Intent.Kind));

        Assert.Equal(
            [
                "Doka.EntityFrameworkCore.SafeMigrations sample",
                "Generated sample migration operations without executing SQL.",
                "Initial-migration example operation count: 14",
                "Maintenance example operation count: 5",
                "See SampleMigrationUsage, InitialSafeMigrationExample, and MaintenanceSafeMigrationExample "
                + "for the recommended migration patterns.",
            ],
            output
                .ToString()
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>A missing output owner fails explicitly without a hidden console dependency.</summary>
    [Fact]
    public void Run_NullWriter_RejectsTheMissingOwner()
    {
        // Arrange
        TextWriter output = null!;

        // Act
        var exception = Record.Exception(() => SampleMigrationUsage.Run(output));

        // Assert
        var argument = Assert.IsType<ArgumentNullException>(exception);

        Assert.Equal("output", argument.ParamName);
    }

    /// <summary>Generated native SQL establishes the intended schema and replay leaves it byte-identical.</summary>
    [Fact]
    public async Task InitialScenario_ApplyAndReplay_PreserveCanonicalSchema()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(CancellationToken.None);
        await using var context = CreateContext(connection);
        var scenario = SampleMigrationUsage.Run(TextWriter.Null);

        // Act
        await ExecuteAsync(context, scenario.InitialOperations, CancellationToken.None);
        var initial = await ReadSchemaAsync(connection, CancellationToken.None);
        await ExecuteAsync(context, scenario.InitialOperations, CancellationToken.None);
        var replay = await ReadSchemaAsync(connection, CancellationToken.None);

        // Assert
        Assert.NotEmpty(initial);
        Assert.Equal(initial, replay);
        Assert.Contains("pk_users", initial, StringComparison.Ordinal);
        Assert.Contains("ux_users_email", initial, StringComparison.Ordinal);
        Assert.Contains("pk_orders", initial, StringComparison.Ordinal);
        Assert.Contains("fk_orders_users_user_id", initial, StringComparison.Ordinal);
        Assert.Contains("ck_orders_total_non_negative", initial, StringComparison.Ordinal);
        Assert.Contains("ix_orders_user_id", initial, StringComparison.Ordinal);
        Assert.Contains("ON DELETE CASCADE", initial, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The sample's actual foreign-key action removes dependent orders rather than orphaning them.</summary>
    [Fact]
    public async Task InitialScenario_DeletePrincipal_CascadesToOrders()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(CancellationToken.None);
        await using var context = CreateContext(connection);
        var scenario = SampleMigrationUsage.Run(TextWriter.Null);
        await ExecuteAsync(context, scenario.InitialOperations, CancellationToken.None);
        _ = await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO users (id, email) VALUES ('user-1', 'sample@example.invalid');"
            + "INSERT INTO orders (id, user_id, total) VALUES ('order-1', 'user-1', 12);",
            CancellationToken.None);

        // Act
        _ = await context.Database.ExecuteSqlRawAsync("DELETE FROM users WHERE id = 'user-1';", CancellationToken.None);
        var remainingOrders = await CountAsync(connection, "SELECT COUNT(*) FROM orders;", CancellationToken.None);

        // Assert
        Assert.Equal(0L, remainingOrders);
    }

    /// <summary>The persisted CHECK and FK independently reject invalid rows without changing retained data.</summary>
    /// <param name="user">The existing or missing principal identity.</param>
    /// <param name="total">The nonnegative or invalid negative order amount.</param>
    /// <param name="extendedCode">The native constraint category proving which invariant rejected the row.</param>
    [Theory]
    [InlineData("user-1", -1, 275)]
    [InlineData("missing-user", 12, 787)]
    public async Task InitialScenario_InvalidOrder_RejectsWithoutChangingRows(
        string user,
        int total,
        int extendedCode
    )
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(CancellationToken.None);
        await using var context = CreateContext(connection);
        var scenario = SampleMigrationUsage.Run(TextWriter.Null);
        await ExecuteAsync(context, scenario.InitialOperations, CancellationToken.None);
        _ = await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO users (id, email) VALUES ('user-1', 'sample@example.invalid');"
            + "INSERT INTO orders (id, user_id, total) VALUES ('order-1', 'user-1', 12);",
            CancellationToken.None);

        // Act
        var exception = await Record.ExceptionAsync(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO orders (id, user_id, total) VALUES ('rejected-order', {user}, {total});",
            CancellationToken.None));

        var retainedRows = await CountAsync(connection, "SELECT COUNT(*) FROM orders;", CancellationToken.None);

        // Assert
        var native = Assert.IsType<SqliteException>(exception);

        Assert.Equal(19, native.SqliteErrorCode);
        Assert.Equal(extendedCode, native.SqliteExtendedErrorCode);
        Assert.Equal(1L, retainedRows);
    }

    /// <summary>Granular convergence refuses a missing required email on populated legacy data atomically.</summary>
    [Fact]
    public async Task InitialScenario_PopulatedPartialTable_RejectsWithoutChangingSchemaOrRows()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(CancellationToken.None);
        await using var context = CreateContext(connection);
        var scenario = SampleMigrationUsage.Run(TextWriter.Null);
        _ = await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE users (id TEXT NOT NULL CONSTRAINT pk_users PRIMARY KEY);"
            + "INSERT INTO users (id) VALUES ('retained-user');",
            CancellationToken.None);

        var before = await ReadSchemaAsync(connection, CancellationToken.None);

        // Act
        var exception = await Record.ExceptionAsync(() =>
            ExecuteAsync(context, scenario.InitialOperations, CancellationToken.None));

        var after = await ReadSchemaAsync(connection, CancellationToken.None);
        var retainedRows = await CountAsync(connection, "SELECT COUNT(*) FROM users;", CancellationToken.None);

        // Assert
        var blocked = Assert.IsType<InvalidOperationException>(exception);

        Assert.Contains("data_blocked", blocked.Message, StringComparison.Ordinal);
        Assert.Contains("email", blocked.Message, StringComparison.Ordinal);
        Assert.Equal(before, after);
        Assert.Equal(1L, retainedRows);
    }

    /// <summary>Uses the sample's complete canonical model with this test's owned native connection.</summary>
    private SampleDbContext CreateContext(
        SqliteConnection connection
    )
    {
        var options = new DbContextOptionsBuilder<SampleDbContext>();
        _ = options
            .UseSqlite(connection)
            .UseSqliteSafeMigrations()
            .UseInternalServiceProvider(_services);

        return new SampleDbContext(options.Options);
    }

    /// <summary>Runs the sample's actual operations through the installed SQLite migration commands.</summary>
    private static async Task ExecuteAsync(
        SampleDbContext context,
        IReadOnlyList<MigrationOperation> operations,
        CancellationToken cancellationToken
    )
    {
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var commands = generator.Generate(operations,
            context.GetService<IDesignTimeModel>()
                .Model);

        var connection = context.GetService<IRelationalConnection>();

        // WHY: Execute the stable generated command snapshot in its actual migration order.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < commands.Count; index++)
        {
            _ = await commands[index]
                .ExecuteNonQueryAsync(connection, cancellationToken: cancellationToken);
        }
    }

    /// <summary>Reads native schema text in stable object order for replay and rollback comparison.</summary>
    private static async Task<string> ReadSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY type, name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new StringBuilder();
        while (await reader.ReadAsync(cancellationToken))
        {
            _ = result
                .Append(reader.GetString(0))
                .Append(':')
                .Append(reader.GetString(1))
                .AppendLine();
        }

        return result.ToString();
    }

    /// <summary>Reads one scalar from the same isolated connection without a separate observer timeout.</summary>
    private static async Task<long> CountAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken);

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }
}
