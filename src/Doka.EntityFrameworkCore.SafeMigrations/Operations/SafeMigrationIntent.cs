namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Base type for the closed set of immutable SafeMigrations intents.
/// </summary>
public abstract class SafeMigrationIntent
{
    private protected SafeMigrationIntent(
        SafeMigrationOperationKind kind
    )
    {
        Kind = kind;
    }

    /// <summary>Gets the operation kind.</summary>
    public SafeMigrationOperationKind Kind { get; }

    /// <summary>Gets the primary database object name.</summary>
    public abstract string ObjectName { get; }
}
