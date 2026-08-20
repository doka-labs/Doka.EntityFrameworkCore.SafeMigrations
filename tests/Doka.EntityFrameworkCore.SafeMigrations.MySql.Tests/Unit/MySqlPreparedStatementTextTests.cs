namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlPreparedStatementTextTests
{
    [Fact]
    public void NormalizerRemovesOnlyOneTrailingTerminator()
    {
        Assert.Equal(
            "CREATE TABLE `a` (`value` varchar(20))",
            MySqlPreparedStatementText.NormalizeSingleStatement("CREATE TABLE `a` (`value` varchar(20));\n"));
    }

    [Fact]
    public void NormalizerPreservesSemicolonsInsideQuotedTokensAndComments()
    {
        const string sql = "CREATE TABLE `a;b` (`value` varchar(20) DEFAULT ';') /* ; */;";

        Assert.Equal(
            "CREATE TABLE `a;b` (`value` varchar(20) DEFAULT ';') /* ; */",
            MySqlPreparedStatementText.NormalizeSingleStatement(sql));
    }

    [Fact]
    public void NormalizerHandlesEveryQuotedAndCommentLexerState()
    {
        var cases = new[]
        {
            (Sql: "SELECT 'a;''b';", Expected: "SELECT 'a;''b'"),
            (Sql: "SELECT \"a;\"\"b\";", Expected: "SELECT \"a;\"\"b\""),
            (Sql: "SELECT `a;``b`;", Expected: "SELECT `a;``b`"),
            (Sql: "SELECT 1 /* ; */;", Expected: "SELECT 1 /* ; */"),
            (Sql: "SELECT 1 -- ;\n;", Expected: "SELECT 1 -- ;"),
            (Sql: "SELECT 1 # ;\n;", Expected: "SELECT 1 # ;"),
        };

        foreach (var testCase in cases)
        {
            Assert.Equal(
                testCase.Expected,
                MySqlPreparedStatementText.NormalizeSingleStatement(testCase.Sql));
        }

        var providerCommand = MySqlPreparedStatementText.NormalizeProviderCommand(
            "SELECT 'single;statement';",
            hasBackslashDdlComment: false);

        Assert.False(providerCommand.IsSqlModeSensitive);
        Assert.Equal("SELECT 'single;statement'", providerCommand.NoBackslashEscapesSql);
        Assert.Equal(providerCommand.NoBackslashEscapesSql, providerCommand.DefaultSqlModeSql);
    }

    [Fact]
    public void NormalizerRejectsEmptyUnterminatedAndUnprotectedStatements()
    {
        Assert.Throws<ArgumentException>(() => MySqlPreparedStatementText.NormalizeSingleStatement(" "));
        Assert.Throws<InvalidOperationException>(() => MySqlPreparedStatementText.NormalizeSingleStatement(";"));
        Assert.Throws<InvalidOperationException>(() =>
            MySqlPreparedStatementText.NormalizeSingleStatement("SELECT 1 --not-a-comment; SELECT 2;"));

        var unterminatedTokens = new[]
        {
            "SELECT 'value",
            "SELECT \"value",
            "SELECT `value",
            "SELECT /* value",
        };

        foreach (var sql in unterminatedTokens)
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                MySqlPreparedStatementText.NormalizeSingleStatement(sql));

            Assert.Contains("unterminated SQL token", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NormalizerRejectsMultipleStatements()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MySqlPreparedStatementText.NormalizeSingleStatement("SELECT 1; SELECT 2;"));
    }

    [Fact]
    public void ProviderCommand_ExtractsModeProtectedDdlWithoutMutatingSessionMode()
    {
        const string sql = """
                           /*! SET @__doka_previous_sql_mode = @@SESSION.sql_mode */;
                           /*! SET SESSION sql_mode = IF(
                           FIND_IN_SET('NO_BACKSLASH_ESCAPES', @@SESSION.sql_mode),
                           @@SESSION.sql_mode,
                           CONCAT_WS(',', NULLIF(@@SESSION.sql_mode, ''), 'NO_BACKSLASH_ESCAPES')) */;
                           ALTER TABLE `items` ADD `value` varchar(40) COMMENT 'slash \ and quote ''x''';
                           /*! SET SESSION sql_mode = @__doka_previous_sql_mode */;
                           """;

        var result = MySqlPreparedStatementText.NormalizeProviderCommand(sql, hasBackslashDdlComment: true);

        Assert.True(result.IsSqlModeSensitive);
        Assert.Equal(
            "ALTER TABLE `items` ADD `value` varchar(40) COMMENT 'slash \\ and quote ''x'''",
            result.NoBackslashEscapesSql);
        Assert.Equal(
            "ALTER TABLE `items` ADD `value` varchar(40) COMMENT 'slash \\\\ and quote ''x'''",
            result.DefaultSqlModeSql);
    }

    [Fact]
    public void ProviderCommand_RejectsUnknownMultiStatementShape()
    {
        Assert.Throws<InvalidOperationException>(() => MySqlPreparedStatementText.NormalizeProviderCommand(
            "SET @value = 1; ALTER TABLE `items` ADD `value` int;",
            hasBackslashDdlComment: true));
    }

    [Fact]
    public void ProviderCommandEscapesOnlyBackslashesInsideSingleQuotedLiterals()
    {
        const string sql = """
                           /*! SET @__doka_previous_sql_mode = @@SESSION.sql_mode */;
                           /*! SET SESSION sql_mode = 'NO_BACKSLASH_ESCAPES' */;
                           ALTER TABLE `path\name` COMMENT 'single\path', ALGORITHM="double\path";
                           /*! SET SESSION sql_mode = @__doka_previous_sql_mode */;
                           """;

        var result = MySqlPreparedStatementText.NormalizeProviderCommand(sql, hasBackslashDdlComment: true);

        Assert.Equal(
            "ALTER TABLE `path\\name` COMMENT 'single\\path', ALGORITHM=\"double\\path\"",
            result.NoBackslashEscapesSql);
        Assert.Equal(
            "ALTER TABLE `path\\name` COMMENT 'single\\\\path', ALGORITHM=\"double\\path\"",
            result.DefaultSqlModeSql);
    }

    [Fact]
    public void ProviderCommandRejectsEveryMalformedModeScopeAndUnterminatedDdl()
    {
        var malformedCommands = new[]
        {
            """
            /*! SET @previous = 1 */;
            /*! SET SESSION sql_mode = 'NO_BACKSLASH_ESCAPES' */;
            ALTER TABLE `items` ADD `value` int;
            /*! SET SESSION sql_mode = @previous */;
            """,
            """
            /*! SET @previous = @@SESSION.sql_mode */;
            /*! SET sql_mode = 'NO_BACKSLASH_ESCAPES' */;
            ALTER TABLE `items` ADD `value` int;
            /*! SET SESSION sql_mode = @previous */;
            """,
            """
            /*! SET @previous = @@SESSION.sql_mode */;
            /*! SET SESSION sql_mode = 'NO_BACKSLASH_ESCAPES' */;
            ALTER TABLE `items` ADD `value` int;
            /*! SET sql_mode = @previous */;
            """,
            """
            /*! SET @previous = @@SESSION.sql_mode */;
            /*! SET SESSION sql_mode = 'NO_BACKSLASH_ESCAPES' */;
            ALTER TABLE `items` COMMENT 'unterminated;
            /*! SET SESSION sql_mode = @previous */;
            """,
        };

        foreach (var sql in malformedCommands)
        {
            Assert.Throws<InvalidOperationException>(() =>
                MySqlPreparedStatementText.NormalizeProviderCommand(sql, hasBackslashDdlComment: true));
        }
    }
}
