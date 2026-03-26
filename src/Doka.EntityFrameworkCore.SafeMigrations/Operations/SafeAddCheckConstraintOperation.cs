namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Represents a safe add-check-constraint operation with attached comparison metadata.
/// </summary>
public sealed class SafeAddCheckConstraintOperation : AddCheckConstraintOperation
{
    /// <summary>
    /// Gets or sets the legacy strict-mode behavior for the operation.
    /// </summary>
    public SafeMigrationStrictMode StrictMode { get; set; }

    /// <summary>
    /// Gets or sets the expected check-constraint definition used for comparison.
    /// </summary>
    public ExpectedCheckConstraintDefinition? ExpectedDefinition { get; set; }

    /// <summary>
    /// Gets or sets the extended execution options for the operation.
    /// </summary>
    public SafeMigrationExecutionOptions? Execution { get; set; }
}
