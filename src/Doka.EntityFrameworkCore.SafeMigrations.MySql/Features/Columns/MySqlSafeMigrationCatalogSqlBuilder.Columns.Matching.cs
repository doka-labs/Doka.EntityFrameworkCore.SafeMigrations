namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    /// <summary>Compares authored comment text without inheriting the catalog's collation or padding rules.</summary>
    /// <param name="comment">
    /// The optional authored comment; no comment and an empty native comment are equivalent.
    /// </param>
    /// <returns>The exact predicate shared by column, strict-table, staged ownership and diagnostics.</returns>
    private string ColumnCommentMatches(
        string? comment
    ) =>
        // WHY: MariaDB metadata equality can fold comment case or trailing
        // spaces. Comments are contract values, not provider identifiers; the
        // same byte-exact predicate must govern state and its digest diagnostic.
        // One binary operand controls the whole comparison, avoiding a second
        // cast and its repeated SQL text without changing comparison semantics.
        $"CAST(COALESCE(c.COLUMN_COMMENT, '') AS BINARY) = {Literal(comment ?? string.Empty)}";

    private string BuildColumnMatches(
        string table,
        ExpectedColumnDefinition definition,
        bool isMariaDb,
        int? ordinal = null,
        bool includeRepairableFacets = true,
        bool requireRepairSafeExtra = false,
        bool requireExactIdentity = false,
        bool requireNativeOwnerIdentity = false
    )
    {
        var mapping = _typeMappingSource.FindMapping(
                definition.ClrType,
                definition.StoreType,
                keyOrIndex: false,
                definition.IsUnicode,
                definition.MaxLength,
                definition.IsRowVersion,
                definition.IsFixedLength,
                definition.Precision,
                definition.Scale)
            ?? throw new InvalidOperationException(
                $"No MySQL type mapping exists for '{definition.ClrType.FullName}'.");

        var storeType = definition.StoreType ?? mapping.StoreType;
        var temporalRowVersion = IsTemporalRowVersion(definition);
        var mariaDbJsonAlias = isMariaDb && StringComparer.OrdinalIgnoreCase.Equals(storeType.Trim(), "json");
        var collationContract = BuildCollationContract(table, definition.Collation, mariaDbJsonAlias);

        ReadOnlySpan<string> conditions =
        [
            BuildStoreTypeMatches(storeType, isMariaDb),
            collationContract.MatchExpression,
            BuildComputedMatches(definition, isMariaDb),
            BuildValueGenerationMatches(definition, temporalRowVersion),
            includeRepairableFacets ? BuildColumnNullabilityMatches(table, definition, isMariaDb) : string.Empty,
            includeRepairableFacets ? ColumnCommentMatches(definition.Comment) : string.Empty,
            includeRepairableFacets
                ? BuildDefaultMatches(
                    table,
                    definition,
                    "c.COLUMN_DEFAULT",
                    definition.DefaultValue,
                    definition.IsNullable,
                    mapping,
                    isMariaDb,
                    temporalRowVersion)
                : string.Empty,
            requireRepairSafeExtra ? OrdinaryColumnExtraMatches() : string.Empty,
            requireExactIdentity || requireNativeOwnerIdentity
                ? BuildRequiredColumnAddOwnerMatches("c.TABLE_SCHEMA", "DATABASE()")
                : string.Empty,
            requireExactIdentity || requireNativeOwnerIdentity
                ? BuildRequiredColumnAddOwnerMatches("c.TABLE_NAME", Literal(table))
                : string.Empty,
            requireExactIdentity
                ? $"CAST(c.COLUMN_NAME AS BINARY) = CAST({Literal(definition.Name)} AS BINARY)"
                : string.Empty,
            ordinal is not null
                ? $"c.ORDINAL_POSITION = {ordinal.Value.ToString(CultureInfo.InvariantCulture)}"
                : string.Empty,
        ];

        return ComposeColumnMatch(Literal(table), Literal(definition.Name), conditions);
    }

    /// <summary>Writes all predicate pieces once without a joined intermediate or a condition-list backing array.</summary>
    /// <param name="tableLiteral">The already provider-rendered owner literal.</param>
    /// <param name="nameLiteral">The already provider-rendered column literal.</param>
    /// <param name="conditions">The ordered facets, with empty optional slots omitted from SQL.</param>
    /// <returns>The exact existence predicate, materialized in one final string.</returns>
    internal static string ComposeColumnMatch(
        string tableLiteral,
        string nameLiteral,
        ReadOnlySpan<string> conditions
    )
    {
        // WHY: Borrowed pieces cannot escape the synchronous string.Create
        // callback. Emit directly into the final predicate rather than joining
        // every facet first and copying that intermediate into another string.
        var length = checked(ColumnMatchPrefix.Length
            + tableLiteral.Length
            + ColumnMatchNamePrefix.Length
            + nameLiteral.Length
            + 1);

        foreach (var condition in conditions)
        {
            if (condition.Length > 0)
            {
                length = checked(length + 5 + condition.Length);
            }
        }

        return string.Create(
            length,
            new ColumnMatchComposition(tableLiteral, nameLiteral, conditions),
            static (
                destination,
                state
            ) =>
            {
                var offset = 0;
                AppendColumnMatchPiece(ColumnMatchPrefix, destination, ref offset);
                AppendColumnMatchPiece(state.TableLiteral, destination, ref offset);
                AppendColumnMatchPiece(ColumnMatchNamePrefix, destination, ref offset);
                AppendColumnMatchPiece(state.NameLiteral, destination, ref offset);
                foreach (var condition in state.Conditions)
                {
                    if (condition.Length > 0)
                    {
                        AppendColumnMatchPiece(" AND ", destination, ref offset);
                        AppendColumnMatchPiece(condition, destination, ref offset);
                    }
                }

                destination[offset++] = ')';
                if (offset != destination.Length)
                {
                    throw new InvalidOperationException("The MySQL column predicate did not fill its destination.");
                }
            });
    }

    /// <summary>Copies a validated immutable piece into its exactly allocated destination.</summary>
    private static void AppendColumnMatchPiece(
        string value,
        Span<char> destination,
        ref int offset
    )
    {
        value
            .AsSpan()
            .CopyTo(destination[offset..]);
        offset += value.Length;
    }

    /// <summary>Retains borrowed predicate pieces only across the synchronous string.Create callback.</summary>
    private readonly ref struct ColumnMatchComposition(
        string tableLiteral,
        string nameLiteral,
        ReadOnlySpan<string> conditions
    )
    {
        /// <summary>Gets the already rendered owner literal without another mapping call.</summary>
        internal string TableLiteral { get; } = tableLiteral;

        /// <summary>Gets the already rendered column literal without changing placeholder order.</summary>
        internal string NameLiteral { get; } = nameLiteral;

        /// <summary>Gets immutable borrowed facets whose lifetime ends with synchronous emission.</summary>
        internal ReadOnlySpan<string> Conditions { get; } = conditions;
    }

    /// <summary>Verifies every expected column of a table definition through one catalog probe.</summary>
    /// <remarks>
    /// WHY: The per-column classifier repeated the same INFORMATION_SCHEMA.COLUMNS lookup once per
    /// column, so a wide table produced one correlated lookup per column in a single statement.
    /// This form reads the catalog once per table and counts how many columns at the expected
    /// positions match, while every facet predicate stays verbatim and server-side.
    ///
    /// The expected set deliberately does not become a derived table joined against the catalog.
    /// MariaDB cannot use the table name as an information_schema lookup value once it sits in a
    /// join condition, so it opens every table definition in the schema for each such join. In a
    /// paired run on MariaDB 11.8, analyzing 100 expected tables took 2,218 ms in that form against
    /// 167 ms in this one, and 3,611 ms against 132 ms with 1,000 further tables in the schema,
    /// while MySQL 8.4 stayed within one percent of itself either way. Counting matches keeps the
    /// name a plain lookup value.
    ///
    /// Counting is equivalent to testing each expected column separately: ORDINAL_POSITION is
    /// unique per table, so at most one row can satisfy each arm. A missing column, a mismatching
    /// facet and a facet predicate that evaluates to NULL all leave the count short, the last one
    /// because a WHERE clause keeps only rows whose predicate is true.
    /// </remarks>
    /// <param name="table">The table whose columns are verified.</param>
    /// <param name="definition">The expected table definition.</param>
    /// <param name="isMariaDb">Whether the connected engine is MariaDB.</param>
    /// <returns>A predicate that is true when every expected column matches at its own position.</returns>
    private string BuildAllColumnsMatch(
        string table,
        ExpectedTableDefinition definition,
        bool isMariaDb
    )
    {
        var facets = new List<(List<string> Conditions, CollationContract Collation)>(definition.Columns.Count);
        foreach (var column in definition.Columns)
        {
            var conditions = new List<string>
            {
                $"c.COLUMN_NAME = {Literal(column.Name)}",
                BuildColumnNullabilityMatches(table, column, isMariaDb),
                ColumnCommentMatches(column.Comment),
            };

            conditions.AddRange(BuildColumnStructuralConditions(table, column, isMariaDb, out var collation));
            facets.Add((conditions, collation));
        }

        // WHY: A column that inherits the table default compares against an uncorrelated subquery
        // over INFORMATION_SCHEMA.TABLES which is the same for every column, so repeating it per
        // column made the catalog work grow with the column count. Carrying it once per table
        // instead measured 353 ms against 906 ms on MySQL 8.4 and 131 ms against 172 ms on MariaDB
        // 11.8, over 50 tables of 16 columns, as the best of three samples per arm. It may only
        // leave the arms when no column expects a collation of its own, because the shared clause
        // applies to every row the count sees.
        var sharedCollation = facets.Count > 0
            && facets.TrueForAll(static facet =>
                facet.Collation.ExpectationKind == CollationExpectationKind.InheritedTableDefault)
                ? facets[0].Collation.MatchExpression
                : null;

        var arms = new StringBuilder(definition.Columns.Count * 256);
        // WHY: Keep each CASE arm's one-based selector aligned with its column facets.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < facets.Count; index++)
        {
            var (conditions, collation) = facets[index];
            if (sharedCollation is null)
            {
                conditions.Add(collation.MatchExpression);
            }

            arms
                .Append(" WHEN ")
                .Append((index + 1).ToString(CultureInfo.InvariantCulture))
                .Append(" THEN (")
                .AppendJoin(" AND ", conditions)
                .Append(')');
        }

        var columnCount = definition.Columns.Count.ToString(CultureInfo.InvariantCulture);

        // WHY: The ordinal bound is implied by the arms, which yield NULL beyond the expected
        // count, but it lets the engine drop surplus columns before evaluating any facet.
        return "(SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS c WHERE c.TABLE_SCHEMA = DATABASE() "
            + $"AND c.TABLE_NAME = {Literal(table)} AND c.ORDINAL_POSITION <= {columnCount} "
            + (sharedCollation is null ? string.Empty : $"AND {sharedCollation} ")
            + $"AND (CASE c.ORDINAL_POSITION{arms} END)) = {columnCount}";
    }

    /// <summary>Builds the column facets whose SQL shape depends on the expected column itself.</summary>
    /// <param name="table">The owning table.</param>
    /// <param name="definition">The expected column.</param>
    /// <param name="isMariaDb">Whether the connected engine is MariaDB.</param>
    /// <param name="collation">The collation contract, returned apart so the caller can share it.</param>
    /// <returns>The structural predicates for that column, without the collation contract.</returns>
    private List<string> BuildColumnStructuralConditions(
        string table,
        ExpectedColumnDefinition definition,
        bool isMariaDb,
        out CollationContract collation
    )
    {
        var mapping = _typeMappingSource.FindMapping(
                definition.ClrType,
                definition.StoreType,
                keyOrIndex: false,
                definition.IsUnicode,
                definition.MaxLength,
                definition.IsRowVersion,
                definition.IsFixedLength,
                definition.Precision,
                definition.Scale)
            ?? throw new InvalidOperationException(
                $"No MySQL type mapping exists for '{definition.ClrType.FullName}'.");

        var storeType = definition.StoreType ?? mapping.StoreType;
        var temporalRowVersion = IsTemporalRowVersion(definition);
        var mariaDbJsonAlias = isMariaDb && StringComparer.OrdinalIgnoreCase.Equals(storeType.Trim(), "json");

        collation = BuildCollationContract(table, definition.Collation, mariaDbJsonAlias);

        return
        [
            BuildStoreTypeMatches(storeType, isMariaDb),
            BuildComputedMatches(definition, isMariaDb),
            BuildValueGenerationMatches(definition, temporalRowVersion),
            BuildDefaultMatches(
                table,
                definition,
                "c.COLUMN_DEFAULT",
                definition.DefaultValue,
                definition.IsNullable,
                mapping,
                isMariaDb,
                temporalRowVersion),
        ];
    }

    private string BuildColumnRepairInvariantMatches(
        string table,
        ExpectedColumnDefinition definition,
        bool isMariaDb,
        bool requireNativeOwnerIdentity = false
    ) => BuildColumnMatches(
        table,
        definition,
        isMariaDb,
        includeRepairableFacets: false,
        requireRepairSafeExtra: true,
        requireNativeOwnerIdentity: requireNativeOwnerIdentity);

    private string BuildStoreTypeMatches(
        string storeType,
        bool isMariaDb
    )
    {
        var typeExpression = storeType
            .AsSpan()
            .Trim();
        var memberStart = typeExpression.IndexOf('(');
        if (memberStart > 0
            && (typeExpression[..memberStart]
                    .TrimEnd()
                    .Equals("enum", StringComparison.OrdinalIgnoreCase)
                || typeExpression[..memberStart]
                    .TrimEnd()
                    .Equals("set", StringComparison.OrdinalIgnoreCase)))
        {
            return BuildEnumSetStoreTypeMatches(storeType);
        }

        if (isMariaDb && StringComparer.OrdinalIgnoreCase.Equals(storeType.Trim(), "json"))
        {
            return "LOWER(c.DATA_TYPE) = 'longtext'";
        }

        if (!isMariaDb
            || storeType.Contains('(', StringComparison.Ordinal))
        {
            return $"LOWER(c.COLUMN_TYPE) = LOWER({Literal(storeType)})";
        }

        var normalized = storeType
            .Trim()
            .ToLowerInvariant();

        var type = normalized.AsSpan();
        var separator = type.IndexOf(' ');
        var name = separator < 0 ? type : type[..separator];
        var canonicalType = name switch
        {
            "tinyint" => "tinyint",
            "smallint" => "smallint",
            "mediumint" => "mediumint",
            "int" or "integer" => "int",
            "bigint" => "bigint",
            _ => null,
        };

        if (canonicalType is null)
        {
            return $"LOWER(c.COLUMN_TYPE) = {Literal(normalized)}";
        }

        // WHY: MariaDB display widths are ignored for integer families, but the
        // literal-space modifier grammar and unsigned semantics must remain exact.
        var expectedUnsigned = false;
        foreach (var range in type.Split(' '))
        {
            if (type[range] is "unsigned")
            {
                expectedUnsigned = true;
                break;
            }
        }

        var expected = expectedUnsigned ? $"{canonicalType} unsigned" : canonicalType;

        return $"CONCAT(LOWER(c.DATA_TYPE), "
            + $"CASE WHEN LOCATE('unsigned', LOWER(c.COLUMN_TYPE)) > 0 "
            + $"THEN ' unsigned' ELSE '' END) = {Literal(expected)}";
    }

    private string BuildComputedMatches(
        ExpectedColumnDefinition definition,
        bool isMariaDb
    )
    {
        if (definition.ComputedColumnSql is null
            && definition.ComputedExpression is null)
        {
            return "(c.GENERATION_EXPRESSION IS NULL OR c.GENERATION_EXPRESSION = '')";
        }

        var expression = definition.CapturedComputedSql
            ?? definition.ComputedColumnSql
            ?? MySqlCapturedExpressionContract.RenderExecutable(definition.ComputedExpression!, _expressionRenderer);

        var candidates = new[]
            {
                MySqlExpressionCanonicalizer.CreateComparisonCandidate(expression),
                MySqlExpressionCanonicalizer.CreateComparisonCandidate($"({expression})")
            }
            .Concat(
                MySqlExpressionCanonicalizer.BuildComparisonCandidates(
                    expression,
                    includeMySqlEncodedDisplay: !isMariaDb))
            .Concat(
                MySqlGeneratedExpressionDisplay.AdditionalNativeCandidates(
                    definition.ComputedExpression,
                    _expressionRenderer,
                    isMariaDb,
                    definition.CapturedComputedSql is not null))
            .DistinctBy(static candidate => candidate.Text, StringComparer.Ordinal);

        var storage = definition.IsStored == true ? "STORED GENERATED" : "VIRTUAL GENERATED";

        return MySqlExpressionCatalogComparison.Build("c.GENERATION_EXPRESSION", candidates, Literal)
            + " "
            + $"AND LOCATE({Literal(storage)}, c.EXTRA) > 0";
    }
}
