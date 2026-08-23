namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>
/// Activates the SafeMigrations operation handler in the Doka MySQL provider's
/// internal EF Core service provider and selects the canonical migrations
/// assembly used by runtime migration discovery and tooling.
/// </summary>
public static class MySqlSafeMigrationOptionsBuilderExtensions
{
    /// <summary>
    /// Adds the SafeMigrations handler without replacing the Doka migrations
    /// SQL generator. The exact runtime context owns migration discovery.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    public static DbContextOptionsBuilder UseMySqlSafeMigrations(
        this DbContextOptionsBuilder optionsBuilder
    )
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(optionsBuilder, canonicalContextType: null);
        return optionsBuilder;
    }

    /// <summary>
    /// Adds SafeMigrations and selects the context that owns migration discovery, history, and model validation.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <typeparam name="TCanonicalMigrationContext">The canonical migration context.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    public static DbContextOptionsBuilder UseMySqlSafeMigrations<TCanonicalMigrationContext>(
        this DbContextOptionsBuilder optionsBuilder
    )
        where TCanonicalMigrationContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(optionsBuilder, typeof(TCanonicalMigrationContext));
        return optionsBuilder;
    }

    /// <summary>
    /// Adds the SafeMigrations handler to a typed options builder. The exact
    /// runtime context owns migration discovery.
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

    /// <summary>
    /// Adds SafeMigrations and selects the context that owns migration discovery, history, and model validation.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <typeparam name="TContext">The runtime context type being configured.</typeparam>
    /// <typeparam name="TCanonicalMigrationContext">The canonical migration context.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    public static DbContextOptionsBuilder<TContext> UseMySqlSafeMigrations<TContext, TCanonicalMigrationContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder
    )
        where TContext : DbContext
        where TCanonicalMigrationContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(optionsBuilder, typeof(TCanonicalMigrationContext));
        return optionsBuilder;
    }

    private static void AddOptionsExtension(
        DbContextOptionsBuilder optionsBuilder,
        Type? canonicalContextType
    )
    {
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(
            MySqlSafeMigrationsOptionsExtension.WithCanonicalContext(canonicalContextType));
    }
}
