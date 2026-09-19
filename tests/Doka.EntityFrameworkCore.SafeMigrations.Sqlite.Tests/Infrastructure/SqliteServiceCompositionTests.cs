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
}
