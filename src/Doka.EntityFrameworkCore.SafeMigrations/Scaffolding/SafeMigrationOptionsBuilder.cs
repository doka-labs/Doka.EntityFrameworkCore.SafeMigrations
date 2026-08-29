namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Configures SafeMigrations behavior used while EF Core scaffolds new
/// migrations.
/// </summary>
/// <remarks>
/// The selected behavior is written into the generated C# migration. Changing
/// this builder later never reinterprets an existing migration.
/// </remarks>
public sealed class SafeMigrationOptionsBuilder
{
    /// <summary>Gets the mode currently selected for generated migrations.</summary>
    internal SafeMigrationScaffoldingMode Mode { get; private set; } = SafeMigrationScaffoldingMode.Strict;

    /// <summary>Gets the policy frozen into legacy-convergence table operations.</summary>
    internal SafeMigrationPolicy LegacyConvergencePolicy { get; private set; } = SafeMigrationPolicy.ThrowIfDifferent;

    /// <summary>
    /// Selects the table strategy written into newly scaffolded migrations.
    /// </summary>
    /// <param name="mode">The scaffolding strategy.</param>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="mode"/> is not a defined scaffolding mode.
    /// </exception>
    public SafeMigrationOptionsBuilder UseScaffoldingMode(
        SafeMigrationScaffoldingMode mode
    )
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        Mode = mode;
        return this;
    }

    /// <summary>
    /// Selects the policy written into newly scaffolded legacy-convergence table
    /// operations.
    /// </summary>
    /// <param name="policy">
    /// <see cref="SafeMigrationPolicy.ThrowIfDifferent"/> to reject every existing
    /// mismatch, or <see cref="SafeMigrationPolicy.RepairIfSafe"/> to repair only
    /// provider-verified safe drift.
    /// </param>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="policy"/> is undefined or cannot preserve convergence
    /// safety.
    /// </exception>
    public SafeMigrationOptionsBuilder UseLegacyConvergencePolicy(
        SafeMigrationPolicy policy
    )
    {
        if (policy is not (SafeMigrationPolicy.ThrowIfDifferent or SafeMigrationPolicy.RepairIfSafe))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        LegacyConvergencePolicy = policy;
        return this;
    }

    /// <summary>Validates cross-setting design-time invariants after configuration.</summary>
    internal void Validate()
    {
        if (LegacyConvergencePolicy != SafeMigrationPolicy.ThrowIfDifferent
            && Mode != SafeMigrationScaffoldingMode.LegacyConvergence)
        {
            throw new InvalidOperationException(
                "A non-default legacy-convergence policy requires LegacyConvergence scaffolding mode.");
        }
    }
}
