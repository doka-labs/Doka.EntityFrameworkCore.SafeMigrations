namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>
/// Maps MySQL and MariaDB column store types to the common, bounded CAST grammar.
/// </summary>
internal static class MySqlSafeMigrationCastTypeRenderer
{
    private const int MaximumStoreTypeLength = 128;

    private const RegexOptions StoreTypeRegexOptions = RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant
        | RegexOptions.NonBacktracking;

    // Migration planning is not a hot loop, and its input is bounded above.
    // Cached non-backtracking patterns avoid per-call construction and
    // catastrophic backtracking without generating a large regex engine into
    // the provider assembly.
    private static readonly Regex s_unsignedIntegerTypePattern = new(
        "^(?:(?:tinyint|smallint|mediumint|int|integer|bigint|int1|int2|int3|int4|int8|bool|boolean)"
        + @"(?:\s*\(\s*\d+\s*\))?\s+(?:unsigned(?:\s+zerofill)?|zerofill)|unsigned(?:\s+integer)?)$",
        StoreTypeRegexOptions);

    private static readonly Regex s_signedIntegerTypePattern = new(
        "^(?:(?:tinyint|smallint|mediumint|int|integer|bigint|int1|int2|int3|int4|int8|bool|boolean)"
        + @"(?:\s*\(\s*\d+\s*\))?(?:\s+signed)?|signed(?:\s+integer)?)$",
        StoreTypeRegexOptions);

    private static readonly Regex s_decimalTypePattern = new(
        @"^(?:decimal|numeric|dec|fixed)(?:\s*\(\s*(?<precision>\d+)(?:\s*,\s*(?<scale>\d+))?\s*\))?$",
        StoreTypeRegexOptions);

    private static readonly Regex s_doubleTypePattern = new("^(?:double(?:\\s+precision)?)$", StoreTypeRegexOptions);

    private static readonly Regex s_floatTypePattern = new("^float$", StoreTypeRegexOptions);

    private static readonly Regex s_characterTypePattern = new(
        @"^(?:char|character|varchar|character\s+varying)(?:\s*\(\s*(?<length>\d+)\s*\))?$",
        StoreTypeRegexOptions);

    private static readonly Regex s_textTypePattern = new(
        "^(?:tinytext|text|mediumtext|longtext)$",
        StoreTypeRegexOptions);

    private static readonly Regex s_binaryTypePattern = new(
        @"^binary(?:\s*\(\s*(?<length>\d+)\s*\))?$",
        StoreTypeRegexOptions);

    private static readonly Regex s_dateTypePattern = new("^date$", StoreTypeRegexOptions);

    private static readonly Regex s_dateTimeTypePattern = new(
        @"^datetime(?:\s*\(\s*(?<precision>\d+)\s*\))?$",
        StoreTypeRegexOptions);

    private static readonly Regex s_timeTypePattern = new(
        @"^time(?:\s*\(\s*(?<precision>\d+)\s*\))?$",
        StoreTypeRegexOptions);

    /// <summary>Renders a validated CAST target or fails before SQL generation.</summary>
    /// <param name="storeType">The provider store type requested by the structured expression.</param>
    /// <returns>The canonical CAST target shared by MySQL and MariaDB.</returns>
    /// <exception cref="NotSupportedException">
    /// The store type cannot be represented by the common MySQL and MariaDB CAST grammar.
    /// </exception>
    public static string Render(
        string storeType
    )
    {
        if (TryRender(storeType, out var castType))
        {
            return castType;
        }

        throw new NotSupportedException(
            $"Store type '{storeType}' cannot be rendered as a common MySQL and MariaDB CAST target.");
    }

