namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Parses the provider's bounded catalog-only diagnostic projection without
/// exposing a malformed database value through the resulting exception.
/// </summary>
internal static class SafeMigrationFacetDifferenceParser
{
    private const char RecordSeparator = '\u001e';
    private const char FieldSeparator = '\u001f';
    private const int MaximumPayloadLength = SafeMigrationFacetDifference.MaximumDifferenceCount
        * ((SafeMigrationFacetDifference.MaximumValueLength * 2) + 67);

    public static IReadOnlyList<SafeMigrationFacetDifference> Parse(
        string? payload,
        string providerName
    )
    {
        if (string.IsNullOrEmpty(payload))
        {
            return [];
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        if (payload.Length > MaximumPayloadLength)
        {
            // WHY: Validate the total envelope before Split allocates strings
            // from malformed provider output. Valid SQL projections remain
            // well below this bound because every record has three capped fields.
            throw Malformed(providerName);
        }

        var records = payload.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (records.Length > SafeMigrationFacetDifference.MaximumDifferenceCount)
        {
            throw Malformed(providerName);
        }

        var differences = new SafeMigrationFacetDifference[records.Length];
        for (var ordinal = 0; ordinal < records.Length; ordinal++)
        {
            var fields = records[ordinal].Split(FieldSeparator, StringSplitOptions.None);
            if (fields.Length != 3
                || !IsSafe(fields[0], maximumLength: 64)
                || !IsSafe(fields[1], SafeMigrationFacetDifference.MaximumValueLength)
                || !IsSafe(fields[2], SafeMigrationFacetDifference.MaximumValueLength))
            {
                throw Malformed(providerName);
            }

            differences[ordinal] = new SafeMigrationFacetDifference(fields[0], fields[1], fields[2]);
        }

        return Array.AsReadOnly(differences);
    }

    private static bool IsSafe(
        string value,
        int maximumLength
    ) => value.Length is > 0
        && value.Length <= maximumLength
        && value.All(static character => character is >= ' ' and <= '~');

    private static InvalidOperationException Malformed(
        string providerName
    ) => new(
        $"{providerName} returned malformed SafeMigrations diagnostic evidence. "
        + "The payload was rejected without rendering its contents.");
}
