namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Defines the annotation keys used to carry safe-migration metadata on EF Core migration operations.
/// </summary>
public static class SafeMigrationAnnotationNames
{
    /// <summary>
    /// Gets the common prefix applied to all safe-migration annotations.
    /// </summary>
    public const string Prefix = "SafeMigrations:";

    /// <summary>
    /// Marks an operation as guarded by an "if not exists" check.
    /// </summary>
    public const string IfNotExists = Prefix + "IfNotExists";

    /// <summary>
    /// Marks an operation as guarded by an "if exists" check.
    /// </summary>
    public const string IfExists = Prefix + "IfExists";

    /// <summary>
    /// Stores the legacy strict-mode value for an operation.
    /// </summary>
    public const string StrictMode = Prefix + "StrictMode";

    /// <summary>
    /// Stores the extended conflict-handling mode for an operation.
    /// </summary>
    public const string ConflictMode = Prefix + "ConflictMode";

    /// <summary>
    /// Marks an operation as preflight-only.
    /// </summary>
    public const string PreflightOnly = Prefix + "PreflightOnly";

    /// <summary>
    /// Stores the serialized expected definition used for comparison.
    /// </summary>
    public const string ExpectedDefinition = Prefix + "ExpectedDefinition";

    /// <summary>
    /// Marks an alter-column operation as safe "alter if different".
    /// </summary>
    public const string AlterIfDifferent = Prefix + "AlterIfDifferent";
}
