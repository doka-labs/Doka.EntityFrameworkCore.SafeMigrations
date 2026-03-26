using Doka.EntityFrameworkCore.SafeMigrations.MariaDb;

namespace Doka.EntityFrameworkCore.SafeMigrations.Tests.Unit;

internal sealed class MariaDbTestContext : DbContext
{
    // No real connection is made — this context exists solely to configure the provider for IMigrationsSqlGenerator resolution.
    private const string _testConnectionStringPlaceholder = "Server=localhost;Database=test;User=test;Password=;";

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder
            .UseMySql(
                _testConnectionStringPlaceholder,
                ServerVersion.Create(new Version(11, 8, 0), ServerType.MariaDb))
            .UseMariaDbSafeMigrations();
    }
}
