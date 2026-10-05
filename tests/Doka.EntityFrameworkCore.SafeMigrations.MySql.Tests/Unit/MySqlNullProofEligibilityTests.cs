namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Checks identical physical eligibility for inline and prepared NULL proofs.</summary>
public sealed class MySqlNullProofEligibilityTests
{
    /// <summary>Places all row SQL inside an explicit catalog-qualified CASE branch.</summary>
    /// <param name="nullable">The synthetic live physical-nullability predicate.</param>
    /// <param name="eligible">The synthetic physical repair predicate.</param>
    [Theory]
    [InlineData("TRUE", "TRUE")]
    [InlineData("FALSE", "TRUE")]
    [InlineData("TRUE", "FALSE")]
    public void InlineNullProof_RequiresPhysicalNullabilityAndRepairInvariant(
        string nullable,
        string eligible
    )
    {
        // Arrange
        const string rowProbe = "EXISTS (SELECT 1 FROM `proof_rows` WHERE `Value` IS NULL LIMIT 1)";
        var plan = new MySqlSafeMigrationRuntimePlan(
            MySqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder,
            "TRUE",
            SafeMigrationRepairCapability.Safe,
            MySqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder)
        {
            NullabilityDataProbe = new MySqlSafeMigrationNullabilityDataProbe(nullable, eligible, rowProbe),
        };

        // Act
        var state = plan.RenderStateExpression(static _ => throw new InvalidOperationException());
        var repair = plan.RenderRepairPrecondition(static _ => throw new InvalidOperationException());
        var runtime = plan.RenderPreparedStateExpression([]);

        // Assert
        Assert.Equal($"CASE WHEN ({nullable}) AND ({eligible}) THEN ({rowProbe}) ELSE FALSE END", state);
        Assert.Equal(state, repair);
        Assert.Equal("@doka_sm_nullability_blocked", runtime);
        Assert.DoesNotContain("@doka_sm_", state, StringComparison.Ordinal);
    }

    /// <summary>Expands shared narrowing eligibility before building the inline NULL-proof gate.</summary>
    /// <param name="transitionEligible">The fresh catalog transition result.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InlineNullProof_UsesCurrentGroupedTransitionEvidence(
        bool transitionEligible
    )
    {
        // Arrange
        var plan = new MySqlSafeMigrationRuntimePlan(
            MySqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder,
            "TRUE",
            SafeMigrationRepairCapability.Safe,
            MySqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder)
        {
            DataProbe = new MySqlSafeMigrationDataProbe(
                "proof_rows", "Value", 800, "`proof_rows`", "`Value`", "fresh_transition", "narrowing"),
            NullabilityDataProbe = new MySqlSafeMigrationNullabilityDataProbe(
                "physical_nullable",
                "physical_same OR " + MySqlSafeMigrationRuntimePlan.TransitionInvariantPlaceholder,
                "fresh_null_probe"),
        };

        // Act
        var analysis = plan.RenderStateExpression(
            static _ => throw new InvalidOperationException(), dataBlocked: false, transitionEligible);
        var runtimeInvariant = plan.RenderPreparedNullabilityRepairInvariantExpression([]);

        // Assert
        var transition = transitionEligible ? "TRUE" : "FALSE";
        Assert.Equal("CASE WHEN (physical_nullable) AND (physical_same OR " + transition
            + ") THEN (fresh_null_probe) ELSE FALSE END", analysis);
        Assert.Equal("physical_same OR @doka_sm_transition_eligible", runtimeInvariant);
        Assert.DoesNotContain("__DOKA_SM_", analysis, StringComparison.Ordinal);
    }
}
