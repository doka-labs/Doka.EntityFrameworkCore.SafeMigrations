namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>
/// Activates the SafeMigrations operation handler in the Doka MySQL provider's
/// internal EF Core service provider.
/// </summary>
public static class MySqlSafeMigrationOptionsBuilderExtensions
{
    /// <summary>
    /// Adds the SafeMigrations handler without replacing the Doka migrations
    /// SQL generator.
    /// </summary>
    public static DbContextOptionsBuilder UseMySqlSafeMigrations(
        this DbContextOptionsBuilder optionsBuilder
    )
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        var extension = optionsBuilder.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>()
            ?? new MySqlSafeMigrationsOptionsExtension();
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
        return optionsBuilder;
    }

    /// <summary>
    /// Adds the SafeMigrations handler to a typed options builder.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> UseMySqlSafeMigrations<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder
    )
        where TContext : DbContext
    {
        UseMySqlSafeMigrations((DbContextOptionsBuilder)optionsBuilder);
        return optionsBuilder;
    }
}
