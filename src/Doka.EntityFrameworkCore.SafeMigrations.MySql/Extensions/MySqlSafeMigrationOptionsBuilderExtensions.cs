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
    /// <exception cref="ArgumentNullException"><paramref name="optionsBuilder"/> is null.</exception>
    public static DbContextOptionsBuilder UseMySqlSafeMigrations(
        this DbContextOptionsBuilder optionsBuilder
    )
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(optionsBuilder, canonicalContextType: null, SafeMigrationScaffoldingMode.Strict);

        return optionsBuilder;
    }

    /// <summary>
    /// Adds the SafeMigrations handler and configures how new migrations are scaffolded.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <param name="configure">The SafeMigrations design-time configuration.</param>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="optionsBuilder"/> or <paramref name="configure"/> is null.
    /// </exception>
    public static DbContextOptionsBuilder UseMySqlSafeMigrations(
        this DbContextOptionsBuilder optionsBuilder,
        Action<SafeMigrationOptionsBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(optionsBuilder, canonicalContextType: null, Configure(configure));

        return optionsBuilder;
    }

    /// <summary>
    /// Adds SafeMigrations and selects the context that owns migration discovery, history, and model validation.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <typeparam name="TCanonicalMigrationContext">The canonical migration context.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="optionsBuilder"/> is null.</exception>
    public static DbContextOptionsBuilder UseMySqlSafeMigrations<TCanonicalMigrationContext>(
        this DbContextOptionsBuilder optionsBuilder
    )
        where TCanonicalMigrationContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(
            optionsBuilder,
            typeof(TCanonicalMigrationContext),
            SafeMigrationScaffoldingMode.Strict);

        return optionsBuilder;
    }

    /// <summary>
    /// Adds SafeMigrations with an explicit canonical context and design-time configuration.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <param name="configure">The SafeMigrations design-time configuration.</param>
    /// <typeparam name="TCanonicalMigrationContext">The canonical migration context.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="optionsBuilder"/> or <paramref name="configure"/> is null.
    /// </exception>
    public static DbContextOptionsBuilder UseMySqlSafeMigrations<TCanonicalMigrationContext>(
        this DbContextOptionsBuilder optionsBuilder,
        Action<SafeMigrationOptionsBuilder> configure
    )
        where TCanonicalMigrationContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(optionsBuilder, typeof(TCanonicalMigrationContext), Configure(configure));

        return optionsBuilder;
    }

    /// <summary>
    /// Adds the SafeMigrations handler to a typed options builder. The exact
    /// runtime context owns migration discovery.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <typeparam name="TContext">The DbContext type being configured.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="optionsBuilder"/> is null.</exception>
    public static DbContextOptionsBuilder<TContext> UseMySqlSafeMigrations<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder
    )
        where TContext : DbContext
    {
        UseMySqlSafeMigrations((DbContextOptionsBuilder)optionsBuilder);

        return optionsBuilder;
    }

    /// <summary>
    /// Adds the SafeMigrations handler and design-time configuration to a typed options builder.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <param name="configure">The SafeMigrations design-time configuration.</param>
    /// <typeparam name="TContext">The DbContext type being configured.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="optionsBuilder"/> or <paramref name="configure"/> is null.
    /// </exception>
    public static DbContextOptionsBuilder<TContext> UseMySqlSafeMigrations<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<SafeMigrationOptionsBuilder> configure
    )
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)optionsBuilder).UseMySqlSafeMigrations(configure);

        return optionsBuilder;
    }

    /// <summary>
    /// Adds SafeMigrations and selects the context that owns migration discovery, history, and model validation.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <typeparam name="TContext">The runtime context type being configured.</typeparam>
    /// <typeparam name="TCanonicalMigrationContext">The canonical migration context.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="optionsBuilder"/> is null.</exception>
    public static DbContextOptionsBuilder<TContext> UseMySqlSafeMigrations<TContext, TCanonicalMigrationContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder
    )
        where TContext : DbContext
        where TCanonicalMigrationContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(
            optionsBuilder,
            typeof(TCanonicalMigrationContext),
            SafeMigrationScaffoldingMode.Strict);

        return optionsBuilder;
    }

    /// <summary>
    /// Adds SafeMigrations with typed runtime and canonical contexts plus design-time configuration.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <param name="configure">The SafeMigrations design-time configuration.</param>
    /// <typeparam name="TContext">The runtime context type being configured.</typeparam>
    /// <typeparam name="TCanonicalMigrationContext">The canonical migration context.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="optionsBuilder"/> or <paramref name="configure"/> is null.
    /// </exception>
    public static DbContextOptionsBuilder<TContext> UseMySqlSafeMigrations<TContext, TCanonicalMigrationContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<SafeMigrationOptionsBuilder> configure
    )
        where TContext : DbContext
        where TCanonicalMigrationContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(optionsBuilder, typeof(TCanonicalMigrationContext), Configure(configure));

        return optionsBuilder;
    }

    private static void AddOptionsExtension(
        DbContextOptionsBuilder optionsBuilder,
        Type? canonicalContextType,
        SafeMigrationScaffoldingMode scaffoldingMode
    )
    {
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(
            MySqlSafeMigrationsOptionsExtension.WithCanonicalContext(canonicalContextType, scaffoldingMode));
    }

    private static SafeMigrationScaffoldingMode Configure(
        Action<SafeMigrationOptionsBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new SafeMigrationOptionsBuilder();
        configure(builder);

        return builder.Mode;
    }
}
