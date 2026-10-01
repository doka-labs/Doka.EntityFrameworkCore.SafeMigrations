namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies delayed classifier binding and payload bounds before database execution.
/// </summary>
public sealed class SqlServerDelayedClassifierSqlTests
{
    /// <summary>
    /// Retains nonzero original ordinals in both successful and guarded fallback rows.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    [InlineData(520)]
    public void DelayedSelection_PreservesOrdinalAndDefersNameBinding(
        int ordinal
    )
    {
        // Arrange
        var plan = new SqlServerSafeMigrationRuntimePlan(
            "CASE WHEN EXISTS (SELECT 1 FROM dbo.missing_items) THEN N'matching' ELSE N'missing' END",
            "0", SafeMigrationRepairCapability.None, "0")
        {
            PrerequisiteExpression = "0",
            RequiresDelayedBinding = true,
        };

        // Act
        var sql = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogSelection(ordinal, plan);

        // Assert
        Assert.Contains("INSERT INTO @doka_analysis EXEC sys.sp_executesql N'SELECT @doka_ordinal,",
            sql, StringComparison.Ordinal);
        Assert.Contains("N'@doka_ordinal int', @doka_ordinal = " + ordinal.ToString(CultureInfo.InvariantCulture),
            sql, StringComparison.Ordinal);
        Assert.Contains("ELSE INSERT INTO @doka_analysis SELECT "
            + ordinal.ToString(CultureInfo.InvariantCulture) + ", N'prerequisite_missing'",
            sql, StringComparison.Ordinal);
        Assert.Contains("N''matching''", sql, StringComparison.Ordinal);
        Assert.InRange(Encoding.UTF8.GetByteCount(sql), 1, SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes);
    }

    /// <summary>
    /// Rejects a small unescaped expression whose dynamic-literal quote expansion exceeds the wire budget.
    /// </summary>
    [Fact]
    public void DelayedSelection_RejectsOversizedEscapedLiteralBeforeAnyReaderExists()
    {
        // Arrange
        var expression = "N'" + new string('\'', SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes / 2) + "'";
        var plan = new SqlServerSafeMigrationRuntimePlan(
            expression, "0", SafeMigrationRepairCapability.None, "0")
        {
            RequiresDelayedBinding = true,
        };

        // Act
        var exception = Record.Exception(() =>
            SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogSelection(257, plan));

        // Assert
        Assert.True(Encoding.UTF8.GetByteCount(expression) < SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes);
        var oversized = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("operation 257 exceeds a bounded query limit", oversized.Message, StringComparison.Ordinal);
        Assert.Contains("utf8_payload_bytes=", oversized.Message, StringComparison.Ordinal);
    }
}
