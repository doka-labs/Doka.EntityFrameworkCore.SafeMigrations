namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Describes the provider-neutral classification of an observed database
/// object or operation precondition.
/// </summary>
public enum SafeMigrationObservedState
{
    /// <summary>The expected object is absent.</summary>
    Missing = 0,

    /// <summary>The observed definition matches the expected definition.</summary>
    Matching = 1,

    /// <summary>The observed definition differs from the expected definition.</summary>
    Different = 2,

    /// <summary>The active engine cannot represent the requested operation.</summary>
    Unsupported = 3,

    /// <summary>Existing data prevents the requested safe transition.</summary>
    DataBlocked = 4,
}
