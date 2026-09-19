namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>
/// Provides <see cref="DbContextOptionsBuilder"/> extensions for SQLite
/// SafeMigrations, baseline-generator composition, and canonical migration discovery.
/// </summary>
public static class SqliteSafeMigrationOptionsBuilderExtensions
{
    /// <summary>Activates SQLite SafeMigrations for the exact runtime context.</summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    public static DbContextOptionsBuilder UseSqliteSafeMigrations(
        this DbContextOptionsBuilder optionsBuilder
    ) => ConfigureOptions(
        optionsBuilder,
        typeof(SqliteMigrationsSqlGenerator),
        canonicalContextType: null,
        configure: null);

    /// <summary>Activates SQLite SafeMigrations with design-time configuration.</summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <param name="configure">The SafeMigrations design-time configuration.</param>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    public static DbContextOptionsBuilder UseSqliteSafeMigrations(
        this DbContextOptionsBuilder optionsBuilder,
        Action<SafeMigrationOptionsBuilder> configure
    ) => ConfigureOptions(optionsBuilder, typeof(SqliteMigrationsSqlGenerator), canonicalContextType: null, configure);

    /// <summary>Activates SQLite SafeMigrations with an explicit canonical migration context.</summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <typeparam name="TCanonicalMigrationContext">The context that owns migration discovery and history.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    public static DbContextOptionsBuilder UseSqliteSafeMigrations<TCanonicalMigrationContext>(
        this DbContextOptionsBuilder optionsBuilder
    )
        where TCanonicalMigrationContext : DbContext => ConfigureOptions(
        optionsBuilder,
        typeof(SqliteMigrationsSqlGenerator),
        typeof(TCanonicalMigrationContext),
        configure: null);

    /// <summary>Activates SQLite SafeMigrations with canonical-context and design-time configuration.</summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <param name="configure">The SafeMigrations design-time configuration.</param>
    /// <typeparam name="TCanonicalMigrationContext">The context that owns migration discovery and history.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    public static DbContextOptionsBuilder UseSqliteSafeMigrations<TCanonicalMigrationContext>(
        this DbContextOptionsBuilder optionsBuilder,
        Action<SafeMigrationOptionsBuilder> configure
    )
        where TCanonicalMigrationContext : DbContext => ConfigureOptions(
        optionsBuilder,
        typeof(SqliteMigrationsSqlGenerator),
        typeof(TCanonicalMigrationContext),
        configure);

    /// <summary>Activates SQLite SafeMigrations with explicit baseline and canonical-context contracts.</summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <typeparam name="TBaselineGenerator">The generator used for ordinary SQLite operations.</typeparam>
    /// <typeparam name="TCanonicalMigrationContext">The context that owns migration discovery and history.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    public static DbContextOptionsBuilder UseSqliteSafeMigrations<TBaselineGenerator, TCanonicalMigrationContext>(
        this DbContextOptionsBuilder optionsBuilder
    )
        where TBaselineGenerator : class, IMigrationsSqlGenerator
        where TCanonicalMigrationContext : DbContext => ConfigureOptions(
        optionsBuilder,
        typeof(TBaselineGenerator),
        typeof(TCanonicalMigrationContext),
        configure: null);

    /// <summary>
    /// Activates SQLite SafeMigrations with explicit baseline, canonical-context,
    /// and design-time contracts.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <param name="configure">The SafeMigrations design-time configuration.</param>
    /// <typeparam name="TBaselineGenerator">The generator used for ordinary SQLite operations.</typeparam>
    /// <typeparam name="TCanonicalMigrationContext">The context that owns migration discovery and history.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    public static DbContextOptionsBuilder UseSqliteSafeMigrations<TBaselineGenerator, TCanonicalMigrationContext>(
        this DbContextOptionsBuilder optionsBuilder,
        Action<SafeMigrationOptionsBuilder> configure
    )
        where TBaselineGenerator : class, IMigrationsSqlGenerator
        where TCanonicalMigrationContext : DbContext => ConfigureOptions(
        optionsBuilder,
        typeof(TBaselineGenerator),
        typeof(TCanonicalMigrationContext),
        configure);

    /// <summary>Activates SQLite SafeMigrations on a typed options builder.</summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <typeparam name="TContext">The runtime context type being configured.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    public static DbContextOptionsBuilder<TContext> UseSqliteSafeMigrations<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder
    )
        where TContext : DbContext => ConfigureTypedOptions<TContext, SqliteMigrationsSqlGenerator>(
        optionsBuilder,
        canonicalContextType: null,
        configure: null);

    /// <summary>Activates SQLite SafeMigrations with design-time configuration on a typed options builder.</summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <param name="configure">The SafeMigrations design-time configuration.</param>
    /// <typeparam name="TContext">The runtime context type being configured.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    public static DbContextOptionsBuilder<TContext> UseSqliteSafeMigrations<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<SafeMigrationOptionsBuilder> configure
    )
        where TContext : DbContext => ConfigureTypedOptions<TContext, SqliteMigrationsSqlGenerator>(
        optionsBuilder,
        canonicalContextType: null,
        configure);

    /// <summary>Activates SQLite SafeMigrations with typed runtime, baseline, and canonical contexts.</summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <typeparam name="TContext">The runtime context type being configured.</typeparam>
    /// <typeparam name="TBaselineGenerator">The generator used for ordinary SQLite operations.</typeparam>
    /// <typeparam name="TCanonicalMigrationContext">The context that owns migration discovery and history.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    public static DbContextOptionsBuilder<TContext> UseSqliteSafeMigrations<TContext, TBaselineGenerator,
        TCanonicalMigrationContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder
    )
        where TContext : DbContext
        where TBaselineGenerator : class, IMigrationsSqlGenerator
        where TCanonicalMigrationContext : DbContext => ConfigureTypedOptions<TContext, TBaselineGenerator>(
        optionsBuilder,
        typeof(TCanonicalMigrationContext),
        configure: null);

    /// <summary>
    /// Activates SQLite SafeMigrations with typed runtime, baseline, canonical-context,
    /// and design-time contracts.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <param name="configure">The SafeMigrations design-time configuration.</param>
    /// <typeparam name="TContext">The runtime context type being configured.</typeparam>
    /// <typeparam name="TBaselineGenerator">The generator used for ordinary SQLite operations.</typeparam>
    /// <typeparam name="TCanonicalMigrationContext">The context that owns migration discovery and history.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    public static DbContextOptionsBuilder<TContext> UseSqliteSafeMigrations<TContext, TBaselineGenerator,
        TCanonicalMigrationContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<SafeMigrationOptionsBuilder> configure
    )
        where TContext : DbContext
        where TBaselineGenerator : class, IMigrationsSqlGenerator
        where TCanonicalMigrationContext : DbContext => ConfigureTypedOptions<TContext, TBaselineGenerator>(
        optionsBuilder,
        typeof(TCanonicalMigrationContext),
        configure);

    private static DbContextOptionsBuilder ConfigureOptions(
        DbContextOptionsBuilder optionsBuilder,
        Type baselineGeneratorType,
        Type? canonicalContextType,
        Action<SafeMigrationOptionsBuilder>? configure
    )
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        var configuration = CreateConfiguration(configure);
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(
            SqliteSafeMigrationsOptionsExtension.WithConfiguration(
                baselineGeneratorType,
                canonicalContextType,
                configuration.Mode,
                configuration.LegacyConvergencePolicy,
                configuration.ExcludeModelManagedDataForExcludedTablesEnabled));

        return optionsBuilder;
    }

    private static DbContextOptionsBuilder<TContext> ConfigureTypedOptions<TContext, TBaselineGenerator>(
        DbContextOptionsBuilder<TContext> optionsBuilder,
        Type? canonicalContextType,
        Action<SafeMigrationOptionsBuilder>? configure
    )
        where TContext : DbContext
        where TBaselineGenerator : class, IMigrationsSqlGenerator
    {
        ConfigureOptions(optionsBuilder, typeof(TBaselineGenerator), canonicalContextType, configure);

        return optionsBuilder;
    }

    private static SafeMigrationOptionsBuilder CreateConfiguration(
        Action<SafeMigrationOptionsBuilder>? configure
    )
    {
        var builder = new SafeMigrationOptionsBuilder();
        configure?.Invoke(builder);
        builder.Validate();

        return builder;
    }
}
