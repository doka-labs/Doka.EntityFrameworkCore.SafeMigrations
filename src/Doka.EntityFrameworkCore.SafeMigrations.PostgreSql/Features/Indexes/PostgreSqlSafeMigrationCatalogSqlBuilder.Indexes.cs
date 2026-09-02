namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    private static string? GetUnsupportedIndexFeature(
        SafeMigrationIntent intent
    ) => intent is EnsureIndexIntent index && index.Definition.Keys.Any(static key => key.PrefixLength is not null)
        ? "index_prefix_length"
        : null;

    private PostgreSqlSafeMigrationRuntimePlan BuildEnsureIndex(
        EnsureIndexIntent intent
    )
    {
        var definition = intent.Definition;
        var table = TableExists(definition.Table, definition.Schema);
        var exists = IndexExists(definition.Name, definition.Schema);
        var matching = IndexMatches(definition, requireExpectedName: true);
        var semanticAlias = IndexMatches(definition, requireExpectedName: false);
        var nonCanonicalAlias = IndexMatches(
            definition,
            requireExpectedName: false,
            requireIndependentIdentity: false);

        // PostgreSQL indexes share the schema relation namespace with tables,
        // views, sequences, and materialized views. Catalog classification must
        // reject any occupied name before CREATE INDEX can raise raw 42P07.
        var namespaceCollision = RelationNameExists(definition.Name, definition.Schema);

        var dataBlocked = definition.Unique ? UniqueIndexDataBlocked(definition) : "FALSE";
        var unsupportedConditions = new List<string>();
        if (definition.NullsDistinct == false)
        {
            unsupportedConditions.Add("current_setting('server_version_num')::integer < 150000");
        }

        if (definition.Keys.Any(static key => key.SortOrder != SafeMigrationIndexSortOrder.ProviderDefault
                || key.NullOrder != SafeMigrationIndexNullOrder.ProviderDefault))
        {
            var method = definition.Method ?? "btree";
            unsupportedConditions.Add(
                "pg_catalog.pg_indexam_has_property((SELECT am.oid FROM pg_catalog.pg_am am "
                + $"WHERE am.amname = {Literal(method)}), 'can_order') IS NOT TRUE");
        }

        var unsupported = unsupportedConditions.Count == 0
            ? "FALSE"
            : $"({string.Join(" OR ", unsupportedConditions)})";

        var satisfied = $"({matching}) OR (NOT ({exists}) AND ({semanticAlias}))";

        return Plan(
            $"CASE WHEN {unsupported} THEN 'unsupported' "
            + $"WHEN NOT {table} THEN 'prerequisite_missing' "
            + $"WHEN {exists} AND {matching} THEN 'matching' "
            + $"WHEN {exists} THEN 'different' "
            + $"WHEN {semanticAlias} THEN 'matching' "
            + $"WHEN {nonCanonicalAlias} THEN 'different' "
            + $"WHEN {namespaceCollision} THEN 'different' "
            + $"WHEN {dataBlocked} THEN 'data_blocked' ELSE 'missing' END",
            satisfied) with
        {
            // Ordered DropIndex -> EnsureIndex projection still needs the
            // duplicate-row proof hidden by an exact-name shape clash. The
            // code carries evidence into Core and never authorizes mutation.
            ClassificationCodeExpression = definition.Unique
                ? $"CASE WHEN {exists} AND NOT ({matching}) AND {dataBlocked} "
                    + "THEN 'index_replacement_data_blocked' ELSE NULL END"
                : null,
        };
    }

    private PostgreSqlSafeMigrationRuntimePlan BuildDropIndex(
        DropIndexIntent intent
    )
    {
        var exists = IndexExists(intent.Name, intent.Schema);
        var belongsToTable = IndexExists(intent.Name, intent.Schema, intent.Table);
        var independentlyOwned = IndependentIndexExists(intent.Name, intent.Schema, intent.Table);

        return Plan(
            $"CASE WHEN NOT {exists} THEN 'missing' "
            + $"WHEN {belongsToTable} AND {independentlyOwned} THEN 'matching' ELSE 'different' END",
            $"NOT {exists}");
    }

    private PostgreSqlSafeMigrationRuntimePlan BuildRenameIndex(
        RenameIndexIntent intent
    )
    {
        var source = IndexExists(intent.Name, intent.Schema);
        var sourceOnTable = IndexExists(intent.Name, intent.Schema, intent.Table);
        var independentlyOwned = IndependentIndexExists(intent.Name, intent.Schema, intent.Table);
        var target = RelationNameExists(intent.NewName, intent.Schema);

        return Plan(
            $"CASE WHEN NOT {source} THEN 'missing' WHEN NOT {sourceOnTable} THEN 'different' "
            + $"WHEN NOT {independentlyOwned} THEN 'different' "
            + $"WHEN {target} THEN 'different' "
            + "ELSE 'matching' END",
            $"NOT {source}");
    }

    private string IndexMatches(
        ExpectedIndexDefinition definition,
        bool requireExpectedName = true,
        bool requireIndependentIdentity = true
    )
    {
        var conditions = new List<string>
        {
            "i.indisvalid",
            "i.indisready",
            "i.indislive",
            $"i.indisunique = {definition.Unique.ToString().ToUpperInvariant()}",
            $"i.indnkeyatts = {definition.Keys.Count.ToString(CultureInfo.InvariantCulture)}",
            $"i.indnatts = {(definition.Keys.Count + definition.IncludedColumns.Count).ToString(CultureInfo.InvariantCulture)}",
            $"am.amname = {Literal(definition.Method ?? "btree")}",
        };

        if (requireIndependentIdentity)
        {
            // Attached partition indexes and constraint-owned backing indexes
            // are not independently managed EF indexes even when keys match.
            conditions.Add(
                "NOT EXISTS (SELECT 1 FROM pg_catalog.pg_inherits inh "
                + "WHERE inh.inhrelid = i.indexrelid)");
            conditions.Add(
                "NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint co "
                + "WHERE co.conindid = i.indexrelid AND co.conrelid = i.indrelid "
                + "AND co.contype IN ('p'::\"char\", 'u'::\"char\", 'x'::\"char\"))");
        }

        conditions.Add(
            definition.Filter is null && definition.StructuredFilter is null
                ? "i.indpred IS NULL"
                : definition.StructuredFilter is not null
                    ? ExpressionMatches("pg_catalog.pg_get_expr(i.indpred, i.indrelid)", definition.StructuredFilter)
                    : ExpressionMatches("pg_catalog.pg_get_expr(i.indpred, i.indrelid)", definition.Filter!));

        var nullsNotDistinct = "POSITION('NULLS NOT DISTINCT' IN pg_catalog.pg_get_indexdef(i.indexrelid)) > 0";
        conditions.Add(definition.NullsDistinct == false ? nullsNotDistinct : $"NOT ({nullsNotDistinct})");

        for (var index = 0; index < definition.Keys.Count; index++)
        {
            var position = index + 1;
            var optionIndex = index.ToString(CultureInfo.InvariantCulture);
            var propertyPosition = position.ToString(CultureInfo.InvariantCulture);
            var key = definition.Keys[index];
            if (key.Column is not null)
            {
                conditions.Add($"i.indkey[{optionIndex}] > 0");
                conditions.Add(
                    "EXISTS (SELECT 1 FROM pg_catalog.pg_attribute key_attribute "
                    + "WHERE key_attribute.attrelid = i.indrelid "
                    + $"AND key_attribute.attnum = i.indkey[{optionIndex}] "
                    + $"AND key_attribute.attname = {Literal(key.Column)})");
            }
            else
            {
                var expected = IndexExpressionSql(key);
                conditions.Add($"i.indkey[{optionIndex}] = 0");
                conditions.Add(
                    $"pg_catalog.pg_get_indexdef(i.indexrelid, {position.ToString(CultureInfo.InvariantCulture)}, TRUE) "
                    + $"IN ({string.Join(", ", expected)})");
            }

            conditions.Add(IndexSortMatches(propertyPosition, key));
            conditions.Add(IndexNullOrderMatches(propertyPosition, key));

            var keySql =
                $"pg_catalog.pg_get_indexdef(i.indexrelid, {position.ToString(CultureInfo.InvariantCulture)}, TRUE)";

            conditions.Add(
                key.Collation is null
                    ? $"POSITION(' COLLATE ' IN {keySql}) = 0"
                    : CatalogIdentifierMatches(
                        $"i.indcollation[{optionIndex}]",
                        "pg_catalog.pg_collation",
                        "coll",
                        "collnamespace",
                        "collname",
                        key.Collation!));

            conditions.Add(
                key.OperatorClass is null
                    ? $"EXISTS (SELECT 1 FROM pg_catalog.pg_opclass opc "
                    + $"WHERE opc.oid = i.indclass[{optionIndex}] AND opc.opcdefault)"
                    : CatalogPathMatches(
                        $"i.indclass[{optionIndex}]",
                        "pg_catalog.pg_opclass",
                        "opc",
                        "opcnamespace",
                        "opcname",
                        key.OperatorClass!));
        }

        for (var index = 0; index < definition.IncludedColumns.Count; index++)
        {
            var position = definition.Keys.Count + index + 1;
            var column = definition.IncludedColumns[index];
            conditions.Add(
                $"pg_catalog.pg_get_indexdef(i.indexrelid, {position.ToString(CultureInfo.InvariantCulture)}, TRUE) "
                + $"IN ({Literal(column)}, {Literal(_sqlGenerationHelper.DelimitIdentifier(column))})");
        }

        return "EXISTS (SELECT 1 FROM pg_catalog.pg_index i "
            + "JOIN pg_catalog.pg_class idx ON idx.oid = i.indexrelid "
            + "JOIN pg_catalog.pg_class tbl ON tbl.oid = i.indrelid "
            + "JOIN pg_catalog.pg_namespace n ON n.oid = idx.relnamespace "
            + "JOIN pg_catalog.pg_am am ON am.oid = idx.relam "
            + $"WHERE n.nspname = {SchemaExpression(definition.Schema)} "
            + $"AND idx.relname {(requireExpectedName ? "=" : "<>")} {Literal(definition.Name)} "
            + $"AND tbl.relname = {Literal(definition.Table)} "
            + $"AND {string.Join(" AND ", conditions)})";
    }

    private static string IndexSortMatches(
        string position,
        ExpectedIndexKeyDefinition key
    )
    {
        var orderable = $"pg_catalog.pg_index_column_has_property(i.indexrelid, {position}, 'orderable')";
        var property = key.SortOrder switch
        {
            SafeMigrationIndexSortOrder.ProviderDefault => "asc",
            SafeMigrationIndexSortOrder.Ascending => "asc",
            SafeMigrationIndexSortOrder.Descending => "desc",
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };

        var matches = $"pg_catalog.pg_index_column_has_property(i.indexrelid, {position}, '{property}') IS TRUE";

        return key.SortOrder == SafeMigrationIndexSortOrder.ProviderDefault
            ? $"({orderable} IS NOT TRUE OR {matches})"
            : $"({orderable} IS TRUE AND {matches})";
    }

    private static string IndexNullOrderMatches(
        string position,
        ExpectedIndexKeyDefinition key
    )
    {
        var orderable = $"pg_catalog.pg_index_column_has_property(i.indexrelid, {position}, 'orderable')";
        var property = key.NullOrder switch
        {
            SafeMigrationIndexNullOrder.ProviderDefault when key.SortOrder == SafeMigrationIndexSortOrder.Descending =>
                "nulls_first",
            SafeMigrationIndexNullOrder.ProviderDefault => "nulls_last",
            SafeMigrationIndexNullOrder.First => "nulls_first",
            SafeMigrationIndexNullOrder.Last => "nulls_last",
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };

        var matches = $"pg_catalog.pg_index_column_has_property(i.indexrelid, {position}, '{property}') IS TRUE";

        return key.NullOrder == SafeMigrationIndexNullOrder.ProviderDefault
            ? $"({orderable} IS NOT TRUE OR {matches})"
            : $"({orderable} IS TRUE AND {matches})";
    }

    private string UniqueIndexDataBlocked(
        ExpectedIndexDefinition definition
    )
    {
        var keys = definition
            .Keys
            .Select(IndexDataExpression)
            .ToArray();

        var predicates = new List<string>();
        if (definition.Filter is not null
            || definition.StructuredFilter is not null)
        {
            predicates.Add($"({definition.Filter ?? _expressionRenderer.Render(definition.StructuredFilter!)})");
        }

        if (definition.NullsDistinct != false)
        {
            predicates.AddRange(keys.Select(static key => $"({key}) IS NOT NULL"));
        }

        return DuplicateDataExists(
            definition.Table,
            definition.Schema,
            keys,
            predicates.Count == 0 ? "TRUE" : string.Join(" AND ", predicates));
    }

    private string IndexDataExpression(
        ExpectedIndexKeyDefinition key
    ) => key.Column is not null
        ? Delimited(key.Column)
        : key.Expression ?? _expressionRenderer.Render(key.StructuredExpression!);

    private List<string> IndexExpressionSql(
        ExpectedIndexKeyDefinition key
    )
    {
        var expression = key.Expression ?? _expressionRenderer.Render(key.StructuredExpression!);
        var roots = new List<(string Sql, bool IsValueExpression)>
        {
            (expression, false),
            ($"({expression})", false),
        };

        if (key.StructuredExpression is not null)
        {
            var catalogCandidate = _expressionRenderer.RenderCatalogCandidateSql(key.StructuredExpression, Literal);
            var deparsedCandidate = _expressionRenderer.RenderCatalogDeparsedCandidateSql(
                key.StructuredExpression,
                Literal);

            roots.Add((catalogCandidate, true));
            roots.Add(($"({Literal("(")} || {catalogCandidate} || {Literal(")")})", true));
            roots.Add((deparsedCandidate, true));
            roots.Add(($"({Literal("(")} || {deparsedCandidate} || {Literal(")")})", true));
        }

        var results = new List<string>();
        foreach (var root in roots)
        {
            var suffix = new StringBuilder();
            if (key.Collation is not null)
            {
                suffix
                    .Append(" COLLATE ")
                    .Append(Delimited(key.Collation));
            }

            if (key.OperatorClass is not null)
            {
                suffix
                    .Append(' ')
                    .Append(DelimitedPath(key.OperatorClass));
            }

            results.Add(
                root.IsValueExpression
                    ? suffix.Length == 0 ? root.Sql : $"({root.Sql} || {Literal(suffix.ToString())})"
                    : Literal(root.Sql + suffix));
        }

        return results
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private string CatalogPathMatches(
        string oidExpression,
        string catalog,
        string alias,
        string namespaceColumn,
        string nameColumn,
        string expectedPath
    )
    {
        var parts = expectedPath.Split('.', StringSplitOptions.None);
        if (parts.Length is < 1 or > 2
            || parts.Any(static part => string.IsNullOrWhiteSpace(part)))
        {
            throw new NotSupportedException(
                "A PostgreSQL catalog identifier must be an unqualified name or schema-qualified name.");
        }

        var name = parts[^1];
        var namespaceCondition = parts.Length == 1 ? string.Empty : $" AND ns.nspname = {Literal(parts[0])}";

        return $"EXISTS (SELECT 1 FROM {catalog} {alias} "
            + $"JOIN pg_catalog.pg_namespace ns ON ns.oid = {alias}.{namespaceColumn} "
            + $"WHERE {alias}.oid = {oidExpression} AND {alias}.{nameColumn} = {Literal(name)}"
            + namespaceCondition
            + ")";
    }

    private string CatalogIdentifierMatches(
        string oidExpression,
        string catalog,
        string alias,
        string namespaceColumn,
        string nameColumn,
        SafeMigrationCollationIdentifier expected
    )
    {
        var namespaceCondition = expected.Schema is null
            ? string.Empty
            : $" AND ns.nspname = {Literal(expected.Schema)}";

        return $"EXISTS (SELECT 1 FROM {catalog} {alias} "
            + $"JOIN pg_catalog.pg_namespace ns ON ns.oid = {alias}.{namespaceColumn} "
            + $"WHERE {alias}.oid = {oidExpression} AND {alias}.{nameColumn} = {Literal(expected.Name)}"
            + namespaceCondition
            + ")";
    }

    private string Delimited(
        SafeMigrationCollationIdentifier value
    ) => value.Schema is null
        ? _sqlGenerationHelper.DelimitIdentifier(value.Name)
        : _sqlGenerationHelper.DelimitIdentifier(value.Name, value.Schema);

    private string IndexExists(
        string name,
        string? schema
    ) => "EXISTS (SELECT 1 FROM pg_catalog.pg_class idx "
        + "JOIN pg_catalog.pg_namespace n ON n.oid = idx.relnamespace "
        + $"WHERE n.nspname = {SchemaExpression(schema)} AND idx.relname = {Literal(name)} "
        + "AND idx.relkind IN ('i', 'I'))";

    private string IndexExists(
        string name,
        string? schema,
        string table
    ) => "EXISTS (SELECT 1 FROM pg_catalog.pg_index i "
        + "JOIN pg_catalog.pg_class idx ON idx.oid = i.indexrelid "
        + "JOIN pg_catalog.pg_class tbl ON tbl.oid = i.indrelid "
        + "JOIN pg_catalog.pg_namespace n ON n.oid = idx.relnamespace "
        + $"WHERE n.nspname = {SchemaExpression(schema)} AND idx.relname = {Literal(name)} "
        + $"AND tbl.relname = {Literal(table)} AND idx.relkind IN ('i', 'I'))";

    private string IndependentIndexExists(
        string name,
        string? schema,
        string table
    ) => "EXISTS (SELECT 1 FROM pg_catalog.pg_index i "
        + "JOIN pg_catalog.pg_class idx ON idx.oid = i.indexrelid "
        + "JOIN pg_catalog.pg_class tbl ON tbl.oid = i.indrelid "
        + "JOIN pg_catalog.pg_namespace n ON n.oid = idx.relnamespace "
        + $"WHERE n.nspname = {SchemaExpression(schema)} AND idx.relname = {Literal(name)} "
        + $"AND tbl.relname = {Literal(table)} AND idx.relkind IN ('i', 'I') "
        + "AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_inherits inh WHERE inh.inhrelid = i.indexrelid) "
        + "AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint co "
        + "WHERE co.conindid = i.indexrelid AND co.conrelid = i.indrelid "
        + "AND co.contype IN ('p'::\"char\", 'u'::\"char\", 'x'::\"char\")))";

    private string ExpressionMatches(
        string catalogExpression,
        string expected
    ) => $"({catalogExpression} = {Literal(expected)} " + $"OR {catalogExpression} = {Literal($"({expected})")})";

    private string ExpressionMatches(
        string catalogExpression,
        SafeMigrationSqlExpression expected
    )
    {
        var rendered = _expressionRenderer.Render(expected);
        var catalogCandidate = _expressionRenderer.RenderCatalogCandidateSql(expected, Literal);
        var deparsedCandidate = _expressionRenderer.RenderCatalogDeparsedCandidateSql(expected, Literal);
        var candidates = new[]
            {
                Literal(rendered), Literal($"({rendered})"), catalogCandidate,
                $"({Literal("(")} || {catalogCandidate} || {Literal(")")})", deparsedCandidate,
                $"({Literal("(")} || {deparsedCandidate} || {Literal(")")})",
            }
            .Distinct(StringComparer.Ordinal)
            .Select(candidate => $"{catalogExpression} = {candidate}");

        return $"({string.Join(" OR ", candidates)})";
    }
}
