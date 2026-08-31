namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlSafeMigrationOptionsBuilderExtensionsTests
{
    private const string OwnedConnectionString =
        "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test";

    private static readonly MySqlServerVersion s_serverVersion =
        MySqlServerVersion.MySql(new Version(8, 4, 11));

    [Fact]
    public void RegistrationRequiresUserVariablesAndPreservesDokaConnectionInvariants()
    {
        var options = new DbContextOptionsBuilder<DbContext>()
            .UseMySql(OwnedConnectionString, s_serverVersion)
            .UseMySqlSafeMigrations()
            .Options;

        using var context = new DbContext(options);
        var effective = new MySqlConnectionStringBuilder(
            context.Database.GetDbConnection().ConnectionString);

        Assert.True(effective.AllowUserVariables);
        Assert.False(effective.UseAffectedRows);
        Assert.Equal(MySqlConnector.MySqlGuidFormat.Binary16, effective.GuidFormat);
    }

    [Fact]
    public void RegistrationBeforeProviderPreservesUserVariableRequirement()
    {
        var optionsBuilder = new DbContextOptionsBuilder<DbContext>();
        optionsBuilder.UseMySqlSafeMigrations();
        optionsBuilder.UseMySql(OwnedConnectionString, s_serverVersion);

        using var context = new DbContext(optionsBuilder.Options);
        var effective = new MySqlConnectionStringBuilder(
            context.Database.GetDbConnection().ConnectionString);

        Assert.True(effective.AllowUserVariables);
        Assert.False(effective.UseAffectedRows);
        Assert.Equal(MySqlConnector.MySqlGuidFormat.Binary16, effective.GuidFormat);
    }

    [Fact]
    public void RegistrationBeforeProviderRejectsIncompatibleBorrowedConnectionWithoutMutation()
    {
        var connectionString = new MySqlConnectionStringBuilder(OwnedConnectionString)
        {
            AllowUserVariables = false,
            GuidFormat = MySqlConnector.MySqlGuidFormat.Binary16,
        }.ConnectionString;

        using var connection = new MySqlConnection(connectionString);
        var optionsBuilder = new DbContextOptionsBuilder<DbContext>();
        optionsBuilder.UseMySqlSafeMigrations();

        var exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            optionsBuilder.UseMySql(connection, s_serverVersion));

        Assert.Contains("AllowUserVariables=true", exception.Message, StringComparison.Ordinal);
        Assert.Equal(connectionString, connection.ConnectionString);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Theory]
    [InlineData("AllowUserVariables=false", "AllowUserVariables=true")]
    [InlineData("UseAffectedRows=true", "matched-row semantics")]
    [InlineData("GuidFormat=Char36", "GuidFormat=Binary16")]
    [InlineData("OldGuids=true", "GuidFormat=Binary16")]
    public void ProviderOwnedContradictoryConnectionOptionsFailBeforeDatabaseIo(
        string option,
        string expectedMessage
    )
    {
        var options = new DbContextOptionsBuilder<DbContext>()
            .UseMySql($"{OwnedConnectionString};{option}", s_serverVersion)
            .UseMySqlSafeMigrations()
            .Options;

        using var context = new DbContext(options);

        var exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            context.Database.GetDbConnection());

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BorrowedConnectionWithoutUserVariablesFailsWithoutMutation()
    {
        var connectionString = new MySqlConnectionStringBuilder(OwnedConnectionString)
        {
            AllowUserVariables = false,
            GuidFormat = MySqlConnector.MySqlGuidFormat.Binary16,
        }.ConnectionString;

        using var connection = new MySqlConnection(connectionString);
        var optionsBuilder = new DbContextOptionsBuilder<DbContext>()
            .UseMySql(connection, s_serverVersion);

        var exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            optionsBuilder.UseMySqlSafeMigrations());

        Assert.Contains("AllowUserVariables=true", exception.Message, StringComparison.Ordinal);
        Assert.Equal(connectionString, connection.ConnectionString);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void CompatibleBorrowedDataSourceIsAccepted()
    {
        var connectionString = new MySqlConnectionStringBuilder(OwnedConnectionString)
        {
            AllowUserVariables = true,
            GuidFormat = MySqlConnector.MySqlGuidFormat.Binary16,
        }.ConnectionString;

        using var dataSource = new MySqlDataSourceBuilder(connectionString).Build();
        var options = new DbContextOptionsBuilder<DbContext>()
            .UseMySql(dataSource, s_serverVersion)
            .UseMySqlSafeMigrations()
            .Options;

        using var context = new DbContext(options);
        var effective = new MySqlConnectionStringBuilder(
            context.Database.GetDbConnection().ConnectionString);

        Assert.True(effective.AllowUserVariables);
        Assert.False(effective.UseAffectedRows);
        Assert.Equal(MySqlConnector.MySqlGuidFormat.Binary16, effective.GuidFormat);
    }

    [Fact]
    public void BorrowedDataSourceWithoutUserVariablesFailsWithoutMutation()
    {
        var connectionString = new MySqlConnectionStringBuilder(OwnedConnectionString)
        {
            AllowUserVariables = false,
            GuidFormat = MySqlConnector.MySqlGuidFormat.Binary16,
        }.ConnectionString;

        using var dataSource = new MySqlDataSourceBuilder(connectionString).Build();
        var optionsBuilder = new DbContextOptionsBuilder<DbContext>()
            .UseMySql(dataSource, s_serverVersion);

        var exception = Assert.ThrowsAny<InvalidOperationException>(optionsBuilder.UseMySqlSafeMigrations);

        Assert.Contains("AllowUserVariables=true", exception.Message, StringComparison.Ordinal);
        Assert.Equal(connectionString, dataSource.ConnectionString);
    }
}
