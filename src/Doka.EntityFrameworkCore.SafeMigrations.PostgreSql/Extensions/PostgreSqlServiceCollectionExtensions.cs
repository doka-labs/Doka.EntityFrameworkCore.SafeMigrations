namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

/// <summary>
/// Provides service-registration helpers for PostgreSQL safe migrations.
/// </summary>
public static class PostgreSqlServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL safe-migrations SQL generator in the service collection.
    /// </summary>
    public static IServiceCollection AddPostgreSqlSafeMigrations(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IMigrationsSqlGenerator, PostgreSqlSafeMigrationsSqlGenerator>());
        return services;
    }
}
