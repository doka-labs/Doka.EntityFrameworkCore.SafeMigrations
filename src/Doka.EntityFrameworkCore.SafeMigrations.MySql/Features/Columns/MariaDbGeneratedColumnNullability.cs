namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Proves a bounded integral generated expression without trusting MariaDB's nullable catalog flag.</summary>
internal static class MariaDbGeneratedColumnNullability
{
    private const int MaximumProofDepth = 64;

    /// <summary>Collects ordinary required integer dependencies for a structurally non-null result.</summary>
    /// <param name="definition">The immutable generated-column definition.</param>
    /// <param name="sources">Distinct physical source columns that must independently satisfy the proof.</param>
    /// <returns>Whether the result is non-null whenever every collected source is a required to be signed integer.</returns>
    internal static bool TryGetRequiredIntegerSources(
        ExpectedColumnDefinition definition,
        out IReadOnlyList<string> sources
    )
    {
        if (definition.ComputedExpression is null
            || !HasSignedIntegerStorage(definition))
        {
            sources = Array.Empty<string>();

            return false;
        }

        var collected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!CollectNonNullInteger(definition.ComputedExpression, collected, depth: 0))
        {
            sources = Array.Empty<string>();

            return false;
        }

        sources = collected.Count == 0 ? Array.Empty<string>() : collected.ToArray();

        return true;
    }

    /// <summary>Qualifies the same proof against exact expected columns before a fresh table exists.</summary>
    internal static bool CanProveForTable(
        ExpectedColumnDefinition definition,
        ExpectedTableDefinition table
    )
    {
        if (!TryGetRequiredIntegerSources(definition, out var sources))
        {
            return false;
        }

        foreach (var source in sources)
        {
            ExpectedColumnDefinition? resolved = null;
            var columnCount = table.Columns.Count;
            // WHY: Prove uniqueness across the immutable source columns without an inner enumerator.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                var candidate = table.Columns[columnIndex];
                if (!StringComparer.OrdinalIgnoreCase.Equals(source, candidate.Name))
                {
                    continue;
                }

                if (resolved is not null)
                {
                    return false;
                }

                resolved = candidate;
            }

            if (resolved is null
                || !IsRequiredOrdinaryInteger(resolved))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Rejects nullable, unsigned, generated and non-integral dependencies without scans.</summary>
    internal static bool IsRequiredOrdinaryInteger(
        ExpectedColumnDefinition definition
    )
    {
        if (definition.IsNullable
            || definition.ComputedColumnSql is not null
            || definition.ComputedExpression is not null)
        {
            return false;
        }

        return HasSignedIntegerStorage(definition);
    }

    /// <summary>Excludes target and source conversions that have no certified integral assignment contract.</summary>
    private static bool HasSignedIntegerStorage(
        ExpectedColumnDefinition definition
    )
    {
        if (definition.StoreType is null)
        {
            return definition.ClrType == typeof(bool)
                || definition.ClrType == typeof(sbyte)
                || definition.ClrType == typeof(short)
                || definition.ClrType == typeof(int)
                || definition.ClrType == typeof(long);
        }

        return IsSignedIntegerStoreType(definition.StoreType);
    }

    /// <summary>Checks native integral metadata without allocating a projected expected-column wrapper.</summary>
    internal static bool IsSignedIntegerStoreType(
        ReadOnlySpan<char> storeType
    )
    {
        storeType = storeType.Trim();
        var displayWidth = storeType.IndexOf('(');
        if (displayWidth >= 0)
        {
            if (storeType[^1] != ')'
                || !int.TryParse(
                    storeType[(displayWidth + 1)..^1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var width)
                || width <= 0)
            {
                return false;
            }

            storeType = storeType[..displayWidth]
                .TrimEnd();
        }

        return storeType.Equals("tinyint", StringComparison.OrdinalIgnoreCase)
            || storeType.Equals("smallint", StringComparison.OrdinalIgnoreCase)
            || storeType.Equals("mediumint", StringComparison.OrdinalIgnoreCase)
            || storeType.Equals("int", StringComparison.OrdinalIgnoreCase)
            || storeType.Equals("integer", StringComparison.OrdinalIgnoreCase)
            || storeType.Equals("bigint", StringComparison.OrdinalIgnoreCase)
            || storeType.Equals("boolean", StringComparison.OrdinalIgnoreCase)
            || storeType.Equals("bool", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Tracks operations whose integer result cannot become NULL under native MariaDB semantics.</summary>
    private static bool CollectNonNullInteger(
        SafeMigrationSqlExpression expression,
        HashSet<string> sources,
        int depth
    )
    {
        while (true)
        {
            if (depth >= MaximumProofDepth)
            {
                return false;
            }

            switch (expression)
            {
                case SafeMigrationSqlIdentifierExpression { Parts.Count: 1 } value:
                    sources.Add(value.Parts[0]);

                    return true;
                case SafeMigrationSqlLiteralExpression { StoreType: null } value:
                    return value.Value is bool
                        or sbyte
                        or byte
                        or short
                        or ushort
                        or int
                        or uint
                        or long
                        or (ulong and <= long.MaxValue);
                case SafeMigrationSqlUnaryExpression value:
                    expression = value.Operand;
                    depth++;
                    continue;
                case SafeMigrationSqlBinaryExpression value:
                    // WHY: Native signed integer overflow raises an error rather
                    // than manufacturing NULL. Division and modulo can produce
                    // NULL, so non-null operands do not certify those operators.
                    return value.Operator is not (SafeMigrationSqlBinaryOperator.Divide
                            or SafeMigrationSqlBinaryOperator.Modulo)
                        && CollectNonNullInteger(value.Left, sources, depth + 1)
                        && CollectNonNullInteger(value.Right, sources, depth + 1);
                case SafeMigrationSqlNullTestExpression:
                    return true;
                case SafeMigrationSqlBetweenExpression value:
                    return CollectNonNullInteger(value.Operand, sources, depth + 1)
                        && CollectNonNullInteger(value.Lower, sources, depth + 1)
                        && CollectNonNullInteger(value.Upper, sources, depth + 1);
                case SafeMigrationSqlInExpression value:
                    if (!CollectNonNullInteger(value.Operand, sources, depth + 1))
                    {
                        return false;
                    }

                    foreach (var candidate in value.Values)
                    {
                        if (!CollectNonNullInteger(candidate, sources, depth + 1))
                        {
                            return false;
                        }
                    }

                    return true;
                case SafeMigrationSqlCaseExpression value:
                    // WHY: A condition may be UNKNOWN and choose ELSE. The proof
                    // therefore requires an explicit non-null ELSE and every THEN
                    // result; no branch-selection or short-circuit assumption is
                    // inferred from the condition or from current table rows.
                    if (value.ElseResult is null
                        || !CollectNonNullInteger(value.ElseResult, sources, depth + 1))
                    {
                        return false;
                    }

                    foreach (var clause in value.WhenClauses)
                    {
                        if (!CollectNonNullInteger(clause.Result, sources, depth + 1))
                        {
                            return false;
                        }
                    }

                    return true;
                default:
                    // WHY: Casts, functions and opaque/provider fragments have no
                    // provider-certified total-conversion contract in this proof.
                    return false;
            }
        }
    }
}
