namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Contains the deterministic action and stable low-cardinality result code
/// selected by the provider-neutral planner.
/// </summary>
public sealed class SafeMigrationDecision
{
    internal SafeMigrationDecision(
        SafeMigrationAction action,
        string code
    )
    {
        Action = action;
        Code = code;
    }

    /// <summary>Gets the selected action.</summary>
    public SafeMigrationAction Action { get; }

    /// <summary>Gets the stable low-cardinality decision code.</summary>
    public string Code { get; }

    /// <summary>Gets whether target DDL or a repair must run.</summary>
    public bool ShouldExecute => Action is SafeMigrationAction.Apply or SafeMigrationAction.Repair;
}
