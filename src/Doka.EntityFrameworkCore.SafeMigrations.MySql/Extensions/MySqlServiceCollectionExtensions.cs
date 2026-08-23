namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>
/// Registers the SafeMigrations Doka operation handler for applications that
/// deliberately own EF Core's internal service provider.
/// </summary>
public static class MySqlServiceCollectionExtensions
{
    /// <summary>
    /// Adds the scoped SafeMigrations handler additively and idempotently.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection so additional registrations can be chained.</returns>
    public static IServiceCollection AddEntityFrameworkDokaMySqlSafeMigrations(
        this IServiceCollection services
    ) => AddEntityFrameworkDokaMySqlSafeMigrations(services, canonicalContextType: null);

    /// <summary>
    /// Adds SafeMigrations and selects the context that owns migration discovery, history, and model validation.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <typeparam name="TCanonicalMigrationContext">The canonical migration context.</typeparam>
    /// <returns>The same service collection so additional registrations can be chained.</returns>
    public static IServiceCollection AddEntityFrameworkDokaMySqlSafeMigrations<TCanonicalMigrationContext>(
        this IServiceCollection services
    )
        where TCanonicalMigrationContext : DbContext =>
        AddEntityFrameworkDokaMySqlSafeMigrations(services, typeof(TCanonicalMigrationContext));

    internal static IServiceCollection AddEntityFrameworkDokaMySqlSafeMigrations(
        this IServiceCollection services,
        Type? canonicalContextType
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        SafeMigrationCanonicalContextConfiguration.Register(
            services,
            typeof(MySqlServiceCollectionExtensions),
            canonicalContextType);

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IMySqlMigrationOperationHandler, MySqlSafeMigrationOperationHandler>());

        services.TryAddScoped<MySqlSafeMigrationPlanCapture>();
        services.TryAddScoped<ISafeMigrationProviderAnalyzer, MySqlSafeMigrationProviderAnalyzer>();
        services.TryAddScoped<ISafeMigrationRunner, SafeMigrationRunner>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IInterceptor, MySqlSafeMigrationConnectionInterceptor>());
        services.Replace(ServiceDescriptor.Scoped<IMigrationsAssembly, SafeMigrationMigrationsAssembly>());

        return services;
    }
}
