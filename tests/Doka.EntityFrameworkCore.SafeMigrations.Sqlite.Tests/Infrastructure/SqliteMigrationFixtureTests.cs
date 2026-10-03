namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Checks every declared SQLite fixture identity without opening a connection.</summary>
public sealed class SqliteMigrationFixtureTests
{
    /// <summary>Fixture IDs satisfy the official EF contract with either migration-service registration.</summary>
    /// <param name="registerSafeMigrations">Whether the SafeMigrations services replace the EF services.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeclaredFixtureIdsUseEfTimestampFormat(
        bool registerSafeMigrations
    )
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations);
        var idGenerator = context.GetService<IMigrationsIdGenerator>();

        // Act
        var ids = typeof(SqliteMigrationFixtureTests).Assembly.GetTypes()
            .Where(static type => typeof(Migration).IsAssignableFrom(type))
            .SelectMany(static type => type.GetCustomAttributes(typeof(MigrationAttribute), inherit: false))
            .Cast<MigrationAttribute>()
            .Select(static attribute => attribute.Id)
            .ToArray();

        // Assert
        Assert.NotEmpty(ids);
        Assert.All(ids, id => Assert.True(idGenerator.IsValidId(id), $"Invalid EF fixture migration ID: {id}"));
        Assert.Equal(ConnectionState.Closed, connection.State);
    }
}
