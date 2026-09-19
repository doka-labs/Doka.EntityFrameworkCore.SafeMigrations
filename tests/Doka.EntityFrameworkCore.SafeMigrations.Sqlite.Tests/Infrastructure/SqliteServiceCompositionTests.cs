namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed class SqliteServiceCompositionTests
{
    [Fact]
    public async Task Registration_ComposesTheOfficialProviderAndSafeMigrationsServices()
    {
        await using var connection = await SqliteIntegrationTestBase.OpenConnectionAsync();
        await using var context = new SqliteSafeMigrationTestContext(connection);

        var generator = context.GetService<IMigrationsSqlGenerator>();
        var runner = context.GetService<ISafeMigrationRunner>();
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        Assert.IsType<SqliteSafeMigrationsSqlGenerator>(generator);
        Assert.IsType<SafeMigrationRunner>(runner);
        Assert.IsType<SqliteSafeMigrationProviderAnalyzer>(analyzer);
    }

    [Fact]
    public void RegistrationWithoutSqliteProvider_FailsWithProviderSpecificMessage()
    {
        var options = new DbContextOptionsBuilder();
        options.UseSqliteSafeMigrations();

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            using var context = new DbContext(options.Options);
        });

        Assert.Contains("SQLite", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredOverloads_PersistScaffoldingAndCanonicalContextContracts()
    {
        var options = new DbContextOptionsBuilder<SqliteSafeMigrationTestContext>();
        options.UseSqliteSafeMigrations<SqliteSafeMigrationTestContext, SqliteMigrationsSqlGenerator,
            SqliteSafeMigrationTestContext>(configuration =>
        {
            configuration.UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence);
            configuration.UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe);
        });

        var extension = options.Options.FindExtension<SqliteSafeMigrationsOptionsExtension>();

        Assert.NotNull(extension);
        Assert.Equal(typeof(SqliteSafeMigrationTestContext), extension.CanonicalContextType);
        Assert.Equal(SafeMigrationScaffoldingMode.LegacyConvergence, extension.ScaffoldingMode);
        Assert.Equal(SafeMigrationPolicy.RepairIfSafe, extension.LegacyConvergencePolicy);
    }

    [Fact]
    public void UntypedConfiguredRegistration_PersistsDesignTimeConfiguration()
    {
        var options = new DbContextOptionsBuilder();

        var result = options.UseSqliteSafeMigrations(configuration =>
            configuration.ExcludeModelManagedDataForExcludedTables());

        var extension = options.Options.FindExtension<SqliteSafeMigrationsOptionsExtension>();

        Assert.Same(options, result);
        Assert.NotNull(extension);
        Assert.Null(extension.CanonicalContextType);
        Assert.True(extension.ExcludeModelManagedDataForExcludedTablesEnabled);
    }

    [Fact]
    public void UntypedCanonicalRegistration_PersistsCanonicalContextAndConfiguration()
    {
        var options = new DbContextOptionsBuilder();

        var result = options.UseSqliteSafeMigrations<SqliteSafeMigrationTestContext>(configuration =>
            configuration.UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence));

        var extension = options.Options.FindExtension<SqliteSafeMigrationsOptionsExtension>();

        Assert.Same(options, result);
        Assert.NotNull(extension);
        Assert.Equal(typeof(SqliteSafeMigrationTestContext), extension.CanonicalContextType);
        Assert.Equal(SafeMigrationScaffoldingMode.LegacyConvergence, extension.ScaffoldingMode);
    }

    [Fact]
    public void UntypedExplicitBaselineRegistration_PersistsProviderContracts()
    {
        var options = new DbContextOptionsBuilder();

        var result = options.UseSqliteSafeMigrations<SqliteMigrationsSqlGenerator,
            SqliteSafeMigrationTestContext>();

        var extension = options.Options.FindExtension<SqliteSafeMigrationsOptionsExtension>();

        Assert.Same(options, result);
        Assert.NotNull(extension);
        Assert.Equal(typeof(SqliteMigrationsSqlGenerator), extension.BaselineGeneratorType);
        Assert.Equal(typeof(SqliteSafeMigrationTestContext), extension.CanonicalContextType);
    }

    [Fact]
    public void TypedDefaultRegistration_PreservesTheRuntimeContextContract()
    {
        var options = new DbContextOptionsBuilder<SqliteSafeMigrationTestContext>();

        var result = options.UseSqliteSafeMigrations();
        var extension = options.Options.FindExtension<SqliteSafeMigrationsOptionsExtension>();

        Assert.Same(options, result);
        Assert.NotNull(extension);
        Assert.Equal(typeof(SqliteMigrationsSqlGenerator), extension.BaselineGeneratorType);
        Assert.Null(extension.CanonicalContextType);
    }

    [Fact]
    public void TypedConfiguredRegistration_PersistsDesignTimeConfiguration()
    {
        var options = new DbContextOptionsBuilder<SqliteSafeMigrationTestContext>();

        var result = options.UseSqliteSafeMigrations(configuration =>
            configuration.ExcludeModelManagedDataForExcludedTables());

        var extension = options.Options.FindExtension<SqliteSafeMigrationsOptionsExtension>();

        Assert.Same(options, result);
        Assert.NotNull(extension);
        Assert.True(extension.ExcludeModelManagedDataForExcludedTablesEnabled);
    }

    [Fact]
    public void GenericServiceRegistration_PreservesCanonicalContextAndFluentCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddSqliteSafeMigrations<SqliteMigrationsSqlGenerator,
            SqliteSafeMigrationTestContext>();

        var canonicalConfiguration = Assert.Single(
            services,
            static descriptor => descriptor.ServiceType == typeof(SafeMigrationCanonicalContextConfiguration));

        var configuration = Assert.IsType<SafeMigrationCanonicalContextConfiguration>(
            canonicalConfiguration.ImplementationInstance);

        Assert.Same(services, result);
        Assert.Equal(typeof(SqliteMigrationsSqlGenerator), configuration.BaselineGeneratorType);
        Assert.Equal(typeof(SqliteSafeMigrationTestContext), configuration.ContextType);
    }
}
