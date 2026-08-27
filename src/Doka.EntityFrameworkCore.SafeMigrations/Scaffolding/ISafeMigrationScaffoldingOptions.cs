namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Exposes the design-time scaffolding mode stored in a provider options extension.
/// </summary>
internal interface ISafeMigrationScaffoldingOptions
{
    /// <summary>Gets the mode frozen into newly scaffolded migrations.</summary>
    SafeMigrationScaffoldingMode ScaffoldingMode { get; }
}
