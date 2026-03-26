namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static class SafeMigrationDecisionPlanner
{
    public static SafeMigrationDecision Plan(
        SafeMigrationExecutionOptions execution,
        SafeMigrationComparisonState comparisonState
    )
        => comparisonState switch
        {
            SafeMigrationComparisonState.Missing => new(
                SafeMigrationExecutionOutcome.Created,
                SafeMigrationPlannedAction.CreateMissingObject,
                ShouldExecute(execution),
                "Object is missing and should be created."),

            SafeMigrationComparisonState.Matches => new(
                SafeMigrationExecutionOutcome.Matched,
                SafeMigrationPlannedAction.None,
                false,
                "Existing definition already matches."),

            SafeMigrationComparisonState.Different => PlanForDifferentDefinition(execution),

            _ => throw new ArgumentOutOfRangeException(nameof(comparisonState))
        };

    public static SafeMigrationDecision PlanUniqueConstraint(
        SafeMigrationExecutionOptions execution,
        SafeMigrationComparisonState comparisonState,
        ExpectedUniqueConstraintDefinition? expected
    )
    {
        // expected: reserved for future provider-specific constraint repair/veto logic (see PlanColumn for active usage)
        return Plan(execution, comparisonState);
    }

    public static SafeMigrationDecision PlanPrimaryKey(
        SafeMigrationExecutionOptions execution,
        SafeMigrationComparisonState comparisonState,
        ExpectedPrimaryKeyDefinition? expected
    )
    {
        // expected: reserved for future provider-specific constraint repair/veto logic (see PlanColumn for active usage)
        return Plan(execution, comparisonState);
    }

    public static SafeMigrationDecision PlanCheckConstraint(
        SafeMigrationExecutionOptions execution,
        SafeMigrationComparisonState comparisonState,
        ExpectedCheckConstraintDefinition? expected
    )
    {
        // expected: reserved for future provider-specific constraint repair/veto logic (see PlanColumn for active usage)
        return Plan(execution, comparisonState);
    }

    public static SafeMigrationDecision PlanForeignKey(
        SafeMigrationExecutionOptions execution,
        SafeMigrationComparisonState comparisonState,
        ExpectedForeignKeyDefinition? expected
    )
    {
        // expected: reserved for future provider-specific constraint repair/veto logic (see PlanColumn for active usage)
        return Plan(execution, comparisonState);
    }

    public static SafeMigrationDecision PlanColumn(
        SafeMigrationExecutionOptions execution,
        SafeMigrationComparisonState comparisonState,
        ExpectedColumnDefinition expected
    )
    {
        ArgumentNullException.ThrowIfNull(expected);

        if (comparisonState == SafeMigrationComparisonState.Missing
            && !SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(expected)
            && execution.ConflictMode != SafeMigrationConflictMode.None)
        {
            return new SafeMigrationDecision(
                SafeMigrationExecutionOutcome.Rejected,
                SafeMigrationPlannedAction.Reject,
                ShouldExecute: false,
                $"Safe additive-column repair is not allowed for column '{expected.Name}' because the missing column is not nullable and has no safe default or computed expression.");
        }

        return Plan(execution, comparisonState);
    }

    private static SafeMigrationDecision PlanForDifferentDefinition(
        SafeMigrationExecutionOptions execution
    )
        => execution.ConflictMode switch
        {
            SafeMigrationConflictMode.None => new(
                SafeMigrationExecutionOutcome.NoOp,
                SafeMigrationPlannedAction.None,
                false,
                "Object already exists and conflict mode is None."),

            SafeMigrationConflictMode.ThrowIfDifferent => new(
                SafeMigrationExecutionOutcome.Rejected,
                SafeMigrationPlannedAction.Reject,
                false,
                "Existing definition differs and conflict mode requires rejection."),

            SafeMigrationConflictMode.RepairIfPossible => new(
                SafeMigrationExecutionOutcome.Rejected,
                SafeMigrationPlannedAction.Reject,
                false,
                "Existing definition differs and cannot be repaired safely."),

            _ => throw new ArgumentOutOfRangeException(nameof(execution.ConflictMode), execution.ConflictMode, null)
        };

    private static bool ShouldExecute(SafeMigrationExecutionOptions execution)
        => !execution.PreflightOnly;
}
