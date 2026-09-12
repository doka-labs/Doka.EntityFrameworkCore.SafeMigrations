namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Contains bounded provider evidence required for projected index validation.</summary>
/// <param name="MaximumKeyBytes">
/// The maximum physical key width, or zero when the target storage engine is unsupported.
/// </param>
/// <param name="Columns">
/// The live physical column shapes available when an ordered composite index
/// includes columns that were not changed earlier in the migration.
/// </param>
internal sealed record SafeMigrationIndexPhysicalEnvironment(
    int MaximumKeyBytes,
    IReadOnlyDictionary<string, SafeMigrationIndexColumnPhysicalShape>? Columns = null);

/// <summary>Contains one live column's bounded physical index-key shape.</summary>
/// <param name="MaximumPrefixUnits">The maximum legal prefix units, when prefixes are supported.</param>
/// <param name="BytesPerPrefixUnit">The maximum bytes represented by one prefix unit.</param>
/// <param name="FullWidthBytes">The complete key width, or null when a full key is unbounded.</param>
/// <param name="SupportsPrefix">Whether this store family permits an explicit key prefix.</param>
internal readonly record struct SafeMigrationIndexColumnPhysicalShape(
    long? MaximumPrefixUnits,
    long BytesPerPrefixUnit,
    long? FullWidthBytes,
    bool SupportsPrefix);
