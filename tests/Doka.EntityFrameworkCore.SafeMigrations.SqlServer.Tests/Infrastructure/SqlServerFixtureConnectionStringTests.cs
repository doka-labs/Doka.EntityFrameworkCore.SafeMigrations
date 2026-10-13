namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies target connection settings without starting the SQL Server container.
/// </summary>
public sealed class SqlServerFixtureConnectionStringTests
{
    /// <summary>
    /// Preserves caller-owned command and transaction wait budgets in both tooling contexts.
    /// </summary>
    /// <param name="legacy">Whether the context uses legacy-convergence scaffolding.</param>
    /// <param name="connectTimeout">The login and transaction-manager timeout, or null for the driver default.</param>
    /// <param name="commandTimeout">The command timeout, or null for the driver default.</param>
    [Theory]
    [InlineData(false, null, null)]
    [InlineData(true, null, null)]
    [InlineData(false, 17, 23)]
    [InlineData(true, 17, 23)]
    [InlineData(false, 120, 120)]
    [InlineData(true, 120, 120)]
    public void ToolingContexts_PreserveExplicitTimeoutsAndDriverDefaults(
        bool legacy,
        int? connectTimeout,
        int? commandTimeout
    )
    {
        // Arrange
        var settings = new SqlConnectionStringBuilder
        {
            DataSource = "127.0.0.1,1",
            InitialCatalog = "tooling_timeout_contract",
            IntegratedSecurity = true,
            Encrypt = false,
        };

        if (connectTimeout is { } connectionBudget)
        {
            settings.ConnectTimeout = connectionBudget;
        }

        if (commandTimeout is { } commandBudget)
        {
            settings.CommandTimeout = commandBudget;
        }

        using SqlServerScaffoldingModelDbContext context = legacy
            ? new SqlServerLegacySafeMigrationScaffoldingDbContext(settings.ConnectionString)
            : new SqlServerSafeMigrationScaffoldingDbContext(settings.ConnectionString);

        // Act
        var connection = (SqlConnection)context.Database.GetDbConnection();
        using var command = connection.CreateCommand();

        // Assert
        Assert.Equal(connectTimeout ?? 15, connection.ConnectionTimeout);
        Assert.Equal(commandTimeout ?? 30, command.CommandTimeout);
        Assert.Null(context.Database.GetCommandTimeout());
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    /// <summary>
    /// Disables pooling even when the root connection string explicitly enables it.
    /// </summary>
    /// <param name="rootPooling">The pooling setting inherited from the root connection string.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TargetConnectionString_DisablesPoolingAndPreservesRootSettings(
        bool rootPooling
    )
    {
        // Arrange
        var root = new SqlConnectionStringBuilder
        {
            DataSource = "127.0.0.1,1433",
            InitialCatalog = "master",
            UserID = "fixture_login",
            Password = "fixture_password",
            Pooling = rootPooling,
            TrustServerCertificate = false,
            ConnectTimeout = 17,
        };

        // Act
        var connectionString = SqlServerContainerFixture.BuildTestConnectionString(root.ConnectionString, "sm_target");
        var target = new SqlConnectionStringBuilder(connectionString);

        // Assert
        Assert.False(target.Pooling);
        Assert.Equal("sm_target", target.InitialCatalog);
        Assert.True(target.TrustServerCertificate);
        Assert.Equal(root.DataSource, target.DataSource);
        Assert.Equal(root.UserID, target.UserID);
        Assert.Equal(root.Password, target.Password);
        Assert.Equal(root.ConnectTimeout, target.ConnectTimeout);
    }

    /// <summary>
    /// Keeps separate target database scopes without changing the root settings.
    /// </summary>
    [Fact]
    public void TargetConnectionString_ScopesEachDatabaseWithoutChangingRoot()
    {
        // Arrange
        const string rootConnectionString =
            "Server=127.0.0.1,1433;Database=master;Integrated Security=True;Pooling=True;TrustServerCertificate=False;";

        // Act
        var first = new SqlConnectionStringBuilder(
            SqlServerContainerFixture.BuildTestConnectionString(rootConnectionString, "sm_first"));

        var second = new SqlConnectionStringBuilder(
            SqlServerContainerFixture.BuildTestConnectionString(rootConnectionString, "sm_second"));

        var root = new SqlConnectionStringBuilder(rootConnectionString);

        // Assert
        Assert.Equal("sm_first", first.InitialCatalog);
        Assert.Equal("sm_second", second.InitialCatalog);
        Assert.NotEqual(first.ConnectionString, second.ConnectionString);
        Assert.Equal("master", root.InitialCatalog);
        Assert.True(root.Pooling);
        Assert.False(root.TrustServerCertificate);
    }
}
