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
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IMySqlMigrationOperationHandler, MySqlSafeMigrationOperationHandler>());

        services.TryAddScoped<ISafeMigrationProviderAnalyzer, MySqlSafeMigrationProviderAnalyzer>();
        services.TryAddScoped<ISafeMigrationRunner, SafeMigrationRunner>();
        services.Replace(ServiceDescriptor.Scoped<IMigrationsAssembly, SafeMigrationMigrationsAssembly>());

        return services;
    }
}
