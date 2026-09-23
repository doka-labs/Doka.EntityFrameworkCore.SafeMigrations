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
    public async Task RuntimeSqlGenerator_PassesEfHistoryBootstrapSqlToTheProviderWithoutAModel()
    {
        // Arrange
        await using var connection = await SqliteIntegrationTestBase.OpenConnectionAsync();
        await using var context = new SqliteSafeMigrationTestContext(connection);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var historySql = context
            .GetService<IHistoryRepository>()
            .GetCreateIfNotExistsScript();
        var relationalConnection = context.GetService<IRelationalConnection>();
        await using var historyTableQuery = connection.CreateCommand();
        historyTableQuery.CommandText = "SELECT COUNT(*) FROM main.sqlite_schema "
            + "WHERE type = 'table' AND name = '__EFMigrationsHistory';";

        // Act
        var commands = generator.Generate([new SqlOperation { Sql = historySql }], model: null);
        var command = commands.Single();
        _ = await command.ExecuteNonQueryAsync(relationalConnection);
        var historyTableCount = Convert.ToInt32(
            await historyTableQuery.ExecuteScalarAsync(CancellationToken.None),
            CultureInfo.InvariantCulture);

        // Assert
        Assert.Single(commands);
        Assert.Contains("CREATE TABLE IF NOT EXISTS", historySql, StringComparison.Ordinal);
        Assert.DoesNotContain("SafeMigrations batch", historySql, StringComparison.Ordinal);
        Assert.Equal(historySql.Trim(), command.CommandText.Trim());
        Assert.Same(connection, relationalConnection.DbConnection);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(1, historyTableCount);
    }

    [Fact]
    public async Task RuntimeSqlGenerator_PassesArbitraryModelLessSqlToTheProvider()
    {
        // Arrange
        await using var connection = await SqliteIntegrationTestBase.OpenConnectionAsync();
        await using var context = new SqliteSafeMigrationTestContext(connection);
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
