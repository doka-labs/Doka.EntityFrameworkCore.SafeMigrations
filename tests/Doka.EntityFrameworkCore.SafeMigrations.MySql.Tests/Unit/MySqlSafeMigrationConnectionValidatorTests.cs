namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Protects required user-variable capability without leaking connection settings.</summary>
public sealed class MySqlSafeMigrationConnectionValidatorTests
{
    /// <summary>Accepts an explicitly enabled user-variable connection contract.</summary>
    /// <param name="connectionString">The uniquely owned native test database.</param>
    [Theory]
    [InlineData("Server=localhost;Allow User Variables=true")]
    [InlineData("Server=localhost;AllowUserVariables=true")]
    public void Validate_AcceptsEnabledUserVariables(
        string connectionString
    )
    {
        // Arrange
        var validate = () => MySqlSafeMigrationConnectionValidator.Validate(connectionString);

        // Act
        var exception = Record.Exception(validate);

        // Assert
        Assert.Null(exception);
    }

    /// <summary>Rejects disabled user variables without including the connection string in the error.</summary>
    /// <param name="connectionString">The uniquely owned native test database.</param>
    [Theory]
    [InlineData("Server=localhost")]
    [InlineData("Server=localhost;Allow User Variables=false")]
    public void Validate_RejectsDisabledUserVariablesWithoutDisclosingTheConnectionString(
        string connectionString
    )
    {
        // Arrange
        var validate = () => MySqlSafeMigrationConnectionValidator.Validate(connectionString);

        // Act
        var capturedException = Record.Exception(validate);

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(capturedException);

        Assert.Contains("AllowUserVariables=true", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Applies the connection capability guard only to owned SafeMigrations commands.</summary>
    [Fact]
    public void Interceptor_ValidatesOnlySafeMigrationCommands()
    {
        // Arrange
        var interceptor = new MySqlSafeMigrationConnectionInterceptor();
        using var connection = new MySqlConnection("Server=localhost");
        using var ordinaryCommand = connection.CreateCommand();
        ordinaryCommand.CommandText = "SELECT 1;";
        using var safeCommand = connection.CreateCommand();
        safeCommand.CommandText = "SET @doka_sm_state = 'matching';";

        // Act
        _ = interceptor.NonQueryExecuting(ordinaryCommand, eventData: null!, default);
        var capturedException = Record.Exception(() =>
            interceptor.NonQueryExecuting(safeCommand, eventData: null!, default));

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(capturedException);
        Assert.Contains("AllowUserVariables=true", exception.Message, StringComparison.Ordinal);
    }
}
