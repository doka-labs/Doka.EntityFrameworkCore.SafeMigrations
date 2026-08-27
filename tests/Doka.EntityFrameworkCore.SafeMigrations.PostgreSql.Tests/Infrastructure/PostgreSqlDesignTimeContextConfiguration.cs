namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

internal static class PostgreSqlDesignTimeContextConfiguration
{
    public static string ConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_CONNECTION_STRING");

        return string.IsNullOrWhiteSpace(connectionString)
            ? throw new InvalidOperationException("SAFE_MIGRATIONS_CONNECTION_STRING is required for EF tooling.")
            : connectionString;
    }
}
