namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Verifies delayed scalar proof contracts through native SqlClient on a clean owned database.</summary>
    [SqlServerLiveFact]
    public async Task ScalarProofs_KeepFreshValuesAndPrivateScopesOnSqlServer()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        // Act
        await SqlServerScalarProofRegressionTests.VerifyLocalAsync(connection);

        // Assert
        Assert.Equal(ConnectionState.Open, connection.State);
    }
}
