namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    /// <summary>Builds a bounded, provider-evaluated uniqueness proof over one authored insert lineage.</summary>
    /// <param name="seeds">Every preceding authored insert in its immutable order.</param>
    /// <param name="columns">The unchanged target table columns.</param>
    /// <param name="uniqueColumns">The ordered candidate key being requested.</param>
    /// <param name="requireCapturedUniqueKey">
    /// Whether every seed must also declare the requested ordered candidate.
    /// </param>
    /// <param name="filter">The supported structured candidate filter, or null for an ordinary key.</param>
    /// <returns>A guarded scalar query, or null when the exact typed contract is unproven.</returns>
    internal string? BuildProjectedSeedKeyProofSql(
        IReadOnlyList<EnsureModelManagedDataIntent> seeds,
        IReadOnlyList<ExpectedColumnDefinition> columns,
        IReadOnlyList<string> uniqueColumns,
        bool requireCapturedUniqueKey = true,
        SafeMigrationSqlExpression? filter = null
    )
    {
        if (seeds.Count == 0 || uniqueColumns.Count == 0 || uniqueColumns.Count > 32)
        {
            return null;
        }

        var primaryColumns = seeds[0].KeyColumns;
        var projected = columns.ToDictionary(static column => column.Name, StringComparer.Ordinal);
        foreach (var definition in columns)
        {
            if (definition.Collation is { } collation
                && _expressionRenderer.GetUnsupportedFeature(new SafeMigrationSqlCollateExpression(
                    SafeMigrationSql.Identifier(definition.Name), collation.Name, collation.Schema)) is not null)
            {
                return null;
            }
        }

        var filterColumns = new Dictionary<string, string>(StringComparer.Ordinal);
        var boundFilter = filter is null || !IsSupportedIndexFilter(filter)
            ? null : BindProjectedSeedFilter(filter, projected, filterColumns);

        if (filter is not null && boundFilter is null)
        {
            return null;
        }

        var selectedColumns = primaryColumns.Concat(uniqueColumns).Concat(filterColumns.Keys).ToArray();
        var aliases = Enumerable.Range(0, primaryColumns.Count).Select(static index => $"k{index}")
            .Concat(Enumerable.Range(0, uniqueColumns.Count).Select(static index => $"u{index}"))
            .Concat(filterColumns.Values).ToArray();

        var collations = new HashSet<string>(StringComparer.Ordinal);
        var valueGuards = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<string>();
        // WHY: The complete lineage has no row-count cap. Account for dynamic
        // literal escaping while accumulating, before materializing large joins.
        var payloadBytes = 4096;
        foreach (var seed in seeds)
        {
            if (GetUnsupportedModelManagedDataFeature(seed) is not null
                || !seed.KeyColumns.SequenceEqual(primaryColumns, StringComparer.Ordinal)
                || requireCapturedUniqueKey && !seed.KeyColumns.SequenceEqual(uniqueColumns, StringComparer.Ordinal)
                && !seed.UniqueKeys.Any(key => key.Columns.SequenceEqual(uniqueColumns, StringComparer.Ordinal)))
            {
                return null;
            }

            foreach (var omitted in columns)
            {
                if (seed.Columns.Contains(omitted.Name, StringComparer.Ordinal))
                {
                    continue;
                }

                if (!TryGetIdentity(omitted, out var identity, out _, out _))
                {
                    return null;
                }

                if (identity || omitted.IsRowVersion)
                {
                    continue;
                }

                var defaultSupport = omitted.ComputedExpression is null
                    ? BuildNonNullDefaultSupportExpression(omitted) : null;

                if (omitted.ComputedColumnSql is not null
                    || omitted.ComputedExpression is not null
                    || defaultSupport is null)
                {
                    if (omitted.IsNullable
                        && (omitted.DefaultValue.Kind == SafeMigrationDefaultValueKind.None
                            || omitted.DefaultValue.IsNullLiteral)
                        && omitted.ComputedColumnSql is null && omitted.ComputedExpression is null)
                    {
                        continue;
                    }

                    // WHY: Default presence does not prove a required omitted
                    // value. Provider-qualified built-ins can prove non-NULL;
                    // computed expressions need a separate row-bound proof.

                    return null;
                }

                if (!AddGuard($"({defaultSupport}) = 1"))
                {
                    return null;
                }
            }

            for (var ordinal = 0; ordinal < seed.Columns.Count; ordinal++)
            {
                if (!projected.TryGetValue(seed.Columns[ordinal], out var definition)
                    || definition.IsRowVersion || definition.ComputedColumnSql is not null
                    || definition.ComputedExpression is not null)
                {
                    return null;
                }

                var mapping = _typeMappingSource.FindMapping(
                    definition.ClrType, definition.StoreType, unicode: definition.IsUnicode,
                    size: definition.MaxLength, rowVersion: definition.IsRowVersion,
                    fixedLength: definition.IsFixedLength, precision: definition.Precision, scale: definition.Scale);

                if (mapping is null || !IsSupportedManagedStoreType(mapping.StoreType)
                    || !StringComparer.OrdinalIgnoreCase.Equals(
                        mapping.StoreType, _typeMappingSource.FindMapping(seed.ColumnTypes[ordinal])?.StoreType))
                {
                    return null;
                }

                for (var row = 0; row < seed.RowCount; row++)
                {
                    var value = seed.Values.GetUnsafeValue(row, ordinal);
                    if (value is null)
                    {
                        if (!definition.IsNullable)
                        {
                            return null;
                        }

                        continue;
                    }

                    var valueMapping = _typeMappingSource.FindMapping(value.GetType(), mapping.StoreType);
                    if (valueMapping is null)
                    {
                        return null;
                    }

                    if (!AddGuard(ManagedValueRepresentationGuard(value, mapping.StoreType)))
                    {
                        return null;
                    }

                    if (IsAnsiManagedStoreType(mapping.StoreType) && value is string or char)
                    {
                        var text = value is string stringValue ? stringValue : ((char)value).ToString();
                        if (!AddGuard(ManagedAnsiValueRepresentationGuard(value, mapping.StoreType,
                            definition.Collation?.Name)))
                        {
                            return null;
                        }

                        if (definition.Collation is not null && text.Any(static character => character > 127))
                        {
                            // WHY: Authored proof must preserve the runtime's
                            // conservative ANSI code-page contract, even when
                            // an isolated constant comparison happens to pass.
                            if (!AddGuard(DatabaseDefaultCollationEquals(definition.Collation.Name)))
                            {
                                return null;
                            }
                        }
                    }
                }

                if (definition.Collation is not null && IsCharacterManagedStoreType(mapping.StoreType))
                {
                    collations.Add(definition.Collation.Name);
                    if (seed.RowCount > 1 && (seed.KeyColumns.Contains(definition.Name, StringComparer.Ordinal)
                        || seed.UniqueKeys.Any(key => key.Columns.Contains(definition.Name, StringComparer.Ordinal))))
                    {
                        // WHY: Existing multi-row seed runtime validation only
                        // proves self-collisions under the database default.
                        if (!AddGuard(DatabaseDefaultCollationEquals(definition.Collation.Name)))
                        {
                            return null;
                        }
                    }
                }
            }

            var types = new string[selectedColumns.Length];
            var ordinals = new int[selectedColumns.Length];
            for (var ordinal = 0; ordinal < selectedColumns.Length; ordinal++)
            {
                var name = selectedColumns[ordinal];
                if (!projected.TryGetValue(name, out var definition)
                    || definition.IsRowVersion || definition.ComputedColumnSql is not null
                    || definition.ComputedExpression is not null)
                {
                    return null;
                }

                var mapping = _typeMappingSource.FindMapping(
                    definition.ClrType, definition.StoreType, unicode: definition.IsUnicode,
                    size: definition.MaxLength, rowVersion: definition.IsRowVersion,
                    fixedLength: definition.IsFixedLength, precision: definition.Precision, scale: definition.Scale);

                var captured = ColumnOrdinalOrMissing(seed.Columns, name);
                if (mapping is null || captured < 0 || !IsSupportedManagedStoreType(mapping.StoreType)
                    || !StringComparer.OrdinalIgnoreCase.Equals(
                        mapping.StoreType, _typeMappingSource.FindMapping(seed.ColumnTypes[captured])?.StoreType))
                {
                    return null;
                }

                types[ordinal] = mapping.StoreType;
                ordinals[ordinal] = captured;
                if (definition.Collation is not null && IsCharacterManagedStoreType(mapping.StoreType))
                {
                    collations.Add(definition.Collation.Name);
                }
            }

            for (var row = 0; row < seed.RowCount; row++)
            {
                var values = new string[selectedColumns.Length + 1];
                values[0] = $"0x{AuthoredRowFingerprint(seed, row)}";
                for (var ordinal = 0; ordinal < selectedColumns.Length; ordinal++)
                {
                    var value = seed.Values.GetUnsafeValue(row, ordinals[ordinal]);
                    var expression = TypedValue(value, types[ordinal]);
                    var definition = projected[selectedColumns[ordinal]];
                    if (IsCharacterManagedStoreType(types[ordinal]))
                    {
                        // WHY: SQL Server, not CLR equality, must decide case,
                        // accent, padding, and NULL candidate-key collisions.
                        expression += definition.Collation is null
                            ? " COLLATE DATABASE_DEFAULT"
                            : $" COLLATE {definition.Collation.Name}";
                    }

                    values[ordinal + 1] = expression;
                }

                var rowSql = $"({string.Join(", ", values)})";
                if (!Accumulate(rowSql))
                {
                    return null;
                }

                rows.Add(rowSql);
            }
        }

        var declared = Delimited("r") + ", " + string.Join(", ", aliases.Select(Delimited));
        var primary = string.Join(", ", aliases.Take(primaryColumns.Count).Select(Delimited));
        var unique = string.Join(", ", aliases.Skip(primaryColumns.Count).Take(uniqueColumns.Count).Select(Delimited));
        var filterSql = boundFilter is null ? string.Empty : $"WHERE ({_expressionRenderer.Render(boundFilter)}) ";
        var body = "WITH doka_seed_rows AS (SELECT DISTINCT * FROM (VALUES "
            + string.Join(", ", rows) + $") AS doka_values({declared})) "
            + "SELECT CASE WHEN NOT ("
            + (valueGuards.Count == 0 ? "1 = 1"
                : string.Join(" AND ", valueGuards.Select(static guard => $"({guard})")))
            + ") THEN -1 WHEN EXISTS (SELECT 1 FROM doka_seed_rows "
            + $"GROUP BY {primary} HAVING COUNT_BIG(*) > 1) "
            + "OR EXISTS (SELECT 1 FROM doka_seed_rows "
            + filterSql + $"GROUP BY {unique} HAVING COUNT_BIG(*) > 1) THEN 0 ELSE 1 END;";

        var collationGuard = collations.Count == 0 ? "1 = 1"
            : string.Join(" AND ", collations.Select(collation =>
                $"EXISTS (SELECT 1 FROM sys.fn_helpcollations() WHERE name = {Literal(collation)})"));

        var guard = $"COALESCE({SqlServerCompatibilityLevel()}, 0) >= 110 AND ({collationGuard})";

        // WHY: Invalid installed collations and overflowed typed constants must
        // become an unproven proof, not escape as a raw binding/conversion error.
        // The hash only deduplicates exact authored rows; SQL still proves keys.
        var sql = $"IF {guard} BEGIN TRY EXEC sys.sp_executesql {Literal(body)}; "
            + "END TRY BEGIN CATCH IF ERROR_NUMBER() IN (241, 242, 245, 248, 529, 8114, 8115) "
            + "SELECT -1; ELSE THROW; END CATCH ELSE SELECT -1;";

        return Encoding.UTF8.GetByteCount(sql) <= SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes
            ? sql : null;

        bool AddGuard(string guard) => !valueGuards.Add(guard) || Accumulate(guard);

        bool Accumulate(string fragment)
        {
            var bytes = Encoding.UTF8.GetByteCount(fragment)
                + fragment.Count(static character => character == '\'') + 8;

            if (bytes > SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes - payloadBytes)
            {
                return false;
            }

            payloadBytes += bytes;

            return true;
        }
    }

    private static int ColumnOrdinalOrMissing(
        IReadOnlyList<string> columns,
        string name
    )
    {
        for (var ordinal = 0; ordinal < columns.Count; ordinal++)
        {
            if (StringComparer.Ordinal.Equals(columns[ordinal], name))
            {
                return ordinal;
            }
        }

        return -1;
    }

    private string DatabaseDefaultCollationEquals(string collation)
        => $"CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation')) = {Literal(collation)}";

    private static string AuthoredRowFingerprint(
        EnsureModelManagedDataIntent seed,
        int row
    )
    {
        using var writer = new CanonicalHashWriter();
        foreach (var name in seed.Columns.Order(StringComparer.Ordinal))
        {
            var ordinal = ColumnOrdinalOrMissing(seed.Columns, name);
            writer.Add(name);
            writer.Add(seed.ColumnTypes[ordinal]);
            SafeMigrationModelManagedValue.Write(writer, seed.Values.GetUnsafeValue(row, ordinal));
        }

        return writer.GetHash();
    }
}
