namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Defines validated SQLite compatibility and batching limits.</summary>
internal static class SqliteSafeMigrationLimits
{
    /// <summary>Gets the minimum supported SQLite engine version.</summary>
    public static readonly Version MinimumVersion = new(3, 46, 1);

    /// <summary>Determines whether an engine version satisfies the supported floor.</summary>
    public static bool IsSupportedVersion(
        string value
    ) => Version.TryParse(value, out var version) && version >= MinimumVersion;

    // WHY: 900 remains below SQLite's historical 999-variable default while
    // leaving capacity for provider-added parameters in custom builds.
    /// <summary>Gets the safe maximum number of parameters emitted per command.</summary>
    public const int MaximumParametersPerCommand = 900;
}
