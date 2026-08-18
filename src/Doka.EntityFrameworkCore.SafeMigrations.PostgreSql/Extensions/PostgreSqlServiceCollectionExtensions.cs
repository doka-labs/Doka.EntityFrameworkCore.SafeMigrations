namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

/// <summary>
/// Provides service-registration helpers for PostgreSQL safe migrations.
/// </summary>
public static class PostgreSqlServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL safe-migrations SQL generator in the service collection.
    /// </summary>
    public static IServiceCollection AddPostgreSqlSafeMigrations(
        this IServiceCollection services
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<NpgsqlMigrationsSqlGenerator>();
        services.TryAddScoped<ISafeMigrationProviderAnalyzer, PostgreSqlSafeMigrationProviderAnalyzer>();
        services.TryAddScoped<ISafeMigrationRunner, SafeMigrationRunner>();
        services.Replace(ServiceDescriptor.Scoped<IMigrationsSqlGenerator, PostgreSqlSafeMigrationsSqlGenerator>());

        return services;
    }
}
