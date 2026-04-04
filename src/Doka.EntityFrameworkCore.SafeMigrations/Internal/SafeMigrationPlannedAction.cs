namespace Doka.EntityFrameworkCore.SafeMigrations;

internal enum SafeMigrationPlannedAction
{
    None = 0,
    CreateMissingObject = 1,
    Reject = 3,
}
