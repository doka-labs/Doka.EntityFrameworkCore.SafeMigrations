namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal static class PostgreSqlSafeMigrationPlanner
{
    public static SafeMigrationDecision PlanIndex(
        SafeMigrationExecutionOptions execution,
        SafeMigrationComparisonState comparisonState,
        ExpectedIndexDefinition? expected
    )
    {
        // expected: reserved for future provider-specific index repair/veto logic
        return SafeMigrationDecisionPlanner.Plan(execution, comparisonState);
    }
}
