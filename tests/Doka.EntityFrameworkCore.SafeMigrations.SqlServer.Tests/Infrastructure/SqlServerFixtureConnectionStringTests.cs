namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies target connection settings without starting the SQL Server container.
/// </summary>
public sealed class SqlServerFixtureConnectionStringTests
{
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
