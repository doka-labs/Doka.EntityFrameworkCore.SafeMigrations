namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlGuardCommandPlanTests
{
    [Fact]
    public void DataReadingSingleBaselineOperation_HasExactBoundedScopedCommandShape()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent(
                "items",
                new ExpectedColumnDefinition("value", typeof(int), isNullable: false, storeType: "int")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate([operation], context.Model);

        var command = Assert.Single(commands);

        Assert.Equal(
            "MySqlScopedMigrationCommand",
            command.GetType()
                .Name);
        Assert.Equal(3, Count(command.CommandText, "PREPARE doka_sm_statement FROM"));
        Assert.Equal(2, Count(command.CommandText, "EXECUTE doka_sm_statement"));
        Assert.Equal(2, Count(command.CommandText, "DEALLOCATE PREPARE doka_sm_statement"));
        Assert.Contains("@doka_sm_prerequisite_ok", command.CommandText, StringComparison.Ordinal);
        Assert.Contains(
            "DROP TEMPORARY TABLE IF EXISTS `__doka_sm_assert`",
            command.CommandText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogOnlySingleBaselineOperation_AvoidsLazyStateCommands()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent(
                "items",
                new ExpectedColumnDefinition("value", typeof(int), isNullable: true, storeType: "int")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate([operation], context.Model);

        var command = Assert.Single(commands);

        Assert.Equal(
            "MySqlScopedMigrationCommand",
            command.GetType()
                .Name);
        Assert.Equal(2, Count(command.CommandText, "PREPARE doka_sm_statement FROM"));
        Assert.Equal(1, Count(command.CommandText, "EXECUTE doka_sm_statement"));
        Assert.Equal(1, Count(command.CommandText, "DEALLOCATE PREPARE doka_sm_statement"));
        Assert.DoesNotContain("SET @doka_sm_prerequisite_ok = COALESCE", command.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void CommentedBaselineOperation_UsesProviderValidatedSqlModeFragments()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent(
                "items",
                new ExpectedColumnDefinition(
                    "value",
                    typeof(string),
                    isNullable: true,
                    storeType: "varchar(40)",
                    comment: "mode\\safe")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var command = Assert.Single(
            context
                .GetService<IMigrationsSqlGenerator>()
                .Generate([operation], context.Model));
        var captureIndex = command.CommandText.IndexOf("@__doka_previous_sql_mode", StringComparison.Ordinal);
        var bodyIndex = command.CommandText.IndexOf(
            "PREPARE doka_sm_statement FROM @doka_sm_sql",
            StringComparison.Ordinal);
        var cleanupIndex = command.CommandText.LastIndexOf("SET SESSION sql_mode", StringComparison.OrdinalIgnoreCase);
        var guardCleanupIndex = command.CommandText.LastIndexOf(
            "DROP TEMPORARY TABLE IF EXISTS `__doka_sm_assert`",
            StringComparison.Ordinal);

        Assert.Equal(
            "MySqlScopedMigrationCommand",
            command.GetType()
                .Name);
        Assert.True(captureIndex >= 0);
        Assert.True(bodyIndex > captureIndex);
        Assert.True(cleanupIndex > bodyIndex);
        Assert.True(guardCleanupIndex > cleanupIndex);
    }

    [Fact]
    public void RepairableEnsureColumn_EmbedsDistinctProviderApplyAndRepairDdl()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent(
                "items",
                new ExpectedColumnDefinition(
                    "value",
                    typeof(string),
                    isNullable: false,
                    storeType: "varchar(40)",
                    maxLength: 40,
                    comment: "canonical",
                    defaultValue: SafeMigrationDefaultValue.Literal("canonical"))),
            SafeMigrationPolicy.RepairIfSafe);

        var command = Assert.Single(
            context
                .GetService<IMigrationsSqlGenerator>()
                .Generate([operation], context.Model));

        var payloads = DecodeHexPayloads(command.CommandText);

        Assert.Contains("WHEN @doka_sm_action = 'apply'", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("WHEN @doka_sm_action = 'repair'", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("THEN ('missing') ELSE NULL END", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("CASE WHEN @doka_sm_state IS NULL", command.CommandText, StringComparison.Ordinal);
        Assert.Contains(
            payloads,
            payload => payload.StartsWith("ALTER TABLE `items` ADD ", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(payloads, payload => payload.Contains("MODIFY COLUMN", StringComparison.OrdinalIgnoreCase));
    }

    private static int Count(
        string value,
        string search
    )
    {
        var count = 0;
        var offset = 0;

        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }

    private static List<string> DecodeHexPayloads(
        string sql
    )
    {
        const string prefix = "CONVERT(0x";
        const string suffix = " USING utf8mb4)";

        var result = new List<string>();
        var offset = 0;
        while ((offset = sql.IndexOf(prefix, offset, StringComparison.Ordinal)) >= 0)
        {
            var valueStart = offset + prefix.Length;
            var valueEnd = sql.IndexOf(suffix, valueStart, StringComparison.Ordinal);
            if (valueEnd < 0)
            {
                throw new InvalidOperationException("A generated hexadecimal SQL payload is unterminated.");
            }

            result.Add(Encoding.UTF8.GetString(Convert.FromHexString(sql[valueStart..valueEnd])));
            offset = valueEnd + suffix.Length;
        }

        return result;
    }
}
