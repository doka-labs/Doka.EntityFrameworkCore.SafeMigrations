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
            SafeMigrationPolicy.ThrowIfDifferent,
            strict.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>()!.LegacyConvergencePolicy);
        Assert.Equal(
            SafeMigrationScaffoldingMode.LegacyConvergence,
            legacy.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>()!.ScaffoldingMode);
        Assert.Equal(strictInfo.GetServiceProviderHashCode(), legacyInfo.GetServiceProviderHashCode());
        Assert.True(strictInfo.ShouldUseSameServiceProvider(legacyInfo));
    }

    [Fact]
    public void ExcludedTableDataOwnership_IsPersistedAndSeparatesServiceProviders()
    {
        var defaults = new DbContextOptionsBuilder();
        defaults.UseMySqlSafeMigrations();

        var ownership = new DbContextOptionsBuilder();
        ownership.UseMySqlSafeMigrations(options =>
            options.ExcludeModelManagedDataForExcludedTables());

        var defaultExtension = defaults.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>()!;
        var ownershipExtension = ownership.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>()!;

        Assert.False(defaultExtension.ExcludeModelManagedDataForExcludedTablesEnabled);
        Assert.True(ownershipExtension.ExcludeModelManagedDataForExcludedTablesEnabled);
        Assert.False(defaultExtension.Info.ShouldUseSameServiceProvider(ownershipExtension.Info));
        Assert.NotEqual(
            defaultExtension.Info.GetServiceProviderHashCode(),
            ownershipExtension.Info.GetServiceProviderHashCode());
    }

    [Fact]
    public void RepairPolicyWithoutLegacyModeIsRejectedBeforeOptionsMutation()
    {
        var options = new DbContextOptionsBuilder();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            options.UseMySqlSafeMigrations(configuration =>
                configuration.UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe)));

        Assert.Contains("requires LegacyConvergence", exception.Message, StringComparison.Ordinal);
        Assert.Null(options.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>());
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
        Assert.Equal(SafeMigrationPolicy.RepairIfSafe, canonicalExtension.LegacyConvergencePolicy);
        Assert.Equal(typeof(SafeMigrationDbContext), canonicalExtension.CanonicalContextType);
        Assert.Equal(SafeMigrationScaffoldingMode.LegacyConvergence, typedExtension.ScaffoldingMode);
        Assert.Equal(typeof(SafeMigrationDbContext), typedCanonicalExtension.CanonicalContextType);
        Assert.Equal(SafeMigrationScaffoldingMode.LegacyConvergence, typedCanonicalExtension.ScaffoldingMode);
        Assert.Equal(SafeMigrationPolicy.RepairIfSafe, typedCanonicalExtension.LegacyConvergencePolicy);
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
    public void RuntimeSqlGenerator_ConsumesLeadingDesignTimeServicesGuardWithoutChangingNeighborCommands()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var generator = context.GetService<IMigrationsSqlGenerator>();

        MigrationOperation[] operations =
        [
            new SafeMigrationDesignTimeServicesRequiredOperation(),
            new SqlOperation { Sql = "SELECT 1;" },
            new SqlOperation { Sql = "SELECT 2;" },
        ];

        var commands = generator.Generate(operations, context.Model);

        Assert.Collection(
            commands,
            command => Assert.Equal("SELECT 1;", command.CommandText.Trim()),
            command => Assert.Equal("SELECT 2;", command.CommandText.Trim()));
    }

    [Fact]
    public void RuntimeSqlGenerator_RejectsMisplacedDesignTimeServicesGuard()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        MigrationOperation[] operations =
        [
            new SqlOperation { Sql = "SELECT 1;" },
            new SafeMigrationDesignTimeServicesRequiredOperation(),
        ];

        var exception = Assert.Throws<MySqlMigrationOperationHandlerException>(() =>
            generator.Generate(operations, context.Model));

        Assert.Contains(
            "must be the first migration operation",
            exception.InnerException?.Message,
            StringComparison.Ordinal);
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
    ) => options
        .UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence)
        .UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe);
}
