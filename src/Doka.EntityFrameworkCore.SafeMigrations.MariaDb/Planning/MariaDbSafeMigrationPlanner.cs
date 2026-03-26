namespace Doka.EntityFrameworkCore.SafeMigrations.MariaDb;

internal static class MariaDbSafeMigrationPlanner
{
    public static SafeMigrationDecision PlanIndex(
        SafeMigrationExecutionOptions execution,
        SafeMigrationComparisonState comparisonState,
        ExpectedIndexDefinition expected
    )
    {
        ArgumentNullException.ThrowIfNull(expected);

        if (execution.ConflictMode != SafeMigrationConflictMode.None
            && !string.IsNullOrWhiteSpace(expected.Filter))
        {
            return new SafeMigrationDecision(
                SafeMigrationExecutionOutcome.Rejected,
                SafeMigrationPlannedAction.Reject,
                ShouldExecute: false,
                $"MariaDB safe-migration repair planning does not support filtered indexes. Index '{expected.Name}' on table '{expected.Table}' uses filter '{expected.Filter}'.");
        }

        return SafeMigrationDecisionPlanner.Plan(execution, comparisonState);
    }
}