    /// <summary>Attempts to map a store type to the common MySQL and MariaDB CAST grammar.</summary>
    /// <param name="storeType">The provider store type requested by the structured expression.</param>
    /// <param name="castType">The canonical CAST target when the mapping succeeds.</param>
    /// <returns><see langword="true" /> when the store type has an exact, safe mapping.</returns>
    public static bool TryRender(
        string storeType,
        out string castType
    )
    {
        ArgumentNullException.ThrowIfNull(storeType);

        var candidate = storeType.Trim();
        if (candidate.Length == 0
            || candidate.Length > MaximumStoreTypeLength
            || !candidate.All(char.IsAscii))
        {
            castType = string.Empty;
            return false;
        }

        // A column definition and a CAST target use different grammars. In
        // particular, both engines store integer columns as INT variants, but
        // their common CAST spelling is SIGNED or UNSIGNED.
        if (s_unsignedIntegerTypePattern.IsMatch(candidate))
        {
            castType = "UNSIGNED";
            return true;
        }

        if (s_signedIntegerTypePattern.IsMatch(candidate))
        {
            castType = "SIGNED";
            return true;
        }

        var decimalMatch = s_decimalTypePattern.Match(candidate);
        if (decimalMatch.Success
            && TryRenderDecimal(decimalMatch, out castType))
        {
            return true;
        }

        if (s_doubleTypePattern.IsMatch(candidate))
        {
            castType = "DOUBLE";
            return true;
        }

        if (s_floatTypePattern.IsMatch(candidate))
        {
            castType = "FLOAT";
            return true;
        }

        var characterMatch = s_characterTypePattern.Match(candidate);
        if (characterMatch.Success
            && TryRenderLength(characterMatch, "CHAR", out castType))
        {
            return true;
        }

        if (s_textTypePattern.IsMatch(candidate))
        {
            castType = "CHAR";
            return true;
        }

        var binaryMatch = s_binaryTypePattern.Match(candidate);
        if (binaryMatch.Success
            && TryRenderLength(binaryMatch, "BINARY", out castType))
        {
            return true;
        }

        if (s_dateTypePattern.IsMatch(candidate))
        {
            castType = "DATE";
            return true;
        }

        var dateTimeMatch = s_dateTimeTypePattern.Match(candidate);
        if (dateTimeMatch.Success
            && TryRenderTemporal(dateTimeMatch, "DATETIME", out castType))
        {
            return true;
        }

        var timeMatch = s_timeTypePattern.Match(candidate);
        if (timeMatch.Success
            && TryRenderTemporal(timeMatch, "TIME", out castType))
        {
            return true;
        }

        castType = string.Empty;
        return false;
    }

    private static bool TryRenderDecimal(
        Match match,
        out string castType
    )
    {
        var precisionGroup = match.Groups["precision"];
        if (!precisionGroup.Success)
        {
            castType = "DECIMAL";
            return true;
        }

        if (!TryParseBoundedInteger(precisionGroup.Value, minimum: 1, maximum: 65, out var precision))
        {
            castType = string.Empty;
            return false;
        }

        var scaleGroup = match.Groups["scale"];
        if (!scaleGroup.Success)
        {
            castType = $"DECIMAL({precision.ToString(CultureInfo.InvariantCulture)})";
            return true;
        }

        if (!TryParseBoundedInteger(scaleGroup.Value, minimum: 0, maximum: 30, out var scale)
            || scale > precision)
        {
            castType = string.Empty;
            return false;
        }

        castType = $"DECIMAL({precision.ToString(CultureInfo.InvariantCulture)},"
            + $"{scale.ToString(CultureInfo.InvariantCulture)})";
        return true;
    }

    private static bool TryRenderLength(
        Match match,
        string target,
        out string castType
    )
    {
        var lengthGroup = match.Groups["length"];
        if (!lengthGroup.Success)
        {
            castType = target;
            return true;
        }

        if (!TryParseBoundedInteger(lengthGroup.Value, minimum: 1, maximum: int.MaxValue, out var length))
        {
            castType = string.Empty;
            return false;
        }

        castType = $"{target}({length.ToString(CultureInfo.InvariantCulture)})";
        return true;
    }

    private static bool TryRenderTemporal(
        Match match,
        string target,
        out string castType
    )
    {
        var precisionGroup = match.Groups["precision"];
        if (!precisionGroup.Success)
        {
            castType = target;
            return true;
        }

        if (!TryParseBoundedInteger(precisionGroup.Value, minimum: 0, maximum: 6, out var precision))
        {
            castType = string.Empty;
            return false;
        }

        castType = $"{target}({precision.ToString(CultureInfo.InvariantCulture)})";
        return true;
    }

    private static bool TryParseBoundedInteger(
        string value,
        int minimum,
        int maximum,
        out int parsed
    ) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)
        && parsed >= minimum
        && parsed <= maximum;
}
