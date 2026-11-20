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

            // WHY: Retain the existing LINQ snapshot and ownership contract instead of spread-based materialization.
            // ReSharper disable once UseCollectionExpression
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
            + "baseline generator, or canonical migration context. Configure one provider, baseline generator, "
            + "and canonical context per internal service provider; keep incompatible context registrations separate.");
    }
}
