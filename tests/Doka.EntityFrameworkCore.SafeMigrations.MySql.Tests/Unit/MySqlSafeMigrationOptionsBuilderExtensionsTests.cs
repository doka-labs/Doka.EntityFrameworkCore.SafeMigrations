namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Protects connection capability registration without mutating borrowed native resources.</summary>
public sealed class MySqlSafeMigrationOptionsBuilderExtensionsTests
{
    private const string OwnedConnectionString =
        "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test";

    private static readonly MySqlServerVersion s_serverVersion =
        MySqlServerVersion.MySql(new Version(8, 4, 11));

    /// <summary>
    /// Enables required user variables without changing Doka's affected-row or connection invariants.
    /// </summary>
    [Fact]
    public void RegistrationRequiresUserVariablesAndPreservesDokaConnectionInvariants()
    {
        // Arrange
        var optionsBuilder = new DbContextOptionsBuilder<DbContext>();

        // Act
        var options = optionsBuilder
            .UseMySql(OwnedConnectionString, s_serverVersion)
            .UseMySqlSafeMigrations()
            .Options;

        using var context = new DbContext(options);
        var effective = new MySqlConnectionStringBuilder(
            context.Database.GetDbConnection()
                .ConnectionString);

        // Assert
        Assert.True(effective.AllowUserVariables);
        Assert.False(effective.UseAffectedRows);
        Assert.Equal(ConnectorMySqlGuidFormat.Binary16, effective.GuidFormat);
    }

    /// <summary>Retains the user-variable requirement when SafeMigrations is configured before Doka.</summary>
    [Fact]
    public void RegistrationBeforeProviderPreservesUserVariableRequirement()
    {
        // Arrange
        var optionsBuilder = new DbContextOptionsBuilder<DbContext>();

        // Act
        optionsBuilder.UseMySqlSafeMigrations();
        optionsBuilder.UseMySql(OwnedConnectionString, s_serverVersion);

        using var context = new DbContext(optionsBuilder.Options);
        var effective = new MySqlConnectionStringBuilder(
            context.Database.GetDbConnection()
                .ConnectionString);

        // Assert
        Assert.True(effective.AllowUserVariables);
        Assert.False(effective.UseAffectedRows);
        Assert.Equal(ConnectorMySqlGuidFormat.Binary16, effective.GuidFormat);
    }

    /// <summary>Rejects an incompatible borrowed connection without mutating its connection settings.</summary>
    [Fact]
    public void RegistrationBeforeProviderRejectsIncompatibleBorrowedConnectionWithoutMutation()
    {
        // Arrange
        var connectionString = new MySqlConnectionStringBuilder(OwnedConnectionString)
        {
            AllowUserVariables = false,
            GuidFormat = ConnectorMySqlGuidFormat.Binary16,
        }.ConnectionString;

        using var connection = new MySqlConnection(connectionString);
        var optionsBuilder = new DbContextOptionsBuilder<DbContext>();
        optionsBuilder.UseMySqlSafeMigrations();

        // Act
        var capturedException = Record.Exception(() =>
            optionsBuilder.UseMySql(connection, s_serverVersion));

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(capturedException, exactMatch: false);
        Assert.Contains("AllowUserVariables=true", exception.Message, StringComparison.Ordinal);
        Assert.Equal(connectionString, connection.ConnectionString);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    /// <summary>Rejects contradictory provider-owned connection options before database I/O.</summary>
    /// <param name="option">The independently changed connection configuration option.</param>
    /// <param name="expectedMessage">The diagnostic fragment identifying the rejected connection invariant.</param>
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
        // Arrange
        var options = new DbContextOptionsBuilder<DbContext>()
            .UseMySql($"{OwnedConnectionString};{option}", s_serverVersion)
            .UseMySqlSafeMigrations()
            .Options;

        using var context = new DbContext(options);

        // Act
        var capturedException = Record.Exception(() =>
            context.Database.GetDbConnection());

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(capturedException, exactMatch: false);
        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Does not repair a caller-owned connection by silently enabling user variables.</summary>
    [Fact]
    public void BorrowedConnectionWithoutUserVariablesFailsWithoutMutation()
    {
        // Arrange
        var connectionString = new MySqlConnectionStringBuilder(OwnedConnectionString)
        {
            AllowUserVariables = false,
            GuidFormat = ConnectorMySqlGuidFormat.Binary16,
        }.ConnectionString;

        using var connection = new MySqlConnection(connectionString);
        var optionsBuilder = new DbContextOptionsBuilder<DbContext>()
            .UseMySql(connection, s_serverVersion);

        // Act
        var capturedException = Record.Exception(() =>
            optionsBuilder.UseMySqlSafeMigrations());

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(capturedException, exactMatch: false);
        Assert.Contains("AllowUserVariables=true", exception.Message, StringComparison.Ordinal);
        Assert.Equal(connectionString, connection.ConnectionString);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    /// <summary>Accepts a borrowed data source that already satisfies the required connection contract.</summary>
    [Fact]
    public void CompatibleBorrowedDataSourceIsAccepted()
    {
        // Arrange
        var connectionString = new MySqlConnectionStringBuilder(OwnedConnectionString)
        {
            AllowUserVariables = true,
            GuidFormat = ConnectorMySqlGuidFormat.Binary16,
        }.ConnectionString;

        using var dataSource = new MySqlDataSourceBuilder(connectionString).Build();

        // Act
        var options = new DbContextOptionsBuilder<DbContext>()
            .UseMySql(dataSource, s_serverVersion)
            .UseMySqlSafeMigrations()
            .Options;

        using var context = new DbContext(options);
        var effective = new MySqlConnectionStringBuilder(
            context.Database.GetDbConnection()
                .ConnectionString);

        // Assert
        Assert.True(effective.AllowUserVariables);
        Assert.False(effective.UseAffectedRows);
        Assert.Equal(ConnectorMySqlGuidFormat.Binary16, effective.GuidFormat);
    }

    /// <summary>Rejects an incompatible borrowed data source without changing its configuration.</summary>
    [Fact]
    public void BorrowedDataSourceWithoutUserVariablesFailsWithoutMutation()
    {
        // Arrange
        var connectionString = new MySqlConnectionStringBuilder(OwnedConnectionString)
        {
            AllowUserVariables = false,
            GuidFormat = ConnectorMySqlGuidFormat.Binary16,
        }.ConnectionString;

        using var dataSource = new MySqlDataSourceBuilder(connectionString).Build();
        var optionsBuilder = new DbContextOptionsBuilder<DbContext>()
            .UseMySql(dataSource, s_serverVersion);

        // Act
        var capturedException = Record.Exception(optionsBuilder.UseMySqlSafeMigrations);

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(capturedException, exactMatch: false);
        Assert.Contains("AllowUserVariables=true", exception.Message, StringComparison.Ordinal);
        Assert.Equal(connectionString, dataSource.ConnectionString);
    }
}
