namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static class SafeMigrationServiceCollectionDecorator
{
    public static void DecorateMigrationsModelDiffer(
        IServiceCollection services
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        var descriptor = services.LastOrDefault(static candidate =>
            candidate.ServiceType == typeof(IMigrationsModelDiffer));

        if (descriptor is null
            || descriptor.ImplementationType == typeof(SafeMigrationMigrationsModelDiffer))
        {
            return;
        }

        services.Remove(descriptor);
        services.Add(
            ServiceDescriptor.Describe(
                typeof(IMigrationsModelDiffer),
                provider => new SafeMigrationMigrationsModelDiffer(CreateProviderDiffer(provider, descriptor)),
                descriptor.Lifetime));
    }

    private static IMigrationsModelDiffer CreateProviderDiffer(
        IServiceProvider provider,
        ServiceDescriptor descriptor
    )
    {
        if (descriptor.ImplementationInstance is IMigrationsModelDiffer instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return (IMigrationsModelDiffer)descriptor.ImplementationFactory(provider);
        }

        var implementationType = descriptor.ImplementationType
            ?? throw new InvalidOperationException("The provider model-differ registration has no implementation.");

        return (IMigrationsModelDiffer)ActivatorUtilities.CreateInstance(provider, implementationType);
    }
}
