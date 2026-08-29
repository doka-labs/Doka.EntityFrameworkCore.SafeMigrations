namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Exposes the design-time scaffolding mode stored in a provider options extension.
/// </summary>
internal interface ISafeMigrationScaffoldingOptions
{
    /// <summary>Gets the mode frozen into newly scaffolded migrations.</summary>
    SafeMigrationScaffoldingMode ScaffoldingMode { get; }

    /// <summary>Gets the policy frozen into legacy-convergence table operations.</summary>
    SafeMigrationPolicy LegacyConvergencePolicy { get; }
}
