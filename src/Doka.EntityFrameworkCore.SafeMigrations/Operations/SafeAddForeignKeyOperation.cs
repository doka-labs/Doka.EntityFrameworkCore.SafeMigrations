namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Represents a safe add-foreign-key operation with attached comparison metadata.
/// </summary>
public sealed class SafeAddForeignKeyOperation : AddForeignKeyOperation
{
    /// <summary>
    /// Gets or sets the legacy strict-mode behavior for the operation.
    /// </summary>
    public SafeMigrationStrictMode StrictMode { get; set; }

    /// <summary>
    /// Gets or sets the expected foreign-key definition used for comparison.
    /// </summary>
    public ExpectedForeignKeyDefinition? ExpectedDefinition { get; set; }

    /// <summary>
    /// Gets or sets the extended execution options for the operation.
    /// </summary>
    public SafeMigrationExecutionOptions? Execution { get; set; }
}
