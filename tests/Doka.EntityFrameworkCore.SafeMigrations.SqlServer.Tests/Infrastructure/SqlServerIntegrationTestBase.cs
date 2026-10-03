namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Shares isolated-database operations without sharing mutable test state.
/// </summary>
/// <remarks>
/// Derived classes join <see cref="SqlServerSharedContainer" /> so the whole
/// assembly shares one container instead of starting one per test class.
/// </remarks>
public abstract class SqlServerIntegrationTestBase : IAsyncLifetime
{
    private readonly List<string> _ownedDatabases = [];

    /// <summary>
    /// Creates the base with the SQL Server container fixture.
    /// </summary>
    protected SqlServerIntegrationTestBase(
        SqlServerContainerFixture fixture
    ) => Fixture = fixture;

    /// <summary>
    /// Gets the fixture used to create a database per test.
    /// </summary>
    protected SqlServerContainerFixture Fixture { get; }

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Drops every database this test created.
    /// </summary>
    /// <remarks>
    /// WHY: xUnit creates one test-class instance per test, so releasing here bounds the
    /// number of attached databases to the tests running concurrently instead of growing
    /// it across the whole session.
    /// </remarks>
    public async Task DisposeAsync()
    {
        foreach (var connectionString in _ownedDatabases)
        {
            await Fixture.ReleaseDatabaseAsync(connectionString, CancellationToken.None);
        }

        _ownedDatabases.Clear();
    }

    /// <summary>
    /// Creates an isolated database that is dropped when the test finishes.
    /// </summary>
    /// <param name="cancellationToken">The token cancelling database creation.</param>
    /// <returns>The pooled connection string scoped to the new database.</returns>
    protected async Task<string> CreateDatabaseAsync(
        CancellationToken cancellationToken = default
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(cancellationToken);

        _ownedDatabases.Add(connectionString);

        return connectionString;
    }

    /// <summary>
    /// Creates a context bound to a test database.
    /// </summary>
    protected static SafeMigrationDbContext CreateContext(
        string connectionString
    )
        => new(connectionString);

    /// <summary>
    /// Generates and executes migration commands individually for direct provider contracts.
    /// </summary>
    protected static async Task ExecuteOperationsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        CancellationToken cancellationToken = default
    )
    {
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var commands = generator.Generate(operations, context.Model);

        // WHY: SQL Server requires separate command execution for DDL that is not valid inside another batch.
        foreach (var command in commands)
        {
            await context.Database.ExecuteSqlRawAsync(command.CommandText, cancellationToken);
        }
    }

    /// <summary>
    /// Executes setup SQL outside the SafeMigrations generator.
    /// </summary>
    protected static async Task ExecuteSqlAsync(
        string connectionString,
        string sql,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Reads a scalar integer from the test database.
    /// </summary>
    protected static async Task<int> ScalarIntAsync(
        string connectionString,
        string sql,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken);

        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }
}
