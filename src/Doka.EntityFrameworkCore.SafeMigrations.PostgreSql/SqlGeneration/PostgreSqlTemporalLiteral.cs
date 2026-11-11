namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

/// <summary>Owns native temporal literal identity, storage-family certification and value validation.</summary>
internal static class PostgreSqlTemporalLiteral
{
    /// <summary>Asks the native mapping to validate only its known value-sensitive temporal inputs.</summary>
    /// <param name="mapping">The exact provider mapping, including any value converter.</param>
    /// <param name="value">The model-side temporal value to validate.</param>
    /// <returns>A precise unsupported feature, or null when native rendering is representable.</returns>
    internal static string? GetUnsupportedFeature(
        RelationalTypeMapping mapping,
        object? value
    )
    {
        if (IsUncertifiedScalarArrayMapping(mapping, value))
        {
            return "temporal_literal_mapping";
        }

        if (value is TimeSpan duration
            && IsUnsupportedClockTime(mapping, duration))
        {
            return "time_literal_out_of_range";
        }

        if (value is not (DateTime or DateTimeOffset or TimeSpan))
        {
            return null;
        }

        try
        {
            // WHY: Npgsql owns timestamp Kind/offset rules, infinity switches
            // and converter semantics. Validate that exact native mapping;
            // duplicating its rules would drift or reject a configured mode.
            _ = mapping.GenerateSqlLiteral(value);

            return null;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidCastException
            or NotSupportedException)
        {
            return "temporal_literal_value";
        }
    }

    /// <summary>Recognizes only native scalar temporal output families, never arrays or custom type input.</summary>
    internal static bool IsNativeTemporalMapping(
        RelationalTypeMapping mapping
    )
    {
        if (mapping
            .StoreType
            .TrimEnd()
            .EndsWith("[]", StringComparison.Ordinal))
        {
            return false;
        }

        var name = mapping.StoreTypeNameBase;

        return name.Equals("date", StringComparison.OrdinalIgnoreCase)
            || name.Equals("interval", StringComparison.OrdinalIgnoreCase)
            || name.Equals("time", StringComparison.OrdinalIgnoreCase)
            || name.Equals("timetz", StringComparison.OrdinalIgnoreCase)
            || name.Equals("timestamp", StringComparison.OrdinalIgnoreCase)
            || name.Equals("timestamptz", StringComparison.OrdinalIgnoreCase)
            || name.Equals("time without time zone", StringComparison.OrdinalIgnoreCase)
            || name.Equals("time with time zone", StringComparison.OrdinalIgnoreCase)
            || name.Equals("timestamp without time zone", StringComparison.OrdinalIgnoreCase)
            || name.Equals("timestamp with time zone", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rejects scalar mappings cloned with array names without rejecting genuine array converters.</summary>
    internal static bool IsUncertifiedScalarArrayMapping(
        RelationalTypeMapping mapping,
        object? value
    ) => value is DateOnly or TimeOnly or DateTime or DateTimeOffset or TimeSpan
        && mapping
            .StoreType
            .TrimEnd()
            .EndsWith("[]", StringComparison.Ordinal)
        && mapping.ElementTypeMapping is null;

    /// <summary>Preserves the resolved storage family instead of inferring it from TimeSpan alone.</summary>
    /// <param name="mapping">The provider mapping that owns the literal's native source type.</param>
    /// <param name="value">The immutable literal value.</param>
    /// <returns>The full native literal with its resolved interval or clock-time semantics.</returns>
    internal static string RenderMapped(
        RelationalTypeMapping mapping,
        object? value
    )
    {
        if (value is not TimeSpan duration)
        {
            return mapping.GenerateSqlLiteral(value);
        }

        if (IsIntervalMapping(mapping))
        {
            return Render(duration);
        }

        if (IsUnsupportedClockTime(mapping, duration))
        {
            throw new NotSupportedException("A PostgreSQL clock-time literal must lie between zero and 24 hours.");
        }

        // WHY: Native TIME includes the exact 24:00 endpoint. Npgsql's hh field
        // would discard its one day; ordinary clock values keep the native mapper.
        return IsClockTimeMapping(mapping) && duration.Ticks == TimeSpan.TicksPerDay
            ? "TIME '24:00:00'"
            : mapping.GenerateSqlLiteral(value);
    }

    /// <summary>Identifies only defaults whose native mapping needs an exact Safe-owned spelling.</summary>
    internal static bool RequiresOwnedDefaultRendering(
        RelationalTypeMapping mapping,
        TimeSpan value
    ) => IsIntervalMapping(mapping)
        || (IsClockTimeMapping(mapping) && value.Ticks == TimeSpan.TicksPerDay);

    /// <summary>Rejects values outside the native TIME domain before a formatter can discard signs or days.</summary>
    private static bool IsUnsupportedClockTime(
        RelationalTypeMapping mapping,
        TimeSpan value
    ) => IsClockTimeMapping(mapping) && value.Ticks is < 0 or > TimeSpan.TicksPerDay;

    /// <summary>Includes native interval field qualifiers without matching another storage family.</summary>
    private static bool IsIntervalMapping(
        RelationalTypeMapping mapping
    ) => mapping.StoreTypeNameBase.Equals("interval", StringComparison.OrdinalIgnoreCase)
        || mapping.StoreTypeNameBase.StartsWith("interval ", StringComparison.OrdinalIgnoreCase);

    /// <summary>Recognizes both native aliases of the finite clock-time mapping.</summary>
    internal static bool IsClockTimeMapping(
        RelationalTypeMapping mapping
    ) => mapping.StoreTypeNameBase.Equals("time", StringComparison.OrdinalIgnoreCase)
        || mapping.StoreTypeNameBase.Equals("time without time zone", StringComparison.OrdinalIgnoreCase);

    /// <summary>Renders a duration without ambiguous signs on separate day and time fields.</summary>
    /// <param name="value">The immutable CLR duration.</param>
    /// <returns>A native interval literal with independently signed day and seconds fields.</returns>
    internal static string Render(
        TimeSpan value
    )
    {
        // WHY: PostgreSQL assigns a leading minus to only the day field in its
        // ordinary interval syntax. Explicit units on both signed fields retain
        // native day/time identity without floating-point rounding or overflow.
        // WHY: Native intervals retain whole microseconds. Keep truncation of
        // the remaining 100-nanosecond ticks in the integer domain explicitly.
        var microseconds = value.Ticks % TimeSpan.TicksPerDay / TimeSpan.TicksPerMicrosecond;
        var seconds = microseconds / 1_000_000m;

        return "INTERVAL '"
            + value.Days.ToString(CultureInfo.InvariantCulture)
            + " days "
            + seconds.ToString("0.######", CultureInfo.InvariantCulture)
            + " seconds'";
    }
}
