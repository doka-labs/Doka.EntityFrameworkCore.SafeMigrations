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
}
