namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Guards nonvacuous matching instrumentation independently of a live database or container fixture.</summary>
public sealed class MySqlRuntimeMatchingProbeInterceptorTests
{
    private const string RequiredAddCatalogExpression = "CASE WHEN EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS "
        + "WHERE COLUMN_NAME = 'value') THEN 'complete' ELSE 'ordinary' END";
    private const string RequiredAddStatusPrefix =
        "SET @doka_sm_add_status = CASE WHEN @doka_sm_state IS NULL THEN (";
    private const string RequiredAddStatusSuffix = ") ELSE NULL END;";
    private const string MatchingCounterPrefix = "CASE WHEN (@runtime_matching_catalog_visits := "
        + "COALESCE(@runtime_matching_catalog_visits, 0) + 1) > 0 THEN ";

    /// <summary>Counts the required-add catalog expression only inside its original qualification gate.</summary>
    [Fact]
    public void RequiredAddStatusFactCountsGuardedCatalogWithoutChangingItsResults()
    {
        // Arrange
        var observer = new MySqlSafeMigrationIntegrationTests.RuntimeMatchingProbeInterceptor();
        var original = RequiredAddStatusPrefix + RequiredAddCatalogExpression + RequiredAddStatusSuffix;
        var expected = RequiredAddStatusPrefix + MatchingCounterPrefix + RequiredAddCatalogExpression
            + " ELSE NULL END" + RequiredAddStatusSuffix;

        // Act
        var instrumented = observer.InstrumentCommand(original);

        // Assert
        Assert.Equal(expected, instrumented);
        Assert.Equal(1, observer.MatchingInstrumentations);
        Assert.Equal(0, observer.FinalRepairInstrumentations);
        Assert.Equal(0, observer.CatalogInstrumentations);
        Assert.Equal(0, observer.RowInstrumentations);
    }

    /// <summary>The earlier operation-local exact-match shape remains independently instrumented.</summary>
    [Fact]
    public void OrdinaryMatchingBranchRetainsItsExistingGuardedInstrumentation()
    {
        // Arrange
        var observer = new MySqlSafeMigrationIntegrationTests.RuntimeMatchingProbeInterceptor();
        const string prefix = "SET @doka_sm_state = CASE WHEN @doka_sm_state IS NULL THEN ";
        const string expression = "CASE WHEN COALESCE((TRUE), FALSE) THEN 'matching' ELSE NULL END";
        const string suffix = " ELSE @doka_sm_state END;";
        var original = prefix + expression + suffix;
        var expected = prefix + MatchingCounterPrefix + expression + " ELSE NULL END" + suffix;

        // Act
        var instrumented = observer.InstrumentCommand(original);

        // Assert
        Assert.Equal(expected, instrumented);
        Assert.Equal(1, observer.MatchingInstrumentations);
        Assert.Equal(0, observer.FinalRepairInstrumentations);
        Assert.Equal(0, observer.CatalogInstrumentations);
        Assert.Equal(0, observer.RowInstrumentations);
    }

    /// <summary>Reading a captured fact or an unrelated setup shape cannot masquerade as fresh catalog work.</summary>
    /// <param name="sql">The non-catalog or deliberately unrecognized command shape.</param>
    [Theory]
    [InlineData("SET @doka_sm_state = CASE WHEN @doka_sm_add_status = 'complete' "
        + "THEN 'matching' ELSE @doka_sm_state END;")]
    [InlineData("SET @doka_sm_required_add_resume = CASE WHEN @doka_sm_action = 'apply' "
        + "THEN COALESCE((@doka_sm_add_status = 'staged'), FALSE) ELSE FALSE END;")]
    [InlineData("SET @doka_sm_future_add_status = CASE WHEN @doka_sm_state IS NULL "
        + "THEN ('complete') ELSE NULL END;")]
    [InlineData("SET @doka_sm_add_status = CASE WHEN @doka_sm_state IS NOT NULL "
        + "THEN ('complete') ELSE NULL END;")]
    [InlineData("SET @doka_sm_add_status = CASE WHEN @doka_sm_state IS NULL "
        + "THEN ('complete') ELSE 'ordinary' END;")]
    [InlineData("EXECUTE doka_sm_statement;SET @doka_sm_post_ok = TRUE;")]
    public void CapturedFactReadsAndUnknownShapesDoNotInventMatchingInstrumentation(string sql)
    {
        // Arrange
        var observer = new MySqlSafeMigrationIntegrationTests.RuntimeMatchingProbeInterceptor();

        // Act
        var instrumented = observer.InstrumentCommand(sql);

        // Assert
        Assert.Equal(sql, instrumented);
        Assert.Equal(0, observer.MatchingInstrumentations);
        Assert.Equal(0, observer.FinalRepairInstrumentations);
        Assert.Equal(0, observer.CatalogInstrumentations);
        Assert.Equal(0, observer.RowInstrumentations);
    }

