namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>
    /// Opens the same logical connection repeatedly after setup changes the database collation.
    /// </summary>
    [SqlServerLiveFact]
    public async Task FixtureConnection_AfterCollationChangeSupportsRepeatedLogicalOpens()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();

        await using var connection = new SqlConnection(connectionString);

        await connection.OpenAsync();

        await using var setup = connection.CreateCommand();

        setup.CommandText = "SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'));";
        var initialCollation = Convert.ToString(await setup.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        var collation = initialCollation == "Latin1_General_100_CS_AS"
            ? "Latin1_General_100_CI_AS"
            : "Latin1_General_100_CS_AS";

        setup.CommandText = "ALTER DATABASE CURRENT COLLATE " + collation + ";";

        await setup.ExecuteNonQueryAsync();
        await connection.CloseAsync();

        var expectedDatabase = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        var observedStates = new List<string>();

        // Act
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();

            command.CommandText = "SELECT DB_NAME() + N':' "
                + "+ CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'));";

            var observed = await command.ExecuteScalarAsync();

            observedStates.Add(Convert.ToString(observed, CultureInfo.InvariantCulture) ?? string.Empty);

            await connection.CloseAsync();
        }

        // Assert
        Assert.NotEqual(initialCollation, collation);
        Assert.Equal(Enumerable.Repeat(expectedDatabase + ":" + collation, 3), observedStates);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }
}
