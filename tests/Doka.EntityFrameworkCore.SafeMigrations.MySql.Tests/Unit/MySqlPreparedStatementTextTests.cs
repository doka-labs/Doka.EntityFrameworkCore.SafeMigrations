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
}
