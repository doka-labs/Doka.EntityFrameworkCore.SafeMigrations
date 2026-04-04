namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

/// <summary>
/// Provides <see cref="DbContextOptionsBuilder"/> extensions for PostgreSQL safe migrations.
/// </summary>
public static class PostgreSqlSafeMigrationOptionsBuilderExtensions
{
    /// <summary>
    /// Replaces the active migrations SQL generator with the PostgreSQL safe-migrations generator.
    /// </summary>
    public static DbContextOptionsBuilder UsePostgreSqlSafeMigrations(
        this DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.ReplaceService<IMigrationsSqlGenerator, PostgreSqlSafeMigrationsSqlGenerator>();
        return optionsBuilder;
    }
}
