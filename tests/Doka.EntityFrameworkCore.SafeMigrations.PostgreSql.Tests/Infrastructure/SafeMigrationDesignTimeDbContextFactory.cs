namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed class SafeMigrationDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SafeMigrationDbContext>
{
    public SafeMigrationDbContext CreateDbContext(
        string[] args
    )
    {
        ArgumentNullException.ThrowIfNull(args);

        return new SafeMigrationDbContext(PostgreSqlDesignTimeContextConfiguration.ConnectionString());
    }
}
