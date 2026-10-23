namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Certifies only lossless signed built-in storage widening with unchanged non-type facets.</summary>
internal static class MySqlSignedIntegerWidening
{
    /// <summary>Checks both declared CLR domains and every non-type facet before granting widening.</summary>
    /// <param name="source">The exact authored source whose native identity is independently verified.</param>
    /// <param name="target">The reviewed target contract.</param>
    /// <returns>Whether every source value and unchanged default fits the wider signed target.</returns>
    internal static bool IsSafe(
        ExpectedColumnDefinition source,
        ExpectedColumnDefinition target
    )
    {
        var sourceBytes = Width(source);
        var targetBytes = Width(target);
        if (sourceBytes == 0
            || targetBytes <= sourceBytes
            || !StringComparer.OrdinalIgnoreCase.Equals(source.Name, target.Name)
            || source.IsNullable != target.IsNullable
            || source.IsUnicode != target.IsUnicode
            || source.MaxLength != target.MaxLength
            || source.IsFixedLength != target.IsFixedLength
            || source.Precision != target.Precision
            || source.Scale != target.Scale
            || !Equals(source.Collation, target.Collation)
            || !StringComparer.Ordinal.Equals(source.Comment, target.Comment)
            || !Ordinary(source)
            || !Ordinary(target)
            || source.DefaultValue.Kind != target.DefaultValue.Kind)
        {
            return false;
        }

        // WHY: Widening an integer does not authorize changing a SQL expression,
        // generated value, NULL contract or unrelated default behavior.
        return source.DefaultValue.Kind == SafeMigrationDefaultValueKind.None
            || (source.DefaultValue.Kind == SafeMigrationDefaultValueKind.Literal
                && SignedLiteral(source.DefaultValue.GetLiteralValue(), out var before)
                && SignedLiteral(target.DefaultValue.GetLiteralValue(), out var after)
                && before == after);
    }

    /// <summary>Resolves the signed native width only when its declared CLR domain agrees.</summary>
    /// <param name="definition">The exact immutable physical definition.</param>
    /// <returns>The storage bytes, or zero for unsigned, decorated, custom or incompatible domains.</returns>
    internal static int Width(
        ExpectedColumnDefinition definition
    )
    {
        var clr = Nullable.GetUnderlyingType(definition.ClrType) ?? definition.ClrType;
        if (clr != typeof(sbyte)
            && clr != typeof(short)
            && clr != typeof(int)
            && clr != typeof(long))
        {
            return 0;
        }

        // WHY: Every reviewed Alter probes this capability, including strings.
        // Reject other CLR families before parsing and compare the borrowed
        // native spelling without allocating a normalized string.
        var type = definition
            .StoreType
            .AsSpan()
            .Trim();

        if (clr == typeof(sbyte))
        {
            return type.Equals("tinyint", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        if (clr == typeof(short))
        {
            return type.Equals("smallint", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        }

        if (clr == typeof(long))
        {
            return type.Equals("bigint", StringComparison.OrdinalIgnoreCase) ? 8 : 0;
        }

        return type.Equals("mediumint", StringComparison.OrdinalIgnoreCase)
            ? 3
            : type.Equals("int", StringComparison.OrdinalIgnoreCase)
            || type.Equals("integer", StringComparison.OrdinalIgnoreCase)
                ? 4
                : 0;
    }

    /// <summary>Excludes generated, identity, row-version and unknown provider-owned column contracts.</summary>
    private static bool Ordinary(
        ExpectedColumnDefinition definition
    ) => SafeMigrationColumnRepairHelper.HasRepairableIntrinsicShape(definition)
        && definition.CapturedComputedSql is null
        && MySqlSafeMigrationColumnMetadata.TryCreate(definition, out var metadata)
        && metadata.GuidFormat is null
        && metadata.ValueGenerationStrategy is null or MySqlValueGenerationStrategy.None;

    /// <summary>Compares unchanged signed literal values without accepting SQL or unsigned coercion.</summary>
    private static bool SignedLiteral(
        object? value,
        out long result
    )
    {
        result = value switch
        {
            sbyte number => number, short number => number, int number => number, long number => number, _ => 0,
        };

        return value is sbyte or short or int or long;
    }
}
