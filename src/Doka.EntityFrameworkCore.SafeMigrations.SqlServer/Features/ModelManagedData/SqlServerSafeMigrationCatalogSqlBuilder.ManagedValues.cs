namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    /// <summary>Rejects scalar values outside the documented built-in SQL Server value domains.</summary>
    /// <param name="value">The authored CLR value before target conversion.</param>
    /// <param name="storeType">The canonical built-in target store type.</param>
    /// <returns>True when static value-domain checks do not disprove representability.</returns>
    internal static bool IsManagedValueRepresentable(
        object? value,
        string storeType
    )
    {
        if (value is null)
        {
            return true;
        }

        if (value is float single && !float.IsFinite(single)
            || value is double number && !double.IsFinite(number))
        {
            return false;
        }

        var type = storeType.Trim().ToLowerInvariant();
        var opening = type.IndexOf('(');
        var name = opening < 0 ? type : type[..opening].TrimEnd();
        var date = value switch
        {
            DateTime dateTime => dateTime,
            DateOnly dateOnly => dateOnly.ToDateTime(TimeOnly.MinValue),
            DateTimeOffset offset => offset.DateTime,
            _ => (DateTime?)null,
        };

        if (date is { } instant)
        {
            if (name == "datetime")
            {
                // WHY: Rounding the final datetime tick to its 1/300-second
                // representation must not overflow into year 10000.
                return instant.Year >= 1753
                    && instant.Ticks < DateTime.MaxValue.Ticks - 16_666;
            }

            if (name == "smalldatetime")
            {
                return instant >= new DateTime(1900, 1, 1)
                    && instant < new DateTime(2079, 6, 6, 23, 59, 29, 999);
            }

            if (name is "datetime2" or "datetimeoffset" && opening >= 0
                && int.TryParse(type.AsSpan(opening + 1, type.Length - opening - 2),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var precision))
            {
                var quantum = 1L;
                for (var ordinal = precision; ordinal < 7; ordinal++)
                {
                    quantum *= 10;
                }

                var limit = DateTime.MaxValue.Ticks - quantum / 2;

                return instant.Ticks <= limit
                    && (value is not DateTimeOffset dateTimeOffset || dateTimeOffset.UtcTicks <= limit);
            }
        }

        if (name == "time" && value is TimeSpan span)
        {
            return span >= TimeSpan.Zero && span < TimeSpan.FromDays(1);
        }

        if (value is not (bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal or Enum))
        {
            return name != "real" || value is not double real || Math.Abs(real) <= float.MaxValue;
        }

        var numeric = Convert.ToDecimal(value, CultureInfo.InvariantCulture);

        return name switch
        {
            "tinyint" => numeric is >= byte.MinValue and <= byte.MaxValue,
            "smallint" => numeric is >= short.MinValue and <= short.MaxValue,
            "int" => numeric is >= int.MinValue and <= int.MaxValue,
            "bigint" => numeric is >= long.MinValue and <= long.MaxValue,
            "smallmoney" => decimal.Round(numeric, 4, MidpointRounding.AwayFromZero)
                is >= -214748.3648m and <= 214748.3647m,
            "money" => decimal.Round(numeric, 4, MidpointRounding.AwayFromZero)
                is >= -922337203685477.5808m and <= 922337203685477.5807m,
            "decimal" or "numeric" => FitsManagedDecimal(numeric, type, opening),
            _ => true,
        };
    }

    private static bool FitsManagedDecimal(
        decimal value,
        string type,
        int opening
    )
    {
        if (opening < 0 || !type.EndsWith(')'))
        {
            return false;
        }

        var args = type.AsSpan(opening + 1, type.Length - opening - 2);
        var comma = args.IndexOf(',');
        if (comma < 0
            || !int.TryParse(args[..comma].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var precision)
            || !int.TryParse(args[(comma + 1)..].Trim(),
                NumberStyles.None, CultureInfo.InvariantCulture, out var scale))
        {
            return false;
        }

        var integralDigits = precision - scale;
        if (integralDigits >= 29)
        {
            return true;
        }

        var bound = 1m;
        for (var ordinal = 0; ordinal < integralDigits; ordinal++)
        {
            bound *= 10;
        }

        // WHY: SQL Server rounds decimal scale before checking precision, so
        // a value just below the bound may still overflow the destination.
        var rounded = scale < 28 ? decimal.Round(value, scale, MidpointRounding.AwayFromZero) : value;

        return rounded > -bound && rounded < bound;
    }

    /// <summary>Builds the shared non-throwing scalar guard used before typed row relations are bound.</summary>
    /// <param name="value">The authored value.</param>
    /// <param name="storeType">The captured canonical target type.</param>
    /// <returns>A conversion predicate, independent of destination rows.</returns>
    internal string ManagedValueRepresentationGuard(
        object? value,
        string storeType
    )
    {
        if (value is null)
        {
            return "1 = 1";
        }

        if (!IsManagedValueRepresentable(value, storeType) || !FitsManagedValueSize(value, storeType))
        {
            return "1 = 0";
        }

        if (value is string { Length: > 4_000 } && IsCharacterManagedStoreType(storeType)
            || value is byte[] { Length: > 8_000 }
                && storeType.Trim().Equals("varbinary(max)", StringComparison.OrdinalIgnoreCase))
        {
            // WHY: TRY_CAST limits large character inputs. Same-family LOB
            // casts cannot overflow; separate size/code-page predicates retain
            // the complete character and binary storage proof.
            return "1 = 1";
        }

        // WHY: The operand must reach a non-throwing target conversion. An
        // unguarded target-typed CAST could raise the error being classified.
        // TRY_CAST also remains bindable below compatibility 110, where the
        // enclosing provider guard must classify Unsupported without SQL errors.

        return $"TRY_CAST({ManagedValueLiteral(value, storeType)} AS {storeType}) IS NOT NULL";
    }

    /// <summary>Proves ANSI encoding roundtrip and byte capacity under the authored input collation.</summary>
    /// <param name="value">The authored string or character value.</param>
    /// <param name="storeType">The bounded or maximum ANSI destination type.</param>
    /// <param name="collation">The validated authored collation, or null for the database default.</param>
    /// <returns>A scalar predicate without destination-row access.</returns>
    internal string ManagedAnsiValueRepresentationGuard(
        object? value,
        string storeType,
        string? collation = null
    )
    {
        var text = value switch
        {
            string stringValue => stringValue,
            char character => character.ToString(),
            _ => null,
        };

        if (text is null || !IsAnsiManagedStoreType(storeType))
        {
            return "1 = 1";
        }

        if (collation is not null && _expressionRenderer.GetUnsupportedFeature(
            new SafeMigrationSqlCollateExpression(SafeMigrationSql.Literal(text), collation)) is not null)
        {
            return "1 = 0";
        }

        var literal = UnicodeManagedLiteral(text);
        // WHY: Applying COLLATE after conversion cannot repair code-page loss.
        // The input must already carry the destination encoding contract.
        var input = "(" + literal + " COLLATE " + (collation ?? "DATABASE_DEFAULT") + ")";
        var encoded = $"CONVERT(varchar(max), {input})";
        var roundtrip = ExactValueMatch(literal, $"CONVERT(nvarchar(max), {encoded})");

        return TryManagedValueMaximum(storeType, out var maximum)
            ? roundtrip + $" AND DATALENGTH({encoded}) <= {maximum.ToString(CultureInfo.InvariantCulture)}"
            : roundtrip;
    }

    private string ManagedValueLiteral(
        object? value,
        string storeType
    )
    {
        if (value is null)
        {
            return "NULL";
        }

        if (value is string text)
        {
            return UnicodeManagedLiteral(text);
        }

        if (value is char character)
        {
            return UnicodeManagedLiteral(character.ToString());
        }

        var mapping = _typeMappingSource.FindMapping(value.GetType())
            ?? _typeMappingSource.FindMapping(value.GetType(), storeType)
            ?? throw new NotSupportedException($"SQL Server has no safe scalar mapping for '{storeType}'.");

        var literal = mapping.GenerateSqlLiteral(value);
        if (value is DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan
            && TryParseStoreType(storeType, out var target)
            && target.Name is "date" or "datetime" or "datetime2" or "datetimeoffset" or "smalldatetime" or "time")
        {
            // WHY: EF's natural temporal literal can contain seven fractional
            // digits or an offset that legacy datetime text parsing rejects.
            // Preserve its source type before the independently guarded target
            // conversion, without a throwing CAST in the conversion proof.

            return $"TRY_CAST({literal} AS {mapping.StoreType})";
        }

        return literal;
    }
}
