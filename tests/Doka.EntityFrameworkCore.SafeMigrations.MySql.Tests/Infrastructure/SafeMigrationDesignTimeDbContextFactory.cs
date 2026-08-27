namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class SafeMigrationDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SafeMigrationDbContext>
{
    public SafeMigrationDbContext CreateDbContext(
        string[] args
    )
    {
        ArgumentNullException.ThrowIfNull(args);

        return new SafeMigrationDbContext(
            MySqlDesignTimeContextConfiguration.ConnectionString(),
            MySqlDesignTimeContextConfiguration.ServerVersion());
    }
}
