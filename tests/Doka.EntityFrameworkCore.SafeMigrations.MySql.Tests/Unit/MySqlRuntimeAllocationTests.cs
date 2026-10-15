namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Measures prepared runtime generation on both MySQL and MariaDB without database latency.</summary>
/// <param name="output">The diagnostic allocation and SQL fingerprint output.</param>
public sealed class MySqlRuntimeAllocationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string CanonicalCorrelation = "00000000000000000000000000000000";

    /// <summary>Runtime column generation preserves every command and its exact SQL.</summary>
    /// <param name="isMariaDb">Whether generation uses the qualified MariaDB catalog contract.</param>
    /// <param name="expectedSqlHash">The fingerprint including intentional exact-comment matching.</param>
    [Theory]
    [InlineData(false, "232327923256A361556E3AC57704C05FDA133FD959BFD5B6198E1A011DFBB2FB")]
    [InlineData(true, "FD97289416687595DB3814B4BBCBA3EF29828FB22E8F8E46C2E6F92602A309F3")]
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

        var canonicalSql = string.Concat(
            commands.Select(static command =>
                MySqlRuntimeSqlCanonicalizer.NormalizeOwnedCorrelation(command.CommandText)));

        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonicalSql)));

        var legacySql = canonicalSql.Replace(
            "CAST(COALESCE(c.COLUMN_COMMENT, '') AS BINARY)",
            "COALESCE(c.COLUMN_COMMENT, '')", StringComparison.Ordinal).Replace(
            "(c.COLUMN_DEFAULT IS NULL OR (CAST(c.COLUMN_DEFAULT AS BINARY) = 'NULL' "
                + "AND LOCATE('DEFAULT_GENERATED', UPPER(COALESCE(c.EXTRA, ''))) > 0))",
            "(c.COLUMN_DEFAULT IS NULL OR UPPER(c.COLUMN_DEFAULT) = 'NULL')", StringComparison.Ordinal);

        var legacyHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(legacySql)));

        // Assert
        output.WriteLine(
            $"Prepared {(isMariaDb ? "MariaDB" : "MySQL")} generation: {allocated} bytes, SQL SHA256 {hash}.");
        output.WriteLine($"Legacy-facet reference SQL SHA256 {legacyHash}.");
        output.WriteLine($"Runtime SQL: {commands.Count} commands, "
            + $"{commands.Sum(static command => command.CommandText.Length)} characters, "
            + $"{commands.Sum(static command => Encoding.UTF8.GetByteCount(command.CommandText))} UTF8 bytes.");

        if (!isMariaDb)
        {
            // WHY: Restoring only the previous comment equality and unsafe
            // NULL-text alternative still covers every other byte of the updated diagnostic contract.
            Assert.Equal("EC63EE950B2CBD949E8CB77D4216C685760472E8F8F1DC3622F3C0E480ECD986", legacyHash);
        }

        else
        {
            Assert.Equal("692F5F7ABDFACE87C44F22765391C4CEE5D3BD71D021CCBE8CF122D74D04B974", legacyHash);
        }

        // WHY: Only platform newlines and positively decoded operation-owned
        // correlations vary. Guards, ordering, cleanup and default-identity SQL remain in both fingerprints.
        Assert.Equal(expectedSqlHash, hash);
        Assert.Equal(operations.Length, commands.Count);
        Assert.All(commands, command =>
            Assert.Contains("@doka_sm_state", command.CommandText, StringComparison.Ordinal));
        Assert.InRange(allocated, 0, 4_000_000);
    }

    /// <summary>Canonicalization preserves authored GUID identifiers, literals and unrelated statements.</summary>
    [Fact]
    public void RuntimeCorrelationNormalizationPreservesAuthoredPayloads()
    {
        // Arrange
        const string nonce = "0123456789abcdef0123456789abcdef";
        const string payload = "SELECT `" + nonce + "`, '" + nonce + "';"
            + "SET @ordinary_value = CONCAT('" + nonce + "-', 7);";

        const string sql = "/*doka_sm_runtime:7:" + nonce + "*/" + payload
            + "SET @doka_sm_action = 'reject_data_blocked', "
            + "@doka_sm_diag = CONCAT('" + nonce + "-', 7);\r\n";

        const string expected = "/*doka_sm_runtime:7:" + CanonicalCorrelation + "*/" + payload
            + "SET @doka_sm_action = 'reject_data_blocked', "
            + "@doka_sm_diag = CONCAT('" + CanonicalCorrelation + "-', 7);\n";

        // Act
        var canonical = MySqlRuntimeSqlCanonicalizer.NormalizeOwnedCorrelation(sql);

        // Assert
        Assert.Equal(expected, canonical);
        Assert.Contains(payload, canonical, StringComparison.Ordinal);
    }

    /// <summary>Unrecognized or malformed prefixes cannot authorize correlation normalization.</summary>
    /// <param name="marker">The untrusted leading metadata text.</param>
    [Theory]
    [InlineData("")]
    [InlineData("/*doka_sm_runtime:7:invalid*/")]
    [InlineData("/*doka_sm_runtime:007:0123456789abcdef0123456789abcdef*/")]
    [InlineData("/*doka_sm_other:7:0123456789abcdef0123456789abcdef*/")]
    public void RuntimeCorrelationNormalizationRequiresOwnedMarker(string marker)
    {
        // Arrange
        var sql = marker
            + "SET @doka_sm_action = 'reject_data_blocked', "
            + "@doka_sm_diag = CONCAT('0123456789abcdef0123456789abcdef-', 7);";

        // Act
        var canonical = MySqlRuntimeSqlCanonicalizer.NormalizeOwnedCorrelation(sql);

        // Assert
        Assert.Equal(sql, canonical);
    }

    /// <summary>A missing command remains an argument failure rather than an empty normalized SQL value.</summary>
    [Fact]
    public void RuntimeCorrelationNormalizationRejectsNullSql()
    {
        // Arrange
        string sql = null!;

        // Act
        var exception = Record.Exception(() => MySqlRuntimeSqlCanonicalizer.NormalizeOwnedCorrelation(sql));

        // Assert
        Assert.Equal("sql", Assert.IsType<ArgumentNullException>(exception).ParamName);
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
