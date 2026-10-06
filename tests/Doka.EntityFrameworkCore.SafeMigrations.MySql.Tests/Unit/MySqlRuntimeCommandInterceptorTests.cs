namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Checks command-level runtime categories without executing or retaining migration SQL.</summary>
public sealed class MySqlRuntimeCommandInterceptorTests
{
    private const string PreparedEvaluation = "PREPARE doka_sm_statement FROM @doka_sm_sql;"
        + "EXECUTE doka_sm_statement;DEALLOCATE PREPARE doka_sm_statement;";
    private const string AssertionSetup = "DROP TEMPORARY TABLE IF EXISTS `__doka_sm_assert`;"
        + "CREATE TEMPORARY TABLE `__doka_sm_assert` (`different_code` TINYINT);"
        + "INSERT INTO `__doka_sm_assert` (`different_code`) VALUES (1);";
    private const string Prerequisite = "SET @doka_sm_prerequisite_ok = COALESCE((TRUE), FALSE);";
    private const string InitialState = "SET @doka_sm_state = CASE WHEN NOT @doka_sm_prerequisite_ok "
        + "THEN 'prerequisite_missing' ELSE NULL END, @doka_sm_repair_ok = FALSE;";
    private const string ActionAssignment = "SET @doka_sm_action = CASE @doka_sm_state "
        + "WHEN 'missing' THEN 'apply' ELSE 'skip' END;";
    private const string DecisionAssertion = "INSERT INTO `__doka_sm_assert` (`different_code`) "
        + "SELECT 1 WHERE @doka_sm_action = 'reject_different';";
    private const string UnknownSetup = "SET @doka_sm_future_probe = FALSE;";

    /// <summary>Routes known commands to fixed categories and preserves cleanup/body precedence.</summary>
    /// <param name="sql">A synthetic command with only test-owned identifiers and literals.</param>
    /// <param name="expectedCategory">The command's one low-cardinality category.</param>
    [Theory]
    [InlineData(AssertionSetup, "assertion_setup")]
    [InlineData(Prerequisite, "prerequisite_qualification")]
    [InlineData(InitialState, "prerequisite_qualification")]
    [InlineData("SET @doka_sm_state = CASE WHEN COALESCE((TRUE), FALSE) THEN NULL ELSE 'unsupported' END;",
        "prerequisite_qualification")]
    [InlineData("SET @doka_sm_state = CASE WHEN @doka_sm_state IS NOT NULL THEN @doka_sm_state "
        + "WHEN NOT @doka_sm_prerequisite_ok THEN 'prerequisite_missing' ELSE NULL END;",
        "prerequisite_qualification")]
    [InlineData("SET @doka_sm_transition_eligible = CASE WHEN @doka_sm_state IS NULL THEN TRUE ELSE FALSE END;",
        "column_data_probe")]
    [InlineData("SET @doka_sm_data_probe_required = FALSE, @doka_sm_data_blocked = FALSE;",
        "column_data_probe")]
    [InlineData("SET @doka_sm_sql = CASE WHEN @doka_sm_data_probe_required THEN CONVERT(0x53454C4543542031 "
        + "USING utf8mb4) ELSE 'DO 0' END;" + PreparedEvaluation, "column_data_probe")]
    [InlineData("SET @doka_sm_state = ('matching'), @doka_sm_repair_ok = (FALSE);",
        "state_classification_repair")]
    [InlineData("SET @doka_sm_repair_ok = CASE WHEN @doka_sm_state = 'different' "
        + "THEN COALESCE((TRUE), FALSE) ELSE FALSE END;", "state_classification_repair")]
    [InlineData("SET @doka_sm_state = CASE WHEN @doka_sm_state IS NULL THEN CASE WHEN TRUE "
        + "THEN 'matching' ELSE NULL END ELSE @doka_sm_state END;", "state_classification_repair")]
    [InlineData(ActionAssignment, "decision_prepare")]
    [InlineData(DecisionAssertion, "decision_prepare")]
    [InlineData("SET @doka_sm_sql = CASE WHEN @doka_sm_action = 'apply' THEN CONVERT(0x444F2030 "
        + "USING utf8mb4) ELSE 'DO 0' END;PREPARE doka_sm_statement FROM @doka_sm_sql;", "decision_prepare")]
    [InlineData("PREPARE doka_sm_statement FROM @doka_sm_sql;", "decision_prepare")]
    [InlineData(UnknownSetup, "other_setup")]
    [InlineData("DO @doka_sm_future_probe;", "other_setup")]
    [InlineData("EXECUTE doka_sm_statement;SET @doka_sm_post_ok = TRUE;" + DecisionAssertion, "guarded_body")]
    [InlineData("SET @doka_sm_state = NULL, @doka_sm_action = NULL;" + AssertionSetup, "guard_cleanup")]
    [InlineData("PREPARE doka_sm_statement FROM 'DO 0';DEALLOCATE PREPARE doka_sm_statement;", "prepared_cleanup")]
    [InlineData("SELECT 1;", "migration_infrastructure")]
    public void Classify_RoutesKnownCommands(
        string sql,
        string expectedCategory
    )
    {
        // Arrange
        var commandText = sql;

        // Act
        var category = MySqlRuntimeCommandInterceptor.Classify(commandText);

        // Assert
        Assert.Equal(expectedCategory, category);
    }

