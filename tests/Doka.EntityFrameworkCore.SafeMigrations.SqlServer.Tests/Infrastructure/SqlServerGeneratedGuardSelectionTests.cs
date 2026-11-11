namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Distinguishes real operation guards from authored identifier text in generated preambles.</summary>
public sealed class SqlServerGeneratedGuardSelectionTests
{
    /// <summary>A marker-shaped table name cannot turn the shared preamble into another operation guard.</summary>
    /// <param name="table">The exact authored owner retained by generated SQL.</param>
    [Theory]
    [InlineData("ordinary_roles")]
    [InlineData("DECLARE @doka_state")]
    [InlineData("owner] DECLARE @doka_state")]
    [InlineData("owner' DECLARE @doka_state")]
    [InlineData("/* DECLARE @doka_state */ owner")]
    [InlineData("-- DECLARE @doka_state owner")]
    [InlineData("owner\" DECLARE @doka_state")]
    public void RenameColumnSelectsOnlyItsRealGuard(
        string table
    )
    {
        // Arrange
        using var context = new DbContext(new DbContextOptionsBuilder()
            .UseSqlServer("Server=127.0.0.1,1433;Database=offline_guard_selection;"
                + "User ID=sa;Password=unused;TrustServerCertificate=True")
            .UseSqlServerSafeMigrations()
            .Options);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameColumnIfExists("Caption", table, "DisplayCaption");
        var operation = Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);
        var preambles = commands.Where(candidate => candidate.CommandText.Contains(
            "CREATE TABLE #doka_sm_identifiers_", StringComparison.Ordinal)).ToArray();

        var inventory = commands.Where(candidate => !SqlServerGuardedSqlTestContract.IsOperationGuard(
            candidate.CommandText)).ToArray();

        // Act
        var body = SqlServerGuardedSqlTestContract.GenerateBody(context, operation);

        // Assert
        Assert.Contains("DECLARE @doka_state nvarchar", body, StringComparison.Ordinal);
        Assert.Contains(
            "N'[dbo].[" + table.Replace("]", "]]", StringComparison.Ordinal)
                .Replace("'", "''", StringComparison.Ordinal) + "]'",
            body,
            StringComparison.Ordinal);

        Assert.Equal(1, SqlServerGuardedSqlTestContract.CountStateDeclarations(body));
        Assert.Single(commands, candidate => SqlServerGuardedSqlTestContract.IsOperationGuard(
            candidate.CommandText));

        Assert.DoesNotContain("CREATE TABLE #doka_sm_identifiers_", body, StringComparison.Ordinal);
        Assert.NotEmpty(preambles);
        Assert.All(preambles, preamble => Assert.False(SqlServerGuardedSqlTestContract.IsOperationGuard(
            preamble.CommandText)));

        Assert.All(inventory, preamble => Assert.Equal(0,
            SqlServerGuardedSqlTestContract.CountStateDeclarations(
                SqlServerGuardedSqlTestContract.DecodeScope(preamble.CommandText))));

        if (table.Contains("DECLARE @doka_state", StringComparison.Ordinal))
        {
            Assert.Contains(inventory, preamble => preamble.CommandText.Contains(
                "DECLARE @doka_state", StringComparison.Ordinal));
        }

        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    /// <summary>Native declarations are distinct from quoted names, data and nested comment text.</summary>
    /// <param name="body">The native batch at one execution level.</param>
    /// <param name="expected">The independently specified number of real private state declarations.</param>
    [Theory]
    [InlineData("DECLARE @doka_state nvarchar(32);", 1)]
    [InlineData("DECLARE /* boundary */ @doka_state nvarchar(32);", 1)]
    [InlineData("DECLARE\t@doka_state nvarchar(32);", 1)]
    [InlineData("DECLARE @doka_state_suffix nvarchar(32);", 0)]
    [InlineData("SELECT N'DECLARE @doka_state';", 0)]
    [InlineData("SELECT N'prefix'' DECLARE @doka_state';", 0)]
    [InlineData("SELECT [DECLARE @doka_state];", 0)]
    [InlineData("SELECT [owner]] DECLARE @doka_state];", 0)]
    [InlineData("SELECT \"DECLARE @doka_state\";", 0)]
    [InlineData("-- DECLARE @doka_state\r\nSELECT 1;", 0)]
    [InlineData("/* outer /* DECLARE @doka_state */ nested */ SELECT 1;", 0)]
    [InlineData("EXEC sys.sp_executesql N'DECLARE @doka_state nvarchar(32);';", 0)]
    [InlineData("DECLARE @doka_state nvarchar(32); /* DECLARE @doka_state */", 1)]
    public void GuardSelectionUsesOnlyDeclarationsAtTheCurrentExecutionLevel(
        string body,
        int expected
    )
    {
        // Arrange
        var command = SqlServerGuardedSqlTestContract.EncodeScope(body);

        // Act
        var count = SqlServerGuardedSqlTestContract.CountStateDeclarations(body);
        var isGuard = SqlServerGuardedSqlTestContract.IsOperationGuard(command);

        // Assert
        Assert.Equal(expected, count);
        Assert.Equal(expected > 0, isGuard);
    }

    /// <summary>Incomplete lexical boundaries fail loudly instead of classifying a partial generated command.</summary>
    /// <param name="body">The unterminated native string, identifier or block comment.</param>
    [Theory]
    [InlineData("SELECT N'unterminated DECLARE @doka_state")]
    [InlineData("SELECT [unterminated DECLARE @doka_state")]
    [InlineData("SELECT \"unterminated DECLARE @doka_state")]
    [InlineData("/* unterminated DECLARE @doka_state")]
    public void MalformedQuotedScopeCannotBeCertifiedAsAGuard(
        string body
    )
    {
        // Arrange
        var command = SqlServerGuardedSqlTestContract.EncodeScope(body);

        // Act
        var failure = Record.Exception(() => SqlServerGuardedSqlTestContract.IsOperationGuard(command));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
    }
}
