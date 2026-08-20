namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

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

        var engine = Environment
                .GetEnvironmentVariable("SAFE_MIGRATIONS_MYSQL_ENGINE")
                ?.Trim()
                .ToLowerInvariant()
            ?? "mariadb";

        var versionText = Environment
                .GetEnvironmentVariable("SAFE_MIGRATIONS_MYSQL_VERSION")
                ?.Trim()
            ?? "11.8.2";

        if (!Version.TryParse(versionText, out var version))
        {
            throw new InvalidOperationException("SAFE_MIGRATIONS_MYSQL_VERSION must be a valid version.");
        }

        var serverVersion = engine switch
        {
            "mysql" => MySqlServerVersion.MySql(version),
            "mariadb" => MySqlServerVersion.MariaDb(version),
            _ => throw new InvalidOperationException("SAFE_MIGRATIONS_MYSQL_ENGINE must be mysql or mariadb."),
        };

        return new SafeMigrationDbContext(connectionString, serverVersion);
    }
}
