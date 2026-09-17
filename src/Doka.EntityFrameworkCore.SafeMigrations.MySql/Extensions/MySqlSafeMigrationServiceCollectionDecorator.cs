namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>
/// Decorates the active Doka migrations SQL generator without replacing its
/// provider-owned rendering implementation or service lifetime.
/// </summary>
internal static class MySqlSafeMigrationServiceCollectionDecorator
{
    public static void DecorateMigrationsSqlGenerator(
        IServiceCollection services
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        var descriptor = services.LastOrDefault(static candidate =>
            candidate.ServiceType == typeof(IMigrationsSqlGenerator));

        if (descriptor is null
            || descriptor.ImplementationType == typeof(MySqlSafeMigrationsSqlGenerator)
            || descriptor.ImplementationFactory?.Target is Factory)
        {
            return;
        }

        var factory = new Factory(descriptor);

        services.Remove(descriptor);
        services.Add(
            ServiceDescriptor.Describe(
                typeof(IMigrationsSqlGenerator),
                factory.Create,
                descriptor.Lifetime));
    }

    private static IMigrationsSqlGenerator CreateProviderGenerator(
        IServiceProvider provider,
        ServiceDescriptor descriptor
    )
    {
        if (descriptor.ImplementationInstance is IMigrationsSqlGenerator instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return (IMigrationsSqlGenerator)descriptor.ImplementationFactory(provider);
        }

        var implementationType = descriptor.ImplementationType
            ?? throw new InvalidOperationException(
                "The MySQL provider migrations SQL generator registration has no implementation.");

        return (IMigrationsSqlGenerator)ActivatorUtilities.CreateInstance(provider, implementationType);
    }

    private sealed class Factory(
        ServiceDescriptor providerDescriptor
    )
    {
        public MySqlSafeMigrationsSqlGenerator Create(
            IServiceProvider provider
        ) => new(
            CreateProviderGenerator(provider, providerDescriptor),
            provider.GetRequiredService<MySqlSafeMigrationPlanCapture>());
    }
}
