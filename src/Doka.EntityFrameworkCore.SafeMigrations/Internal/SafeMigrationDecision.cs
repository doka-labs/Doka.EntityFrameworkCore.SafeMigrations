namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed record SafeMigrationDecision(
    SafeMigrationExecutionOutcome Outcome,
    SafeMigrationPlannedAction PlannedAction,
    bool ShouldExecute,
    string Reason
);
