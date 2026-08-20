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
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<NpgsqlMigrationsSqlGenerator>();
        services.TryAddScoped<ISafeMigrationProviderAnalyzer, PostgreSqlSafeMigrationProviderAnalyzer>();
        services.TryAddScoped<ISafeMigrationRunner, SafeMigrationRunner>();
        services.Replace(ServiceDescriptor.Scoped<IMigrationsAssembly, SafeMigrationMigrationsAssembly>());
        services.Replace(ServiceDescriptor.Scoped<IMigrationsSqlGenerator, PostgreSqlSafeMigrationsSqlGenerator>());

        return services;
    }
}
