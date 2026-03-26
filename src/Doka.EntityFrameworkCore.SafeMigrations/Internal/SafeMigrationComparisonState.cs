namespace Doka.EntityFrameworkCore.SafeMigrations;

internal enum SafeMigrationComparisonState
{
    Missing = 0,
    Matches = 1,
    Different = 2
}
