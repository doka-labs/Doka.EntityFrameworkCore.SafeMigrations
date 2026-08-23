namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

/// <summary>
/// Provides service-registration helpers for PostgreSQL safe migrations.
/// </summary>
public static class PostgreSqlServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL safe-migrations SQL generator in the service collection.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection so additional registrations can be chained.</returns>
    public static IServiceCollection AddPostgreSqlSafeMigrations(
        this IServiceCollection services
    ) => AddPostgreSqlSafeMigrations(services, typeof(NpgsqlMigrationsSqlGenerator), canonicalContextType: null);

    /// <summary>
    /// Registers SafeMigrations with explicit ordinary-operation and canonical-context contracts.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <typeparam name="TBaselineGenerator">The generator used for ordinary PostgreSQL operations.</typeparam>
    /// <typeparam name="TCanonicalMigrationContext">The context that owns migration discovery and history.</typeparam>
    /// <returns>The same service collection so additional registrations can be chained.</returns>
    public static IServiceCollection AddPostgreSqlSafeMigrations<TBaselineGenerator, TCanonicalMigrationContext>(
        this IServiceCollection services
    )
        where TBaselineGenerator : class, IMigrationsSqlGenerator
        where TCanonicalMigrationContext : DbContext => AddPostgreSqlSafeMigrations(
        services,
        typeof(TBaselineGenerator),
        typeof(TCanonicalMigrationContext));

    internal static IServiceCollection AddPostgreSqlSafeMigrations(
        this IServiceCollection services,
        Type baselineGeneratorType,
        Type? canonicalContextType
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        ArgumentNullException.ThrowIfNull(baselineGeneratorType);

        if (!typeof(IMigrationsSqlGenerator).IsAssignableFrom(baselineGeneratorType)
            || baselineGeneratorType == typeof(PostgreSqlSafeMigrationsSqlGenerator))
        {
            throw new InvalidOperationException(
                "The PostgreSQL baseline generator must implement IMigrationsSqlGenerator and must not be the SafeMigrations wrapper.");
        }

        SafeMigrationCanonicalContextConfiguration.Register(
            services,
            typeof(PostgreSqlServiceCollectionExtensions),
            canonicalContextType,
            baselineGeneratorType);

        services.TryAddScoped(baselineGeneratorType);
        services.TryAddScoped(
            typeof(IPostgreSqlSafeMigrationsBaselineGenerator),
            typeof(PostgreSqlSafeMigrationsBaselineGenerator<>).MakeGenericType(baselineGeneratorType));

        services.TryAddScoped<ISafeMigrationProviderAnalyzer, PostgreSqlSafeMigrationProviderAnalyzer>();
        services.TryAddScoped<ISafeMigrationRunner, SafeMigrationRunner>();
        services.Replace(ServiceDescriptor.Scoped<IMigrationsAssembly, SafeMigrationMigrationsAssembly>());
        services.Replace(ServiceDescriptor.Scoped<IMigrationsSqlGenerator, PostgreSqlSafeMigrationsSqlGenerator>());

        return services;
    }
}
