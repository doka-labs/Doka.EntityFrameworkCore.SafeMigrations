namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>
/// Ensures a completed operation cannot fake proof that the native cancellation boundary was reached.
/// </summary>
public sealed class MySqlNativeCancellationCheckpointTests
{
    /// <summary>Rejects vacuous completion without attempting an unrelated database connection.</summary>
    /// <param name="completion">
    /// The successful, canceled, or failed producer that never reached the native checkpoint.
    /// </param>
    [Theory]
    [InlineData("success")]
    [InlineData("cancellation")]
    [InlineData("failure")]
    public async Task CompletedOperationDoesNotProveNativeDispatch(
        string completion
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var operation = completion switch
        {
            "success" => Task.FromResult(42),
            "cancellation" => Task.FromCanceled<int>(new CancellationToken(canceled: true)),
            "failure" => Task.FromException<int>(new InvalidOperationException("unobserved-native-dispatch")),
            _ => throw new ArgumentOutOfRangeException(nameof(completion)),
        };

        // Act
        var exception = await Record.ExceptionAsync(() => MySqlNativeCancellationCheckpoint.CancelWhenDispatchedAsync(
            "not a connection string",
            1,
            "owned_statement",
            cancellation,
            operation,
            CancellationToken.None));

        // Assert
        var invalid = Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("The operation completed before its native cancellation checkpoint.",
            invalid.Message);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(operation.IsCompleted);
        if (completion == "cancellation")
        {
            Assert.IsType<OperationCanceledException>(
                invalid.Data["MySqlNativeCheckpoint.OperationFailure"], exactMatch: false);
        }
        else if (completion == "failure")
        {
            var aggregate = Assert.IsType<AggregateException>(operation.Exception);
            Assert.Same(aggregate.InnerException, invalid.Data["MySqlNativeCheckpoint.OperationFailure"]);
        }
    }
}
