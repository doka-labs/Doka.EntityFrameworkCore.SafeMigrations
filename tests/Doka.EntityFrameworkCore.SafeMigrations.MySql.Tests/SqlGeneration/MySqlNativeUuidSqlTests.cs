namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Guards native UUID widths in every independently generated runtime physical proof.</summary>
public sealed class MySqlNativeUuidSqlTests : IDisposable
{
    private readonly ServiceProvider _services;

    /// <summary>Owns the provider used to render this test case's physical proof statements.</summary>
    public MySqlNativeUuidSqlTests()
    {
        // WHY: The offline qualification profile belongs to this test case;
        // it must not add a retained variant to EF's global provider cache.
        _services = new ServiceCollection()
            .AddEntityFrameworkDokaMySql()
            .AddEntityFrameworkDokaMySqlSafeMigrations()
            .BuildServiceProvider();
    }

    /// <summary>Disposes the test-owned provider after the proof-generation context has been released.</summary>
    public void Dispose() => _services.Dispose();

    /// <summary>UUID siblings consume sixteen bytes in inline, declared-row and dependent-key budgets.</summary>
    [Fact]
    public void NativeUuidSiblingsHaveFixedWidthsInAllColumnRepairBudgets()
    {
        // Arrange
        using var context = CreateContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("native_uuid_budget",
            new ExpectedColumnDefinition("value", typeof(string), true, "varchar(64)", maxLength: 64),
            SafeMigrationPolicy.RepairIfSafe);
        // WHY: Explicit type alterations add an inline-record proof that the
        // separate Ensure repair path does not generate.
        builder.AlterColumnIfDifferent("native_uuid_budget",
            new ExpectedColumnDefinition("alter_value", typeof(string), true, "varchar(64)", maxLength: 64),
            new ExpectedColumnDefinition("alter_value", typeof(string), true, "varchar(32)", maxLength: 32),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var sql = Generate(context, builder.Operations);

        // Assert
        Assert.Contains("WHEN LOWER(inline_column.DATA_TYPE) = 'uuid' THEN 16 ", sql,
            StringComparison.Ordinal);
        Assert.Contains("WHEN LOWER(row_column.DATA_TYPE) = 'uuid' THEN 16 ", sql,
            StringComparison.Ordinal);
        Assert.Contains("WHEN LOWER(ic.DATA_TYPE) = 'uuid' THEN 16 ", sql, StringComparison.Ordinal);
    }

    /// <summary>A native UUID key has its scalar width without claiming textual prefix semantics.</summary>
    [Fact]
    public void NativeUuidKeysHaveFixedWidthsInStandaloneIndexProofs()
    {
        // Arrange
        using var context = CreateContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("ix_native_uuid_budget_id", "native_uuid_budget", ["id"]);

        // Act
        var sql = Generate(context, builder.Operations);

        // Assert
        Assert.Contains("WHEN c.DATA_TYPE = 'uuid' THEN 16 ", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("IN ('char', 'varchar', 'uuid'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("IN ('binary', 'varbinary', 'uuid'", sql, StringComparison.Ordinal);
    }

    /// <summary>Generates real guarded migration commands without requiring a live connection.</summary>
    /// <param name="context">The configured MariaDB provider and SafeMigrations services.</param>
    /// <param name="operations">The authored immutable migration contracts.</param>
    /// <returns>The command text and decoded prepared statements, including physical catalog predicates.</returns>
    private static string Generate(DbContext context, IReadOnlyList<MigrationOperation> operations)
    {
        var sql = string.Join(Environment.NewLine,
            context.GetService<IMigrationsSqlGenerator>().Generate(operations, context.Model)
                .Select(static command => command.CommandText));

        // WHY: Lazy runtime catalog probes are transported as prepared UTF-8
        // hex payloads. Inspect their actual SQL, not incidental hex spelling.
        const string prefix = "CONVERT(0x";
        const string suffix = " USING utf8mb4)";
        var decoded = new StringBuilder(sql);
        var offset = 0;
        while ((offset = sql.IndexOf(prefix, offset, StringComparison.Ordinal)) >= 0)
        {
            var start = offset + prefix.Length;
            var end = sql.IndexOf(suffix, start, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidOperationException("A generated prepared SQL payload is unterminated.");
            }

            decoded.AppendLine().Append(Encoding.UTF8.GetString(Convert.FromHexString(sql.AsSpan(start, end - start))));
            offset = end + suffix.Length;
        }

        return decoded.ToString();
    }

    /// <summary>Uses the lowest qualified MariaDB line, where native UUID is already supported.</summary>
    /// <returns>An offline context retaining the provider's Binary16 default.</returns>
    private SafeMigrationDbContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        builder.UseInternalServiceProvider(_services);
        builder.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MariaDb(new Version(10, 11, 0)));
        builder.UseMySqlSafeMigrations();

        return new SafeMigrationDbContext(builder.Options);
    }
}
