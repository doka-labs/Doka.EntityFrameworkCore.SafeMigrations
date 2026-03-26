using Doka.EntityFrameworkCore.SafeMigrations.MariaDb;

namespace Doka.EntityFrameworkCore.SafeMigrations.Tests.Unit;

/// <summary>
/// Regression guard: verifies that object names containing single quotes and/or
/// backslashes are correctly escaped in the information_schema SQL string literals generated
/// by SqlLiteral (which now delegates to EscapeSqlLiteral).
/// Prior to §6.21, SqlLiteral used EscapeSqlIdentifier which only escaped single quotes and
/// did not escape backslashes, producing incorrect SQL for names like "my\table".
/// Operations that use ExistsTableSql (e.g. RenameTableIfExists) are used here because they
/// embed the object name via SqlLiteral in a WHERE clause, unlike DROP/ADD DDL which uses
/// backtick-delimited identifier quoting.
/// </summary>
public sealed class MariaDbSafeMigrationsSqlGeneratorEscapingTests
{
    [Fact]
    public void RenameTableIfExists_TableNameWithSingleQuote_IsCorrectlyEscapedInSql()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.RenameTableIfExists(
            name: "table'name",
            newName: "new_table");

        var sql = string.Join("\n", generator.Generate(migrationBuilder.Operations, context.Model).Select(c => c.CommandText));

        Assert.Contains("'table''name'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void RenameTableIfExists_TableNameWithBackslash_IsCorrectlyEscapedInSql()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.RenameTableIfExists(
            name: @"table\name",
            newName: "new_table");

        var sql = string.Join("\n", generator.Generate(migrationBuilder.Operations, context.Model).Select(c => c.CommandText));

        // EscapeSqlLiteral doubles backslashes: table\name → table\\name → SQL literal 'table\\name'
        Assert.Contains(@"'table\\name'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void RenameTableIfExists_TableNameWithBothSpecialChars_IsCorrectlyEscapedInSql()
    {
        using var context = new MariaDbTestContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);

        migrationBuilder.RenameTableIfExists(
            name: @"tab\le'name",
            newName: "new_table");

        var sql = string.Join("\n", generator.Generate(migrationBuilder.Operations, context.Model).Select(c => c.CommandText));

        // tab\le'name → tab\\le''name → SQL literal 'tab\\le''name'
        Assert.Contains(@"'tab\\le''name'", sql, StringComparison.Ordinal);
    }

}
