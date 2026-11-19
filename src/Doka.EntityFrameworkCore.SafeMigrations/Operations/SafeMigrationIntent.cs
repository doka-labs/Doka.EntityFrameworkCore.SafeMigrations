namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Base type for the closed set of immutable SafeMigrations intents.
/// </summary>
[PublicAPI]
public abstract class SafeMigrationIntent
{
    /// <summary>Initializes one of the assembly-owned migration intent contracts.</summary>
    /// <param name="kind">The operation family represented by the concrete intent.</param>
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
