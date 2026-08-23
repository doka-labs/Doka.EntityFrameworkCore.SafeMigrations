namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed record SafeMigrationCanonicalContextConfiguration(
    Type ProviderRegistrationType,
    Type? ContextType,
    Type? BaselineGeneratorType
)
{
    internal static void Register(
        IServiceCollection services,
        Type providerRegistrationType,
        Type? contextType,
        Type? baselineGeneratorType = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(providerRegistrationType);

        var expected = new SafeMigrationCanonicalContextConfiguration(
            providerRegistrationType,
            contextType,
            baselineGeneratorType);

        var registrations = services
            .Where(static descriptor => descriptor.ServiceType == typeof(SafeMigrationCanonicalContextConfiguration))
            .ToArray();

        if (registrations.Length == 0)
        {
            services.Add(ServiceDescriptor.Singleton(expected));
            return;
        }

        if (registrations is [{ ImplementationInstance: SafeMigrationCanonicalContextConfiguration existing }]
            && existing == expected)
        {
            return;
        }

        throw new InvalidOperationException(
            "SafeMigrations services are already configured with a different provider, "
            + "baseline generator, or canonical migration context.");
    }
}
