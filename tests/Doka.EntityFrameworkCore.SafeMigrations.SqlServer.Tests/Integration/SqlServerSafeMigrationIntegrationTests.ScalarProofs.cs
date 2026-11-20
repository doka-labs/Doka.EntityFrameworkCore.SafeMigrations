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
        var connectionString = await CreateDatabaseAsync(cancellationToken: CancellationToken.None);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        // Act
        await SqlServerScalarProofRegressionTests.VerifyLocalAsync(
            connection,
            connectionString,
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>
    /// Pre-canceled capture is rejected before binding an invalid scalar proof and leaves the session reusable.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ScalarProofs_RejectPreCanceledCaptureBeforeNativeAdmission()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(cancellationToken: CancellationToken.None);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        var plan = new SqlServerSafeMigrationRuntimePlan(
            "N'matching'",
            "1=1",
            SafeMigrationRepairCapability.None,
            "0=1")
        {
            PhysicalTableSupportExpression = "(SELECT missing FROM dbo.scalar_pre_cancel_missing)",
        };

        var results = new SafeMigrationProviderAnalysis[1];
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var failure = await Record.ExceptionAsync(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
                connection,
                null,
                10,
                [plan],
                0,
                results,
                cancellation.Token));

        await using var reuse = connection.CreateCommand();
        reuse.CommandText = "SELECT 17;";
        var reusedValue = await reuse.ExecuteScalarAsync(CancellationToken.None);

        // Assert
        Assert.IsType<OperationCanceledException>(failure, exactMatch: false);
        Assert.Null(results[0]);
        Assert.Equal(17, reusedValue);
    }
}
