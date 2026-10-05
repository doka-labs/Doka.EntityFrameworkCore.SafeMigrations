namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>
/// Provides service-registration helpers for SQL Server safe migrations.
/// </summary>
public static class SqlServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQL Server safe-migrations SQL generator in the service collection.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection so additional registrations can be chained.</returns>
    public static IServiceCollection AddSqlServerSafeMigrations(
        this IServiceCollection services
    ) => AddSqlServerSafeMigrations(services, typeof(SqlServerMigrationsSqlGenerator), canonicalContextType: null);

    /// <summary>
    /// Registers SafeMigrations with explicit ordinary-operation and canonical-context contracts.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <typeparam name="TBaselineGenerator">The generator used for ordinary SQL Server operations.</typeparam>
    /// <typeparam name="TCanonicalMigrationContext">The context that owns migration discovery and history.</typeparam>
    /// <returns>The same service collection so additional registrations can be chained.</returns>
    public static IServiceCollection AddSqlServerSafeMigrations<TBaselineGenerator, TCanonicalMigrationContext>(
        this IServiceCollection services
    )
        where TBaselineGenerator : class, IMigrationsSqlGenerator
        where TCanonicalMigrationContext : DbContext => AddSqlServerSafeMigrations(
        services,
        typeof(TBaselineGenerator),
        typeof(TCanonicalMigrationContext));

    internal static IServiceCollection AddSqlServerSafeMigrations(
        this IServiceCollection services,
        Type baselineGeneratorType,
        Type? canonicalContextType
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        ArgumentNullException.ThrowIfNull(baselineGeneratorType);

        if (!typeof(IMigrationsSqlGenerator).IsAssignableFrom(baselineGeneratorType)
            || baselineGeneratorType == typeof(SqlServerSafeMigrationsSqlGenerator))
        {
            throw new InvalidOperationException(
                "The SQL Server baseline generator must implement IMigrationsSqlGenerator "
                + "and must not be the SafeMigrations wrapper.");
        }

        SafeMigrationCanonicalContextConfiguration.Register(
            services,
            typeof(SqlServerServiceCollectionExtensions),
            canonicalContextType,
            baselineGeneratorType);

        services.TryAddScoped(baselineGeneratorType);
        services.TryAddScoped(
            typeof(ISqlServerSafeMigrationsBaselineGenerator),
            typeof(SqlServerSafeMigrationsBaselineGenerator<>).MakeGenericType(baselineGeneratorType));

        services.TryAddScoped<ISafeMigrationProviderAnalyzer, SqlServerSafeMigrationProviderAnalyzer>();
        services.TryAddScoped<ISafeMigrationRunner, SafeMigrationRunner>();
        services.Replace(ServiceDescriptor.Scoped<IMigrationsAssembly, SafeMigrationMigrationsAssembly>());
        services.Replace(ServiceDescriptor.Scoped<IMigrationsSqlGenerator, SqlServerSafeMigrationsSqlGenerator>());
        SafeMigrationServiceCollectionDecorator.DecorateMigrationsModelDiffer(services);

        return services;
    }
}
