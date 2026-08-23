namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlServiceCompositionTests
{
    private const string CanonicalConfigurationTypeName =
        "Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationCanonicalContextConfiguration";

    [Fact]
    public void RepeatedEquivalentRegistrationIsIdempotent()
    {
        var services = new ServiceCollection();

        services.AddEntityFrameworkDokaMySqlSafeMigrations<SafeMigrationDbContext>();
        services.AddEntityFrameworkDokaMySqlSafeMigrations<SafeMigrationDbContext>();

        Assert.Single(CanonicalConfigurations(services));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IMigrationsAssembly));
    }

    [Fact]
    public void ConflictingRegistrationFailsBeforeChangingTheServiceCollection()
    {
        var services = new ServiceCollection();
        services.AddEntityFrameworkDokaMySqlSafeMigrations();
        var originalDescriptors = services.ToArray();

        var exception = Assert.Throws<InvalidOperationException>(
            services.AddEntityFrameworkDokaMySqlSafeMigrations<SafeMigrationDbContext>);

        Assert.Contains("different provider", exception.Message, StringComparison.Ordinal);
        Assert.Equal(originalDescriptors, services);
        Assert.Single(CanonicalConfigurations(services));
    }

    private static IEnumerable<ServiceDescriptor> CanonicalConfigurations(
        IServiceCollection services
    ) => services.Where(static descriptor => descriptor.ServiceType.FullName == CanonicalConfigurationTypeName);
}
