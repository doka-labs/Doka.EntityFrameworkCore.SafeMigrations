namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Checks every declared PostgreSQL fixture identity without opening a connection.</summary>
public sealed class PostgreSqlMigrationFixtureTests
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
        using var context = new SafeMigrationDbContext(
            "Host=127.0.0.1;Database=fixture_identity;", registerSafeMigrations);

        var idGenerator = context.GetService<IMigrationsIdGenerator>();

        // Act
        var ids = typeof(PostgreSqlMigrationFixtureTests).Assembly.GetTypes()
            .Where(static type => typeof(Migration).IsAssignableFrom(type))
            .SelectMany(static type => type.GetCustomAttributes(typeof(MigrationAttribute), inherit: false))
            .Cast<MigrationAttribute>()
            .Select(static attribute => attribute.Id)
            .ToArray();

        // Assert
        Assert.NotEmpty(ids);
        Assert.All(ids, id => Assert.True(idGenerator.IsValidId(id), $"Invalid EF fixture migration ID: {id}"));
    }
}
