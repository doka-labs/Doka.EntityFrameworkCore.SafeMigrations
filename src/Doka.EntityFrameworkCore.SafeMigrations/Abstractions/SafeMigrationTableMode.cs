namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Defines how an existing table is compared.
/// </summary>
public enum SafeMigrationTableMode
{
    /// <summary>
    /// Compares object kind, ordered columns, primary key, unique constraints,
    /// checks and foreign keys.
    /// </summary>
    StrictDefinition = 0,

    /// <summary>
    /// Compares only table existence and object kind. Subsequent granular safe
    /// operations establish the complete definition.
    /// </summary>
    ConvergenceContainer = 1,
}
