namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    private string? GetUnsupportedColumnFeature(
        SafeMigrationIntent intent
    )
    {
        var definitions = intent switch
        {
            EnsureTableIntent value => value.Definition.Columns,
            EnsureColumnIntent value => [value.Definition],
            AlterColumnIntent value => [value.Definition],
            _ => [],
        };

        if (definitions.Any(definition => !CanMap(definition)))
        {
            return "column_type_mapping";
        }

        if (definitions.Any(static definition => !PostgreSqlSafeMigrationColumnMetadata.Supports(definition)))
        {
            return "provider_column_annotation";
        }

        return definitions.Any(static definition =>
            (definition.ComputedColumnSql is not null || definition.ComputedExpression is not null)
            && definition.IsStored == false)
            ? "virtual_generated_column"
            : null;
    }

    private PostgreSqlSafeMigrationRuntimePlan BuildEnsureColumn(
        EnsureColumnIntent intent,
        bool repairRequested,
        bool includeAnalysisEvidence,
        bool includeTransitionEvidence
    )
    {
        var table = TableExists(intent.Table, intent.Schema);
        var exists = ColumnExists(intent.Table, intent.Schema, intent.Definition.Name);
        var matching = ColumnMatches(intent.Table, intent.Schema, intent.Definition);
        var unsafeAdd = !SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(intent.Definition);
        var repairCapability = repairRequested
            && PostgreSqlSafeMigrationColumnMetadata.CanSafelyConverge(intent.Definition)
            ? SafeMigrationRepairCapability.Safe
            : SafeMigrationRepairCapability.None;

        var transition = repairCapability == SafeMigrationRepairCapability.Safe
            ? BuildColumnRepairTransition(
                intent.Table,
                intent.Schema,
                intent.Definition,
                includeTransitionEvidence)
            : PostgreSqlColumnRepairTransition.None;

        var repairInvariant = repairCapability == SafeMigrationRepairCapability.Safe
            ? $"({ColumnRepairInvariantMatches(intent.Table, intent.Schema, intent.Definition)}) "
                + $"OR ({transition.InvariantExpression})"
            : "FALSE";

        var dataBlocked = unsafeAdd
            ? $"EXISTS (SELECT 1 FROM {Qualified(intent.Table, intent.Schema)} LIMIT 1)"
            : "FALSE";

        var nullabilityDataProbe = repairCapability == SafeMigrationRepairCapability.Safe
            && !intent.Definition.IsNullable
                ? new PostgreSqlSafeMigrationNullabilityDataProbe(
                    ColumnNotNullProof(intent.Table, intent.Schema, intent.Definition.Name),
                    repairInvariant,
                    $"EXISTS (SELECT 1 FROM {Qualified(intent.Table, intent.Schema)} WHERE "
                        + $"{_sqlGenerationHelper.DelimitIdentifier(intent.Definition.Name)} IS NULL LIMIT 1)",
                    Qualified(intent.Table, intent.Schema))
                : null;

        var hasNull = nullabilityDataProbe is null
            ? "FALSE"
            : PostgreSqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder;

        // WHY: Runtime state, repair, and NULL gates share one fresh physical
        // predicate per evaluation instead of copying its catalog SQL.
        var repairInvariantExpression = nullabilityDataProbe is null
            ? repairInvariant
            : PostgreSqlSafeMigrationRuntimePlan.NullabilityRepairInvariantPlaceholder;

        var dataTransitionBlocked = transition.DataBlockedExpression;
        var repairDataBlocked = $"({hasNull}) OR ({dataTransitionBlocked})";

        var repairPrecondition = repairCapability == SafeMigrationRepairCapability.Safe
            ? $"({repairInvariantExpression}) AND NOT ({repairDataBlocked})"
            : "FALSE";

        var plan = Plan(
            $"CASE WHEN NOT {table} THEN 'prerequisite_missing' "
            + $"WHEN NOT {exists} AND {dataBlocked} THEN 'data_blocked' "
            + $"WHEN NOT {exists} THEN 'missing' "
            + $"WHEN ({repairInvariantExpression}) AND ({repairDataBlocked}) THEN 'data_blocked' "
            + $"WHEN {matching} THEN 'matching' ELSE 'different' END",
            matching,
            repairCapability,
            repairPrecondition) with
        {
            ClassificationCodeExpression = transition.HasDataProbe
                ? $"CASE WHEN {dataTransitionBlocked} THEN 'varchar_narrowing_value_too_long' ELSE NULL END"
                : null,
            RepairOperationalImpact = transition.OperationalImpact,
            DataProbe = transition.DataProbe,
            NullabilityDataProbe = nullabilityDataProbe,
            MayRequireNullabilityDataProof = repairCapability == SafeMigrationRepairCapability.Safe
                && !intent.Definition.IsNullable,
            DiagnosticEvidenceExpression = includeAnalysisEvidence
                ? BuildColumnDiagnosticEvidence(
                    intent.Table,
                    intent.Schema,
                    intent.Definition)
                : null,
        };

        // PostgreSQL resolves column names when the data-reading statement is
        // first executed. Keep that statement behind a catalog-only guard so
        // a missing target remains a valid Missing/DataBlocked classification.
        return repairCapability == SafeMigrationRepairCapability.Safe
            && (!intent.Definition.IsNullable || transition.HasDataProbe)
                ? plan with
                {
                    StateEvaluationGuardExpression = exists,
                    StateEvaluationGuardFailureExpression = unsafeAdd
                        ? $"CASE WHEN {dataBlocked} THEN 'data_blocked' ELSE 'missing' END"
                        : "'missing'",
                }
                : plan;
    }

    private PostgreSqlSafeMigrationRuntimePlan BuildDropColumn(
        DropColumnIntent intent
    )
    {
        var relation = RelationExists(intent.Table, intent.Schema);
        var table = TableExists(intent.Table, intent.Schema);
        var exists = ColumnExists(intent.Table, intent.Schema, intent.Name);

        return Plan(
            $"CASE WHEN NOT {relation} THEN 'missing' WHEN NOT {table} THEN 'different' "
            + $"WHEN {exists} THEN 'matching' ELSE 'missing' END",
            $"NOT {exists}");
    }

    private PostgreSqlSafeMigrationRuntimePlan BuildRenameColumn(
        RenameColumnIntent intent
    )
    {
        var table = TableExists(intent.Table, intent.Schema);
        var source = ColumnExists(intent.Table, intent.Schema, intent.Name);
        var target = ColumnExists(intent.Table, intent.Schema, intent.NewName);

        return Plan(
            $"CASE WHEN NOT {source} THEN 'missing' WHEN NOT {table} THEN 'different' "
            + $"WHEN {target} THEN 'different' ELSE 'matching' END",
            $"NOT {source}");
    }

    private PostgreSqlSafeMigrationRuntimePlan BuildAlterColumn(
        AlterColumnIntent intent,
        bool repairRequested,
        bool includeAnalysisEvidence,
        bool includeTransitionEvidence
    )
    {
        var exists = ColumnExists(intent.Table, intent.Schema, intent.Definition.Name);
        var matching = ColumnMatches(intent.Table, intent.Schema, intent.Definition);
        var sameTypeRepair = repairRequested
            && intent.OldDefinition is not null
            && SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(intent.OldDefinition, intent.Definition);

        var transition = repairRequested
            && !sameTypeRepair
            && intent.OldDefinition is not null
            && CanAlterVarcharLength(intent.OldDefinition, intent.Definition)
                ? BuildColumnRepairTransition(
                    intent.Table,
                    intent.Schema,
                    intent.Definition,
                    includeTransitionEvidence,
                    intent.OldDefinition)
                : PostgreSqlColumnRepairTransition.None;

        var repair = sameTypeRepair || transition.HasDataProbe
            ? SafeMigrationRepairCapability.Safe
            : SafeMigrationRepairCapability.None;

        var repairPrecondition = sameTypeRepair
            ? ColumnMatches(intent.Table, intent.Schema, intent.OldDefinition!)
            : transition.InvariantExpression;

        var nullabilityDataProbe =
            repair == SafeMigrationRepairCapability.Safe
            && intent.OldDefinition!.IsNullable
            && !intent.Definition.IsNullable
                ? new PostgreSqlSafeMigrationNullabilityDataProbe(
                    ColumnNotNullProof(intent.Table, intent.Schema, intent.Definition.Name),
                    repairPrecondition,
                    $"EXISTS (SELECT 1 FROM {Qualified(intent.Table, intent.Schema)} WHERE "
                        + $"{_sqlGenerationHelper.DelimitIdentifier(intent.Definition.Name)} IS NULL LIMIT 1)",
                    Qualified(intent.Table, intent.Schema))
                : null;

        var nullBlocked = nullabilityDataProbe is null
            ? "FALSE"
            : PostgreSqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder;

        var repairInvariantExpression = nullabilityDataProbe is null
            ? repairPrecondition
            : PostgreSqlSafeMigrationRuntimePlan.NullabilityRepairInvariantPlaceholder;

        var dataBlocked = transition.HasDataProbe
            ? $"({nullBlocked}) OR ({transition.DataBlockedExpression})"
            : nullBlocked;

        return Plan(
            $"CASE WHEN NOT {exists} THEN 'different' "
            + $"WHEN ({repairInvariantExpression}) AND ({dataBlocked}) THEN 'data_blocked' "
            + $"WHEN {matching} THEN 'matching' ELSE 'different' END",
            matching,
            repair,
            $"({repairInvariantExpression}) AND NOT ({dataBlocked})") with
        {
            DataProbe = transition.DataProbe,
            ClassificationCodeExpression = transition.HasDataProbe
                ? $"CASE WHEN {transition.DataBlockedExpression} "
                    + "THEN 'varchar_narrowing_value_too_long' ELSE NULL END"
                : null,
            RepairOperationalImpact = transition.HasDataProbe
                ? transition.OperationalImpact
                : SafeMigrationOperationalImpact.Unknown,
            NullabilityDataProbe = nullabilityDataProbe,
            StateEvaluationGuardExpression = exists,
            StateEvaluationGuardFailureExpression = "'different'",
            MayRequireNullabilityDataProof = repair == SafeMigrationRepairCapability.Safe
                && intent.OldDefinition!.IsNullable
                && !intent.Definition.IsNullable,
            DiagnosticEvidenceExpression = includeAnalysisEvidence
                ? BuildColumnDiagnosticEvidence(intent.Table, intent.Schema, intent.Definition)
                : null,
        };
    }

    /// <summary>Checks provider support before an empty projected table can authorize a length transition.</summary>
    /// <param name="intent">The complete reviewed old and target contract.</param>
    /// <returns>Whether the same static boundary used by runtime VARCHAR repair is satisfied.</returns>
    public bool SupportsProjectedAlterColumn(
        AlterColumnIntent intent
    ) => GetUnsupportedFeature(intent) is null
        && intent.OldDefinition is not null
        && CanAlterVarcharLength(intent.OldDefinition, intent.Definition);

    /// <summary>Keeps the provider length exception separate from unchanged intrinsic column facets.</summary>
    private bool CanAlterVarcharLength(
        ExpectedColumnDefinition source,
        ExpectedColumnDefinition target
    ) => source.ClrType == typeof(string)
        && target.ClrType == typeof(string)
        && PostgreSqlSafeMigrationColumnMetadata.CanSafelyConverge(source)
        && PostgreSqlSafeMigrationColumnMetadata.CanSafelyConverge(target)
        && PostgreSqlSafeMigrationColumnMetadata.GetValueGenerationStrategy(source)
            is null or NpgsqlValueGenerationStrategy.None
        && PostgreSqlSafeMigrationColumnMetadata.GetValueGenerationStrategy(target)
            is null or NpgsqlValueGenerationStrategy.None
        && StringComparer.Ordinal.Equals(source.Name, target.Name)
        && source.IsUnicode == target.IsUnicode
        && source.IsFixedLength == target.IsFixedLength
        && source.Precision == target.Precision
        && source.Scale == target.Scale
        && Equals(source.Collation, target.Collation)
        && IsVarcharType(ResolveStoreType(source))
        && TryParseVarcharLength(ResolveStoreType(target), out _);

    private static bool IsVarcharType(
        string storeType
    ) => TryParseVarcharLength(storeType, out _)
        || storeType.AsSpan().Trim().Equals("varchar", StringComparison.OrdinalIgnoreCase)
        || storeType.AsSpan().Trim().Equals("character varying", StringComparison.OrdinalIgnoreCase);

    private string ColumnMatches(
        string table,
        string? schema,
        ExpectedColumnDefinition definition,
        int? ordinal = null,
        bool includeRepairableFacets = true
    )
    {
        var conditions = ColumnFacetConditions(definition, includeRepairableFacets);
        if (ordinal is not null)
        {
            conditions.Add($"a.attnum = {ordinal.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        return "EXISTS (SELECT 1 FROM pg_catalog.pg_attribute a "
            + "JOIN pg_catalog.pg_class c ON c.oid = a.attrelid "
            + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
            + "JOIN pg_catalog.pg_type t ON t.oid = a.atttypid "
            + "LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum "
            + $"WHERE n.nspname = {SchemaExpression(schema)} AND c.relname = {Literal(table)} "
            + $"AND a.attname = {Literal(definition.Name)} AND a.attnum > 0 AND NOT a.attisdropped "
            + $"AND {string.Join(" AND ", conditions)})";
    }

    /// <summary>Builds the facet predicates one expected column requires of the catalog row.</summary>
    /// <remarks>
    /// The predicates reference the outer aliases <c>a</c>, <c>c</c>, <c>t</c> and <c>d</c>, so the
    /// caller owns the catalog join. Both the single-column classifier and the set-oriented
    /// table-definition classifier consume this list, which keeps one definition of column equality.
    /// </remarks>
    /// <param name="definition">The expected column.</param>
    /// <param name="includeRepairableFacets">Whether nullability, comment and default facets apply.</param>
    /// <returns>The predicates, combined by the caller with <c>AND</c>.</returns>
    private List<string> ColumnFacetConditions(
        ExpectedColumnDefinition definition,
        bool includeRepairableFacets
    )
    {
        var mapping = ColumnMapping(definition);
        var conditions = new List<string>
        {
            $"pg_catalog.format_type(a.atttypid, a.atttypmod) = {Literal(ColumnStoreType(definition, mapping))}",
        };

        if (includeRepairableFacets)
        {
            conditions.Add(ColumnNullabilityMatches(definition));
            conditions.Add(
                $"pg_catalog.col_description(c.oid, a.attnum) IS NOT DISTINCT FROM "
                + (definition.Comment is null ? "NULL" : Literal(definition.Comment)));
        }

        conditions.AddRange(ColumnStructuralFacetConditions(definition, mapping, includeRepairableFacets));

        return conditions;
    }

    /// <summary>Resolves the store type one expected column requires of the catalog row.</summary>
    /// <param name="definition">The expected column.</param>
    /// <param name="mapping">The already resolved type mapping for that column.</param>
    /// <returns>The expected store type as the catalog renders it.</returns>
    private static string ColumnStoreType(
        ExpectedColumnDefinition definition,
        RelationalTypeMapping mapping
    )
    {
        var storeType = definition.StoreType ?? mapping.StoreType;
        if (storeType.StartsWith("character varying(", StringComparison.Ordinal))
        {
            return storeType;
        }

        // WHY: format_type renders the canonical spelling even when the public
        // old/target contract uses PostgreSQL's VARCHAR alias.
        return TryParseVarcharLength(storeType, out var length)
            ? $"character varying({length.ToString(CultureInfo.InvariantCulture)})"
            : IsVarcharType(storeType) ? "character varying" : storeType;
    }

    private RelationalTypeMapping ColumnMapping(
        ExpectedColumnDefinition definition
    ) => _typeMappingSource.FindMapping(
            definition.ClrType,
            definition.StoreType,
            keyOrIndex: false,
            unicode: definition.IsUnicode,
            size: definition.MaxLength,
            rowVersion: definition.IsRowVersion,
            fixedLength: definition.IsFixedLength,
            precision: definition.Precision,
            scale: definition.Scale)
        ?? throw new InvalidOperationException(
            $"No PostgreSQL type mapping exists for '{definition.ClrType.FullName}'.");

    /// <summary>Builds the column facets whose SQL shape depends on the expected column itself.</summary>
    /// <remarks>
    /// Collation and default or generation comparisons render differently per column, so they
    /// cannot be carried as values and stay per-column in the set-oriented form. The remaining
    /// facets are plain values and are compared against the expected row instead.
    /// </remarks>
    /// <param name="definition">The expected column.</param>
    /// <param name="mapping">The already resolved type mapping for that column.</param>
    /// <param name="includeRepairableFacets">Whether the default and generation facet applies.</param>
    /// <returns>The structural predicates for that column.</returns>
    private List<string> ColumnStructuralFacetConditions(
        ExpectedColumnDefinition definition,
        RelationalTypeMapping mapping,
        bool includeRepairableFacets
    ) =>
    [
        CollationMatches(definition),
        includeRepairableFacets
            ? DefaultAndGenerationMatches(definition, mapping)
            : GenerationMatches(definition),
    ];

    /// <summary>Verifies every expected column of a table definition through one catalog join.</summary>
    /// <remarks>
    /// WHY: The per-column classifier repeats the same five-way catalog join once per column, so a
    /// wide table produced dozens of identical joins in one statement and the optimizer planned all
    /// of them. Driving the expected columns as a VALUES list joins the catalog once and keeps every
    /// facet predicate server-side and unchanged, which preserves the collation authority the
    /// catalog comparison relies on.
    /// </remarks>
    /// <param name="definition">The expected table definition whose columns are verified.</param>
    /// <returns>A predicate that is true when every expected column matches at its own position.</returns>
    private string AllColumnsMatch(
        ExpectedTableDefinition definition
    )
    {
        // WHY: Composing every row and arm as its own string and joining them copied the same
        // bytes repeatedly. Appending into one pre-sized buffer keeps those fragments off the heap.
        // WHY: StringBuilder grows by linking chunks instead of copying, so a modest estimate
        // costs nothing when it is exceeded while an inflated one allocates the slack up front.
        var sql = new StringBuilder(512 + (definition.Columns.Count * 256));
        sql.Append("NOT EXISTS (SELECT 1 FROM pg_catalog.pg_class c ")
            .Append("JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace ")
            .Append("CROSS JOIN (VALUES ");

        for (var index = 0; index < definition.Columns.Count; index++)
        {
            var column = definition.Columns[index];
            if (index != 0)
            {
                sql.Append(", ");
            }

            // WHY: The expected name is cast to the catalog's own name type so the comparison
            // resolves exactly as the single-column classifier's unknown-typed literal does.
            // Store type, comment and nullability are plain values and travel with the row, so
            // their predicates appear once instead of once per column.
            sql.Append('(').Append(index + 1)
                .Append(", CAST(").Append(Literal(column.Name)).Append(" AS pg_catalog.name), CAST(")
                .Append(Literal(ColumnStoreType(column, ColumnMapping(column)))).Append(" AS text), CAST(")
                .Append(column.Comment is null ? "NULL" : Literal(column.Comment)).Append(" AS text), CAST(")
                .Append(column.IsNullable ? "TRUE" : "FALSE").Append(" AS boolean))");
        }

        sql.Append(") AS expected(attnum, attname, store_type, comment, is_nullable) ")
            .Append("LEFT JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid ")
            .Append("AND a.attname = expected.attname AND a.attnum > 0 AND NOT a.attisdropped ")
            .Append("LEFT JOIN pg_catalog.pg_type t ON t.oid = a.atttypid ")
            .Append("LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum ")
            .Append("WHERE n.nspname = ").Append(SchemaExpression(definition.Schema))
            .Append(" AND c.relname = ").Append(Literal(definition.Table))
            .Append(" AND (a.attnum IS NULL OR a.attnum <> expected.attnum")

            // WHY: A facet predicate can evaluate to NULL. The per-column form returned no row and
            // therefore reported a mismatch, so the absent-or-unequal test must coalesce to FALSE
            // to keep that verdict instead of silently accepting the column.
            .Append(" OR NOT COALESCE(pg_catalog.format_type(a.atttypid, a.atttypmod) = expected.store_type")
            .Append(" AND pg_catalog.col_description(c.oid, a.attnum) IS NOT DISTINCT FROM expected.comment")
            .Append(" AND CASE WHEN expected.is_nullable THEN NOT a.attnotnull ELSE ")
            .Append(ValidatedNotNullContract())
            .Append(" END, FALSE)")
            .Append(" OR NOT COALESCE(CASE expected.attnum");

        for (var index = 0; index < definition.Columns.Count; index++)
        {
            var column = definition.Columns[index];
            var structural = ColumnStructuralFacetConditions(column, ColumnMapping(column), true);
            sql.Append(" WHEN ").Append(index + 1).Append(" THEN (");
            for (var facet = 0; facet < structural.Count; facet++)
            {
                if (facet != 0)
                {
                    sql.Append(" AND ");
                }

                sql.Append(structural[facet]);
            }

            sql.Append(')');
        }

        sql.Append(" END, FALSE)))");

        return sql.ToString();
    }

    private string ColumnRepairInvariantMatches(
        string table,
        string? schema,
        ExpectedColumnDefinition definition
    ) => ColumnMatches(table, schema, definition, includeRepairableFacets: false);

    /// <summary>Builds a fresh catalog proof that covers the complete row-query relation.</summary>
    private string ColumnNotNullProof(
        string table,
        string? schema,
        string column
    ) => "EXISTS (SELECT 1 FROM pg_catalog.pg_attribute a "
        + "JOIN pg_catalog.pg_class c ON c.oid = a.attrelid "
        + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
        + $"WHERE n.nspname = {SchemaExpression(schema)} AND c.relname = {Literal(table)} "
        + $"AND a.attname = {Literal(column)} AND a.attnum > 0 AND NOT a.attisdropped "
        + $"AND NOT c.relhassubclass AND ({ValidatedNotNullContract()}))";

    /// <summary>Checks the declared nullability without treating an unvalidated constraint as matching.</summary>
    private static string ColumnNullabilityMatches(
        ExpectedColumnDefinition definition
    ) => definition.IsNullable ? "NOT a.attnotnull" : ValidatedNotNullContract();

    // WHY: PostgreSQL 18 allows attnotnull with an unvalidated constraint.
    // JSON field access keeps conenforced safe to parse on PostgreSQL 14-17.
    /// <summary>Checks the enforced and validated local NOT NULL contract on every supported catalog version.</summary>
    private static string ValidatedNotNullContract() => "a.attnotnull AND ("
        + "pg_catalog.current_setting('server_version_num')::integer < 180000 "
        + "OR EXISTS (SELECT 1 FROM pg_catalog.pg_constraint not_null_constraint "
        + "WHERE not_null_constraint.conrelid = c.oid AND not_null_constraint.contype = 'n' "
        + "AND not_null_constraint.conkey = ARRAY[a.attnum] AND not_null_constraint.convalidated "
        + "AND COALESCE((pg_catalog.to_jsonb(not_null_constraint)->>'conenforced')::boolean, FALSE)))";

    private string BuildColumnDiagnosticEvidence(
        string table,
        string? schema,
        ExpectedColumnDefinition definition
    )
    {
        var mapping = _typeMappingSource.FindMapping(
                definition.ClrType,
                definition.StoreType,
                keyOrIndex: false,
                unicode: definition.IsUnicode,
                size: definition.MaxLength,
                rowVersion: definition.IsRowVersion,
                fixedLength: definition.IsFixedLength,
                precision: definition.Precision,
                scale: definition.Scale)
            ?? throw new InvalidOperationException(
                $"No PostgreSQL type mapping exists for '{definition.ClrType.FullName}'.");

        var storeType = ColumnStoreType(definition, mapping);
        var defaultMatches = DefaultAndGenerationMatches(definition, mapping);
        var defaultKind = DefaultKind(definition);
        var expectedDefaultPayload = DefaultDiagnosticPayload(definition, mapping);
        var records = new List<string>
        {
            DiagnosticRecord(
                $"pg_catalog.format_type(a.atttypid, a.atttypmod) <> {Literal(storeType)}",
                "column_store_type",
                Literal(storeType),
                "LEFT(pg_catalog.format_type(a.atttypid, a.atttypmod), 256)"),
            DiagnosticRecord(
                $"NOT ({ColumnNullabilityMatches(definition)})",
                "column_nullability",
                definition.IsNullable ? "'nullable'" : "'not_nullable'",
                $"CASE WHEN {ValidatedNotNullContract()} THEN 'not_nullable' "
                    + "WHEN a.attnotnull THEN 'not_nullable_unvalidated_or_unenforced' ELSE 'nullable' END"),
            DiagnosticRecord(
                $"NOT ({CollationMatches(definition)})",
                "column_collation",
                definition.Collation is null
                    ? "COALESCE((SELECT default_coll.collname FROM pg_catalog.pg_collation default_coll "
                        + "WHERE default_coll.oid = t.typcollation), 'default')"
                    : Literal(definition.Collation.Name),
                "COALESCE(live_coll.collname, 'default')"),
            DiagnosticRecord(
                $"({ActualDefaultKindExpression()}) <> {Literal(defaultKind)}",
                "column_default_kind",
                Literal(defaultKind),
                ActualDefaultKindExpression()),
            DiagnosticRecord(
                $"NOT ({defaultMatches})",
                "column_default_digest",
                $"'md5:' || md5({Literal(expectedDefaultPayload)})",
                "'md5:' || md5(COALESCE(pg_catalog.pg_get_expr(d.adbin, d.adrelid), '<none>'))"),
            DiagnosticRecord(
                $"NOT ({GenerationMatches(definition)})",
                "column_value_generation",
                Literal(ValueGenerationKind(definition)),
                "CASE WHEN a.attidentity <> '' THEN 'identity' WHEN a.attgenerated <> '' THEN 'generated' "
                    + "ELSE 'none' END"),
            DiagnosticRecord(
                "pg_catalog.col_description(c.oid, a.attnum) IS DISTINCT FROM "
                    + (definition.Comment is null ? "NULL" : Literal(definition.Comment)),
                "column_comment_digest",
                $"md5({Literal(definition.Comment ?? string.Empty)})",
                "md5(COALESCE(pg_catalog.col_description(c.oid, a.attnum), ''))"),
        };

        // WHY: The information schema exposes the declared limit and represents
        // unbounded varchar as null. Decoding atttypmod would bind this contract
        // to PostgreSQL's type-specific internal representation.
        var hasVarcharLength = TryParseVarcharLength(storeType, out var targetLength);

        if (hasVarcharLength)
        {
            records.Insert(
                1,
                DiagnosticRecord(
                    $"live_column.character_maximum_length IS DISTINCT FROM {targetLength}",
                    "column_max_length",
                    Literal(targetLength.ToString(CultureInfo.InvariantCulture)),
                    "COALESCE(live_column.character_maximum_length::text, 'unbounded')"));
        }

        return "(SELECT NULLIF(concat_ws(chr(30), "
            + string.Join(", ", records)
            + "), '') FROM pg_catalog.pg_attribute a "
            + "JOIN pg_catalog.pg_class c ON c.oid = a.attrelid "
            + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
            + "JOIN pg_catalog.pg_type t ON t.oid = a.atttypid "
            + (hasVarcharLength
                ? "JOIN information_schema.columns live_column "
                    + "ON live_column.table_schema = n.nspname "
                    + "AND live_column.table_name = c.relname "
                    + "AND live_column.column_name = a.attname "
                : string.Empty)
            + "LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum "
            + "LEFT JOIN pg_catalog.pg_collation live_coll ON live_coll.oid = a.attcollation "
            + $"WHERE n.nspname = {SchemaExpression(schema)} AND c.relname = {Literal(table)} "
            + $"AND a.attname = {Literal(definition.Name)} AND a.attnum > 0 AND NOT a.attisdropped LIMIT 1)";
    }

    private static string DiagnosticRecord(
        string mismatch,
        string facet,
        string expected,
        string actual
    ) => $"CASE WHEN {mismatch} THEN concat_ws(chr(31), '{facet}', {expected}, {actual}) END";

    private static string DefaultKind(
        ExpectedColumnDefinition definition
    )
    {
        if (PostgreSqlSafeMigrationColumnMetadata.GetValueGenerationStrategy(definition)
            is NpgsqlValueGenerationStrategy.IdentityAlwaysColumn
            or NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
        {
            return "identity";
        }

        if (definition.ComputedColumnSql is not null || definition.ComputedExpression is not null)
        {
            return "generated";
        }

        return definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.None ? "none" : "present";
    }

    private string DefaultDiagnosticPayload(
        ExpectedColumnDefinition definition,
        RelationalTypeMapping mapping
    )
    {
        if (PostgreSqlSafeMigrationColumnMetadata.GetValueGenerationStrategy(definition)
            is NpgsqlValueGenerationStrategy.IdentityAlwaysColumn
            or NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
        {
            return DefaultKind(definition);
        }

        if (definition.ComputedColumnSql is not null)
        {
            return definition.ComputedColumnSql;
        }

        if (definition.ComputedExpression is not null)
        {
            return _expressionRenderer.Render(definition.ComputedExpression);
        }

        if (definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.None)
        {
            return "<none>";
        }

        if (definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.Sql)
        {
            return definition.DefaultValue.SqlExpression
                ?? _expressionRenderer.Render(definition.DefaultValue.StructuredExpression!);
        }

        var literal = definition.DefaultValue.GetLiteralValue();

        return literal is null ? "NULL" : mapping.GenerateSqlLiteral(literal);
    }

    private static string ActualDefaultKindExpression() =>
        "CASE WHEN a.attidentity <> '' THEN 'identity' WHEN a.attgenerated <> '' THEN 'generated' "
        + "WHEN d.oid IS NOT NULL THEN 'present' ELSE 'none' END";

    private static string ValueGenerationKind(
        ExpectedColumnDefinition definition
    )
    {
        if (PostgreSqlSafeMigrationColumnMetadata.GetValueGenerationStrategy(definition)
            is NpgsqlValueGenerationStrategy.IdentityAlwaysColumn
            or NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
        {
            return "identity";
        }

        return definition.ComputedColumnSql is not null || definition.ComputedExpression is not null
            ? "generated"
            : "none";
    }

    private PostgreSqlColumnRepairTransition BuildColumnRepairTransition(
        string table,
        string? schema,
        ExpectedColumnDefinition definition,
        bool includeTransitionEvidence,
        ExpectedColumnDefinition? oldDefinition = null
    )
    {
        var storeType = ResolveStoreType(definition);
        if (!TryParseVarcharLength(storeType, out var targetLength)
            || definition.ClrType != typeof(string))
        {
            return PostgreSqlColumnRepairTransition.None;
        }

        var narrowing = "EXISTS (SELECT 1 FROM information_schema.columns narrowing_column "
            + $"WHERE narrowing_column.table_schema = {SchemaExpression(schema)} "
            + $"AND narrowing_column.table_name = {Literal(table)} "
            + $"AND narrowing_column.column_name = {Literal(definition.Name)} "
            + "AND narrowing_column.data_type = 'character varying' "
            + "AND (narrowing_column.character_maximum_length IS NULL "
            + $"OR narrowing_column.character_maximum_length > {targetLength}))";

        return new PostgreSqlColumnRepairTransition(
            PostgreSqlSafeMigrationRuntimePlan.TransitionInvariantPlaceholder,
            PostgreSqlSafeMigrationRuntimePlan.DataProbePlaceholder,
            new PostgreSqlSafeMigrationDataProbe(
                table,
                schema,
                definition.Name,
                targetLength,
                Qualified(table, schema),
                _sqlGenerationHelper.DelimitIdentifier(definition.Name),
                includeTransitionEvidence
                    ? (oldDefinition is null
                        ? string.Empty
                        : $"({ColumnMatches(table, schema, oldDefinition)}) AND ")
                        + ColumnWithInvariantExists(
                            table,
                            schema,
                            definition.Name,
                            "t.typname = 'varchar' "
                            + $"AND live_column.character_maximum_length IS DISTINCT FROM {targetLength} "
                            + $"AND {CollationMatches(definition)} "
                            + $"AND {GenerationMatches(definition)}")
                    : null,
                narrowing),
            SafeMigrationOperationalImpact.TableRewritePossible);
    }

    private string ColumnWithInvariantExists(
        string table,
        string? schema,
        string column,
        string invariant
    ) => "EXISTS (SELECT 1 FROM pg_catalog.pg_attribute a "
        + "JOIN pg_catalog.pg_class c ON c.oid = a.attrelid "
        + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
        + "JOIN pg_catalog.pg_type t ON t.oid = a.atttypid "
        + "JOIN information_schema.columns live_column "
        + "ON live_column.table_schema = n.nspname "
        + "AND live_column.table_name = c.relname "
        + "AND live_column.column_name = a.attname "
        + $"WHERE n.nspname = {SchemaExpression(schema)} AND c.relname = {Literal(table)} "
        + $"AND a.attname = {Literal(column)} AND a.attnum > 0 AND NOT a.attisdropped "
        + $"AND ({invariant}))";

    private string ResolveStoreType(
        ExpectedColumnDefinition definition
    ) => definition.StoreType
        ?? _typeMappingSource.FindMapping(
                definition.ClrType,
                definition.StoreType,
                keyOrIndex: false,
                unicode: definition.IsUnicode,
                size: definition.MaxLength,
                rowVersion: definition.IsRowVersion,
                fixedLength: definition.IsFixedLength,
                precision: definition.Precision,
                scale: definition.Scale)
            ?.StoreType
        ?? throw new InvalidOperationException(
            $"No PostgreSQL type mapping exists for '{definition.ClrType.FullName}'.");

    private static bool TryParseVarcharLength(
        string storeType,
        out int length
    )
    {
        var value = storeType.AsSpan().Trim();
        var prefixLength = value.StartsWith("character varying(", StringComparison.OrdinalIgnoreCase)
            ? "character varying(".Length
            : value.StartsWith("varchar(", StringComparison.OrdinalIgnoreCase)
                ? "varchar(".Length
                : 0;

        if (prefixLength == 0
            || value.Length <= prefixLength
            || value[^1] != ')'
            || !int.TryParse(value[prefixLength..^1], CultureInfo.InvariantCulture, out length))
        {
            length = 0;

            return false;
        }

        return length > 0;
    }

    private sealed record PostgreSqlColumnRepairTransition(
        string InvariantExpression,
        string DataBlockedExpression,
        PostgreSqlSafeMigrationDataProbe? DataProbe,
        SafeMigrationOperationalImpact OperationalImpact
    )
    {
        public bool HasDataProbe => DataProbe is not null;

        public static PostgreSqlColumnRepairTransition None { get; } = new(
            "FALSE",
            "FALSE",
            DataProbe: null,
            SafeMigrationOperationalImpact.NotApplicable);
    }

    private static string GenerationMatches(
        ExpectedColumnDefinition definition
    ) => definition.ComputedColumnSql is null
        && definition.ComputedExpression is null
        && PostgreSqlSafeMigrationColumnMetadata.GetValueGenerationStrategy(definition)
            is null or NpgsqlValueGenerationStrategy.None
            ? "a.attgenerated = '' AND a.attidentity = ''"
            : "FALSE";

    private string CollationMatches(
        ExpectedColumnDefinition definition
    )
    {
        if (definition.Collation is null)
        {
            return "a.attcollation = t.typcollation";
        }

        var expected = "(SELECT coll.oid FROM pg_catalog.pg_collation coll "
            + "JOIN pg_catalog.pg_namespace ns ON ns.oid = coll.collnamespace "
            + $"WHERE coll.collname = {Literal(definition.Collation.Name)} "
            + (definition.Collation.Schema is null
                ? $"AND pg_catalog.pg_collation_is_visible(coll.oid) LIMIT 1)"
                : $"AND ns.nspname = {Literal(definition.Collation.Schema)})");

        return $"{expected} IS NOT NULL AND a.attcollation = {expected}";
    }

    private string DefaultAndGenerationMatches(
        ExpectedColumnDefinition definition,
        RelationalTypeMapping mapping
    )
    {
        var strategy = PostgreSqlSafeMigrationColumnMetadata.GetValueGenerationStrategy(definition);

        if (strategy == NpgsqlValueGenerationStrategy.IdentityAlwaysColumn)
        {
            return "a.attgenerated = '' AND a.attidentity = 'a'";
        }

        if (strategy == NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
        {
            return "a.attgenerated = '' AND a.attidentity = 'd'";
        }

        if (definition.ComputedColumnSql is not null
            || definition.ComputedExpression is not null)
        {
            var generation = definition.IsStored == false ? "'v'" : "'s'";
            return $"a.attidentity = '' AND a.attgenerated = {generation} AND d.oid IS NOT NULL AND "
                + (definition.ComputedExpression is not null
                    ? ExpressionMatches("pg_catalog.pg_get_expr(d.adbin, d.adrelid)", definition.ComputedExpression)
                    : ExpressionMatches("pg_catalog.pg_get_expr(d.adbin, d.adrelid)", definition.ComputedColumnSql!));
        }

        if (definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.None)
        {
            return "a.attidentity = '' AND a.attgenerated = '' AND d.oid IS NULL";
        }

        var expected = definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.Sql
            ? definition.DefaultValue.SqlExpression
            ?? _expressionRenderer.Render(definition.DefaultValue.StructuredExpression!)
            : mapping.GenerateSqlLiteral(definition.DefaultValue.GetLiteralValue());

        var expressionMatches = definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.Literal
            ? LiteralDefaultMatches(
                "pg_catalog.pg_get_expr(d.adbin, d.adrelid)",
                expected,
                definition.DefaultValue.GetLiteralValue())
            : definition.DefaultValue.StructuredExpression is not null
                ? ExpressionMatches(
                    "pg_catalog.pg_get_expr(d.adbin, d.adrelid)",
                    definition.DefaultValue.StructuredExpression)
                : ExpressionMatches("pg_catalog.pg_get_expr(d.adbin, d.adrelid)", expected);

        return "a.attidentity = '' AND a.attgenerated = '' AND d.oid IS NOT NULL AND " + expressionMatches;
    }

    private bool CanMap(
        ExpectedColumnDefinition definition
    )
    {
        try
        {
            return _typeMappingSource.FindMapping(
                definition.ClrType,
                definition.StoreType,
                keyOrIndex: false,
                unicode: definition.IsUnicode,
                size: definition.MaxLength,
                rowVersion: definition.IsRowVersion,
                fixedLength: definition.IsFixedLength,
                precision: definition.Precision,
                scale: definition.Scale) is not null;
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or InvalidOperationException
                                              or NotSupportedException)
        {
            return false;
        }
    }

    private string ColumnExists(
        string table,
        string? schema,
        string column
    ) => "EXISTS (SELECT 1 FROM pg_catalog.pg_attribute a "
        + "JOIN pg_catalog.pg_class c ON c.oid = a.attrelid "
        + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
        + $"WHERE n.nspname = {SchemaExpression(schema)} AND c.relname = {Literal(table)} "
        + $"AND a.attname = {Literal(column)} AND a.attnum > 0 AND NOT a.attisdropped)";

    private string LiteralDefaultMatches(
        string catalogExpression,
        string expected,
        object? value
    )
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal) { expected };
        AddLiteralDefaultCandidates(candidates, value);
        var conditions = new List<string>();
        foreach (var candidate in candidates)
        {
            var castPrefix = Literal($"{candidate}::");
            var castExpression = $"({castPrefix} || pg_catalog.format_type(a.atttypid, NULL))";

            conditions.Add($"{catalogExpression} = {Literal(candidate)}");
            conditions.Add($"{catalogExpression} = {Literal($"({candidate})")}");
            conditions.Add($"{catalogExpression} = {castExpression}");
            conditions.Add($"{catalogExpression} = ('(' || {castExpression} || ')')");
        }

        return $"({string.Join(" OR ", conditions)})";
    }

    private static void AddLiteralDefaultCandidates(
        HashSet<string> candidates,
        object? value
    )
    {
        switch (value)
        {
            case null:
                candidates.Add("NULL");
                break;
            case bool boolean:
                candidates.Add(boolean ? "true" : "false");
                break;
            case byte[] bytes:
                candidates.Add($"'\\x{Convert.ToHexStringLower(bytes)}'");
                break;
            case string text:
                candidates.Add(QuoteStoredLiteral(text));
                break;
            case char character:
                var charLiteral = QuoteStoredLiteral(character.ToString());
                candidates.Add(charLiteral);
                candidates.Add($"{charLiteral}::bpchar");
                break;
            case Guid guid:
                candidates.Add(QuoteStoredLiteral(guid.ToString("D")));
                break;
            case DateOnly date:
                candidates.Add(QuoteStoredLiteral(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                break;
            case TimeOnly time:
                candidates.Add(QuoteStoredLiteral(time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
                candidates.Add(QuoteStoredLiteral(time.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture)));
                break;
            case DateTime dateTime:
                candidates.Add(
                    QuoteStoredLiteral(dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
                candidates.Add(
                    QuoteStoredLiteral(dateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)));
                break;
            case DateTimeOffset dateTimeOffset:
                var utc = dateTimeOffset.ToUniversalTime();
                candidates.Add(
                    QuoteStoredLiteral(utc.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture)));
                candidates.Add(
                    QuoteStoredLiteral(utc.ToString("yyyy-MM-dd HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture)));
                candidates.Add(QuoteStoredLiteral(utc.ToString("yyyy-MM-dd HH:mm:sszz", CultureInfo.InvariantCulture)));
                candidates.Add(
                    QuoteStoredLiteral(utc.ToString("yyyy-MM-dd HH:mm:ss.ffffffzz", CultureInfo.InvariantCulture)));
                break;
            case TimeSpan timeSpan:
                candidates.Add(QuoteStoredLiteral(timeSpan.ToString("c", CultureInfo.InvariantCulture)));
                candidates.Add(QuoteStoredLiteral(timeSpan.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)));
                break;
            case Enum enumeration:
                candidates.Add(
                    Convert.ToString(
                        Convert.ChangeType(
                            enumeration,
                            Enum.GetUnderlyingType(enumeration.GetType()),
                            CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture)!);
                break;
            default:
                var invariant = Convert.ToString(value, CultureInfo.InvariantCulture);
                if (invariant is not null)
                {
                    candidates.Add(invariant);
                    AddNumericParserCandidates(candidates, value, invariant);
                }

                break;
        }
    }

    private static void AddNumericParserCandidates(
        HashSet<string> candidates,
        object value,
        string invariant
    )
    {
        string[] casts = value switch
        {
            sbyte or byte or short or ushort => ["smallint", "integer",],
            int or uint => ["integer", "bigint",],
            long or ulong => ["bigint", "numeric",],
            decimal => ["numeric"],
            float => ["real", "double precision",],
            double => ["double precision", "numeric",],
            _ => [],
        };

        if (casts.Length == 0)
        {
            return;
        }

        var quoted = QuoteStoredLiteral(invariant);
        candidates.Add(quoted);
        foreach (var cast in casts)
        {
            candidates.Add($"{quoted}::{cast}");
        }
    }

    private static string QuoteStoredLiteral(
        string value
    ) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
