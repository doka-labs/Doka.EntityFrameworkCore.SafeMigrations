namespace Doka.EntityFrameworkCore.SafeMigrations;

internal enum SafeMigrationExecutionOutcome
{
    NoOp = 0,
    Created = 1,
    Matched = 2,
    Rejected = 3,
}
