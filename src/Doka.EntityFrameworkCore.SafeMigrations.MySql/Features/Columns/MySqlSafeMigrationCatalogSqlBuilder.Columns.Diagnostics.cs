namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private string BuildColumnDiagnosticEvidence(
        string table,
        ExpectedColumnDefinition definition,
        bool isMariaDb
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

        var defaultMatches = BuildDefaultMatches(
            table,
            definition,
            "c.COLUMN_DEFAULT",
            definition.DefaultValue,
            definition.IsNullable,
            mapping,
            isMariaDb,
            temporalRowVersion);

        var defaultKindMatches = BuildDefaultKindMatches(
            "c.COLUMN_DEFAULT",
            definition.DefaultValue,
            definition.IsNullable,
            temporalRowVersion);

        var expectedDefaultPayload = DefaultDiagnosticPayload(definition.DefaultValue, mapping, temporalRowVersion);

        var records = new List<string>
        {
            DiagnosticRecord(
                $"NOT ({BuildStoreTypeMatches(storeType, isMariaDb)})",
                "column_store_type",
                Literal(storeType),
                "LEFT(COALESCE(c.COLUMN_TYPE, 'none'), 256)"),
            DiagnosticRecord(
                $"NOT ({BuildColumnNullabilityMatches(table, definition, isMariaDb)})",
                "column_nullability",
                definition.IsNullable ? "'nullable'" : "'not_nullable'",
                "CASE c.IS_NULLABLE WHEN 'YES' THEN 'nullable' WHEN 'NO' THEN 'not_nullable' ELSE 'unknown' END"),
            DiagnosticRecord(
                $"NOT ({collationContract.MatchExpression})",
                "column_collation",
                BuildCollationExpectedExpression(collationContract),
                "LEFT(COALESCE(c.COLLATION_NAME, 'none'), 256)"),
            DiagnosticRecord(
                $"NOT ({defaultKindMatches})",
                "column_default_kind",
                Literal(DefaultKind(definition.DefaultValue, definition.IsNullable, temporalRowVersion)),
                "CASE WHEN c.COLUMN_DEFAULT IS NULL THEN 'none' "
                + "WHEN UPPER(TRIM(c.COLUMN_DEFAULT)) = 'NULL' THEN 'null' ELSE 'present' END"),
            DiagnosticRecord(
                $"NOT ({defaultMatches})",
                "column_default_digest",
                $"CONCAT('sha256:', LOWER(SHA2({Literal(expectedDefaultPayload)}, 256)))",
                "CONCAT('sha256:', LOWER(SHA2(COALESCE(c.COLUMN_DEFAULT, '<none>'), 256)))"),
            DiagnosticRecord(
                $"NOT ({BuildValueGenerationMatches(definition, temporalRowVersion)})",
                "column_value_generation",
                Literal(ValueGenerationKind(definition, temporalRowVersion)),
                "CASE WHEN LOCATE('auto_increment', LOWER(COALESCE(c.EXTRA, ''))) > 0 THEN 'identity' "
                + "WHEN LOCATE('on update', LOWER(COALESCE(c.EXTRA, ''))) > 0 THEN 'row_version' "
                + "WHEN LOCATE('generated', LOWER(COALESCE(c.EXTRA, ''))) > 0 THEN 'generated' "
                + "ELSE 'none' END"),
            DiagnosticRecord(
                $"NOT ({ColumnCommentMatches(definition.Comment)})",
                "column_comment_digest",
                $"LOWER(SHA2({Literal(definition.Comment ?? string.Empty)}, 256))",
                "LOWER(SHA2(COALESCE(c.COLUMN_COMMENT, ''), 256))"),
        };

        if (TryParseVarcharLength(storeType, out var targetLength))
        {
            records.Insert(
                1,
                DiagnosticRecord(
                    $"c.CHARACTER_MAXIMUM_LENGTH <> {targetLength}",
                    "column_max_length",
                    Literal(targetLength.ToString(CultureInfo.InvariantCulture)),
                    "COALESCE(CAST(c.CHARACTER_MAXIMUM_LENGTH AS CHAR), 'none')"));
        }

        return "(SELECT NULLIF(CONCAT_WS(CHAR(30), "
            + string.Join(", ", records)
            + "), '') FROM INFORMATION_SCHEMA.COLUMNS c "
            + $"WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = {Literal(table)} "
            + $"AND c.COLUMN_NAME = {Literal(definition.Name)} LIMIT 1)";
    }

    private static string DiagnosticRecord(
        string mismatch,
        string facet,
        string expected,
        string actual
    ) => $"CASE WHEN {mismatch} THEN CONCAT_WS(CHAR(31), '{facet}', {expected}, {actual}) END";

    private static string ValueGenerationKind(
        ExpectedColumnDefinition definition,
        bool temporalRowVersion
    )
    {
        if (temporalRowVersion)
        {
            return "row_version";
        }

        return MySqlSafeMigrationColumnMetadata.TryCreate(definition, out var metadata)
            && metadata.ValueGenerationStrategy == MySqlValueGenerationStrategy.AutoIncrement
                ? "identity"
                : definition.ComputedColumnSql is not null || definition.ComputedExpression is not null
                    ? "generated"
                    : "none";
    }
}
