namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Describes whether the provider has proven a lossless repair for the
/// classified difference.
/// </summary>
public enum SafeMigrationRepairCapability
{
    /// <summary>No lossless repair is available.</summary>
    None = 0,

    /// <summary>A lossless repair is available and its preconditions passed.</summary>
    Safe = 1,
}
