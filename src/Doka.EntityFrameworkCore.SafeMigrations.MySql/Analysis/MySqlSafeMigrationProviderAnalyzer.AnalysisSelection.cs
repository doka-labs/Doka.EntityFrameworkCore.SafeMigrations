namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    /// <summary>Renders one selection in the same parameter-registration and diagnostic field order.</summary>
    /// <param name="plan">The exact operation's captured runtime plan.</param>
    /// <param name="dataProbeResult">The previously qualified narrowing proof, when the plan requires one.</param>
    /// <param name="parameterizer">The statement-owned parameter registry and rollback checkpoint domain.</param>
    /// <param name="resultOrdinal">The operation's stable original full-stream ordinal.</param>
    /// <param name="captureDropCompleteness">
    /// Whether this statement needs the generated-column completeness field.
    /// </param>
    /// <returns>The unmodified classification SQL selection.</returns>
    private static string BuildClassificationSelection(
        MySqlSafeMigrationRuntimePlan plan,
        MySqlDataProbeResult? dataProbeResult,
        MySqlCatalogQueryParameterizer parameterizer,
        int resultOrdinal,
        bool captureDropCompleteness
    )
    {
        var stateExpression = plan.DataProbe is null
            ? plan.RenderStateExpression(parameterizer.Add)
            : plan.RenderStateExpression(
                parameterizer.Add,
                dataProbeResult!.Value.IsBlocked,
                dataProbeResult.Value.IsTransitionEligible);

        var postcondition = plan.RenderPostcondition(parameterizer.Add);
        var repairPrecondition = plan.DataProbe is null
            ? plan.RenderRepairPrecondition(parameterizer.Add)
            : plan.RenderRepairPrecondition(
                parameterizer.Add,
                dataProbeResult!.Value.IsBlocked,
                dataProbeResult.Value.IsTransitionEligible);

        var classificationCode = plan.ClassificationCodeExpression switch
        {
            null => "NULL",
            _ when plan.DataProbe is null => plan.RenderClassificationCodeExpression(parameterizer.Add),
            _ => plan.RenderClassificationCodeExpression(parameterizer.Add, dataProbeResult!.Value.IsBlocked),
        };

        var rowEvidence = plan.ModelManagedRowEvidenceExpression is null
            ? "NULL"
            : plan.RenderModelManagedRowEvidenceExpression(parameterizer.Add);

        var dependencyCounts = plan.ModelManagedDependencyCountsExpression is null
            ? "NULL"
            : plan.RenderModelManagedDependencyCountsExpression(parameterizer.Add);

        var diagnosticEvidence = plan.DiagnosticEvidenceExpression is null
            ? "NULL"
            : plan.RenderDiagnosticEvidenceExpression(parameterizer.Add);

        var matchedObjectName = plan.MatchedObjectNameExpression is null
            ? "NULL"
            : plan.RenderMatchedObjectNameExpression(parameterizer.Add);

        var selection = $"""
            SELECT {resultOrdinal.ToString(
                CultureInfo.InvariantCulture)}, ({stateExpression}), COALESCE(({postcondition}),
            FALSE), COALESCE(({repairPrecondition}), FALSE), ({classificationCode}),
            ({rowEvidence}), ({dependencyCounts}), ({diagnosticEvidence}),
            ({matchedObjectName})
            """;

        if (captureDropCompleteness)
        {
            selection += plan.DropLeavesNoGeneratedColumnsExpression is not null
                ? ", " + plan.RenderDropLeavesNoGeneratedColumnsExpression(parameterizer.Add)
                : ", NULL";
        }

        return selection;
    }
}
