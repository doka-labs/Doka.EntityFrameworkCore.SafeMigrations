namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Length-free binary casts retain case, byte, trailing-space and NULL comparison semantics.</summary>
    /// <param name="first">The first native string value or SQL NULL.</param>
    /// <param name="second">The second native string value or SQL NULL.</param>
    /// <param name="expected">The binary equality result, or null for SQL's unknown result.</param>
    [Theory]
    [InlineData("same", "same", 1)]
    [InlineData("a", "A", 0)]
    [InlineData("a", "a ", 0)]
    [InlineData("a\0", "a", 0)]
    [InlineData("\u00e4", "a", 0)]
    [InlineData("\u00e4", "\u00e4", 1)]
    [InlineData(null, "a", null)]
    [InlineData("a", null, null)]
    [InlineData(null, null, null)]
    public async Task BinaryCatalogCastPreservesByteIdentity(string? first, string? second, int? expected)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(@first AS BINARY) = CAST(@second AS BINARY);";
        command.Parameters.Add(new MySqlParameter("@first", MySqlDbType.VarChar)
        {
            Value = first ?? (object)DBNull.Value,
        });

        command.Parameters.Add(new MySqlParameter("@second", MySqlDbType.VarChar)
        {
            Value = second ?? (object)DBNull.Value,
        });

        // Act
        var result = await command.ExecuteScalarAsync(CancellationToken.None);

        // Assert
        if (expected is null)
        {
            Assert.Equal(DBNull.Value, result);
        }
        else
        {
            Assert.Equal(expected.Value, Convert.ToInt32(result, CultureInfo.InvariantCulture));
        }
    }
}
