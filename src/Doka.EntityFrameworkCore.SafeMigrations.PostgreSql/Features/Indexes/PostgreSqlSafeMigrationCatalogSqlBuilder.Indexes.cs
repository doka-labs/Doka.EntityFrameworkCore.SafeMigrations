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
        var semanticCandidates = IndexMatchQuery(
            definition,
            requireExpectedName: false,
            requireIndependentIdentity: true);

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
            DiagnosticEvidenceExpression = BuildIndexDiagnosticEvidence(definition),
            MatchedObjectNameExpression = ResolveMatchingObjectName(
                matching,
                Literal(definition.Name),
                semanticCandidates),
            // Ordered DropIndex -> EnsureIndex projection still needs the
            // duplicate-row proof hidden by an exact-name shape clash. The
            // code carries evidence into Core and never authorizes mutation.
            ClassificationCodeExpression = definition.Unique
                ? $"CASE WHEN {exists} AND NOT ({matching}) AND {dataBlocked} "
                    + "THEN 'index_replacement_data_blocked' ELSE NULL END"
                : null,
        };
    }

    private string BuildIndexDiagnosticEvidence(
        ExpectedIndexDefinition definition
    )
    {
        var expectedKeys = string.Join(
            ",",
            definition.Keys.Select(static key => key.Column ?? "[expression]"));

        var actualKeysExpression = "array_to_string(ARRAY(SELECT CASE WHEN key.attnum = 0 "
            + "THEN '[expression]' ELSE key_attribute.attname END "
            + "FROM unnest(i.indkey) WITH ORDINALITY AS key(attnum, ord) "
            + "LEFT JOIN pg_catalog.pg_attribute key_attribute "
            + "ON key_attribute.attrelid = i.indrelid AND key_attribute.attnum = key.attnum "
            + "WHERE key.ord <= i.indnkeyatts ORDER BY key.ord), ',')";

        var actualKeys = expectedKeys.Length <= SafeMigrationFacetDifference.MaximumValueLength
            ? $"LEFT({actualKeysExpression}, {SafeMigrationFacetDifference.MaximumValueLength})"
            : $"'md5:' || pg_catalog.md5({actualKeysExpression})";

        var expectedKeysDiagnostic = expectedKeys.Length <= SafeMigrationFacetDifference.MaximumValueLength
            ? Literal(expectedKeys)
            : $"'md5:' || pg_catalog.md5({Literal(expectedKeys)})";

        return "(SELECT NULLIF(concat_ws(chr(30), "
            + DiagnosticRecord(
                $"{actualKeysExpression} <> {Literal(expectedKeys)}",
                "index_key_order",
                expectedKeysDiagnostic,
                actualKeys)
            + ", "
            + DiagnosticRecord(
                $"i.indisunique <> {definition.Unique.ToString().ToUpperInvariant()}",
                "index_uniqueness",
                definition.Unique ? "'unique'" : "'non_unique'",
                "CASE WHEN i.indisunique THEN 'unique' ELSE 'non_unique' END")
            + ", "
            + DiagnosticRecord(
                $"am.amname <> {Literal(definition.Method ?? "btree")}",
                "index_method",
                Literal(definition.Method ?? "btree"),
                "LEFT(am.amname, 256)")
            + "), '') FROM pg_catalog.pg_index i "
            + "JOIN pg_catalog.pg_class idx ON idx.oid = i.indexrelid "
            + "JOIN pg_catalog.pg_class tbl ON tbl.oid = i.indrelid "
            + "JOIN pg_catalog.pg_namespace n ON n.oid = idx.relnamespace "
            + "JOIN pg_catalog.pg_am am ON am.oid = idx.relam "
            + $"WHERE n.nspname = {SchemaExpression(definition.Schema)} "
            + $"AND idx.relname = {Literal(definition.Name)} "
            + $"AND tbl.relname = {Literal(definition.Table)} LIMIT 1)";
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
    ) => $"EXISTS ({IndexMatchQuery(definition, requireExpectedName, requireIndependentIdentity)})";

    private string IndexMatchQuery(
        ExpectedIndexDefinition definition,
        bool requireExpectedName,
        bool requireIndependentIdentity
    )
    {
        var conditions = new List<string>
        {
            "i.indisvalid",
            "i.indisready",
            "i.indislive",
            $"i.indisunique = {definition.Unique.ToString().ToUpperInvariant()}",
            $"i.indnkeyatts = {definition.Keys.Count.ToString(CultureInfo.InvariantCulture)}",
            "i.indnatts = "
                + (definition.Keys.Count + definition.IncludedColumns.Count).ToString(CultureInfo.InvariantCulture),
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

        var structuredFilter = GetStructuredIndexFilter(definition);
        conditions.Add(
            definition.Filter is null && definition.StructuredFilter is null
                ? "i.indpred IS NULL"
                : structuredFilter is not null
                    ? ExpressionMatches("pg_catalog.pg_get_expr(i.indpred, i.indrelid)", structuredFilter)
                    : ExpressionMatches("pg_catalog.pg_get_expr(i.indpred, i.indrelid)", definition.Filter!));

        var nullsNotDistinct = "POSITION('NULLS NOT DISTINCT' IN pg_catalog.pg_get_indexdef(i.indexrelid)) > 0";
        conditions.Add(definition.NullsDistinct == false ? nullsNotDistinct : $"NOT ({nullsNotDistinct})");

        var plainKeys = new List<(int Position, ExpectedIndexKeyDefinition Key)>(definition.Keys.Count);
        for (var index = 0; index < definition.Keys.Count; index++)
        {
            var position = index + 1;
            var optionIndex = index.ToString(CultureInfo.InvariantCulture);
            var propertyPosition = position.ToString(CultureInfo.InvariantCulture);
            var key = definition.Keys[index];

            // WHY: A key that names a column and leaves collation and operator class to the
            // provider needs only value comparisons, so it joins the correlated set instead of
            // opening its own catalog lookups.
            if (key.Column is not null
                && key.Collation is null
                && key.OperatorClass is null)
            {
                plainKeys.Add((position, key));

                continue;
            }

            var storedExpression = IndexStoredExpression(key);
            var storedColumn = key.Column
                ?? (storedExpression is SafeMigrationSqlIdentifierExpression { Parts.Count: 1 } identifier
                    ? identifier.Parts[0]
                    : null);

            if (storedColumn is not null)
            {
                // WHY: Per-column deparsing omits COLLATE decorations; only the physical
                // catalog identity proves that a key uses its column's default collation.
                conditions.Add($"i.indkey[{optionIndex}] > 0");
                conditions.Add(
                    "EXISTS (SELECT 1 FROM pg_catalog.pg_attribute key_attribute "
                    + "WHERE key_attribute.attrelid = i.indrelid "
                    + $"AND key_attribute.attnum = i.indkey[{optionIndex}] "
                    + $"AND key_attribute.attname = {Literal(storedColumn)}"
                    + (key.Collation is null && key.Column is not null
                        ? $" AND i.indcollation[{optionIndex}] = key_attribute.attcollation)"
                        : ")"));
            }
            else
            {
                var expected = IndexExpressionSql(key);
                conditions.Add($"i.indkey[{optionIndex}] = 0");
                conditions.Add(
                    "pg_catalog.pg_get_indexdef(i.indexrelid, "
                    + $"{position.ToString(CultureInfo.InvariantCulture)}, TRUE) "
                    + $"IN ({string.Join(", ", expected)})");
            }

            conditions.Add(IndexSortMatches(propertyPosition, key));
            conditions.Add(IndexNullOrderMatches(propertyPosition, key));

            if (key.Collation is not null)
            {
                conditions.Add(
                    CatalogIdentifierMatches(
                        $"i.indcollation[{optionIndex}]",
                        "pg_catalog.pg_collation",
                        "coll",
                        "collnamespace",
                        "collname",
                        key.Collation!));
            }
            else if (key.Column is null)
            {
                conditions.Add(
                    ExpressionIndexDefaultCollationMatches(definition, key, optionIndex));
            }


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

        if (plainKeys.Count > 0)
        {
            conditions.Add(PlainIndexKeysMatch(plainKeys));
        }

        for (var index = 0; index < definition.IncludedColumns.Count; index++)
        {
            var position = definition.Keys.Count + index + 1;
            var column = definition.IncludedColumns[index];
            conditions.Add(
                $"pg_catalog.pg_get_indexdef(i.indexrelid, {position.ToString(CultureInfo.InvariantCulture)}, TRUE) "
                + $"IN ({Literal(column)}, {Literal(_sqlGenerationHelper.DelimitIdentifier(column))})");
        }

        return "SELECT idx.relname AS candidate_name FROM pg_catalog.pg_index i "
            + "JOIN pg_catalog.pg_class idx ON idx.oid = i.indexrelid "
            + "JOIN pg_catalog.pg_class tbl ON tbl.oid = i.indrelid "
            + "JOIN pg_catalog.pg_namespace n ON n.oid = idx.relnamespace "
            + "JOIN pg_catalog.pg_am am ON am.oid = idx.relam "
            + $"WHERE n.nspname = {SchemaExpression(definition.Schema)} "
            + $"AND idx.relname {(requireExpectedName ? "=" : "<>")} {Literal(definition.Name)} "
            + $"AND tbl.relname = {Literal(definition.Table)} "
            + $"AND {string.Join(" AND ", conditions)}";
    }

    private static string IndexSortMatches(
        string position,
        ExpectedIndexKeyDefinition key
    )
    {
        var orderable = $"pg_catalog.pg_index_column_has_property(i.indexrelid, {position}, 'orderable')";
        var matches = "pg_catalog.pg_index_column_has_property(i.indexrelid, "
            + $"{position}, '{IndexSortProperty(key)}') IS TRUE";

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
        var matches = "pg_catalog.pg_index_column_has_property(i.indexrelid, "
            + $"{position}, '{IndexNullOrderProperty(key)}') IS TRUE";

        return key.NullOrder == SafeMigrationIndexNullOrder.ProviderDefault
            ? $"({orderable} IS NOT TRUE OR {matches})"
            : $"({orderable} IS TRUE AND {matches})";
    }

    /// <summary>Resolves the catalog sort property one expected index key requires.</summary>
    /// <param name="key">The expected index key.</param>
    /// <returns>The index-column property name the catalog must report as true.</returns>
    private static string IndexSortProperty(
        ExpectedIndexKeyDefinition key
    ) => key.SortOrder switch
    {
        SafeMigrationIndexSortOrder.ProviderDefault => "asc",
        SafeMigrationIndexSortOrder.Ascending => "asc",
        SafeMigrationIndexSortOrder.Descending => "desc",
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    /// <summary>Resolves the catalog null-order property one expected index key requires.</summary>
    /// <param name="key">The expected index key.</param>
    /// <returns>The index-column property name the catalog must report as true.</returns>
    private static string IndexNullOrderProperty(
        ExpectedIndexKeyDefinition key
    ) => key.NullOrder switch
    {
        SafeMigrationIndexNullOrder.ProviderDefault when key.SortOrder == SafeMigrationIndexSortOrder.Descending =>
            "nulls_first",
        SafeMigrationIndexNullOrder.ProviderDefault => "nulls_last",
        SafeMigrationIndexNullOrder.First => "nulls_first",
        SafeMigrationIndexNullOrder.Last => "nulls_last",
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    /// <summary>Verifies every plain index key through one correlated set instead of one scan each.</summary>
    /// <remarks>
    /// WHY: A plain key -- a column without an explicit collation or operator class -- needs only
    /// value comparisons, so the whole set can be driven from a VALUES list while the catalog
    /// lookups appear once. Keys carrying an expression, a collation or an operator class render
    /// per key and keep their own conditions.
    ///
    /// The catalog's indkey and indclass vectors are zero-based while
    /// pg_index_column_has_property and pg_get_indexdef take one-based positions, so the row
    /// carries the one-based position and the vector lookups subtract one.
    /// </remarks>
    /// <param name="keys">The plain keys with their one-based positions.</param>
    /// <returns>A predicate that is true when every plain key matches at its own position.</returns>
    private string PlainIndexKeysMatch(
        IReadOnlyList<(int Position, ExpectedIndexKeyDefinition Key)> keys
    )
    {
        var rows = keys
            .Select(entry => $"({entry.Position.ToString(CultureInfo.InvariantCulture)}, "
                + $"CAST({Literal(entry.Key.Column!)} AS pg_catalog.name), "
                + $"{Literal(IndexSortProperty(entry.Key))}, "
                + $"{(entry.Key.SortOrder == SafeMigrationIndexSortOrder.ProviderDefault ? "TRUE" : "FALSE")}, "
                + $"{Literal(IndexNullOrderProperty(entry.Key))}, "
                + $"{(entry.Key.NullOrder == SafeMigrationIndexNullOrder.ProviderDefault ? "TRUE" : "FALSE")})")
            .ToArray();

        const string orderable = "pg_catalog.pg_index_column_has_property(i.indexrelid, key.pos, 'orderable')";
        var sortMatches = "pg_catalog.pg_index_column_has_property(i.indexrelid, key.pos, key.sort_property) IS TRUE";
        var nullMatches = "pg_catalog.pg_index_column_has_property(i.indexrelid, key.pos, key.null_property) IS TRUE";
        var facets = "i.indkey[key.pos - 1] > 0 "
            + "AND EXISTS (SELECT 1 FROM pg_catalog.pg_attribute key_attribute "
            + "WHERE key_attribute.attrelid = i.indrelid "
            + "AND key_attribute.attnum = i.indkey[key.pos - 1] "
            + "AND key_attribute.attname = key.attname "
            + "AND i.indcollation[key.pos - 1] = key_attribute.attcollation) "
            + $"AND CASE WHEN key.sort_default THEN ({orderable} IS NOT TRUE OR {sortMatches}) "
            + $"ELSE ({orderable} IS TRUE AND {sortMatches}) END "
            + $"AND CASE WHEN key.null_default THEN ({orderable} IS NOT TRUE OR {nullMatches}) "
            + $"ELSE ({orderable} IS TRUE AND {nullMatches}) END "
            + "AND EXISTS (SELECT 1 FROM pg_catalog.pg_opclass opc "
            + "WHERE opc.oid = i.indclass[key.pos - 1] AND opc.opcdefault)";

        // WHY: Per-column pg_get_indexdef omits collation decorations. Compare physical OIDs
        // with the column default instead, including zero for noncollatable keys, so an explicit
        // index collation cannot silently satisfy a different default-collation contract.
        // A facet can evaluate to NULL. The per-key form required every condition to be true,
        // so the absent test coalesces to FALSE and keeps that verdict.
        return $"NOT EXISTS (SELECT 1 FROM (VALUES {string.Join(", ", rows)}) "
            + "AS key(pos, attname, sort_property, sort_default, null_property, null_default) "
            + $"WHERE NOT COALESCE({facets}, FALSE))";
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

    /// <summary>Proves an expression key's derived default collation without reading or evaluating table rows.</summary>
    /// <param name="definition">The index's qualified table contract.</param>
    /// <param name="key">The expression key without an explicit key-level collation.</param>
    /// <param name="optionIndex">The key's zero-based catalog vector position.</param>
    /// <returns>A comparison against the expression's PostgreSQL-derived collation identity.</returns>
    private string ExpressionIndexDefaultCollationMatches(
        ExpectedIndexDefinition definition,
        ExpectedIndexKeyDefinition key,
        string optionIndex
    )
    {
        // WHY: Per-column deparsing omits key-level COLLATE. PostgreSQL derives the scalar
        // subquery's type and collation even with LIMIT 0; pg_collation_for reads that metadata,
        // not its NULL value. This avoids tuple reads and row-wise evaluation, not general
        // planner constant folding. The noncollatable branch avoids integer/boolean type errors.
        var expression = IndexDataExpression(key);
        var table = Qualified(definition.Table, definition.Schema);

        return $"CASE WHEN i.indcollation[{optionIndex}] = 0 THEN TRUE ELSE "
            + $"i.indcollation[{optionIndex}] = pg_catalog.to_regcollation("
            + $"pg_catalog.pg_collation_for((SELECT {expression} FROM {table} LIMIT 0)))::oid END";
    }

    private List<string> IndexExpressionSql(
        ExpectedIndexKeyDefinition key
    )
    {
        var storedExpression = IndexStoredExpression(key);
        var expression = key.Expression ?? _expressionRenderer.Render(storedExpression!);
        var roots = new List<(string Sql, bool IsValueExpression)>
        {
            (expression, false),
            ($"({expression})", false),
        };

        if (storedExpression is not null)
        {
            var catalogCandidate = _expressionRenderer.RenderCatalogCandidateSql(storedExpression, Literal);
            var deparsedCandidate = _expressionRenderer.RenderCatalogDeparsedCandidateSql(
                storedExpression,
                Literal);

            roots.Add((catalogCandidate, true));
            roots.Add(($"({Literal("(")} || {catalogCandidate} || {Literal(")")})", true));
            roots.Add((deparsedCandidate, true));
            roots.Add(($"({Literal("(")} || {deparsedCandidate} || {Literal(")")})", true));
        }

        // WHY: pg_get_indexdef with a column position returns only the expression. Collation
        // and operator-class decorations are matched separately by physical catalog identity.
        return roots
            .Select(root => root.IsValueExpression ? root.Sql : Literal(root.Sql))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Resolves the expression shape PostgreSQL stores after removing top-level collation nodes.</summary>
    /// <param name="key">The immutable authored index key.</param>
    /// <returns>The stored structured operand; raw expressions and column keys return null.</returns>
    private static SafeMigrationSqlExpression? IndexStoredExpression(
        ExpectedIndexKeyDefinition key
    )
    {
        // WHY: ComputeIndexAttrs captures exprCollation before stripping top-level CollateExpr.
        // Nested collations remain part of the expression. Keep the authored tree intact for the
        // derived-collation proof, prerequisites, execution and contract fingerprint.
        var expression = key.StructuredExpression;
        while (expression is SafeMigrationSqlCollateExpression collate)
        {
            expression = collate.Operand;
        }

        return expression;
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
