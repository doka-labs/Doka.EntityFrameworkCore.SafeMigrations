namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Prevents timeout qualification from passing on an unrelated driver failure.</summary>
public sealed class PostgreSqlCommandTimeoutAssertionsTests
{
    /// <summary>The Npgsql timeout wrapper is accepted without comparing localized message text.</summary>
    [Fact]
    public void NativeTimeoutCauseIsAccepted()
    {
        // Arrange
        var native = new NpgsqlException("fixture timeout", new TimeoutException("fixture timeout cause"));

        // Act
        var failure = Record.Exception(() => PostgreSqlCommandTimeoutAssertions.AssertTimeout(native));

        // Assert
        Assert.Null(failure);
    }

    /// <summary>Neither a server error nor an unrelated Npgsql wrapper proves a configured command timeout.</summary>
    /// <param name="serverError">Whether the negative control is a native server-side SQL error.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnrelatedErrorsAreRejected(
        bool serverError
    )
    {
        // Arrange
        Exception native = serverError
            ? new PostgresException("fixture undefined table", "ERROR", "ERROR", "42P01")
            : new NpgsqlException("fixture transport failure", new IOException("fixture connection failure"));

        // Act
        var failure = Record.Exception(() => PostgreSqlCommandTimeoutAssertions.AssertTimeout(native));

        // Assert
        Assert.IsAssignableFrom<XunitException>(failure);
    }
}
