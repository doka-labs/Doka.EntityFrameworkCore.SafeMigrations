namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlServiceCompositionTests
{
    private const string CanonicalConfigurationTypeName =
        "Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationCanonicalContextConfiguration";

    [Fact]
    public void ScaffoldingConfigurationDefaultsToStrictAndAcceptsLegacyConvergence()
    {
        var strict = new DbContextOptionsBuilder();
        strict.UseMySqlSafeMigrations();

        var legacy = new DbContextOptionsBuilder();
        legacy.UseMySqlSafeMigrations(options =>
            options.UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence));

        var strictInfo = strict.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>()!.Info;
        var legacyInfo = legacy.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>()!.Info;

        Assert.Equal(
            SafeMigrationScaffoldingMode.Strict,
            strict.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>()!.ScaffoldingMode);
        Assert.Equal(
            SafeMigrationScaffoldingMode.LegacyConvergence,
            legacy.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>()!.ScaffoldingMode);
        Assert.Equal(strictInfo.GetServiceProviderHashCode(), legacyInfo.GetServiceProviderHashCode());
        Assert.True(strictInfo.ShouldUseSameServiceProvider(legacyInfo));
    }

    [Fact]
    public void NullScaffoldingConfigurationIsRejectedBeforeOptionsMutation()
    {
        var options = new DbContextOptionsBuilder();

        Assert.Throws<ArgumentNullException>(() =>
            options.UseMySqlSafeMigrations(configure: null!));
        Assert.Null(options.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>());
    }

    [Fact]
    public void ConfiguredOverloadFamiliesPersistLegacyModeAndCanonicalContext()
    {
        var canonical = new DbContextOptionsBuilder();
        canonical.UseMySqlSafeMigrations<SafeMigrationDbContext>(ConfigureLegacy);

        var typed = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        typed.UseMySqlSafeMigrations(ConfigureLegacy);

        var typedCanonical = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        typedCanonical.UseMySqlSafeMigrations<SafeMigrationDbContext, SafeMigrationDbContext>(ConfigureLegacy);

        var canonicalExtension = canonical.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>()!;
        var typedExtension = typed.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>()!;
        var typedCanonicalExtension = typedCanonical.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>()!;

        Assert.Equal(SafeMigrationScaffoldingMode.LegacyConvergence, canonicalExtension.ScaffoldingMode);
        Assert.Equal(typeof(SafeMigrationDbContext), canonicalExtension.CanonicalContextType);
        Assert.Equal(SafeMigrationScaffoldingMode.LegacyConvergence, typedExtension.ScaffoldingMode);
        Assert.Equal(typeof(SafeMigrationDbContext), typedCanonicalExtension.CanonicalContextType);
        Assert.Equal(SafeMigrationScaffoldingMode.LegacyConvergence, typedCanonicalExtension.ScaffoldingMode);
    }

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

    private static void ConfigureLegacy(
        SafeMigrationOptionsBuilder options
    ) => options.UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence);
}