    /// <summary>Distinguishes hex-encoded row guards from state classification without decoding payloads.</summary>
    /// <param name="statement">The test-owned statement encoded as the real handler encodes prepared SQL.</param>
    /// <param name="expectedCategory">The category of the complete assignment and prepared-control group.</param>
    [Theory]
    [InlineData("SELECT CASE WHEN NOT COALESCE((TRUE), FALSE) THEN 'data_blocked' ELSE NULL END "
        + "INTO @doka_sm_state", "column_data_probe")]
    [InlineData("SELECT ('matching'), COALESCE((FALSE), FALSE) INTO @doka_sm_state, @doka_sm_repair_ok",
        "state_classification_repair")]
    [InlineData("SELECT ('matching') INTO @doka_sm_state", "state_classification_repair")]
    public void Classify_HexAssignmentsUseOnlyKnownPurposePrefixes(
        string statement,
        string expectedCategory
    )
    {
        // Arrange
        var hexadecimal = Convert.ToHexString(Encoding.UTF8.GetBytes(statement));
        var sql = "SET @doka_sm_sql = CASE WHEN @doka_sm_state IS NULL THEN CONVERT(0x"
            + hexadecimal + " USING utf8mb4) ELSE 'DO 0' END;" + PreparedEvaluation;

        // Act
        var category = MySqlRuntimeCommandInterceptor.Classify(sql);

        // Assert
        Assert.Equal(expectedCategory, category);
    }

    /// <summary>Keeps the NULL-proof eligibility and prepared group together as one row-probe measurement.</summary>
    [Fact]
    public void Classify_NullabilityEligibilityAndPreparedGroupShareOnePurpose()
    {
        // Arrange
        const string sql = "SET @doka_sm_column_repair_eligible = CASE WHEN @doka_sm_state IS NULL "
            + "THEN COALESCE((TRUE), FALSE) ELSE FALSE END, @doka_sm_nullability_blocked = FALSE;"
            + "SET @doka_sm_sql = CASE WHEN @doka_sm_state IS NULL THEN CASE WHEN COALESCE((TRUE) "
            + "AND @doka_sm_column_repair_eligible, FALSE) THEN CONVERT(0x53454C4543542031 USING utf8mb4) "
            + "ELSE 'DO 0' END ELSE 'DO 0' END;" + PreparedEvaluation;

        // Act
        var category = MySqlRuntimeCommandInterceptor.Classify(sql);

        // Assert
        Assert.Equal("column_data_probe", category);
    }

    /// <summary>Does not label one mixed dispatch's elapsed duration as a single setup phase.</summary>
    /// <param name="first">The first independently meaningful owned setup fragment.</param>
    /// <param name="second">A later fragment with a different purpose, including unknown future setup.</param>
    [Theory]
    [InlineData(AssertionSetup, Prerequisite)]
    [InlineData(Prerequisite, ActionAssignment)]
    [InlineData(ActionAssignment, UnknownSetup)]
    [InlineData(UnknownSetup, AssertionSetup)]
    [InlineData(AssertionSetup, DecisionAssertion)]
    public void Classify_MixedOwnedSetupUsesOneBatchCategory(
        string first,
        string second
    )
    {
        // Arrange
        var sql = first + second;

        // Act
        var category = MySqlRuntimeCommandInterceptor.Classify(sql);

        // Assert
        Assert.Equal("owned_setup_batch", category);
    }

    /// <summary>Retains a shared purpose when grouped fragments belong to the same setup phase.</summary>
    /// <param name="sql">The concatenated same-purpose fragments.</param>
    /// <param name="expectedCategory">The shared command-level category.</param>
    [Theory]
    [InlineData(Prerequisite + InitialState, "prerequisite_qualification")]
    [InlineData(ActionAssignment + DecisionAssertion, "decision_prepare")]
    public void Classify_SamePurposeGroupingKeepsItsCategory(
        string sql,
        string expectedCategory
    )
    {
        // Arrange
        var commandText = sql;

        // Act
        var category = MySqlRuntimeCommandInterceptor.Classify(commandText);

        // Assert
        Assert.Equal(expectedCategory, category);
    }
}
