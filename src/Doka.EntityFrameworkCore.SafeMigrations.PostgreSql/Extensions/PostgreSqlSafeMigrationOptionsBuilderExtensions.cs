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
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        AddOptionsExtension(optionsBuilder);
        return optionsBuilder;
    }

    /// <summary>
    /// Activates PostgreSQL SafeMigrations on a typed options builder.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> UsePostgreSqlSafeMigrations<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder
    )
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(optionsBuilder);
        return optionsBuilder;
    }

    private static void AddOptionsExtension(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        var extension = optionsBuilder.Options.FindExtension<PostgreSqlSafeMigrationsOptionsExtension>()
            ?? new PostgreSqlSafeMigrationsOptionsExtension();

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
    }
}
