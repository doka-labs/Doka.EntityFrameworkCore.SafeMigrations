namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>
/// Provides isolated SQLite coverage for service composition.
/// </summary>
public sealed class SqliteServiceCompositionTests
{
    /// <summary>
    /// Registration composes the official provider and SafeMigrations services.
    /// </summary>
    [Fact]
    public async Task Registration_ComposesTheOfficialProviderAndSafeMigrationsServices()
    {
        // Arrange
        await using var connection = await SqliteIntegrationTestBase.OpenConnectionAsync(
            cancellationToken: CancellationToken.None);

        await using var context = new SqliteSafeMigrationTestContext(connection);

        // Act
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var runner = context.GetService<ISafeMigrationRunner>();
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Assert
        Assert.IsType<SqliteSafeMigrationsSqlGenerator>(generator);
        Assert.IsType<SafeMigrationRunner>(runner);
        Assert.IsType<SqliteSafeMigrationProviderAnalyzer>(analyzer);
    }

    /// <summary>
    /// Runtime SQL generator passes EF history bootstrap SQL to the provider without a model.
    /// </summary>
    [Fact]
    public async Task RuntimeSqlGenerator_PassesEfHistoryBootstrapSqlToTheProviderWithoutAModel()
    {
        // Arrange
        await using var connection = await SqliteIntegrationTestBase.OpenConnectionAsync(
            cancellationToken: CancellationToken.None);

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

    /// <summary>
    /// Runtime SQL generator passes arbitrary model less SQL to the provider.
    /// </summary>
    [Fact]
    public async Task RuntimeSqlGenerator_PassesArbitraryModelLessSqlToTheProvider()
    {
        // Arrange
        await using var connection = await SqliteIntegrationTestBase.OpenConnectionAsync(
            cancellationToken: CancellationToken.None);

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

    /// <summary>
    /// Registration without SQLite provider fails with provider specific message.
    /// </summary>
    [Fact]
    public void RegistrationWithoutSqliteProvider_FailsWithProviderSpecificMessage()
    {
        // Arrange
        var options = new DbContextOptionsBuilder();

        // Act
        options.UseSqliteSafeMigrations();

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            using var context = new DbContext(options.Options);
        });

        // Assert
        Assert.Contains("SQLite", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Configured overloads persist scaffolding and canonical context contracts.
    /// </summary>
    [Fact]
    public void ConfiguredOverloads_PersistScaffoldingAndCanonicalContextContracts()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<SqliteSafeMigrationTestContext>();

        // Act
        options.UseSqliteSafeMigrations<SqliteSafeMigrationTestContext, SqliteMigrationsSqlGenerator,
            SqliteSafeMigrationTestContext>(configuration =>
        {
            configuration.UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence);
            configuration.UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe);
        });

        var extension = options.Options.FindExtension<SqliteSafeMigrationsOptionsExtension>();

        // Assert
        Assert.NotNull(extension);
        Assert.Equal(typeof(SqliteSafeMigrationTestContext), extension.CanonicalContextType);
        Assert.Equal(SafeMigrationScaffoldingMode.LegacyConvergence, extension.ScaffoldingMode);
        Assert.Equal(SafeMigrationPolicy.RepairIfSafe, extension.LegacyConvergencePolicy);
    }

    /// <summary>
    /// Untyped configured registration persists design time configuration.
    /// </summary>
    [Fact]
    public void UntypedConfiguredRegistration_PersistsDesignTimeConfiguration()
    {
        // Arrange
        var options = new DbContextOptionsBuilder();

        // Act
        var result = options.UseSqliteSafeMigrations(configuration =>
            configuration.ExcludeModelManagedDataForExcludedTables());

        var extension = options.Options.FindExtension<SqliteSafeMigrationsOptionsExtension>();

        // Assert
        Assert.Same(options, result);
        Assert.NotNull(extension);
        Assert.Null(extension.CanonicalContextType);
        Assert.True(extension.ExcludeModelManagedDataForExcludedTablesEnabled);
    }

    /// <summary>
    /// Untyped canonical registration persists canonical context and configuration.
    /// </summary>
    [Fact]
    public void UntypedCanonicalRegistration_PersistsCanonicalContextAndConfiguration()
    {
        // Arrange
        var options = new DbContextOptionsBuilder();

        // Act
        var result = options.UseSqliteSafeMigrations<SqliteSafeMigrationTestContext>(configuration =>
            configuration.UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence));

        var extension = options.Options.FindExtension<SqliteSafeMigrationsOptionsExtension>();

        // Assert
        Assert.Same(options, result);
        Assert.NotNull(extension);
        Assert.Equal(typeof(SqliteSafeMigrationTestContext), extension.CanonicalContextType);
        Assert.Equal(SafeMigrationScaffoldingMode.LegacyConvergence, extension.ScaffoldingMode);
    }

    /// <summary>
    /// Untyped explicit baseline registration persists provider contracts.
    /// </summary>
    [Fact]
    public void UntypedExplicitBaselineRegistration_PersistsProviderContracts()
    {
        // Arrange
        var options = new DbContextOptionsBuilder();

        // Act
        var result = options.UseSqliteSafeMigrations<SqliteMigrationsSqlGenerator,
            SqliteSafeMigrationTestContext>();

        var extension = options.Options.FindExtension<SqliteSafeMigrationsOptionsExtension>();

        // Assert
        Assert.Same(options, result);
        Assert.NotNull(extension);
        Assert.Equal(typeof(SqliteMigrationsSqlGenerator), extension.BaselineGeneratorType);
        Assert.Equal(typeof(SqliteSafeMigrationTestContext), extension.CanonicalContextType);
    }

    /// <summary>
    /// Typed default registration preserves the runtime context contract.
    /// </summary>
    [Fact]
    public void TypedDefaultRegistration_PreservesTheRuntimeContextContract()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<SqliteSafeMigrationTestContext>();

        // Act
        var result = options.UseSqliteSafeMigrations();
        var extension = options.Options.FindExtension<SqliteSafeMigrationsOptionsExtension>();

        // Assert
        Assert.Same(options, result);
        Assert.NotNull(extension);
        Assert.Equal(typeof(SqliteMigrationsSqlGenerator), extension.BaselineGeneratorType);
        Assert.Null(extension.CanonicalContextType);
    }

    /// <summary>
    /// Typed configured registration persists design time configuration.
    /// </summary>
    [Fact]
    public void TypedConfiguredRegistration_PersistsDesignTimeConfiguration()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<SqliteSafeMigrationTestContext>();

        // Act
        var result = options.UseSqliteSafeMigrations(configuration =>
            configuration.ExcludeModelManagedDataForExcludedTables());

        var extension = options.Options.FindExtension<SqliteSafeMigrationsOptionsExtension>();

        // Assert
        Assert.Same(options, result);
        Assert.NotNull(extension);
        Assert.True(extension.ExcludeModelManagedDataForExcludedTablesEnabled);
    }

    /// <summary>
    /// Generic service registration preserves canonical context and fluent collection.
    /// </summary>
    [Fact]
    public void GenericServiceRegistration_PreservesCanonicalContextAndFluentCollection()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var result = services.AddSqliteSafeMigrations<SqliteMigrationsSqlGenerator,
            SqliteSafeMigrationTestContext>();

        // Assert
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
