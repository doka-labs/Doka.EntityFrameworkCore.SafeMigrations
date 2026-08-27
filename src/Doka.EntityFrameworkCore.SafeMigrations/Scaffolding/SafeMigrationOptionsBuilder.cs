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
}
