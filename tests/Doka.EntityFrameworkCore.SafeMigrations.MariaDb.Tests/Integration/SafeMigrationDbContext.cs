namespace Doka.EntityFrameworkCore.SafeMigrations.MariaDb.Tests.Integration;

internal sealed class SafeMigrationDbContext : DbContext
{
    private readonly string _connectionString;

    public SafeMigrationDbContext(string connectionString)
    {
        _connectionString = connectionString;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder
            .UseMySql(
                _connectionString,
                ServerVersion.Create(new Version(11, 8, 0), ServerType.MariaDb))
            .UseMariaDbSafeMigrations();
    }
}
