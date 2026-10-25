namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    /// <summary>Rechecks MySQL's native ban on generated references to an AUTO_INCREMENT source.</summary>
    private MySqlSafeMigrationRuntimePlan ApplyGeneratedIdentityDependencyQualification(
        SafeMigrationIntent intent,
        MySqlSafeMigrationRuntimePlan plan,
        bool isMariaDb
    )
    {
        var target = intent switch
        {
            EnsureColumnIntent value => (value.Table, Definition: value.Definition),
            AlterColumnIntent value => (value.Table, Definition: value.Definition),
            _ => default,
        };

        if (isMariaDb
            || target.Table is null
            || target.Definition?.ComputedExpression is null
            || plan.IsStaticallyUnsupported)
        {
            return plan;
        }

        var sources = MySqlGeneratedColumnDependencies.Collect(target.Definition);
        if (sources.Count == 0)
        {
            return plan;
        }

        var blocked = "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS identity_source "
            + "WHERE identity_source.TABLE_SCHEMA = DATABASE() "
            + $"AND identity_source.TABLE_NAME = {Literal(target.Table)} "
            + "AND identity_source.COLUMN_NAME IN ("
            + string.Join(", ", sources.Select(Literal))
            + ") AND LOCATE('auto_increment', LOWER(identity_source.EXTRA)) > 0)";

        // WHY: An integer column with AUTO_INCREMENT is not an ordinary integer
        // operand in MySQL's generated grammar. A live catalog guard and accepted
        // projected metadata must agree; nullable targets do not waive this rule.
        return plan with
        {
            StateEvaluationGuardExpression = $"NOT ({blocked}) AND ({plan.StateEvaluationGuardExpression})",
            StateEvaluationGuardFailureExpression =
            $"CASE WHEN ({blocked}) THEN 'unsupported' "
            + $"ELSE ({plan.StateEvaluationGuardFailureExpression ?? "NULL"}) END",
            ClassificationCodeExpression =
            $"CASE WHEN ({blocked}) THEN 'generated_column_identity_dependency' "
            + $"ELSE ({plan.ClassificationCodeExpression ?? "NULL"}) END",
            Postcondition = $"NOT ({blocked}) AND ({plan.Postcondition})",
            RepairPrecondition = $"NOT ({blocked}) AND ({plan.RepairPrecondition})",
            RequiresLazyStateEvaluation = true,
        };
    }
}
