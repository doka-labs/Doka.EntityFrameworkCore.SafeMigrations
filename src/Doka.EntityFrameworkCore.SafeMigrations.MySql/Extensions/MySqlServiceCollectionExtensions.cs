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
    public static IServiceCollection AddEntityFrameworkDokaMySqlSafeMigrations(
        this IServiceCollection services
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IMySqlMigrationOperationHandler, MySqlSafeMigrationOperationHandler>());

        services.TryAddScoped<ISafeMigrationProviderAnalyzer, MySqlSafeMigrationProviderAnalyzer>();
        services.TryAddScoped<ISafeMigrationRunner, SafeMigrationRunner>();

        return services;
    }
}
