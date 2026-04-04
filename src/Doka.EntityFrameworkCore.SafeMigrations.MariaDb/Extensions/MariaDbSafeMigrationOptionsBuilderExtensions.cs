namespace Doka.EntityFrameworkCore.SafeMigrations.MariaDb;

/// <summary>
/// Provides <see cref="DbContextOptionsBuilder"/> extensions for MariaDB safe migrations.
/// </summary>
public static class MariaDbSafeMigrationOptionsBuilderExtensions
{
    /// <summary>
    /// Replaces the active migrations SQL generator with the MariaDB safe-migrations generator.
    /// </summary>
    public static DbContextOptionsBuilder UseMariaDbSafeMigrations(
        this DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.ReplaceService<IMigrationsSqlGenerator, MariaDbSafeMigrationsSqlGenerator>();
        return optionsBuilder;
    }
}
