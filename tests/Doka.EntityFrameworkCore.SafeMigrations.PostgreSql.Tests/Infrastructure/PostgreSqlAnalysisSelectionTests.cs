namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Pins classifier extraction to the earlier SQL shape, including UTF-8 and owner-proof boundaries.</summary>
public sealed class PostgreSqlAnalysisSelectionTests
{
    /// <summary>Lists independent native proof and metadata shapes as separately discovered cases.</summary>
    /// <returns>Independent row/NULL proofs, each owner-guard route and optional evidence.</returns>
    public static IEnumerable<object[]> SelectionCases()
    {
        for (var probeState = -1; probeState < 4; probeState++)
        {
            for (var nullability = 0; nullability < 2; nullability++)
            {
                for (var owner = 0; owner < 3; owner++)
                {
                    for (var evidence = 0; evidence < 2; evidence++)
                    {
                        yield return [probeState, nullability != 0, owner, evidence != 0];
                    }
                }
            }
        }
    }

    /// <summary>The extracted renderer preserves every byte of the original inline classifier.</summary>
    /// <param name="probeState">Absent row evidence, or the independent blocked/transition flags.</param>
    /// <param name="nullability">Whether a NULL-proof eligibility expression is present.</param>
    /// <param name="owner">No owner guard, folded catalog-only guard, or an earlier prerequisite guard.</param>
    /// <param name="evidence">Whether optional diagnostic and matched-name slots carry expressions.</param>
    [Theory]
    [MemberData(nameof(SelectionCases))]
    public void RenderAnalysisSelection_PreservesInlineSqlAndUtf8Bytes(
        int probeState,
        bool nullability,
        int owner,
        bool evidence
    )
    {
        // Arrange
        const int ordinal = 157;
        var plan = CreatePlan(probeState >= 0, nullability, owner, evidence);
        (bool IsBlocked, bool IsTransitionEligible)? proof = probeState < 0
            ? null
            : ((probeState & 1) != 0, (probeState & 2) != 0);

        var expected = RenderEarlierInlineSelection(plan, ordinal, proof);

        // Act
        var actual = plan.RenderAnalysisSelection(ordinal, proof);

        // Assert
        Assert.Equal(expected, actual);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
        Assert.DoesNotContain("\r", actual, StringComparison.Ordinal);
        Assert.StartsWith("SELECT 157, ", actual, StringComparison.Ordinal);
    }

    /// <summary>Missing required row evidence is rejected rather than silently receiving false proof values.</summary>
    [Fact]
    public void RenderAnalysisSelection_MissingRequiredProofIsRejected()
    {
        // Arrange
        var plan = CreatePlan(hasProbe: true, nullability: false, owner: 0, evidence: false);

        // Act
        var failure = Record.Exception(() => plan.RenderAnalysisSelection(0, dataProbe: null));

        // Assert
        var invalid = Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal("The PostgreSQL narrowing probe returned no classification result.", invalid.Message);
    }

