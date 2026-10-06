namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Measures prepared runtime generation on both MySQL and MariaDB without database latency.</summary>
/// <param name="output">The diagnostic allocation and SQL fingerprint output.</param>
public sealed class MySqlRuntimeAllocationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>Runtime column generation preserves every command and its exact SQL.</summary>
    [Theory]
    [InlineData(false, "2F3741B301CF5FAB1F738720C341692E951B8168E2FC99A77526083E332C8FBA")]
    [InlineData(true, "C6233A2E99E6316A8117A741C58CE924718E172226A90280680C59D5B290C8BE")]
    public void PreparedRuntimeColumnGenerationRemainsBounded(bool isMariaDb, string expectedSqlHash)
    {
        // Arrange
        using var context = CreateContext(isMariaDb);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var operations = Enumerable.Range(0, 128)
            .Select(index => (MigrationOperation)new SafeMigrationOperation(
                new EnsureColumnIntent(
                    "items", new ExpectedColumnDefinition($"value_{index}", typeof(int), true, "int")),
                SafeMigrationPolicy.ThrowIfDifferent))
            .ToArray();

        _ = generator.Generate(operations, context.Model);

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        var commands = generator.Generate(operations, context.Model);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var sql = string.Concat(commands.Select(static command => command.CommandText));
        var canonicalSql = sql.Replace("\r\n", "\n", StringComparison.Ordinal);
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonicalSql)));

        var legacySql = canonicalSql.Replace(
            "(c.COLUMN_DEFAULT IS NULL OR (CAST(c.COLUMN_DEFAULT AS BINARY) = 'NULL' "
                + "AND LOCATE('DEFAULT_GENERATED', UPPER(COALESCE(c.EXTRA, ''))) > 0))",
            "(c.COLUMN_DEFAULT IS NULL OR UPPER(c.COLUMN_DEFAULT) = 'NULL')", StringComparison.Ordinal);

        var legacyHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(legacySql)));

        // Assert
        output.WriteLine(
            $"Prepared {(isMariaDb ? "MariaDB" : "MySQL")} generation: {allocated} bytes, SQL SHA256 {hash}.");

        if (!isMariaDb)
        {
            // WHY: Restoring only the unsafe NULL-text alternative reproduces
            // the previous fingerprint, proving no unrelated SQL drift.
            Assert.Equal("EE6F0215DD906B2FF4E60421E84F8984ACDD1093E6C67438411EC99D789BC0E9", legacyHash);
        }

        // Normalize platform newlines only; allocation changes must preserve
        // guards, ordering, cleanup, and the qualified default-identity SQL.
        Assert.Equal(expectedSqlHash, hash);
        Assert.Equal(operations.Length, commands.Count);
        Assert.All(commands, command =>
            Assert.Contains("@doka_sm_state", command.CommandText, StringComparison.Ordinal));
        Assert.InRange(allocated, 0, 4_000_000);
    }

    /// <summary>MariaDB integer aliases and modifiers preserve the generated canonical catalog predicate.</summary>
    [Theory]
    [InlineData("int", "int")]
    [InlineData(" INTEGER ", "int")]
    [InlineData("BIGINT   UNSIGNED", "bigint unsigned")]
    [InlineData("smallint zerofill unsigned", "smallint unsigned")]
    [InlineData("mediumint unsigned zerofill", "mediumint unsigned")]
    [InlineData("tinyint  zerofill", "tinyint")]
    [InlineData("smallint", "smallint")]
    [InlineData("mediumint", "mediumint")]
    [InlineData("bigint", "bigint")]
    public void MariaDbIntegerCatalogPredicatesPreserveCanonicalTypes(string storeType, string expected)
    {
        // Arrange
        using var context = CreateContext(isMariaDb: true);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var capture = context.GetService<MySqlSafeMigrationPlanCapture>();
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent("items", new ExpectedColumnDefinition("value", typeof(int), true, storeType)),
            SafeMigrationPolicy.ThrowIfDifferent);
        using var lease = capture.Begin([operation]);

        // Act
        _ = generator.Generate([operation], context.Model);
        var plan = Assert.Single(lease.Complete());

        // Assert
        Assert.Contains("CONCAT(LOWER(c.DATA_TYPE)", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains(plan.ParameterValues, parameter => Equals(parameter.Value, expected));
    }

    private static DbContext CreateContext(bool isMariaDb)
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=application;Allow User Variables=true",
            isMariaDb
                ? MySqlServerVersion.MariaDb(new Version(11, 8, 8))
                : MySqlServerVersion.MySql(new Version(8, 4, 11)));
        options.UseMySqlSafeMigrations();

        // WHY: SQL generation needs provider services but must never open a live connection for this allocation proof.
        return new DbContext(options.Options);
    }
}
