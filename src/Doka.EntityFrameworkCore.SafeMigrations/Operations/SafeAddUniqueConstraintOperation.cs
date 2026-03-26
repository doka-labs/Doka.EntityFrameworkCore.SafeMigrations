namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Represents a safe add-unique-constraint operation with attached comparison metadata.
/// </summary>
public sealed class SafeAddUniqueConstraintOperation : AddUniqueConstraintOperation
{
    /// <summary>
    /// Gets or sets the legacy strict-mode behavior for the operation.
    /// </summary>
    public SafeMigrationStrictMode StrictMode { get; set; }

    /// <summary>
    /// Gets or sets the expected unique-constraint definition used for comparison.
    /// </summary>
    public ExpectedUniqueConstraintDefinition? ExpectedDefinition { get; set; }

    /// <summary>
    /// Gets or sets the extended execution options for the operation.
    /// </summary>
    public SafeMigrationExecutionOptions? Execution { get; set; }
}
