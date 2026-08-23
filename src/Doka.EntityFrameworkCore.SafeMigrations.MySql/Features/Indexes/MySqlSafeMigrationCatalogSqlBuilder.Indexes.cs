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
        var matching = BuildIndexMatches(definition, isMariaDb);
        var dataBlocked = definition.Unique ? UniqueIndexDataBlocked(definition) : "FALSE";

        return Plan(
            $"CASE WHEN NOT {tableExists} THEN 'prerequisite_missing' "
            + $"WHEN NOT {indexExists} AND {dataBlocked} THEN 'data_blocked' "
            + $"WHEN NOT {indexExists} THEN 'missing' "
            + $"WHEN {matching} THEN 'matching' ELSE 'different' END",
            matching);
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
        bool isMariaDb
    )
    {
        var conditions = new List<string>
        {
            $"(SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS s "
            + $"WHERE s.TABLE_SCHEMA = DATABASE() AND s.TABLE_NAME = {Literal(definition.Table)} "
            + $"AND s.INDEX_NAME = {Literal(definition.Name)}) "
            + $"= {definition.Keys.Count.ToString(CultureInfo.InvariantCulture)}",
            $"NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS s "
            + $"WHERE s.TABLE_SCHEMA = DATABASE() AND s.TABLE_NAME = {Literal(definition.Table)} "
            + $"AND s.INDEX_NAME = {Literal(definition.Name)} "
            + $"AND s.NON_UNIQUE <> {(definition.Unique ? "0" : "1")})",
        };

        if (definition.Method is not null)
        {
            conditions.Add(
                $"NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS s "
                + $"WHERE s.TABLE_SCHEMA = DATABASE() AND s.TABLE_NAME = {Literal(definition.Table)} "
                + $"AND s.INDEX_NAME = {Literal(definition.Name)} "
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
                + $"AND s.INDEX_NAME = {Literal(definition.Name)} "
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
}
