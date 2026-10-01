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

    /// <summary>Parses bounded evidence into owned values without copying intermediate records.</summary>
    /// <param name="payload">The provider's delimiter-separated evidence payload.</param>
    /// <param name="providerName">The non-sensitive provider name used for malformed-output diagnostics.</param>
    /// <returns>The ordered, immutable evidence collection.</returns>
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
            // WHY: Validate the total envelope before scanning malformed provider output.
            // Valid SQL projections remain
            // well below this bound because every record has three capped fields.
            throw Malformed(providerName);
        }

        var source = payload.AsSpan();
        var recordCount = 0;
        foreach (var range in source.Split(RecordSeparator))
        {
            if (!source[range].IsEmpty
                && ++recordCount > SafeMigrationFacetDifference.MaximumDifferenceCount)
            {
                throw Malformed(providerName);
            }
        }

        // WHY: The first pass preserves the record cap without allocating record strings;
        // only the three final, validated field strings need to outlive this span scan.
        var differences = new SafeMigrationFacetDifference[recordCount];
        var ordinal = 0;
        foreach (var range in source.Split(RecordSeparator))
        {
            var record = source[range];
            if (record.IsEmpty)
            {
                continue;
            }

            var firstSeparator = record.IndexOf(FieldSeparator);
            var secondSeparator = firstSeparator < 0
                ? -1
                : record[(firstSeparator + 1)..].IndexOf(FieldSeparator);
            if (firstSeparator < 0
                || secondSeparator < 0)
            {
                throw Malformed(providerName);
            }

            secondSeparator += firstSeparator + 1;
            var facet = record[..firstSeparator];
            var expected = record[(firstSeparator + 1)..secondSeparator];
            var actual = record[(secondSeparator + 1)..];
            if (!IsSafe(facet, maximumLength: 64)
                || !IsSafe(expected, SafeMigrationFacetDifference.MaximumValueLength)
                || !IsSafe(actual, SafeMigrationFacetDifference.MaximumValueLength))
            {
                throw Malformed(providerName);
            }

            differences[ordinal++] = new SafeMigrationFacetDifference(
                facet.ToString(), expected.ToString(), actual.ToString());
        }

        return Array.AsReadOnly(differences);
    }

    private static bool IsSafe(
        ReadOnlySpan<char> value,
        int maximumLength
    )
    {
        if (value.IsEmpty
            || value.Length > maximumLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    private static InvalidOperationException Malformed(
        string providerName
    ) => new(
        $"{providerName} returned malformed SafeMigrations diagnostic evidence. "
        + "The payload was rejected without rendering its contents.");
}
