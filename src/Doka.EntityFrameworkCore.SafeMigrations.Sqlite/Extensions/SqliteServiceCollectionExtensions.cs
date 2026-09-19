namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Provides service-registration helpers for SQLite SafeMigrations.</summary>
public static class SqliteServiceCollectionExtensions
{
    /// <summary>Registers SQLite SafeMigrations with the provider's standard SQL generator.</summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection so additional registrations can be chained.</returns>
    public static IServiceCollection AddSqliteSafeMigrations(
        this IServiceCollection services
    ) => services.AddSqliteSafeMigrations(typeof(SqliteMigrationsSqlGenerator), canonicalContextType: null);

    /// <summary>Registers SQLite SafeMigrations with explicit baseline and canonical-context contracts.</summary>
    /// <param name="services">The service collection to configure.</param>
    /// <typeparam name="TBaselineGenerator">The generator used for ordinary SQLite operations.</typeparam>
    /// <typeparam name="TCanonicalMigrationContext">The context that owns migration discovery and history.</typeparam>
    /// <returns>The same service collection so additional registrations can be chained.</returns>
    public static IServiceCollection AddSqliteSafeMigrations<TBaselineGenerator, TCanonicalMigrationContext>(
        this IServiceCollection services
    )
        where TBaselineGenerator : class, IMigrationsSqlGenerator
        where TCanonicalMigrationContext : DbContext => services.AddSqliteSafeMigrations(
        typeof(TBaselineGenerator),
        typeof(TCanonicalMigrationContext));

    internal static IServiceCollection AddSqliteSafeMigrations(
        this IServiceCollection services,
        Type baselineGeneratorType,
        Type? canonicalContextType
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(baselineGeneratorType);

        if (!typeof(IMigrationsSqlGenerator).IsAssignableFrom(baselineGeneratorType)
            || baselineGeneratorType == typeof(SqliteSafeMigrationsSqlGenerator))
        {
            throw new InvalidOperationException(
                "The SQLite baseline generator must implement IMigrationsSqlGenerator "
                + "and must not be the SafeMigrations wrapper.");
        }

        SafeMigrationCanonicalContextConfiguration.Register(
            services,
            typeof(SqliteServiceCollectionExtensions),
            canonicalContextType,
            baselineGeneratorType);

        services.TryAddScoped(baselineGeneratorType);
        services.TryAddScoped(
            typeof(ISqliteSafeMigrationsBaselineGenerator),
            typeof(SqliteSafeMigrationsBaselineGenerator<>).MakeGenericType(baselineGeneratorType));
        services.TryAddScoped<SqliteSafeMigrationExecutionState>();
        services.TryAddScoped<ISafeMigrationProviderAnalyzer, SqliteSafeMigrationProviderAnalyzer>();
        services.TryAddScoped<ISafeMigrationRunner, SafeMigrationRunner>();
        services.Replace(ServiceDescriptor.Scoped<IMigrationsAssembly, SafeMigrationMigrationsAssembly>());
        services.Replace(
            ServiceDescriptor.Scoped<IMigrationsSqlGenerator>(provider => new SqliteSafeMigrationsSqlGenerator(
                provider.GetRequiredService<ISqliteSafeMigrationsBaselineGenerator>(),
                provider.GetRequiredService<ISafeMigrationProviderAnalyzer>(),
                provider.GetRequiredService<SqliteSafeMigrationExecutionState>(),
                provider.GetRequiredService<MigrationsSqlGeneratorDependencies>())));
        SafeMigrationServiceCollectionDecorator.DecorateMigrationsModelDiffer(services);

        return services;
    }
}
