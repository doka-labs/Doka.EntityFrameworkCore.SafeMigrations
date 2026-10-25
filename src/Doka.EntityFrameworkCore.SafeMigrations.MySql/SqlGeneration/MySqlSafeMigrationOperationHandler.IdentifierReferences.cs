namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationOperationHandler
{
    /// <summary>Assigns native syntax facts only to the result variable of their unique operation scope.</summary>
    private static MySqlSafeMigrationRuntimePlan BindRuntimeIdentifierReferences(
        MySqlSafeMigrationRuntimePlan plan
    )
    {
        if (plan.IdentifierReferenceProofs.Count == 0)
        {
            return plan;
        }

        var bound = new MySqlSafeMigrationIdentifierReferenceProof[plan.IdentifierReferenceProofs.Count];
        // WHY: Fill each bound proof slot using the matching original proof ordinal.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < bound.Length; index++)
        {
            var proof = plan.IdentifierReferenceProofs[index];
            bound[index] = proof with { Binding = proof.ResultVariable };
        }

        return plan with { IdentifierReferenceProofs = bound };
    }

    /// <summary>Compiles suspect roots only after the operation's identity and prerequisite guards complete.</summary>
    private static string BuildIdentifierReferenceProofEvaluation(
        MySqlSafeMigrationRuntimePlan plan
    )
    {
        var builder = new StringBuilder();
        foreach (var proof in plan.IdentifierReferenceProofs)
        {
            var root = Convert.ToHexString(Encoding.UTF8.GetBytes(proof.RootIdentifier));
            var eligibility = "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.KEYWORDS k "
                + $"WHERE k.WORD = CONVERT(0x{root} USING utf8mb4))";

            builder.Append(
                MySqlSafeMigrationIdentifierReferenceProof.BuildEvaluationSql(
                    proof,
                    "@doka_sm_state IS NULL",
                    eligibility));
        }

        return builder.ToString();
    }
}
