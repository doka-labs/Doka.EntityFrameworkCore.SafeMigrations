namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Protects service registration identity, strict defaults, and provider-owned SQL boundaries.</summary>
public sealed class MySqlServiceCompositionTests
{
    private const string CanonicalConfigurationTypeName =
        "Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationCanonicalContextConfiguration";

    /// <summary>
    /// Keeps strict scaffolding as the default and persists explicitly selected legacy convergence policy.
    /// </summary>
    [Fact]
    public void ScaffoldingConfigurationDefaultsToStrictAndAcceptsLegacyConvergence()
    {
        // Arrange
        var strict = new DbContextOptionsBuilder();
        var legacy = new DbContextOptionsBuilder();

        // Act
        strict.UseMySqlSafeMigrations();
        legacy.UseMySqlSafeMigrations(options =>
            options.UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence));

        var strictInfo = strict.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>()!.Info;
        var legacyInfo = legacy.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>()!.Info;

        // Assert
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

    /// <summary>Separates service-provider caches when excluded-table seed ownership changes.</summary>
    [Fact]
    public void ExcludedTableDataOwnership_IsPersistedAndSeparatesServiceProviders()
    {
        // Arrange
        var defaults = new DbContextOptionsBuilder();
        var ownership = new DbContextOptionsBuilder();

        // Act
        defaults.UseMySqlSafeMigrations();
        ownership.UseMySqlSafeMigrations(options =>
            options.ExcludeModelManagedDataForExcludedTables());

        var defaultExtension = defaults.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>()!;
        var ownershipExtension = ownership.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>()!;

        // Assert
        Assert.False(defaultExtension.ExcludeModelManagedDataForExcludedTablesEnabled);
        Assert.True(ownershipExtension.ExcludeModelManagedDataForExcludedTablesEnabled);
        Assert.False(defaultExtension.Info.ShouldUseSameServiceProvider(ownershipExtension.Info));
        Assert.NotEqual(
            defaultExtension.Info.GetServiceProviderHashCode(),
            ownershipExtension.Info.GetServiceProviderHashCode());
    }

    /// <summary>Rejects a repair policy outside legacy mode without partially mutating provider options.</summary>
    [Fact]
    public void RepairPolicyWithoutLegacyModeIsRejectedBeforeOptionsMutation()
    {
        // Arrange
        var options = new DbContextOptionsBuilder();

        // Act
        var failure = Record.Exception(() =>
            options.UseMySqlSafeMigrations(configuration =>
                configuration.UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe)));

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(failure);

        Assert.Contains("requires LegacyConvergence", exception.Message, StringComparison.Ordinal);
        Assert.Null(options.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>());
    }

    /// <summary>Rejects a null scaffolding callback before registering a partially configured extension.</summary>
    [Fact]
    public void NullScaffoldingConfigurationIsRejectedBeforeOptionsMutation()
    {
        // Arrange
        var options = new DbContextOptionsBuilder();

        // Act
        var failure = Record.Exception(() =>
            options.UseMySqlSafeMigrations(configure: null!));

        // Assert
        Assert.IsType<ArgumentNullException>(failure);
        Assert.Null(options.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>());
    }

    /// <summary>
    /// Persists the canonical context and selected legacy policy through every configuration overload.
    /// </summary>
    [Fact]
    public void ConfiguredOverloadFamiliesPersistLegacyModeAndCanonicalContext()
    {
        // Arrange
        var canonical = new DbContextOptionsBuilder();
        var typed = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        var typedCanonical = new DbContextOptionsBuilder<SafeMigrationDbContext>();

        // Act
        canonical.UseMySqlSafeMigrations<SafeMigrationDbContext>(ConfigureLegacy);
        typed.UseMySqlSafeMigrations(ConfigureLegacy);
        typedCanonical.UseMySqlSafeMigrations<SafeMigrationDbContext, SafeMigrationDbContext>(ConfigureLegacy);

        var canonicalExtension = canonical.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>()!;
        var typedExtension = typed.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>()!;
        var typedCanonicalExtension = typedCanonical.Options.FindExtension<MySqlSafeMigrationsOptionsExtension>()!;

        // Assert
        Assert.Equal(SafeMigrationScaffoldingMode.LegacyConvergence, canonicalExtension.ScaffoldingMode);
        Assert.Equal(SafeMigrationPolicy.RepairIfSafe, canonicalExtension.LegacyConvergencePolicy);
        Assert.Equal(typeof(SafeMigrationDbContext), canonicalExtension.CanonicalContextType);
        Assert.Equal(SafeMigrationScaffoldingMode.LegacyConvergence, typedExtension.ScaffoldingMode);
        Assert.Equal(typeof(SafeMigrationDbContext), typedCanonicalExtension.CanonicalContextType);
        Assert.Equal(SafeMigrationScaffoldingMode.LegacyConvergence, typedCanonicalExtension.ScaffoldingMode);
        Assert.Equal(SafeMigrationPolicy.RepairIfSafe, typedCanonicalExtension.LegacyConvergencePolicy);
    }

    /// <summary>Does not register duplicate migration services for an equivalent repeated configuration.</summary>
    [Fact]
    public void RepeatedEquivalentRegistrationIsIdempotent()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddEntityFrameworkDokaMySqlSafeMigrations<SafeMigrationDbContext>();
        services.AddEntityFrameworkDokaMySqlSafeMigrations<SafeMigrationDbContext>();

        // Assert
        Assert.Single(CanonicalConfigurations(services));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IMigrationsAssembly));
    }

    /// <summary>
    /// Consumes only the leading design-time guard while retaining neighboring provider-generated commands.
    /// </summary>
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
        var alterDatabase = new AlterDatabaseOperation
        {
            Collation = "utf8mb4_unicode_ci",
            ["Doka:MySql:CharSet"] = "utf8mb4",
        };

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

    /// <summary>Leaves ordinary column removal to Doka instead of adding an unexpected SafeMigrations guard.</summary>
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

    /// <summary>Rejects a misplaced design-time guard with the provider handler's precise failure contract.</summary>
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
        var alterDatabase = new AlterDatabaseOperation
        {
            Collation = "utf8mb4_unicode_ci",
            ["Doka:MySql:CharSet"] = "utf8mb4",
        };

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

    /// <summary>Allows EF's model-free history bootstrap without requiring an application model.</summary>
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

    /// <summary>
    /// Passes unrelated model-free SQL through unchanged rather than treating it as owned migration SQL.
    /// </summary>
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

    /// <summary>
    /// Preserves the existing service collection when a conflicting provider registration is rejected.
    /// </summary>
    [Fact]
    public void ConflictingRegistrationFailsBeforeChangingTheServiceCollection()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddEntityFrameworkDokaMySqlSafeMigrations();

        // WHY: Retain this explicit materialization/copy path and its collection lowering.
        // ReSharper disable once UseCollectionExpression
        var originalDescriptors = services.ToArray();

        // Act
        var failure = Record.Exception(
            services.AddEntityFrameworkDokaMySqlSafeMigrations<SafeMigrationDbContext>);

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(failure);

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
