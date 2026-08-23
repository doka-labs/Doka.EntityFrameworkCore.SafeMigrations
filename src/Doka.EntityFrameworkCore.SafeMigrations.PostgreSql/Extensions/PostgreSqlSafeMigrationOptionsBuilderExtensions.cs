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
    public static DbContextOptionsBuilder UsePostgreSqlSafeMigrations(
        this DbContextOptionsBuilder optionsBuilder
    )
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(optionsBuilder, typeof(NpgsqlMigrationsSqlGenerator), canonicalContextType: null);
        return optionsBuilder;
    }

    /// <summary>
    /// Activates PostgreSQL SafeMigrations with an explicit canonical migration context.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <typeparam name="TCanonicalMigrationContext">The context that owns migration discovery and history.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    public static DbContextOptionsBuilder UsePostgreSqlSafeMigrations<TCanonicalMigrationContext>(
        this DbContextOptionsBuilder optionsBuilder
    )
        where TCanonicalMigrationContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(optionsBuilder, typeof(NpgsqlMigrationsSqlGenerator), typeof(TCanonicalMigrationContext));
        return optionsBuilder;
    }

    /// <summary>
    /// Activates PostgreSQL SafeMigrations with explicit baseline-generator and canonical-context contracts.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <typeparam name="TBaselineGenerator">The generator used for ordinary PostgreSQL operations.</typeparam>
    /// <typeparam name="TCanonicalMigrationContext">The context that owns migration discovery and history.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    public static DbContextOptionsBuilder UsePostgreSqlSafeMigrations<TBaselineGenerator, TCanonicalMigrationContext>(
        this DbContextOptionsBuilder optionsBuilder
    )
        where TBaselineGenerator : class, IMigrationsSqlGenerator
        where TCanonicalMigrationContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(optionsBuilder, typeof(TBaselineGenerator), typeof(TCanonicalMigrationContext));
        return optionsBuilder;
    }

    /// <summary>
    /// Activates PostgreSQL SafeMigrations on a typed options builder. The
    /// exact runtime context owns migration discovery.
    /// </summary>
    /// <param name="optionsBuilder">The EF Core options builder to configure.</param>
    /// <typeparam name="TContext">The DbContext type being configured.</typeparam>
    /// <returns>The same options builder so additional calls can be chained.</returns>
    public static DbContextOptionsBuilder<TContext> UsePostgreSqlSafeMigrations<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder
    )
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(optionsBuilder, typeof(NpgsqlMigrationsSqlGenerator), canonicalContextType: null);
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
    public static DbContextOptionsBuilder<TContext> UsePostgreSqlSafeMigrations<TContext, TBaselineGenerator,
        TCanonicalMigrationContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder
    )
        where TContext : DbContext
        where TBaselineGenerator : class, IMigrationsSqlGenerator
        where TCanonicalMigrationContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        AddOptionsExtension(optionsBuilder, typeof(TBaselineGenerator), typeof(TCanonicalMigrationContext));
        return optionsBuilder;
    }

    private static void AddOptionsExtension(
        DbContextOptionsBuilder optionsBuilder,
        Type baselineGeneratorType,
        Type? canonicalContextType
    )
    {
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(
            PostgreSqlSafeMigrationsOptionsExtension.WithConfiguration(baselineGeneratorType, canonicalContextType));
    }
}
