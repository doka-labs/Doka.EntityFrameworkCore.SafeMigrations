namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed class SafeMigrationDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SafeMigrationDbContext>
{
    public SafeMigrationDbContext CreateDbContext(
        string[] args
    )
    {
        ArgumentNullException.ThrowIfNull(args);
        var connectionString = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("SAFE_MIGRATIONS_CONNECTION_STRING is required for EF tooling.");
        }

        return new SafeMigrationDbContext(connectionString);
    }
}