    /// <summary>Required-add fact instrumentation does not remove repair or prepared row-proof observers.</summary>
    [Fact]
    public void RequiredAddCatalogFactPreservesIndependentRepairAndRowInstrumentation()
    {
        // Arrange
        var observer = new MySqlSafeMigrationIntegrationTests.RuntimeMatchingProbeInterceptor();
        var nullProof = "SELECT (FALSE) INTO @doka_sm_nullability_blocked;";
        var lengthProof = "SELECT (FALSE) INTO @doka_sm_data_blocked;";
        var original = RequiredAddStatusPrefix + RequiredAddCatalogExpression + RequiredAddStatusSuffix
            + "SET @doka_sm_transition_eligible = CASE WHEN @doka_sm_state IS NULL "
            + "THEN COALESCE((TRUE), FALSE) ELSE FALSE END;"
            + "SET @doka_sm_column_repair_eligible = CASE WHEN @doka_sm_state IS NULL "
            + "THEN COALESCE((TRUE), FALSE) ELSE FALSE END, @doka_sm_nullability_blocked = FALSE;"
            + "SET @doka_sm_repair_ok = CASE WHEN @doka_sm_state = 'different' "
            + "THEN COALESCE((TRUE), FALSE) ELSE FALSE END;"
            + RuntimeMatchingPreparedProof(nullProof) + RuntimeMatchingPreparedProof(lengthProof);

        // Act
        var instrumented = observer.InstrumentCommand(original);

        // Assert
        Assert.Equal(1, observer.MatchingInstrumentations);
        Assert.Equal(1, observer.FinalRepairInstrumentations);
        Assert.Equal(2, observer.CatalogInstrumentations);
        Assert.Equal(2, observer.RowInstrumentations);
        Assert.Contains(MatchingCounterPrefix + RequiredAddCatalogExpression, instrumented,
            StringComparison.Ordinal);
        Assert.Contains("@runtime_matching_transition_visits", instrumented, StringComparison.Ordinal);
        Assert.Contains("@runtime_matching_repair_visits", instrumented, StringComparison.Ordinal);
        Assert.Contains("@runtime_matching_final_repair_visits", instrumented, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexString(Encoding.UTF8.GetBytes("@runtime_matching_null_visits")),
            instrumented, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexString(Encoding.UTF8.GetBytes("@runtime_matching_length_visits")),
            instrumented, StringComparison.Ordinal);
    }

    /// <summary>Creates the handler's real hex-encoded prepared row-proof transport for observer self-tests.</summary>
    /// <param name="statement">The anonymous row-proof SQL payload.</param>
    /// <returns>The complete guarded assignment and contiguous prepared-control group.</returns>
    private static string RuntimeMatchingPreparedProof(string statement) =>
        "SET @doka_sm_sql = CASE WHEN @doka_sm_state IS NULL THEN CONVERT(0x"
        + Convert.ToHexString(Encoding.UTF8.GetBytes(statement))
        + " USING utf8mb4) ELSE 'DO 0' END;PREPARE doka_sm_statement FROM @doka_sm_sql;"
        + "EXECUTE doka_sm_statement;DEALLOCATE PREPARE doka_sm_statement;";
}
