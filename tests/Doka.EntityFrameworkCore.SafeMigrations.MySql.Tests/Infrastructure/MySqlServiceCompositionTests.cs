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
        // Arrange
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var alterDatabase = new AlterDatabaseOperation { Collation = "utf8mb4_unicode_ci" };
        alterDatabase["Doka:MySql:CharSet"] = "utf8mb4";

        MigrationOperation[] operations =
        [
            new SafeMigrationDesignTimeServicesRequiredOperation(),
            alterDatabase,
        ];

        // Act
        var commands = generator.Generate(operations, context.Model);

        // Assert
        var command = Assert.Single(commands);
        Assert.Contains("ALTER DATABASE", command.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DO 0", command.CommandText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeSqlGenerator_LeavesOrdinaryDropColumnProviderOwned()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var operation = new DropColumnOperation
        {
            Table = "historical_drop",
            Name = "obsolete",
        };

        // Act
        var commands = generator.Generate([operation], context.Model);

        // Assert
        var command = Assert.Single(commands);

        Assert.Contains("DROP COLUMN `obsolete`", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("doka_sm", command.CommandText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeSqlGenerator_RejectsMisplacedDesignTimeServicesGuard()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var alterDatabase = new AlterDatabaseOperation { Collation = "utf8mb4_unicode_ci" };
        alterDatabase["Doka:MySql:CharSet"] = "utf8mb4";

        MigrationOperation[] operations =
        [
            alterDatabase,
            new SafeMigrationDesignTimeServicesRequiredOperation(),
        ];

        // Act
        var exception = Record.Exception(() =>
            generator.Generate(operations, context.Model));

        // Assert
        var handlerException = Assert.IsType<MySqlMigrationOperationHandlerException>(exception);

        Assert.Contains(
            "must be the first migration operation",
            handlerException.InnerException?.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeSqlGenerator_PassesEfHistoryBootstrapSqlToTheProviderWithoutAModel()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var historySql = context
            .GetService<IHistoryRepository>()
            .GetCreateIfNotExistsScript();

        // Act
        var commands = generator.Generate([new SqlOperation { Sql = historySql }], model: null);
        var command = commands.Single();

        // Assert
        Assert.Single(commands);
        Assert.Contains("CREATE TABLE IF NOT EXISTS", historySql, StringComparison.Ordinal);
        Assert.DoesNotContain("SafeMigrations", historySql, StringComparison.Ordinal);
        Assert.Equal(historySql.Trim(), command.CommandText.Trim());
    }

    [Fact]
    public void RuntimeSqlGenerator_PassesArbitraryModelLessSqlToTheProvider()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var generator = context.GetService<IMigrationsSqlGenerator>();

        // Act
        var commands = generator.Generate(
            [new SqlOperation { Sql = "SELECT 1;" }],
            model: null);

        // Assert
        var command = Assert.Single(commands);

        Assert.Equal("SELECT 1;", command.CommandText.Trim());
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
