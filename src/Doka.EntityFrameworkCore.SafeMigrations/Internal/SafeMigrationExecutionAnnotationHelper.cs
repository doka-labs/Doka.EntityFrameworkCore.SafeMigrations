namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static class SafeMigrationExecutionAnnotationHelper
{
    public static void Apply(
        MigrationOperation operation,
        SafeMigrationExecutionOptions execution
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(execution);

        operation[SafeMigrationAnnotationNames.ConflictMode] = execution.ConflictMode;
        operation[SafeMigrationAnnotationNames.PreflightOnly] = execution.PreflightOnly;
    }

    public static SafeMigrationExecutionOptions GetExecutionOptions(
        MigrationOperation operation
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (operation[SafeMigrationAnnotationNames.ConflictMode] is SafeMigrationConflictMode conflictMode)
        {
            return new SafeMigrationExecutionOptions(
                conflictMode,
                operation[SafeMigrationAnnotationNames.PreflightOnly] is true);
        }

        return new SafeMigrationExecutionOptions(GetFallbackConflictMode(operation));
    }

    public static SafeMigrationStrictMode GetCompatibleStrictMode(
        SafeMigrationExecutionOptions execution
    ) => execution.ConflictMode switch
    {
        SafeMigrationConflictMode.None => SafeMigrationStrictMode.None,
        SafeMigrationConflictMode.ThrowIfDifferent => SafeMigrationStrictMode.ThrowIfDifferent,
        SafeMigrationConflictMode.RepairIfPossible => SafeMigrationStrictMode.ThrowIfDifferent,
        _ => throw new ArgumentOutOfRangeException(nameof(execution)),
    };

    private static SafeMigrationConflictMode GetFallbackConflictMode(
        MigrationOperation operation
    ) => operation[SafeMigrationAnnotationNames.StrictMode] is SafeMigrationStrictMode strictMode
        ? strictMode switch
        {
            SafeMigrationStrictMode.None => SafeMigrationConflictMode.None,
            SafeMigrationStrictMode.ThrowIfDifferent => SafeMigrationConflictMode.ThrowIfDifferent,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        }
        : SafeMigrationConflictMode.None;
}
