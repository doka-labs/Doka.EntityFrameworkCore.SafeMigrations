namespace Doka.EntityFrameworkCore.SafeMigrations.MariaDb;

/// <summary>
/// Provides service-registration helpers for MariaDB safe migrations.
/// </summary>
public static class MariaDbServiceCollectionExtensions
{
    /// <summary>
    /// Registers the MariaDB safe-migrations SQL generator in the service collection.
    /// </summary>
    public static IServiceCollection AddMariaDbSafeMigrations(
        this IServiceCollection services
    )
    {
        services.Replace(ServiceDescriptor.Singleton<IMigrationsSqlGenerator, MariaDbSafeMigrationsSqlGenerator>());
        return services;
    }
}
