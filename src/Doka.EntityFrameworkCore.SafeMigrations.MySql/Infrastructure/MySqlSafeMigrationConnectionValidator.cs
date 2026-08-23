namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal static class MySqlSafeMigrationConnectionValidator
{
    private const string ConfigurationError =
        "MySQL safe migrations require AllowUserVariables=true in the MySqlConnector connection string.";

    public static void Validate(
        DbConnection connection
    )
    {
        ArgumentNullException.ThrowIfNull(connection);

        Validate(connection.ConnectionString);
    }

    public static void Validate(
        string connectionString
    )
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        MySqlConnectionStringBuilder builder;
        try
        {
            builder = new MySqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "MySQL safe migrations could not validate the MySqlConnector connection string.",
                exception);
        }

        if (!builder.AllowUserVariables)
        {
            throw new InvalidOperationException(ConfigurationError);
        }
    }
}
