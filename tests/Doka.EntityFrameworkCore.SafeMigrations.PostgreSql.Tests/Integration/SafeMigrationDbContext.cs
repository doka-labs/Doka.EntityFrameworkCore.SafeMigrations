namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests.Integration;

internal sealed class SafeMigrationDbContext : DbContext
{
    private readonly string _connectionString;

    public SafeMigrationDbContext(
        string connectionString
    )
    {
        _connectionString = connectionString;
    }

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder
            .UseNpgsql(_connectionString)
            .UsePostgreSqlSafeMigrations();
    }
}
