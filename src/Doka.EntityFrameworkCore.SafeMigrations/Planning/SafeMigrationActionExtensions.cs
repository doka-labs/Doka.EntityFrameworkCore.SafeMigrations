namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static class SafeMigrationActionExtensions
{
    /// <summary>Determines whether an action blocks preflight execution.</summary>
    internal static bool RejectsExecution(
        this SafeMigrationAction action
    ) => action switch
    {
        SafeMigrationAction.Apply => false,
        SafeMigrationAction.NoOp => false,
        SafeMigrationAction.Repair => false,
        SafeMigrationAction.RejectUnsupported => true,
        SafeMigrationAction.RejectDifferent => true,
        SafeMigrationAction.RejectDataBlocked => true,
        SafeMigrationAction.RejectPrerequisiteMissing => true,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}
