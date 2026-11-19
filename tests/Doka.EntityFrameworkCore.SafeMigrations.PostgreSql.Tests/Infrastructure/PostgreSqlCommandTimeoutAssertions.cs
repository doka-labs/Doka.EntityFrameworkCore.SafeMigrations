namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Distinguishes the Npgsql command-timeout contract from unrelated PostgreSQL or transport errors.</summary>
internal static class PostgreSqlCommandTimeoutAssertions
{
    /// <summary>Requires the original Npgsql exception and its client timeout cause.</summary>
    /// <param name="failure">The actual command failure captured after the configured timeout.</param>
    internal static void AssertTimeout(
        Exception? failure
    )
    {
        var native = Assert.IsType<NpgsqlException>(failure);
        Assert.IsType<TimeoutException>(native.InnerException);
    }
}
