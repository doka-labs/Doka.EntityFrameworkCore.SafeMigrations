namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Rejects unrelated native errors as evidence that the configured command timeout was honored.</summary>
internal static class MySqlCommandTimeoutAssertions
{
    /// <summary>Requires the driver's command-timeout identity rather than an arbitrary server rejection.</summary>
    /// <param name="exception">The failure returned by the actual catalog operation.</param>
    internal static void AssertCommandTimeout(
        Exception? exception
    )
    {
        var native = Assert.IsType<MySqlException>(exception);
        Assert.Equal(MySqlErrorCode.CommandTimeoutExpired, native.ErrorCode);
        Assert.Equal((int)MySqlErrorCode.CommandTimeoutExpired, native.Number);
    }

    /// <summary>Requires the precise interruption identity exposed while reading a later native batch result.</summary>
    /// <param name="exception">The failure from the configured catalog batch with an uncanceled caller token.</param>
    internal static void AssertCatalogBatchTimeout(
        Exception? exception
    )
    {
        // WHY: Locked Connector 2.6.2 converts a timed-out first result in ActivateResultSet,
        // but ScanResultSetAsync rethrows QueryInterrupted unchanged for a later result.
        // Keep both execution-path contracts exact instead of accepting arbitrary native errors.
        var native = Assert.IsType<MySqlException>(exception);
        Assert.Equal(MySqlErrorCode.QueryInterrupted, native.ErrorCode);
        Assert.Equal((int)MySqlErrorCode.QueryInterrupted, native.Number);
        Assert.Equal("70100", native.SqlState);
    }
}
