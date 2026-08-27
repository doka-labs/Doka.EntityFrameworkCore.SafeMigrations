namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

internal static class MySqlDesignTimeContextConfiguration
{
    public static string ConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_CONNECTION_STRING");

        return string.IsNullOrWhiteSpace(connectionString)
            ? throw new InvalidOperationException("SAFE_MIGRATIONS_CONNECTION_STRING is required for EF tooling.")
            : connectionString;
    }

    public static MySqlServerVersion ServerVersion()
    {
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

        return engine switch
        {
            "mysql" => MySqlServerVersion.MySql(version),
            "mariadb" => MySqlServerVersion.MariaDb(version),
            _ => throw new InvalidOperationException("SAFE_MIGRATIONS_MYSQL_ENGINE must be mysql or mariadb."),
        };
    }
}
