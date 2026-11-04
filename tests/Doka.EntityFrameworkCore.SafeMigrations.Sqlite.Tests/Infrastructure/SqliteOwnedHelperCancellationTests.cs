namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Verifies explicit caller cancellation at the owned native test-helper boundary.</summary>
public sealed class SqliteOwnedHelperCancellationTests : SqliteIntegrationTestBase
{
    /// <summary>A non-canceled caller can open, write and read through the same explicit token.</summary>
    [Fact]
    public async Task ActiveCallerTokenAllowsNativeHelperWork()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();

        // Act
        await using var connection = await OpenConnectionAsync(cancellation.Token);
        await ExecuteSqlAsync(connection, "CREATE TABLE helper_probe (Id INTEGER NOT NULL); "
            + "INSERT INTO helper_probe VALUES (17);", cancellation.Token);
        var count = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM helper_probe;", cancellation.Token);

        // Assert
        Assert.Equal(1, count);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Pre-canceled helper calls retain the caller token and cannot mutate the existing catalog.</summary>
    /// <param name="operation">The independently exercised open, command or scalar helper.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PreCanceledHelpersPreserveTheCallerTokenAndExistingRows(int operation)
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE helper_probe (Id INTEGER NOT NULL); INSERT INTO helper_probe VALUES (17);";
        await command.ExecuteNonQueryAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        var exception = await Record.ExceptionAsync(async () =>
        {
            switch (operation)
            {
                case 0:
                    await using (await OpenConnectionAsync(cancellation.Token))
                    {
                    }

                    break;
                case 1:
                    await ExecuteSqlAsync(connection, "INSERT INTO helper_probe VALUES (29);", cancellation.Token);

                    break;
                case 2:
                    _ = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM helper_probe;", cancellation.Token);

                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        });

        // Assert
        var canceled = Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        command.CommandText = "SELECT COUNT(*) FROM helper_probe;";
        Assert.Equal(1L, await command.ExecuteScalarAsync(CancellationToken.None));
        Assert.Equal(ConnectionState.Open, connection.State);
    }
}
