namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationOperationHandler
{
    /// <summary>Binds each proof only to a provider-owned result variable within this operation.</summary>
    /// <param name="plan">The unbound runtime contract.</param>
    /// <returns>The same plan when no binary proof is required, otherwise an operation-local binding.</returns>
    private static MySqlSafeMigrationRuntimePlan BindRuntimeBinaryDefaults(MySqlSafeMigrationRuntimePlan plan)
    {
        if (plan.BinaryDefaultProofs.Count == 0)
        {
            return plan;
        }

        var proofs = new MySqlSafeMigrationBinaryDefaultProof[plan.BinaryDefaultProofs.Count];
        for (var ordinal = 0; ordinal < proofs.Length; ordinal++)
        {
            proofs[ordinal] = plan.BinaryDefaultProofs[ordinal] with
            {
                Binding = MySqlSafeMigrationBinaryDefaultProof.Variable(ordinal),
            };
        }

        return plan with { BinaryDefaultProofs = proofs };
    }

    /// <summary>Materializes current native defaults only after completed identity or action guards.</summary>
    /// <param name="plan">The bound operation-local runtime plan.</param>
    /// <param name="values">The prepared provider literal values.</param>
    /// <param name="executionGuard">The independently completed scope guard.</param>
    /// <returns>One guarded SELECT for every owned default, or the shared empty string.</returns>
    private static string BuildBinaryDefaultProofEvaluation(
        MySqlSafeMigrationRuntimePlan plan,
        IReadOnlyList<string> values,
        string executionGuard
    )
    {
        if (plan.BinaryDefaultProofs.Count == 0)
        {
            return string.Empty;
        }

        var eligibility = new string[plan.BinaryDefaultProofs.Count];
        for (var ordinal = 0; ordinal < eligibility.Length; ordinal++)
        {
            eligibility[ordinal] = MySqlSafeMigrationRuntimePlan.RenderPreparedBinaryDefaultEligibilityExpression(
                plan.BinaryDefaultProofs[ordinal], values);
        }

        return MySqlSafeMigrationBinaryDefaultProof.BuildEvaluationSql(
            plan.BinaryDefaultProofs, eligibility, executionGuard, PreparedStatementName, "@doka_sm_sql");
    }
}
