namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

/// <summary>
/// Provides <see cref="DbContextOptionsBuilder"/> extensions for PostgreSQL
/// safe migrations, baseline-generator composition, and canonical migration
/// discovery.
/// </summary>
public static class PostgreSqlSafeMigrationOptionsBuilderExtensions
{
    /// <summary>
    /// Replaces the active migrations SQL generator with the PostgreSQL
    /// safe-migrations generator. The exact runtime context owns migration
    /// discovery.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="optionsBuilder"/> is null.</exception>
    public static DbContextOptionsBuilder UsePostgreSqlSafeMigrations(
        this DbContextOptionsBuilder optionsBuilder
    )
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(
            optionsBuilder,
            typeof(NpgsqlMigrationsSqlGenerator),
            canonicalContextType: null,
            SafeMigrationScaffoldingMode.Strict);

        return optionsBuilder;
    }

    /// <summary>
    /// Activates PostgreSQL SafeMigrations and configures how new migrations are scaffolded.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <param name="configure">The SafeMigrations design-time configuration.</param>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="optionsBuilder"/> or <paramref name="configure"/> is null.
    /// </exception>
    public static DbContextOptionsBuilder UsePostgreSqlSafeMigrations(
        this DbContextOptionsBuilder optionsBuilder,
        Action<SafeMigrationOptionsBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(
            optionsBuilder,
            typeof(NpgsqlMigrationsSqlGenerator),
            canonicalContextType: null,
            Configure(configure));

        return optionsBuilder;
    }

    /// <summary>
    /// Activates PostgreSQL SafeMigrations with an explicit canonical migration context.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <typeparam name="TCanonicalMigrationContext">The context that owns migration discovery and history.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="optionsBuilder"/> is null.</exception>
    public static DbContextOptionsBuilder UsePostgreSqlSafeMigrations<TCanonicalMigrationContext>(
        this DbContextOptionsBuilder optionsBuilder
    )
        where TCanonicalMigrationContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(
            optionsBuilder,
            typeof(NpgsqlMigrationsSqlGenerator),
            typeof(TCanonicalMigrationContext),
            SafeMigrationScaffoldingMode.Strict);

        return optionsBuilder;
    }

    /// <summary>
    /// Activates PostgreSQL SafeMigrations with a canonical context and design-time configuration.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <param name="configure">The SafeMigrations design-time configuration.</param>
    /// <typeparam name="TCanonicalMigrationContext">The context that owns migration discovery and history.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="optionsBuilder"/> or <paramref name="configure"/> is null.
    /// </exception>
    public static DbContextOptionsBuilder UsePostgreSqlSafeMigrations<TCanonicalMigrationContext>(
        this DbContextOptionsBuilder optionsBuilder,
        Action<SafeMigrationOptionsBuilder> configure
    )
        where TCanonicalMigrationContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(
            optionsBuilder,
            typeof(NpgsqlMigrationsSqlGenerator),
            typeof(TCanonicalMigrationContext),
            Configure(configure));

        return optionsBuilder;
    }

    /// <summary>
    /// Activates PostgreSQL SafeMigrations with explicit baseline-generator and canonical-context contracts.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <typeparam name="TBaselineGenerator">The generator used for ordinary PostgreSQL operations.</typeparam>
    /// <typeparam name="TCanonicalMigrationContext">The context that owns migration discovery and history.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="optionsBuilder"/> is null.</exception>
    public static DbContextOptionsBuilder UsePostgreSqlSafeMigrations<TBaselineGenerator, TCanonicalMigrationContext>(
        this DbContextOptionsBuilder optionsBuilder
    )
        where TBaselineGenerator : class, IMigrationsSqlGenerator
        where TCanonicalMigrationContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(
            optionsBuilder,
            typeof(TBaselineGenerator),
            typeof(TCanonicalMigrationContext),
            SafeMigrationScaffoldingMode.Strict);

        return optionsBuilder;
    }

    /// <summary>
    /// Activates PostgreSQL SafeMigrations with explicit baseline, canonical-context, and scaffolding contracts.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <param name="configure">The SafeMigrations design-time configuration.</param>
    /// <typeparam name="TBaselineGenerator">The generator used for ordinary PostgreSQL operations.</typeparam>
    /// <typeparam name="TCanonicalMigrationContext">The context that owns migration discovery and history.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="optionsBuilder"/> or <paramref name="configure"/> is null.
    /// </exception>
    public static DbContextOptionsBuilder UsePostgreSqlSafeMigrations<TBaselineGenerator, TCanonicalMigrationContext>(
        this DbContextOptionsBuilder optionsBuilder,
        Action<SafeMigrationOptionsBuilder> configure
    )
        where TBaselineGenerator : class, IMigrationsSqlGenerator
        where TCanonicalMigrationContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(
            optionsBuilder,
            typeof(TBaselineGenerator),
            typeof(TCanonicalMigrationContext),
            Configure(configure));

        return optionsBuilder;
    }

    /// <summary>
    /// Activates PostgreSQL SafeMigrations on a typed options builder. The
    /// exact runtime context owns migration discovery.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <typeparam name="TContext">The DbContext type being configured.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="optionsBuilder"/> is null.</exception>
    public static DbContextOptionsBuilder<TContext> UsePostgreSqlSafeMigrations<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder
    )
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(
            optionsBuilder,
            typeof(NpgsqlMigrationsSqlGenerator),
            canonicalContextType: null,
            SafeMigrationScaffoldingMode.Strict);

        return optionsBuilder;
    }

    /// <summary>
    /// Activates PostgreSQL SafeMigrations with design-time configuration on a typed options builder.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <param name="configure">The SafeMigrations design-time configuration.</param>
    /// <typeparam name="TContext">The DbContext type being configured.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="optionsBuilder"/> or <paramref name="configure"/> is null.
    /// </exception>
    public static DbContextOptionsBuilder<TContext> UsePostgreSqlSafeMigrations<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<SafeMigrationOptionsBuilder> configure
    )
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(
            optionsBuilder,
            typeof(NpgsqlMigrationsSqlGenerator),
            canonicalContextType: null,
            Configure(configure));

        return optionsBuilder;
    }

    /// <summary>
    /// Activates PostgreSQL SafeMigrations with explicit baseline-generator and canonical-context contracts.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <typeparam name="TContext">The runtime context type being configured.</typeparam>
    /// <typeparam name="TBaselineGenerator">The generator used for ordinary PostgreSQL operations.</typeparam>
    /// <typeparam name="TCanonicalMigrationContext">The context that owns migration discovery and history.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="optionsBuilder"/> is null.</exception>
    public static DbContextOptionsBuilder<TContext> UsePostgreSqlSafeMigrations<TContext, TBaselineGenerator,
        TCanonicalMigrationContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder
    )
        where TContext : DbContext
        where TBaselineGenerator : class, IMigrationsSqlGenerator
        where TCanonicalMigrationContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(
            optionsBuilder,
            typeof(TBaselineGenerator),
            typeof(TCanonicalMigrationContext),
            SafeMigrationScaffoldingMode.Strict);

        return optionsBuilder;
    }

    /// <summary>
    /// Activates PostgreSQL SafeMigrations with typed runtime, baseline, canonical-context, and scaffolding contracts.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <param name="configure">The SafeMigrations design-time configuration.</param>
    /// <typeparam name="TContext">The runtime context type being configured.</typeparam>
    /// <typeparam name="TBaselineGenerator">The generator used for ordinary PostgreSQL operations.</typeparam>
    /// <typeparam name="TCanonicalMigrationContext">The context that owns migration discovery and history.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="optionsBuilder"/> or <paramref name="configure"/> is null.
    /// </exception>
    public static DbContextOptionsBuilder<TContext> UsePostgreSqlSafeMigrations<TContext, TBaselineGenerator,
        TCanonicalMigrationContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<SafeMigrationOptionsBuilder> configure
    )
        where TContext : DbContext
        where TBaselineGenerator : class, IMigrationsSqlGenerator
        where TCanonicalMigrationContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(
            optionsBuilder,
            typeof(TBaselineGenerator),
            typeof(TCanonicalMigrationContext),
            Configure(configure));

        return optionsBuilder;
    }

    private static void AddOptionsExtension(
        DbContextOptionsBuilder optionsBuilder,
        Type baselineGeneratorType,
        Type? canonicalContextType,
        SafeMigrationScaffoldingMode scaffoldingMode
    )
    {
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(
            PostgreSqlSafeMigrationsOptionsExtension.WithConfiguration(
                baselineGeneratorType,
                canonicalContextType,
                scaffoldingMode));
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
