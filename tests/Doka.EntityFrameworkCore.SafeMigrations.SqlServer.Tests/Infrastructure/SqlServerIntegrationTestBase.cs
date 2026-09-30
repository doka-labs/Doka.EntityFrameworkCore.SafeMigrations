namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Shares isolated-database operations without sharing mutable test state.
/// </summary>
public abstract class SqlServerIntegrationTestBase : IClassFixture<SqlServerContainerFixture>
{
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