    /// <summary>Constructs finite native plans with independent placeholders for the actual render consumers.</summary>
    private static PostgreSqlSafeMigrationRuntimePlan CreatePlan(
        bool hasProbe,
        bool nullability,
        int owner,
        bool evidence
    )
    {
        var state = hasProbe
            ? $"CASE WHEN {PostgreSqlSafeMigrationRuntimePlan.DataProbePlaceholder} "
            + $"THEN 'data_blocked' WHEN {PostgreSqlSafeMigrationRuntimePlan.TransitionInvariantPlaceholder} "
            + "THEN 'matching' ELSE 'different' END"
            : "'matching'";

        var repair = hasProbe
            ? PostgreSqlSafeMigrationRuntimePlan.TransitionInvariantPlaceholder
            : "repair_native";

        if (nullability)
        {
            state = $"CASE WHEN {PostgreSqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder} "
                + $"THEN 'data_blocked' ELSE ({state}) END";

            repair = PostgreSqlSafeMigrationRuntimePlan.NullabilityRepairInvariantPlaceholder;
        }

        return new PostgreSqlSafeMigrationRuntimePlan(
            state,
            "postcondition_native",
            SafeMigrationRepairCapability.Safe,
            repair)
        {
            PrerequisiteExpression = owner == 2
                ? "prerequisite_native"
                : "TRUE",
            ObjectIdentityGuardExpression = owner == 0
                ? "TRUE"
                : "owner_native",
            DataProbe = hasProbe
                ? new PostgreSqlSafeMigrationDataProbe(
                    "owned",
                    "app",
                    "Value",
                    17,
                    "app.owned",
                    "\"Value\"",
                    "transition_native",
                    "narrowing_native")
                : null,
            NullabilityDataProbe = nullability
                ? new PostgreSqlSafeMigrationNullabilityDataProbe(
                    "not_null_native",
                    hasProbe
                        ? PostgreSqlSafeMigrationRuntimePlan.TransitionInvariantPlaceholder
                        : "repair_invariant_native",
                    "null_blocked_native",
                    "app.owned")
                : null,
            ClassificationCodeExpression = evidence
                ? "'native_reason_\u20AC'"
                : null,
            ModelManagedRowEvidenceExpression = evidence
                ? "row_evidence_native"
                : null,
            ModelManagedDependencyCountsExpression = evidence
                ? "dependency_counts_native"
                : null,
            DiagnosticEvidenceExpression = evidence
                ? "diagnostic_native"
                : null,
            MatchedObjectNameExpression = evidence
                ? "matched_name_native"
                : null,
        };
    }

    /// <summary>Retains the earlier inline assembly as an independent, byte-sensitive extraction oracle.</summary>
    private static string RenderEarlierInlineSelection(
        PostgreSqlSafeMigrationRuntimePlan plan,
        int ordinal,
        (bool IsBlocked, bool IsTransitionEligible)? dataProbe
    )
    {
        var state = plan.DataProbe is null
            ? plan.RenderStateExpression()
            : plan.RenderStateExpression(dataProbe!.Value.IsBlocked, dataProbe.Value.IsTransitionEligible);

        var repair = plan.DataProbe is null
            ? plan.RenderRepairPrecondition()
            : plan.RenderRepairPrecondition(dataProbe!.Value.IsBlocked, dataProbe.Value.IsTransitionEligible);

        var nullability = plan.DataProbe is null
            ? plan.RenderNullabilityDataProbeRequiredExpression()
            : plan.RenderNullabilityDataProbeRequiredExpression(dataProbe!.Value.IsTransitionEligible);

        var code = plan.DataProbe is null
            ? plan.RenderClassificationCodeExpression() ?? "NULL"
            : plan.RenderClassificationCodeExpression(dataProbe!.Value.IsBlocked) ?? "NULL";

        var postcondition = plan.Postcondition;
        if (plan.PrerequisiteExpression == "TRUE"
            && plan.ObjectIdentityGuardExpression != "TRUE")
        {
            var rejected = "NOT COALESCE((" + plan.ObjectIdentityGuardExpression + "), FALSE)";
            state = $"CASE WHEN {rejected} THEN 'unsupported' ELSE ({state}) END";
            code = $"CASE WHEN {rejected} THEN 'schema_search_path_mismatch' ELSE ({code}) END";
            postcondition = $"CASE WHEN {rejected} THEN FALSE ELSE ({postcondition}) END";
            repair = $"CASE WHEN {rejected} THEN FALSE ELSE ({repair}) END";
        }

        // WHY: This is the exact previous selection, not a SQL-whitespace-normalized comparison.
        // An extra newline or changed evidence order changes byte budgeting and must fail parity.
        var rowEvidence = plan.ModelManagedRowEvidenceExpression ?? "NULL";
        var dependencyCounts = plan.ModelManagedDependencyCountsExpression ?? "NULL";
        var diagnosticEvidence = plan.DiagnosticEvidenceExpression ?? "NULL";
        var matchedObjectName = plan.MatchedObjectNameExpression ?? "NULL";

        return $"""
            SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, ({state})::text,
            COALESCE(({postcondition}), FALSE), COALESCE(({repair}),
            FALSE), ({code}), ({rowEvidence}),
            ({dependencyCounts}), ({diagnosticEvidence}), ({matchedObjectName}),
            COALESCE(({nullability}), FALSE)
            """;
    }
}
