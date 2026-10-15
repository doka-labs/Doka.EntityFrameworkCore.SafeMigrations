namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies scalar proofs SQL Server contracts against isolated databases.</summary>
public sealed class SqlServerSafeMigrationScalarProofsIntegrationTests : SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Creates the feature suite with the assembly-owned SQL Server fixture.</summary>
    /// <param name="fixture">The shared fixture providing a database per test.</param>
    public SqlServerSafeMigrationScalarProofsIntegrationTests(
        SqlServerContainerFixture fixture
    ) : base(fixture) { }

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
