namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private static string DefaultKind(
        SafeMigrationDefaultValue value,
        bool isNullable,
        bool temporalRowVersion
    )
    {
        if (temporalRowVersion)
        {
            return "present";
        }

        if (value is { Kind: SafeMigrationDefaultValueKind.Literal, IsNullLiteral: true })
        {
            return "null";
        }

        if (value.Kind != SafeMigrationDefaultValueKind.None)
        {
            return "present";
        }

        return isNullable ? "none_or_null" : "none";
    }

    private string DefaultDiagnosticPayload(
        SafeMigrationDefaultValue value,
        RelationalTypeMapping mapping,
        bool temporalRowVersion
    )
    {
        if (temporalRowVersion)
        {
            return "CURRENT_TIMESTAMP(6)";
        }

        if (value.Kind == SafeMigrationDefaultValueKind.None)
        {
            return "<none>";
        }

        if (value.Kind == SafeMigrationDefaultValueKind.Sql)
        {
            return value.SqlExpression ?? _expressionRenderer.RenderDefault(value.StructuredExpression!);
        }

        var literal = value.GetLiteralValue();

        return literal is null ? "NULL" : mapping.GenerateSqlLiteral(literal);
    }

    private string BuildDefaultMatches(
        string table,
        ExpectedColumnDefinition definition,
        string catalogExpression,
        SafeMigrationDefaultValue expected,
        bool isNullable,
        RelationalTypeMapping mapping,
        bool isMariaDb,
        bool temporalRowVersion = false
    )
    {
        // WHY: MariaDB stores logically required generated columns as physically
        // nullable. Its implicit NULL-default display follows that physical
        // facet; the separate semantic dependency proof still enforces non-NULL.
        isNullable |= RequiresMariaDbGeneratedNonNullProof(definition, isMariaDb);
        if (expected.Kind == SafeMigrationDefaultValueKind.None)
        {
            if (temporalRowVersion)
            {
                var providerDefaultCandidates = BuildDefaultSqlCandidates("CURRENT_TIMESTAMP(6)")
                    .Select(Literal);

                return $"{catalogExpression} IN ({string.Join(", ", providerDefaultCandidates)})";
            }

            return isNullable ? NullDefaultMatches() : $"{catalogExpression} IS NULL";
        }

        var structuredString = (mapping.ClrType == typeof(string) || mapping.ClrType == typeof(char))
            && expected.StructuredExpression is SafeMigrationSqlLiteralExpression { StoreType: null } literal
                ? literal
                : null;

        if (mapping.ClrType == typeof(byte[])
            && expected.StructuredExpression is SafeMigrationSqlLiteralExpression
            {
                Value: byte[] binary, StoreType: null
            })
        {
            // WHY: Native X'...' and 0x... expressions retain the same exact
            // binary identity, including NUL bytes. This is not a typed CAST
            // or the truncated MySQL literal-default catalog representation.
            return BuildBinaryDefaultMatches(table, definition, catalogExpression, binary, mapping, isMariaDb);
        }

        if (expected.Kind == SafeMigrationDefaultValueKind.Sql
            && structuredString?.Value is not (string or char))
        {
            var expression = expected.SqlExpression
                ?? _expressionRenderer.RenderDefault(expected.StructuredExpression!);

            IEnumerable<string> sqlCandidateValues = BuildDefaultSqlCandidates(expression);
            if (expected.StructuredExpression is SafeMigrationSqlLiteralExpression
                {
                    Value: string temporal, StoreType: null
                }
                && BuildTemporalSqlLiteralCatalogValue(mapping.StoreType, temporal) is { } temporalDisplay)
            {
                // WHY: Temporal metadata pads declared precision even when
                // caller-owned SQL has no fraction. Compare that exact display
                // without changing the default kind or accepting conversion.
                sqlCandidateValues = sqlCandidateValues
                    .Concat([temporalDisplay, $"'{temporalDisplay}'"])
                    .Distinct(StringComparer.Ordinal);

                if (!isMariaDb)
                {
                    var temporalLiteralMatches = $"CAST({catalogExpression} AS BINARY) IN "
                        + $"({string.Join(", ", sqlCandidateValues.Select(Literal))})";

                    return BuildMySqlStringDefaultMatches(catalogExpression, temporal, temporalLiteralMatches);
                }
            }

            var sqlCandidates = sqlCandidateValues.Select(Literal);

            return $"{catalogExpression} IN ({string.Join(", ", sqlCandidates)})";
        }

        var value = structuredString?.Value ?? expected.GetLiteralValue();
        if (value is char character)
        {
            // WHY: Doka's char mapping uses the same textual literal grammar;
            // a one-character default needs the same exact persisted identity.
            value = character.ToString();
        }

        if (value is null)
        {
            return NullDefaultMatches();
        }

        if (value is byte[] bytes)
        {
            return BuildBinaryDefaultMatches(table, definition, catalogExpression, bytes, mapping, isMariaDb);
        }

        if (value is string text)
        {
            return isMariaDb
                ? BuildMariaDbStringDefaultMatches(catalogExpression, text)
                : BuildMySqlStringDefaultMatches(catalogExpression, text);
        }

        var providerLiteral = mapping.GenerateSqlLiteral(value);
        var candidates = new HashSet<string>(StringComparer.Ordinal) { providerLiteral };
        AddSimpleStringLiteralDisplayCandidate(candidates, providerLiteral);
        AddExpressionDefaultDisplayCandidate(candidates, providerLiteral);

        var invariant = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (invariant is not null)
        {
            candidates.Add(invariant);
        }

        if (value is bool boolean)
        {
            candidates.Add(boolean ? "1" : "0");
        }

        AddTemporalDefaultCandidates(candidates, value);

        return $"{catalogExpression} IN ({string.Join(", ", candidates.Select(Literal))})";

        // WHY: MySQL exposes DEFAULT (NULL) as the exact text NULL plus
        // DEFAULT_GENERATED. Neither a plain string 'NULL' nor its serialized
        // expression spelling may impersonate that SQL NULL identity.
        string NullDefaultMatches() => isMariaDb
            ? $"({catalogExpression} IS NULL OR UPPER({catalogExpression}) = 'NULL')"
            : $"({catalogExpression} IS NULL OR (CAST({catalogExpression} AS BINARY) = 'NULL' "
            + "AND LOCATE('DEFAULT_GENERATED', UPPER(COALESCE(c.EXTRA, ''))) > 0))";
    }

    /// <summary>Separates MySQL raw literal values from exact serialized expression defaults.</summary>
    /// <param name="catalogExpression">The owning column's default metadata expression.</param>
    /// <param name="value">The exact expected string value.</param>
    /// <param name="literalMatchExpression">An optional trusted predicate for a normalized temporal literal.</param>
    /// <returns>A binary comparison qualified by the catalog's expression-default marker.</returns>
    private string BuildMySqlStringDefaultMatches(
        string catalogExpression,
        string value,
        string? literalMatchExpression = null
    )
    {
        // WHY: MySQL serializes an expression literal, then prints its binary
        // UTF-8 bytes as individual code points for INFORMATION_SCHEMA. This
        // spelling differs from both the value and MariaDB's quoted display.
        // Keeping DEFAULT_GENERATED separate prevents a plain value containing
        // quotes from impersonating a serialized expression for another value.
        var generated = "LOCATE('DEFAULT_GENERATED', UPPER(COALESCE(c.EXTRA, ''))) > 0";
        var persistedConversion = Literal(MySqlSafeMigrationSqlExpressionRenderer.RenderStringDefault(value));
        var literalMatches = literalMatchExpression ?? $"CAST({catalogExpression} AS BINARY) = {Literal(value)}";
        var expressionMatches = $"CAST({catalogExpression} AS BINARY) = {persistedConversion}";

        // WHY: The fixed escape alphabet needs no per-column LINQ enumerator.
        // Scan the original UTF-16 span without changing any payload bytes.
        if (value
                .AsSpan()
                .IndexOfAny(s_modeDependentDefaultCharacters)
            < 0)
        {
            var expression = "_utf8mb4'" + EscapeMySqlDefaultDisplay(value) + "'";
            var display = EscapeMySqlDefaultDisplay(Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(expression)));
            expressionMatches = $"CAST({catalogExpression} AS BINARY) IN "
                + $"({Literal(display)}, {persistedConversion})";
        }

        // WHY: The stable owned conversion survives table reopening in either
        // SQL mode. Compare its exact canonical text, never a lowercased or
        // unquoted payload that could alias a different authored value. An old
        // escaped expression can change value in another session, regardless
        // of the current mode, and must require explicit safe repair.
        return $"CASE WHEN {generated} THEN {expressionMatches} " + $"ELSE {literalMatches} END";
    }

    /// <summary>Escapes one serialization layer without normalizing case, Unicode, or quoted payloads.</summary>
    /// <param name="value">The literal value or binary expression display being serialized.</param>
    /// <returns>The exact MySQL display spelling for the input characters.</returns>
    private static string EscapeMySqlDefaultDisplay(
        string value
    ) => value
        .Replace("\\", @"\\", StringComparison.Ordinal)
        .Replace("\0", "\\0", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\u001A", "\\Z", StringComparison.Ordinal)
        .Replace("'", "\\'", StringComparison.Ordinal);

    private static string BuildDefaultKindMatches(
        string catalogExpression,
        SafeMigrationDefaultValue expected,
        bool isNullable,
        bool temporalRowVersion
    )
    {
        if (temporalRowVersion)
        {
            return $"{catalogExpression} IS NOT NULL " + $"AND UPPER(TRIM({catalogExpression})) <> 'NULL'";
        }

        if (expected is { Kind: SafeMigrationDefaultValueKind.Literal, IsNullLiteral: true })
        {
            return $"({catalogExpression} IS NULL OR UPPER(TRIM({catalogExpression})) = 'NULL')";
        }

        if (expected.Kind != SafeMigrationDefaultValueKind.None)
        {
            return $"{catalogExpression} IS NOT NULL " + $"AND UPPER(TRIM({catalogExpression})) <> 'NULL'";
        }

        return isNullable
            ? $"({catalogExpression} IS NULL OR UPPER(TRIM({catalogExpression})) = 'NULL')"
            : $"{catalogExpression} IS NULL";
    }

    private static void AddSimpleStringLiteralDisplayCandidate(
        HashSet<string> candidates,
        string providerLiteral
    )
    {
        if (providerLiteral.Length < 2
            || providerLiteral[0] != '\''
            || providerLiteral[^1] != '\'')
        {
            return;
        }

        var interior = providerLiteral[1..^1];
        var unescaped = interior.Replace("''", "'", StringComparison.Ordinal);
        var escapedQuoteCount = interior.Count(static character => character == '\'');
        var unescapedQuoteCount = unescaped.Count(static character => character == '\'');

        if (unescapedQuoteCount * 2 != escapedQuoteCount)
        {
            return;
        }

        candidates.Add(unescaped);
    }

    private static void AddExpressionDefaultDisplayCandidate(
        HashSet<string> candidates,
        string providerLiteral
    )
    {
        if (providerLiteral.Contains('\'', StringComparison.Ordinal))
        {
            candidates.Add(providerLiteral.Replace("'", "\\'", StringComparison.Ordinal));
        }
    }

    /// <summary>Separates MariaDB TEXT expression display from ordinary quoted character-default display.</summary>
    /// <param name="catalogExpression">The owning column's default metadata expression.</param>
    /// <param name="value">The exact expected string value.</param>
    /// <returns>A byte-exact comparison selected by the live column's storage family.</returns>
    private string BuildMariaDbStringDefaultMatches(
        string catalogExpression,
        string value
    )
    {
        // WHY: TEXT defaults remain expression items and use backslash quote
        // escaping. Ordinary character defaults use doubled quotes instead.
        // Select by actual storage rather than accepting ambiguous raw values.
        var ordinaryDisplay = Literal(BuildMariaDbStringDefaultDisplay(value));
        var expressionDisplay = Literal(
            "'"
            + EscapeMySqlDefaultDisplay(value)
                .Replace("\b", @"\b", StringComparison.Ordinal)
                .Replace("\t", @"\t", StringComparison.Ordinal)
            + "'");

        var persistedConversion = Literal(MySqlSafeMigrationSqlExpressionRenderer.RenderStringDefault(value));

        return $"(CAST({catalogExpression} AS BINARY) = {persistedConversion} "
            + $"OR CAST({catalogExpression} AS BINARY) = CASE "
            + "WHEN LOWER(c.DATA_TYPE) IN ('tinytext', 'text', 'mediumtext', 'longtext') "
            + $"THEN {expressionDisplay} ELSE {ordinaryDisplay} END)";
    }

    /// <summary>Produces MariaDB's exact quoted character-default display without raw-value alternatives.</summary>
    /// <param name="value">The expected string value, including any quote or backslash payload.</param>
    /// <returns>The canonical quoted catalog representation.</returns>
    private static string BuildMariaDbStringDefaultDisplay(
        string value
    )
    {
        // MariaDB can expose a character default as its quoted SQL text even
        // when the provider emitted a hexadecimal literal to avoid sql_mode
        // ambiguity. A raw-value alternative would let quote characters in
        // the expected payload impersonate a different quoted catalog value.
        var escaped = value
            .Replace("\\", @"\\", StringComparison.Ordinal)
            .Replace("\0", @"\0", StringComparison.Ordinal)
            .Replace("\n", @"\n", StringComparison.Ordinal)
            .Replace("\r", @"\r", StringComparison.Ordinal)
            .Replace("'", "''", StringComparison.Ordinal);

        return $"'{escaped}'";
    }

    private string BuildBinaryDefaultMatches(
        string table,
        ExpectedColumnDefinition definition,
        string catalogExpression,
        byte[] value,
        RelationalTypeMapping mapping,
        bool isMariaDb
    )
    {
        var hex = Convert.ToHexString(value);
        // WHY: Both candidate sets always have exactly two values. Direct
        // rendering preserves parameter order without arrays or iterators.
        var prefixedLiteral = Literal($"0X{hex}");
        var quotedLiteral = Literal($"X'{hex}'");
        var textualMatch = $"UPPER({catalogExpression}) IN ({prefixedLiteral}, {quotedLiteral})";

        if (!isMariaDb
            && definition.ClrType == typeof(byte[]))
        {
            var proof = RegisterBinaryDefaultProof(table, definition, mapping, value);

            // WHY: MySQL's ordinary binary literal metadata truncates at NUL,
            // so even an exact catalog prefix can impersonate another value.
            // Expression defaults retain their complete SQL identity; only an
            // ordinary literal must additionally prove DEFAULT(column).
            return "CASE WHEN LOCATE('DEFAULT_GENERATED', UPPER(COALESCE(c.EXTRA, ''))) > 0 "
                + $"THEN ({textualMatch}) ELSE {proof.Marker} END";
        }

        if (isMariaDb)
        {
            var stable = Literal(MySqlSafeMigrationSqlExpressionRenderer.RenderBinaryDefault(value));
            textualMatch = $"(CAST({catalogExpression} AS BINARY) = {stable} OR {textualMatch})";

            if (value
                    .AsSpan()
                    .Contains((byte)'?')
                || value
                    .AsSpan()
                    .IndexOfAnyInRange((byte)0x80, byte.MaxValue)
                >= 0)
            {
                // WHY: A lossy legacy quoted '?' can describe either a real
                // question mark or an invalid binary UTF-8 sequence. Only
                // intact hex/expression metadata can prove these identities.
                return textualMatch;
            }
        }

        var encodedLiteral = Literal(hex);
        var encodedQuotedLiteral = Literal($"27{hex}27");

        return $"({textualMatch} "
            + $"OR UPPER(HEX({catalogExpression})) IN ({encodedLiteral}, {encodedQuotedLiteral}))";
    }

    /// <summary>Registers one operation-owned exact literal proof without caching live catalog evidence.</summary>
    /// <param name="table">The authored owner in the selected database.</param>
    /// <param name="definition">The complete target column contract.</param>
    /// <param name="mapping">The provider mapping used to resolve implicit binary width.</param>
    /// <param name="value">The expected immutable binary literal snapshot.</param>
    /// <returns>The deduplicated proof consumed by classification, diagnostics and postconditions.</returns>
    private MySqlSafeMigrationBinaryDefaultProof RegisterBinaryDefaultProof(
        string table,
        ExpectedColumnDefinition definition,
        RelationalTypeMapping mapping,
        byte[] value
    )
    {
        var expectedHex = Convert.ToHexString(value);
        var storeType = (definition.StoreType ?? mapping.StoreType)
            .AsSpan()
            .Trim();

        var arguments = storeType.IndexOf('(');
        if (arguments > 0
            && storeType[..arguments]
                .Trim()
                .Equals("binary", StringComparison.OrdinalIgnoreCase)
            && storeType[^1] == ')'
            && int.TryParse(
                storeType[(arguments + 1)..^1]
                    .Trim(),
                CultureInfo.InvariantCulture,
                out var fixedWidth)
            && fixedWidth > value.Length)
        {
            // WHY: DEFAULT(BINARY(n)) exposes the stored, zero-padded value,
            // not merely the authored prefix. VARBINARY remains byte-exact.
            expectedHex = expectedHex.PadRight(checked(fixedWidth * 2), '0');
        }

        var query = "SELECT EXISTS (SELECT 1 FROM (SELECT 1) AS doka_sm_seed "
            + $"LEFT JOIN {Delimited(table)} AS doka_sm_default ON FALSE "
            + $"WHERE CAST(HEX(DEFAULT(doka_sm_default.{Delimited(definition.Name)})) AS BINARY) = '{expectedHex}')";

        _binaryDefaultProofs ??= [];
        foreach (var existing in _binaryDefaultProofs)
        {
            if (StringComparer.Ordinal.Equals(existing.QueryExpression, query))
            {
                return existing;
            }
        }

        var eligibility = "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS binary_default_column "
            + "INNER JOIN INFORMATION_SCHEMA.TABLES binary_default_table "
            + "ON binary_default_table.TABLE_SCHEMA = binary_default_column.TABLE_SCHEMA "
            + "AND binary_default_table.TABLE_NAME = binary_default_column.TABLE_NAME "
            + "WHERE binary_default_column.TABLE_SCHEMA = DATABASE() "
            + $"AND binary_default_column.TABLE_NAME = {Literal(table)} "
            + $"AND binary_default_column.COLUMN_NAME = {Literal(definition.Name)} "
            + $"AND {BuildRequiredColumnAddOwnerMatches("binary_default_column.TABLE_SCHEMA", "DATABASE()")} "
            + $"AND {BuildRequiredColumnAddOwnerMatches("binary_default_column.TABLE_NAME", Literal(table))} "
            + "AND binary_default_table.TABLE_TYPE = 'BASE TABLE' "
            + "AND LOWER(binary_default_column.DATA_TYPE) IN "
            + "('binary', 'varbinary', 'tinyblob', 'blob', 'mediumblob', 'longblob') "
            + "AND binary_default_column.COLUMN_DEFAULT IS NOT NULL "
            + "AND COALESCE(binary_default_column.GENERATION_EXPRESSION, '') = '' "
            + "AND LOCATE('DEFAULT_GENERATED', UPPER(COALESCE(binary_default_column.EXTRA, ''))) = 0)";

        var marker = MySqlSafeMigrationBinaryDefaultProof.MarkerPrefix
            + _binaryDefaultProofs.Count.ToString(CultureInfo.InvariantCulture)
            + "__";

        var proof = new MySqlSafeMigrationBinaryDefaultProof(marker, eligibility, query);
        _binaryDefaultProofs.Add(proof);

        return proof;
    }

    private static string[] BuildDefaultSqlCandidates(
        string expression
    )
    {
        var trimmed = expression.Trim();
        var candidates = new List<string>
        {
            trimmed,
            $"({trimmed})",
        };

        const string currentTimestamp = "CURRENT_TIMESTAMP";
        if (trimmed.StartsWith(currentTimestamp, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = trimmed[currentTimestamp.Length..];
            if (suffix.Length == 0
                || (suffix is ['(', _, ..]
                    && suffix[^1] == ')'
                    && suffix[1..^1]
                        .All(char.IsAsciiDigit)))
            {
                candidates.Add($"current_timestamp{suffix}");
                candidates.Add($"now{suffix}");
            }
        }

        return candidates
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddTemporalDefaultCandidates(
        HashSet<string> candidates,
        object value
    )
    {
        switch (value)
        {
            case DateOnly date:
                AddTemporalTypedCandidate(
                    candidates,
                    "DATE",
                    date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case TimeOnly time:
                AddTemporalTypedCandidate(candidates, "TIME", time.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                AddTemporalTypedCandidate(
                    candidates,
                    "TIME",
                    time.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture));
                break;
            case DateTime dateTime:
                AddQuotedCandidate(candidates, dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                AddQuotedCandidate(
                    candidates,
                    dateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture));
                break;
            case TimeSpan timeSpan:
                AddTemporalTypedCandidate(
                    candidates,
                    "TIME",
                    timeSpan.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
                AddTemporalTypedCandidate(
                    candidates,
                    "TIME",
                    timeSpan.ToString(@"hh\:mm\:ss\.ffffff", CultureInfo.InvariantCulture));
                break;
        }
    }

    /// <summary>Builds an exact temporal catalog value for an uncast structured SQL literal.</summary>
    /// <param name="storeType">The resolved timestamp or datetime target type and precision.</param>
    /// <param name="value">The caller-owned ISO date-time text without timezone or time-only syntax.</param>
    /// <returns>The zero-padded value, or null when calendar, domain or precision conversion is unproven.</returns>
    internal static string? BuildTemporalSqlLiteralCatalogValue(
        string storeType,
        string value
    )
    {
        ArgumentNullException.ThrowIfNull(storeType);
        ArgumentNullException.ThrowIfNull(value);

        var type = storeType
            .AsSpan()
            .Trim();
        var open = type.IndexOf('(');
        var kind = open < 0
            ? type
            : type[..open]
                .TrimEnd();

        if (!kind.Equals("timestamp", StringComparison.OrdinalIgnoreCase)
            && !kind.Equals("datetime", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var precision = 0;
        if (open >= 0
            && (type[^1] != ')'
                || !int.TryParse(
                    type[(open + 1)..^1]
                        .Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out precision)
                || precision is < 0 or > 6))
        {
            return null;
        }

        if (value.Length is < 19 or > 26
            || !DateTime.TryParseExact(
                value.AsSpan(0, 19),
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            return null;
        }

        var fractionLength = value.Length > 19 ? value.Length - 20 : 0;
        if ((value.Length > 19 && (value[19] != '.' || fractionLength == 0))
            || fractionLength > precision)
        {
            return null;
        }

        foreach (var character in value.AsSpan(Math.Min(value.Length, 20)))
        {
            if (!char.IsAsciiDigit(character))
            {
                return null;
            }
        }

        return precision == 0
            ? value[..19]
            : value[..19] + "." + (value.Length > 19 ? value[20..] : string.Empty).PadRight(precision, '0');
    }

    private static void AddTemporalTypedCandidate(
        HashSet<string> candidates,
        string keyword,
        string value
    )
    {
        AddQuotedCandidate(candidates, value);
        candidates.Add($"{keyword} '{value}'");
        candidates.Add($"{keyword}'{value}'");
        candidates.Add($@"{keyword}\'{value}\'");
        candidates.Add($"_utf8mb4'{value}'");
        candidates.Add($@"_utf8mb4\'{value}\'");
    }

    private static void AddQuotedCandidate(
        HashSet<string> candidates,
        string value
    )
    {
        candidates.Add(value);
        candidates.Add($"'{value.Replace("'", "''", StringComparison.Ordinal)}'");
    }
}
