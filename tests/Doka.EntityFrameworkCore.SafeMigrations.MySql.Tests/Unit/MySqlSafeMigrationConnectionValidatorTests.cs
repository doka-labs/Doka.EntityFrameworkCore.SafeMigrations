namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlSafeMigrationConnectionValidatorTests
{
    [Theory]
    [InlineData("Server=localhost;Allow User Variables=true")]
    [InlineData("Server=localhost;AllowUserVariables=true")]
    public void Validate_AcceptsEnabledUserVariables(
        string connectionString
    ) => MySqlSafeMigrationConnectionValidator.Validate(connectionString);

    [Theory]
    [InlineData("Server=localhost")]
    [InlineData("Server=localhost;Allow User Variables=false")]
    public void Validate_RejectsDisabledUserVariablesWithoutDisclosingTheConnectionString(
        string connectionString
    )
    {
        var exception =
            Assert.Throws<InvalidOperationException>(() =>
                MySqlSafeMigrationConnectionValidator.Validate(connectionString));

        Assert.Contains("AllowUserVariables=true", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Interceptor_ValidatesOnlySafeMigrationCommands()
    {
        var interceptor = new MySqlSafeMigrationConnectionInterceptor();
        using var connection = new MySqlConnection("Server=localhost");
        using var ordinaryCommand = connection.CreateCommand();
        ordinaryCommand.CommandText = "SELECT 1;";
        using var safeCommand = connection.CreateCommand();
        safeCommand.CommandText = "SET @doka_sm_state = 'matching';";

        _ = interceptor.NonQueryExecuting(ordinaryCommand, eventData: null!, default);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            interceptor.NonQueryExecuting(safeCommand, eventData: null!, default));

        Assert.Contains("AllowUserVariables=true", exception.Message, StringComparison.Ordinal);
    }
}
