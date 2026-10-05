namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Generated guards run under a connection collation other than the character-set default.</summary>
    /// <remarks>
    /// WHY: Script and bundle generation renders values inline, and the provider renders a value
    /// containing a backslash as an introducer literal, which carries the character set's default
    /// collation rather than the connection collation. A set-oriented guard unites such a literal
    /// with plain ones, which raised error 1271 under utf8mb4_unicode_ci while passing under the
    /// server default. The analyzer path parameterises and therefore never reproduced it.
    ///
    /// Both collations below exist on MySQL and on MariaDB, and each differs from the character
    /// set default of at least one of them, so the case is exercised on either engine.
    /// </remarks>
    /// <param name="collation">The connection collation the generated guard runs under.</param>
    [Theory]
    [InlineData("utf8mb4_general_ci")]
    [InlineData("utf8mb4_unicode_ci")]
    public async Task GeneratedGuards_RunUnderAnyUtf8Mb4ConnectionCollation(string collation)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `collation_guard` (`plain` int NOT NULL, "
            + "`escaped` int NOT NULL COMMENT 'a\\\\b');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "collation_guard",
                [
                    new ExpectedColumnDefinition("plain", typeof(int), false, "int"),
                    new ExpectedColumnDefinition("escaped", typeof(int), false, "int", comment: @"a\b"),
                ]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using (var names = connection.CreateCommand())
        {
            names.CommandText = $"SET NAMES utf8mb4 COLLATE {collation};";
            await names.ExecuteNonQueryAsync(CancellationToken.None);
        }

        // Act
        var failure = await Record.ExceptionAsync(async () =>
        {
            foreach (var command in commands)
            {
                await using var guarded = connection.CreateCommand();
                guarded.CommandText = command.CommandText;
                await guarded.ExecuteNonQueryAsync(CancellationToken.None);
            }
        });

        // Assert
        Assert.Null(failure);
    }
}
