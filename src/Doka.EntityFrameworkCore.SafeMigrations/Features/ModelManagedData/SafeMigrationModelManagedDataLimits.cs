namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static class SafeMigrationModelManagedDataLimits
{
    public const int MaximumCellsPerOperation = 4_096;

    public const int MaximumRowsPerOperation = 128;
}
