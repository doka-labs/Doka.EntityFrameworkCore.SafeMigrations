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
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <returns>The same options builder so additional calls can be chained.</returns>
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
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <typeparam name="TContext">The DbContext type being configured.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    public static DbContextOptionsBuilder<TContext> UseMySqlSafeMigrations<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder
    )
        where TContext : DbContext
    {
        UseMySqlSafeMigrations((DbContextOptionsBuilder)optionsBuilder);
        return optionsBuilder;
    }
}
