namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Measures real row reads for the former duplicate proof and the generated shared proof.</summary>
    /// <remarks>Handler reads, not wall time, guard the regression independently of CI hardware.</remarks>
    /// <param name="rowCount">The number of unindexed, non-NULL values that require a complete proof.</param>
    [Theory]
    [InlineData(1000)]
    [InlineData(10000)]
    public async Task RuntimeNullProof_SharedProofReducesExaminedRows(int rowCount)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        const string digits = "(SELECT 0 d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 "
            + "UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 "
            + "UNION ALL SELECT 8 UNION ALL SELECT 9)";

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `null_read_rows` (`Id` int NOT NULL PRIMARY KEY, `Value` int NULL); "
            + "INSERT INTO `null_read_rows` SELECT a.d + 10*b.d + 100*c.d + 1000*d.d, 7 "
            + $"FROM {digits} a CROSS JOIN {digits} b CROSS JOIN {digits} c CROSS JOIN {digits} d "
            + $"LIMIT {rowCount.ToString(CultureInfo.InvariantCulture)};");

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("null_read_rows",
            new ExpectedColumnDefinition("Value", typeof(int), isNullable: false, storeType: "int")),
            SafeMigrationPolicy.RepairIfSafe);

        var command = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));
        var payloads = DecodeNullProofMeasurementPayloads(command.CommandText);
        var proofSql = Assert.Single(payloads, sql => sql.EndsWith(
            "INTO @doka_sm_nullability_blocked", StringComparison.Ordinal));

        var stateSql = Assert.Single(payloads, sql => sql.EndsWith(
            "INTO @doka_sm_state, @doka_sm_repair_ok", StringComparison.Ordinal));

        const string eligibilityPrefix = "SET @doka_sm_column_repair_eligible = CASE WHEN @doka_sm_state IS NULL "
            + "THEN COALESCE((";

        const string eligibilitySuffix = "), FALSE) ELSE FALSE END, @doka_sm_nullability_blocked = FALSE;";
        var eligibilityStart = command.CommandText.IndexOf(eligibilityPrefix, StringComparison.Ordinal)
            + eligibilityPrefix.Length;

        var eligibilityEnd = command.CommandText.IndexOf(eligibilitySuffix, eligibilityStart, StringComparison.Ordinal);
        var eligibilitySql = command.CommandText[eligibilityStart..eligibilityEnd];

        // WHY: Expand only the cache references in this generic fixture to retain
        // exactly the former classification/repair double-read shape. Both paths
        // use the same catalog SQL and unchanged table, excluding DDL rewrite I/O.
        var baselineSql = stateSql.Replace("@doka_sm_nullability_blocked",
                "EXISTS (SELECT 1 FROM `null_read_rows` WHERE `Value` IS NULL LIMIT 1)", StringComparison.Ordinal)
            .Replace("@doka_sm_column_repair_eligible", "(" + eligibilitySql + ")", StringComparison.Ordinal);

        var connection = context.Database.GetDbConnection();

        // Act
        var before = await ReadNullProofHandlerReadsAsync(connection);
        await ExecuteNullProofMeasurementSqlAsync(connection, baselineSql);
        var baselineReads = await ReadNullProofHandlerReadsAsync(connection) - before;
        before = await ReadNullProofHandlerReadsAsync(connection);
        await ExecuteNullProofMeasurementSqlAsync(connection,
            "SET @doka_sm_column_repair_eligible = (" + eligibilitySql + ");");
        await ExecuteNullProofMeasurementSqlAsync(connection, proofSql);
        await ExecuteNullProofMeasurementSqlAsync(connection, stateSql);
        var sharedReads = await ReadNullProofHandlerReadsAsync(connection) - before;
        Console.WriteLine(
            $"NULL-proof row-read evidence: rows={rowCount}, baseline={baselineReads}, shared={sharedReads}");

        // Assert
        Assert.True(baselineReads >= 2L * rowCount);
        Assert.True(sharedReads >= rowCount);
        // WHY: A few fewer catalog reads must not hide a second full data scan.
        // This compares engine work, not hardware-dependent elapsed time.
        Assert.True(sharedReads <= baselineReads * 3 / 4);
        Assert.Equal(1, await ContextScalarIntAsync(context,
            "SELECT @doka_sm_state = 'different' AND @doka_sm_repair_ok AND NOT @doka_sm_nullability_blocked;"));
        Assert.Equal(rowCount, await ContextScalarIntAsync(context, "SELECT COUNT(*) FROM `null_read_rows`;"));
    }

    /// <summary>Reads the selected session's full-scan counter without retaining SQL or connection details.</summary>
    private static async Task<long> ReadNullProofHandlerReadsAsync(DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SHOW SESSION STATUS LIKE 'Handler_read_rnd_next';";
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        if (!await reader.ReadAsync(CancellationToken.None))
        {
            throw new InvalidOperationException("The engine did not return its session row-read counter.");
        }

        return long.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
    }

    /// <summary>Executes only proof/classification statements against an already qualified synthetic table.</summary>
    private static async Task ExecuteNullProofMeasurementSqlAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>Decodes generated prepared statements for a byte-exact synthetic before/after comparison.</summary>
    private static List<string> DecodeNullProofMeasurementPayloads(string sql)
    {
        const string prefix = "CONVERT(0x";
        const string suffix = " USING utf8mb4)";
        var payloads = new List<string>();
        var offset = 0;
        while ((offset = sql.IndexOf(prefix, offset, StringComparison.Ordinal)) >= 0)
        {
            var start = offset + prefix.Length;
            var end = sql.IndexOf(suffix, start, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidOperationException("The generated measurement SQL is unterminated.");
            }

            payloads.Add(Encoding.UTF8.GetString(Convert.FromHexString(sql.AsSpan(start, end - start))));
            offset = end + suffix.Length;
        }

        return payloads;
    }
}
