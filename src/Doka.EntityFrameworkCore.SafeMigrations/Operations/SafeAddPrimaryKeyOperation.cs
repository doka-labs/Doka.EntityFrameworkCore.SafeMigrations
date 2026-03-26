namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Represents a safe add-primary-key operation with attached comparison metadata.
/// </summary>
public sealed class SafeAddPrimaryKeyOperation : AddPrimaryKeyOperation
{
    /// <summary>
    /// Gets or sets the legacy strict-mode behavior for the operation.
    /// </summary>
    public SafeMigrationStrictMode StrictMode { get; set; }

    /// <summary>
    /// Gets or sets the expected primary-key definition used for comparison.
    /// </summary>
    public ExpectedPrimaryKeyDefinition? ExpectedDefinition { get; set; }

    /// <summary>
    /// Gets or sets the extended execution options for the operation.
    /// </summary>
    public SafeMigrationExecutionOptions? Execution { get; set; }
}
