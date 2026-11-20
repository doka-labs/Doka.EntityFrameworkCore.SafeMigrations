// WHY: The linked test and benchmark assemblies consume different image families and parser entry points.
// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global
// ReSharper disable MemberCanBePrivate.Global

namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

/// <summary>Resolves fixture and benchmark images from the embedded canonical data-only manifest.</summary>
internal static class DatabaseImageCatalog
{
    /// <summary>Identifies the same embedded catalog in each consuming test or benchmark assembly.</summary>
    internal const string ResourceName = "Doka.SafeMigrations.DatabaseImages";

    /// <summary>Identifies the existing default native family for MySQL-provider qualification.</summary>
    internal const string DefaultMySqlEngine = "mariadb";

    /// <summary>Identifies the reviewed default MariaDB version without changing its native defaults.</summary>
    internal const string DefaultMySqlVersion = "12.3.3";

    /// <summary>Identifies the existing default PostgreSQL qualification version.</summary>
    internal const string DefaultPostgreSqlVersion = "18.7";

    /// <summary>Identifies the existing default supported x64 SQL Server target.</summary>
    internal const string DefaultSqlServerTarget = "sqlserver2022";

    private static readonly Dictionary<string, string> s_images = ReadManifest();

    /// <summary>Gets an exact image reference for one declared native qualification target.</summary>
    /// <param name="target">The canonical FROM-stage target.</param>
    /// <returns>The tagged, digest-pinned image reference.</returns>
    internal static string GetImage(
        string target
    ) => s_images.TryGetValue(target, out var image)
        ? image
        : throw new InvalidOperationException("The requested database image target is not declared.");

    /// <summary>Preserves explicit image/version overrides while pinning catalog-supported defaults.</summary>
    /// <param name="repository">The existing fixture's native repository identity.</param>
    /// <param name="version">The selected native version or tag.</param>
    /// <param name="configuredImage">An explicit user image override, or null to resolve the version.</param>
    /// <returns>The explicit override, canonical pinned reference, or explicit unlisted-version tag.</returns>
    internal static string Resolve(
        string repository,
        string version,
        string? configuredImage
    )
    {
        if (!string.IsNullOrWhiteSpace(configuredImage))
        {
            return configuredImage.Trim();
        }

        var tagged = repository + ":" + version;
        // WHY: Keep direct dictionary-value enumeration and stop at the first exact tagged image.
        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
        foreach (var image in s_images.Values)
        {
            if (image
                .AsSpan(0, image.IndexOf('@'))
                .SequenceEqual(tagged))
            {
                return image;
            }
        }

        // WHY: Existing fixture version overrides may deliberately exercise a
        // not-yet-listed engine. Only explicit image/version selection opts out
        // of the reviewed default; it must not be replaced by another version.
        return tagged;
    }

    /// <summary>Parses a fresh catalog without executing or accepting other Dockerfile instructions.</summary>
    /// <param name="reader">The manifest reader owned and disposed by the caller.</param>
    /// <returns>An isolated map of validated target identities and pinned references.</returns>
    internal static Dictionary<string, string> Parse(
        TextReader reader
    )
    {
        Dictionary<string, string> images = new(StringComparer.Ordinal);
        while (reader.ReadLine() is { } source)
        {
            var line = source.Trim();
            if (line.Length == 0
                || line.StartsWith('#'))
            {
                continue;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields is not ["FROM", _, "AS", _]
                || !IsTarget(fields[3])
                || !IsImage(fields[1])
                || !images.TryAdd(fields[3], fields[1]))
            {
                throw new InvalidOperationException(
                    "Database image catalog requires unique data-only FROM declarations with tagged SHA-256 images.");
            }
        }

        return images.Count == 0
            ? throw new InvalidOperationException("Database image catalog must declare a native target.")
            : images;
    }

    /// <summary>Loads the reviewed catalog linked into the actual consuming assembly.</summary>
    /// <returns>The validated immutable-by-ownership target map.</returns>
    private static Dictionary<string, string> ReadManifest()
    {
        using var stream = typeof(DatabaseImageCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The consuming assembly does not embed its database image catalog.");

        using var reader = new StreamReader(stream);

        return Parse(reader);
    }

    /// <summary>Recognizes only explicit lowercase/alphanumeric qualification-stage names.</summary>
    /// <param name="value">The authored stage name.</param>
    /// <returns>Whether the name is a bounded data identity rather than an expression.</returns>
    private static bool IsTarget(
        string value
    ) => value.Length > 0
        && value[0] is >= 'a' and <= 'z'
        && value.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9');

    /// <summary>Recognizes the shared tagged-reference grammar without mutable or expression forms.</summary>
    /// <param name="value">The authored native image reference.</param>
    /// <returns>Whether a tag and exact lowercase SHA-256 digest are present.</returns>
    private static bool IsImage(
        string value
    )
    {
        const string marker = "@sha256:";
        var separator = value.IndexOf(marker, StringComparison.Ordinal);
        var tag = value.LastIndexOf(':',
            separator < 0
                ? value.Length - 1
                : separator);

        if (separator <= 0
            || value.Length - separator - marker.Length != 64
            || tag <= 0
            || tag == separator - 1
            || value[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9'))
        {
            return false;
        }

        // WHY: Validation depends on each character's position relative to the tag and digest boundaries.
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            var valid = index < tag
                ? character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '/' or '_' or '-'
                : index == tag
                || (index >= separator && index < separator + marker.Length)
                || (index < separator
                    ? character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-'
                    : character is >= 'a' and <= 'f' or >= '0' and <= '9');

            if (!valid)
            {
                return false;
            }
        }

        return value[tag + 1] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
    }
}
