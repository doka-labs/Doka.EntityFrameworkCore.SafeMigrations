namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Represents the immutable design-time configuration resolved from the active
/// provider options extension.
/// </summary>
/// <param name="IsEnabled">Whether SafeMigrations scaffolding is active.</param>
/// <param name="Mode">The mode written into newly scaffolded migrations.</param>
internal sealed record SafeMigrationScaffoldingConfiguration(
    bool IsEnabled,
    SafeMigrationScaffoldingMode Mode)
{
    /// <summary>Creates a configuration snapshot from EF Core context options.</summary>
    /// <param name="options">The active context options, or null when unavailable.</param>
    /// <returns>The resolved immutable scaffolding configuration.</returns>
    public static SafeMigrationScaffoldingConfiguration From(
        IDbContextOptions? options
    )
    {
        var extension = options?.Extensions.OfType<ISafeMigrationScaffoldingOptions>().SingleOrDefault();

        return extension is null
            ? new SafeMigrationScaffoldingConfiguration(false, SafeMigrationScaffoldingMode.Strict)
            : new SafeMigrationScaffoldingConfiguration(true, extension.ScaffoldingMode);
    }
}
