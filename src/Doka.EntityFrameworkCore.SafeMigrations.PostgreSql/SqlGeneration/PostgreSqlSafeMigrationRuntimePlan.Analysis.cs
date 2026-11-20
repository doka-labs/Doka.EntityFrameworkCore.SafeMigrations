namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial record PostgreSqlSafeMigrationRuntimePlan
{
    /// <summary>Renders the fixed classifier result shape without owning transport or observing new metadata.</summary>
    /// <param name="ordinal">The original safe-operation ordinal, not a compacted transport offset.</param>
    /// <param name="dataProbe">The existing source-bound row proof when this plan requires one.</param>
    /// <returns>The same parameter-bound classifier selection consumed by the catalog reader.</returns>
    internal string RenderAnalysisSelection(
        int ordinal,
        (bool IsBlocked, bool IsTransitionEligible)? dataProbe
    )
    {
        if (DataProbe is not null
            && dataProbe is null)
        {
            throw new InvalidOperationException("The PostgreSQL narrowing probe returned no classification result.");
        }

        var (isBlocked, isTransitionEligible) = dataProbe.GetValueOrDefault();
        var stateExpression = DataProbe is null
            ? RenderStateExpression()
            : RenderStateExpression(isBlocked, isTransitionEligible);

        var repairPrecondition = DataProbe is null
            ? RenderRepairPrecondition()
            : RenderRepairPrecondition(isBlocked, isTransitionEligible);

        var nullabilityProofRequired = DataProbe is null
            ? RenderNullabilityDataProbeRequiredExpression()
            : RenderNullabilityDataProbeRequiredExpression(isTransitionEligible);

        var classificationCode = DataProbe is null
            ? RenderClassificationCodeExpression() ?? "NULL"
            : RenderClassificationCodeExpression(isBlocked) ?? "NULL";

        var postcondition = Postcondition;
        if (PrerequisiteExpression == "TRUE"
            && ObjectIdentityGuardExpression != "TRUE")
        {
            // WHY: Catalog-only classifiers fold owner identity into their existing selection.
            // Plans reading rows keep the earlier identity-first prerequisite before probing data.
            var rejectedIdentity = "NOT COALESCE((" + ObjectIdentityGuardExpression + "), FALSE)";
            stateExpression = $"CASE WHEN {rejectedIdentity} THEN 'unsupported' ELSE ({stateExpression}) END";
            classificationCode = $"CASE WHEN {rejectedIdentity} "
                + $"THEN '{SchemaIdentityMismatchCode}' ELSE ({classificationCode}) END";

            postcondition = $"CASE WHEN {rejectedIdentity} THEN FALSE ELSE ({postcondition}) END";
            repairPrecondition = $"CASE WHEN {rejectedIdentity} THEN FALSE ELSE ({repairPrecondition}) END";
        }

        var rowEvidence = ModelManagedRowEvidenceExpression ?? "NULL";
        var dependencyCounts = ModelManagedDependencyCountsExpression ?? "NULL";
        var diagnosticEvidence = DiagnosticEvidenceExpression ?? "NULL";
        var matchedObjectName = MatchedObjectNameExpression ?? "NULL";

        return $"""
            SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, ({stateExpression})::text,
            COALESCE(({postcondition}), FALSE), COALESCE(({repairPrecondition}),
            FALSE), ({classificationCode}), ({rowEvidence}),
            ({dependencyCounts}), ({diagnosticEvidence}), ({matchedObjectName}),
            COALESCE(({nullabilityProofRequired}), FALSE)
            """;
    }
}
