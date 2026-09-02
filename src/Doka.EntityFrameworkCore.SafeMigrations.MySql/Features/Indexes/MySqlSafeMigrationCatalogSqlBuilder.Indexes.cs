namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private static string? GetUnsupportedIndexFeature(
        SafeMigrationIntent intent,
        MySqlMigrationFeatureSet features
    )
    {
        if (intent is not EnsureIndexIntent index)
        {
            return null;
        }

        if ((index.Definition.Filter is not null || index.Definition.StructuredFilter is not null)
            && !Supported(features, MySqlMigrationFeature.FilteredIndexes))
        {
            return "filtered_index";
        }

        if (index.Definition.Keys.Any(static key => key.Expression is not null || key.StructuredExpression is not null)
            && !Supported(features, MySqlMigrationFeature.FunctionalIndexes))
        {
            return "functional_index";
        }

        if (index.Definition.Keys.Any(static key => key.PrefixLength is not null)
            && !Supported(features, MySqlMigrationFeature.IndexPrefixLengths))
        {
            return "index_prefix_length";
        }

        if (index.Definition.Keys.Any(static key => key.NullOrder != SafeMigrationIndexNullOrder.ProviderDefault))
        {
            return "index_null_order";
        }

        if (StringComparer.OrdinalIgnoreCase.Equals(index.Definition.Method, "HASH")
            && index.Definition.Keys.Any(static key => key.SortOrder != SafeMigrationIndexSortOrder.ProviderDefault))
        {
            return "index_sort_order";
        }

        if (index.Definition.Keys.Any(static key => key.SortOrder == SafeMigrationIndexSortOrder.Descending)
            && !Supported(features, MySqlMigrationFeature.DescendingIndexes))
        {
            return "descending_index";
        }

        if (index.Definition.IncludedColumns.Count > 0)
        {
            return "included_columns";
        }

        if (index.Definition.NullsDistinct == false)
        {
            return "nulls_not_distinct";
        }

        if (index.Definition.Keys.Any(static key => key.Collation is not null))
        {
            return "index_key_collation";
        }

        return index.Definition.Keys.Any(static key => key.OperatorClass is not null) ? "operator_class" : null;
    }

    private MySqlSafeMigrationRuntimePlan BuildEnsureIndex(
        EnsureIndexIntent intent,
        bool isMariaDb
    )
    {
        var definition = intent.Definition;
        var tableExists = BaseTableExists(definition.Table);
        var indexExists = IndexExists(definition.Table, definition.Name);
        var matching = BuildIndexMatches(definition, isMariaDb, requireExpectedName: true);
        var semanticAlias = BuildIndexMatches(definition, isMariaDb, requireExpectedName: false);
        var dataBlocked = definition.Unique ? UniqueIndexDataBlocked(definition) : "FALSE";
        var physicallyAchievable = BuildIndexPhysicalShapeSupported(definition);

        var physicalFailureCode = definition.Keys.Any(
                static key => key.Expression is not null || key.StructuredExpression is not null)
            || definition.Method is not null
            && !StringComparer.OrdinalIgnoreCase.Equals(definition.Method, "BTREE")
                ? "index_key_length_unverifiable"
                : "index_prefix_required_for_key_limit";

        var satisfied = $"({matching}) OR (NOT ({indexExists}) AND ({semanticAlias}))";

        return Plan(
            $"CASE WHEN NOT {tableExists} THEN 'prerequisite_missing' "
            + $"WHEN {indexExists} AND {matching} THEN 'matching' "
            + $"WHEN {indexExists} THEN 'different' "
            + $"WHEN {semanticAlias} THEN 'matching' "
            + $"WHEN NOT ({physicallyAchievable}) THEN 'unsupported' "
            + $"WHEN {dataBlocked} THEN 'data_blocked' "
            + "ELSE 'missing' END",
            satisfied) with
        {
            UnsupportedCode = physicalFailureCode,
            // Ordered DropIndex -> EnsureIndex projection still needs the
            // live duplicate-row proof hidden by an exact-name shape clash.
            // The operation remains Different until the preceding drop is
            // accepted; this code carries evidence, not mutation authority.
            ClassificationCodeExpression = definition.Unique
                ? $"CASE WHEN {indexExists} AND NOT ({matching}) AND {dataBlocked} "
                    + "THEN 'index_replacement_data_blocked' ELSE NULL END"
                : null,
        };
    }

    private MySqlSafeMigrationRuntimePlan BuildDropIndex(
        DropIndexIntent intent
    )
    {
        var exists = IndexExists(intent.Table, intent.Name);
        return Plan(
            $"CASE WHEN NOT {BaseTableExists(intent.Table)} OR NOT {exists} " + "THEN 'missing' ELSE 'matching' END",
            $"NOT {exists}");
    }

    private MySqlSafeMigrationRuntimePlan BuildRenameIndex(
        RenameIndexIntent intent
    )
    {
        var source = IndexExists(intent.Table, intent.Name);
        var target = IndexExists(intent.Table, intent.NewName);

        return Plan(
            $"CASE WHEN NOT {source} THEN 'missing' "
            + $"WHEN NOT {BaseTableExists(intent.Table)} THEN 'different' "
            + $"WHEN {target} THEN 'different' ELSE 'matching' END",
            $"NOT {source}");
    }

    private string BuildIndexMatches(
        ExpectedIndexDefinition definition,
        bool isMariaDb,
        bool requireExpectedName = true
    )
    {
        const string candidate = "candidate";
        var matching = BuildIndexCandidateMatches(definition, isMariaDb, candidate);
        var nameOperator = requireExpectedName ? "=" : "<>";

        return $"EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS {candidate} "
            + $"WHERE {candidate}.TABLE_SCHEMA = DATABASE() "
            + $"AND {candidate}.TABLE_NAME = {Literal(definition.Table)} "
            + $"AND {candidate}.INDEX_NAME {nameOperator} {Literal(definition.Name)} "
            + $"AND {candidate}.SEQ_IN_INDEX = 1 AND {matching})";
    }

    private string BuildIndexCandidateMatches(
        ExpectedIndexDefinition definition,
        bool isMariaDb,
        string candidate
    )
    {
        var conditions = new List<string>
        {
            $"(SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS s "
            + $"WHERE s.TABLE_SCHEMA = DATABASE() AND s.TABLE_NAME = {Literal(definition.Table)} "
            + $"AND s.INDEX_NAME = {candidate}.INDEX_NAME) "
            + $"= {definition.Keys.Count.ToString(CultureInfo.InvariantCulture)}",
            $"NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS s "
            + $"WHERE s.TABLE_SCHEMA = DATABASE() AND s.TABLE_NAME = {Literal(definition.Table)} "
            + $"AND s.INDEX_NAME = {candidate}.INDEX_NAME "
            + $"AND s.NON_UNIQUE <> {(definition.Unique ? "0" : "1")})",
            isMariaDb ? $"{candidate}.IGNORED = 'NO'" : $"{candidate}.IS_VISIBLE = 'YES'",
        };

        if (definition.Method is not null)
        {
            conditions.Add(
                $"NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS s "
                + $"WHERE s.TABLE_SCHEMA = DATABASE() AND s.TABLE_NAME = {Literal(definition.Table)} "
                + $"AND s.INDEX_NAME = {candidate}.INDEX_NAME "
                + $"AND s.INDEX_TYPE <> {Literal(definition.Method)})");
        }

        for (var ordinal = 0; ordinal < definition.Keys.Count; ordinal++)
        {
            var key = definition.Keys[ordinal];
            var position = (ordinal + 1).ToString(CultureInfo.InvariantCulture);
            var keyConditions = new List<string>
            {
                $"s.SEQ_IN_INDEX = {position}",
                key.Column is not null
                    ? $"s.COLUMN_NAME = {Literal(key.Column)}"
                    : isMariaDb
                        ? "FALSE"
                        : BuildIndexExpressionMatches(
                            "s.EXPRESSION",
                            key.Expression ?? _expressionRenderer.Render(key.StructuredExpression!)),
                BuildIndexSortMatches(key),
                key.PrefixLength is null
                    ? "s.SUB_PART IS NULL"
                    : $"s.SUB_PART = {key.PrefixLength.Value.ToString(CultureInfo.InvariantCulture)}",
            };

            conditions.Add(
                $"EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS s "
                + $"WHERE s.TABLE_SCHEMA = DATABASE() AND s.TABLE_NAME = {Literal(definition.Table)} "
                + $"AND s.INDEX_NAME = {candidate}.INDEX_NAME "
                + $"AND {string.Join(" AND ", keyConditions)})");
        }

        return $"({string.Join(" AND ", conditions)})";
    }

    private static string BuildIndexSortMatches(
        ExpectedIndexKeyDefinition key
    ) => key.SortOrder switch
    {
        SafeMigrationIndexSortOrder.ProviderDefault => "(s.COLLATION IS NULL OR s.COLLATION = 'A')",
        SafeMigrationIndexSortOrder.Ascending => "s.COLLATION = 'A'",
        SafeMigrationIndexSortOrder.Descending => "s.COLLATION = 'D'",
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private string UniqueIndexDataBlocked(
        ExpectedIndexDefinition definition
    )
    {
        var keys = definition
            .Keys
            .Select(IndexDataExpression)
            .ToArray();

        var nonNull = string.Join(" AND ", keys.Select(static key => $"({key}) IS NOT NULL"));

        return DuplicateDataExists(definition.Table, keys, nonNull);
    }

    private string IndexDataExpression(
        ExpectedIndexKeyDefinition key
    )
    {
        if (key.Expression is not null
            || key.StructuredExpression is not null)
        {
            return key.Expression ?? _expressionRenderer.Render(key.StructuredExpression!);
        }

        var column = Delimited(key.Column!);

        return key.PrefixLength is null
            ? column
            : $"LEFT({column}, {key.PrefixLength.Value.ToString(CultureInfo.InvariantCulture)})";
    }

    private string BuildIndexExpressionMatches(
        string catalogExpression,
        string expected
    )
    {
        var candidates = new[] { expected, $"({expected})", }
            .Distinct(StringComparer.Ordinal)
            .Select(Literal);

        return $"{catalogExpression} IN ({string.Join(", ", candidates)})";
    }

    private string IndexExists(
        string table,
        string name
    ) => $"EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS s "
        + $"WHERE s.TABLE_SCHEMA = DATABASE() AND s.TABLE_NAME = {Literal(table)} "
        + $"AND s.INDEX_NAME = {Literal(name)})";

    private string BuildIndexPhysicalShapeSupported(
        ExpectedIndexDefinition definition
    )
    {
        if (definition.Keys.Any(static key => key.Expression is not null || key.StructuredExpression is not null)
            || definition.Method is not null
            && !StringComparer.OrdinalIgnoreCase.Equals(definition.Method, "BTREE"))
        {
            // Catalog matching remains available for existing provider-supported
            // indexes. Creation is rejected because SafeMigrations cannot prove
            // the physical key width for this access method or expression.
            return "FALSE";
        }

        var keyWidths = definition.Keys
            .Select(key => BuildIndexKeyWidth(definition.Table, key))
            .ToArray();

        var totalWidth = string.Join(" + ", keyWidths.Select(static width => $"COALESCE(({width}), 2147483647)"));
        var maximumWidth = "CASE "
            + "WHEN UPPER(COALESCE(t.ROW_FORMAT, '')) IN ('COMPACT', 'REDUNDANT') THEN 767 "
            + "WHEN @@innodb_page_size = 4096 THEN 768 "
            + "WHEN @@innodb_page_size = 8192 THEN 1536 "
            + "ELSE 3072 END";

        return $"EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES t "
            + $"WHERE t.TABLE_SCHEMA = DATABASE() AND t.TABLE_NAME = {Literal(definition.Table)} "
            + $"AND UPPER(t.ENGINE) = 'INNODB' AND ({totalWidth}) <= ({maximumWidth}))";
    }

    private string BuildIndexKeyWidth(
        string table,
        ExpectedIndexKeyDefinition key
    )
    {
        var prefixLength = key.PrefixLength?.ToString(CultureInfo.InvariantCulture);
        var characterWidth = prefixLength is null
            ? "CASE WHEN c.DATA_TYPE IN ('tinytext', 'text', 'mediumtext', 'longtext') "
                + "THEN NULL ELSE c.CHARACTER_OCTET_LENGTH END"
            : $"CASE WHEN {prefixLength} <= c.CHARACTER_MAXIMUM_LENGTH "
                + $"THEN {prefixLength} * CEIL(c.CHARACTER_OCTET_LENGTH "
                + "/ NULLIF(c.CHARACTER_MAXIMUM_LENGTH, 0)) ELSE NULL END";

        var binaryWidth = prefixLength is null
            ? "CASE WHEN c.DATA_TYPE IN ('tinyblob', 'blob', 'mediumblob', 'longblob') "
                + "THEN NULL ELSE c.CHARACTER_OCTET_LENGTH END"
            : $"CASE WHEN {prefixLength} <= c.CHARACTER_OCTET_LENGTH "
                + $"THEN {prefixLength} ELSE NULL END";

        var decimalWidth = "(4 * FLOOR((c.NUMERIC_PRECISION - c.NUMERIC_SCALE) / 9) "
            + "+ CASE MOD(c.NUMERIC_PRECISION - c.NUMERIC_SCALE, 9) "
            + "WHEN 0 THEN 0 WHEN 1 THEN 1 WHEN 2 THEN 1 WHEN 3 THEN 2 WHEN 4 THEN 2 "
            + "WHEN 5 THEN 3 WHEN 6 THEN 3 ELSE 4 END "
            + "+ 4 * FLOOR(c.NUMERIC_SCALE / 9) "
            + "+ CASE MOD(c.NUMERIC_SCALE, 9) "
            + "WHEN 0 THEN 0 WHEN 1 THEN 1 WHEN 2 THEN 1 WHEN 3 THEN 2 WHEN 4 THEN 2 "
            + "WHEN 5 THEN 3 WHEN 6 THEN 3 ELSE 4 END)";

        var fractionalSeconds = "CASE COALESCE(c.DATETIME_PRECISION, 0) "
            + "WHEN 0 THEN 0 WHEN 1 THEN 1 WHEN 2 THEN 1 WHEN 3 THEN 2 WHEN 4 THEN 2 ELSE 3 END";

        var scalarWidth = "CASE "
            + "WHEN c.DATA_TYPE = 'tinyint' THEN 1 WHEN c.DATA_TYPE = 'smallint' THEN 2 "
            + "WHEN c.DATA_TYPE = 'mediumint' THEN 3 WHEN c.DATA_TYPE IN ('int', 'integer') THEN 4 "
            + "WHEN c.DATA_TYPE = 'float' THEN CASE WHEN c.NUMERIC_PRECISION <= 24 THEN 4 ELSE 8 END "
            + "WHEN c.DATA_TYPE IN ('bigint', 'double', 'real') THEN 8 "
            + $"WHEN c.DATA_TYPE IN ('decimal', 'numeric') THEN {decimalWidth} "
            + "WHEN c.DATA_TYPE = 'bit' THEN CEIL(c.NUMERIC_PRECISION / 8) "
            + "WHEN c.DATA_TYPE = 'date' THEN 3 WHEN c.DATA_TYPE = 'year' THEN 1 "
            + $"WHEN c.DATA_TYPE = 'time' THEN 3 + {fractionalSeconds} "
            + $"WHEN c.DATA_TYPE = 'datetime' THEN 5 + {fractionalSeconds} "
            + $"WHEN c.DATA_TYPE = 'timestamp' THEN 4 + {fractionalSeconds} "
            + "WHEN c.DATA_TYPE = 'enum' THEN 2 WHEN c.DATA_TYPE = 'set' THEN 8 ELSE NULL END";

        // Prefix lengths have character-count semantics for character keys and
        // byte-count semantics for binary keys. MySQL rejects prefixes on all
        // scalar families, so return NULL and let preflight fail closed before
        // a provider DDL command can discover that error after mutation begins.
        var width = prefixLength is null
            ? "CASE "
                + $"WHEN c.DATA_TYPE IN ('char', 'varchar', 'tinytext', 'text', 'mediumtext', 'longtext') THEN {characterWidth} "
                + $"WHEN c.DATA_TYPE IN ('binary', 'varbinary', 'tinyblob', 'blob', 'mediumblob', 'longblob') THEN {binaryWidth} "
                + $"ELSE {scalarWidth} END"
            : "CASE "
                + $"WHEN c.DATA_TYPE IN ('char', 'varchar', 'tinytext', 'text', 'mediumtext', 'longtext') THEN {characterWidth} "
                + $"WHEN c.DATA_TYPE IN ('binary', 'varbinary', 'tinyblob', 'blob', 'mediumblob', 'longblob') THEN {binaryWidth} "
                + "ELSE NULL END";

        return $"SELECT {width} FROM INFORMATION_SCHEMA.COLUMNS c "
            + $"WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = {Literal(table)} "
            + $"AND c.COLUMN_NAME = {Literal(key.Column!)}";
    }
}
