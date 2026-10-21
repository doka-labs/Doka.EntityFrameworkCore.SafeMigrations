namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    private bool TableFixedLayoutIsSupported(
        ExpectedTableDefinition definition
    )
    {
        var fixedBytes = 0;
        var bitColumns = 0;
        foreach (var column in definition.Columns)
        {
            if (!TryGetColumnStorageLayout(column, out var storage))
            {
                return false;
            }

            fixedBytes += storage.FixedBytes;
            bitColumns += storage.IsBit ? 1 : 0;
        }

        // WHY: CREATE admission rejects the fixed data, packed bits, header,
        // and null bitmap above 8,060 bytes. Variable payloads and offsets can
        // yield a maximum-row warning without preventing CREATE, so this is
        // deliberately not a claim that every future row value will fit.
        var minimumRowBytes = fixedBytes + (bitColumns + 7) / 8
            + 4 + 2 + (definition.Columns.Count + 7) / 8;

        return minimumRowBytes <= 8060;
    }

    private SqlServerSafeMigrationRuntimePlan BuildEnsureTable(
        EnsureTableIntent intent,
        SafeMigrationExpectedTableConstraints? expectedTableConstraints,
        bool targetTableKnownAbsent
    )
    {
        var definition = intent.Definition;
        var table = TableExists(definition.Table, definition.Schema);
        var occupied = ObjectExists(definition.Table, definition.Schema);
        // WHY: The analysis-only occupancy probe proves absence, not a matching definition. Avoid
        // compiling the full matcher for thousands of missing tables. A concurrent new occupant
        // is still Different; every schema, authored-facet and inline-FK guard remains in the plan.
        var matching = targetTableKnownAbsent ? "1 = 0" : intent.Mode == SafeMigrationTableMode.ConvergenceContainer
            ? table
            : TableMatches(definition, expectedTableConstraints);

        var execution = targetTableKnownAbsent ? matching : intent.Mode == SafeMigrationTableMode.ConvergenceContainer
            ? table
            : TableMatches(definition, expectedTableConstraints: null);

        var stamps = definition.Columns
            .Where(static column => column.DefaultValue.Kind != SafeMigrationDefaultValueKind.None)
            .Select(column => BuildDefaultStampSql(definition.Table, definition.Schema, column))
            .Concat(definition.CheckConstraints.Select(BuildCheckStampSql))
            .ToArray();

        var preamble = SqlServerForeignKeySafety.BuildCatalogPreamble(
            definition.ForeignKeys, Literal, onlyWhenTableMissing: true);

        List<ExpectedColumnDefinition>? computed = null;
        var simpleAbsentClassifier = targetTableKnownAbsent && definition.PrimaryKey is null
            && definition.UniqueConstraints.Count == 0 && definition.CheckConstraints.Count == 0
            && definition.ForeignKeys.Count == 0;

        foreach (var column in definition.Columns)
        {
            var isComputed = SqlServerComputedColumnContract.IsComputed(column);
            simpleAbsentClassifier &= !isComputed && column.Collation is null
                && column.DefaultValue.Kind == SafeMigrationDefaultValueKind.None;

            if (isComputed)
            {
                (computed ??= []).Add(column);
            }
        }

        var computedPreamble = computed is null ? null
            : BuildComputedColumnPreamble(definition.Table, definition.Schema, computed, definition);

        var computedSupport = computed is null ? "1 = 1" : string.Join(" AND ", computed.Select(column
            => ComputedVariable(column, "support") + " = 1"));

        var computedIdentityFailure = computed is null ? "1 = 0" : string.Join(" OR ", computed.Select(column
            => ComputedVariable(column, "support") + " = -1"));

        var computedKeys = computed is null ? null : ComputedInlineKeySupport(definition);
        if (computedKeys is not null)
        {
            computedSupport += " AND (" + computedKeys + ")";
        }

        var topologyGuard = preamble is null ? string.Empty
            : $"WHEN NOT {occupied} AND NOT ({SqlServerForeignKeySafety.GraphSafePredicate}) "
                + "THEN N'prerequisite_missing' ";

        var inlinePhysical = SqlServerForeignKeySafety.BuildInlinePhysicalPredicate(definition, Literal);
        var inlinePrerequisites = definition.ForeignKeys.Count == 0 ? "1 = 1"
            : string.Join(" AND ", definition.ForeignKeys.Select(foreignKey =>
                "(" + BuildInlineForeignKeyPrerequisite(definition, foreignKey) + ")"));

        return Plan(
            "CASE " + topologyGuard
            + $"WHEN NOT ({computedSupport}) THEN N'unsupported' "
            + $"WHEN NOT {occupied} AND NOT ({inlinePhysical}) THEN N'prerequisite_missing' "
            + $"WHEN NOT {occupied} AND NOT ({inlinePrerequisites}) THEN N'prerequisite_missing' "
            + $"WHEN NOT {occupied} THEN N'missing' WHEN NOT {table} THEN N'different' "
            + $"WHEN {matching} THEN N'matching' ELSE N'different' END",
            Bit(matching)) with
        {
            // WHY: Only the occupancy-certified ordinary missing fast path has
            // no definition/default/collation/inline-constraint compiler work.
            // Its inexpensive 32-way UNION need not inherit the complex budget.
            IsSimpleAbsentTableClassifier = simpleAbsentClassifier
                && preamble is null && computedPreamble is null,
            ExecutionPostcondition = Bit(execution),
            CatalogPreambleSql = computedPreamble is null ? preamble
                : preamble is null ? computedPreamble : preamble + "\n" + computedPreamble,
            PostconditionPreambleSql = computedPreamble,
            ClassificationCodeExpression = $"CASE WHEN ({computedIdentityFailure}) "
                + "THEN N'computed_column_operand_identity_unproven' "
                + $"WHEN NOT ({computedSupport}) "
                + "THEN N'computed_column_native_type_unproven' "
                + $"WHEN NOT {occupied} AND NOT ({inlinePrerequisites}) "
                + "THEN N'inline_foreign_key_prerequisite' "
                + (preamble is null ? string.Empty
                    : $"WHEN NOT ({SqlServerForeignKeySafety.GraphSafePredicate}) "
                        + "THEN N'foreign_key_cascade_topology' ")
                + "ELSE NULL END",
            PostApplySql = stamps.Length == 0 ? null : string.Join("; ", stamps),
        };
    }

    private static string? ComputedInlineKeySupport(ExpectedTableDefinition definition)
    {
        var predicates = new List<string>();
        if (definition.PrimaryKey is { } primary)
        {
            Add(primary.Columns, primary: true);
        }

        foreach (var unique in definition.UniqueConstraints)
        {
            Add(unique.Columns, primary: false);
        }

        return predicates.Count == 0 ? null : string.Join(" AND ", predicates)
            + " AND (" + ComputedIndexSessionPredicate + ")";

        void Add(IReadOnlyList<string> names, bool primary)
        {
            foreach (var name in names)
            {
                var column = definition.Columns.First(value => value.Name == name);
                if (!SqlServerComputedColumnContract.IsComputed(column))
                {
                    continue;
                }

                predicates.Add(ComputedVariable(column, "indexable") + "=1");
                if (primary)
                {
                    // WHY: A logical finite non-null proof does not turn the
                    // native nullable catalog facet into a legal primary-key operand.
                    predicates.Add(ComputedVariable(column, "nullable") + "=0");
                }
            }
        }
    }

    private SqlServerSafeMigrationRuntimePlan BuildDropTable(DropTableIntent intent)
    {
        var table = TableExists(intent.Table, intent.Schema);
        var occupied = ObjectExists(intent.Table, intent.Schema);
        var incomingForeignKey = $"EXISTS (SELECT 1 FROM sys.foreign_keys fk WHERE fk.referenced_object_id = "
            + $"{TableId(intent.Table, intent.Schema)} "
            + $"AND fk.parent_object_id <> {TableId(intent.Table, intent.Schema)})";

        var otherDependency = $"EXISTS (SELECT 1 FROM sys.sql_expression_dependencies d "
            + $"WHERE d.referenced_id = {TableId(intent.Table, intent.Schema)} "
            + $"AND d.referencing_id <> {TableId(intent.Table, intent.Schema)} "
            + "AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys fk WHERE fk.object_id = d.referencing_id "
            + $"AND fk.referenced_object_id = {TableId(intent.Table, intent.Schema)})) "
            + $"OR EXISTS (SELECT 1 FROM sys.tables t WHERE t.object_id = {TableId(intent.Table, intent.Schema)} "
            + "AND (t.temporal_type <> 0 OR t.is_filetable = 1))";

        return Plan(
            $"CASE WHEN NOT {occupied} THEN N'missing' WHEN NOT {table} OR {incomingForeignKey} OR {otherDependency} "
            + "THEN N'different' ELSE N'matching' END",
            Bit($"NOT {occupied}")) with
        {
            RequiresExpressionDependencyRead = true,
            // WHY: Projection may discharge incoming FKs through an earlier
            // accepted dependent/FK DROP, but must preserve every independent
            // view, temporal, FileTable, or wrong-object-kind conflict.
            ClassificationCodeExpression = $"CASE WHEN {table} AND {incomingForeignKey} "
                + $"AND NOT ({otherDependency}) THEN N'incoming_foreign_key_dependency' ELSE NULL END",
        };
    }

    private SqlServerSafeMigrationRuntimePlan BuildRenameTable(RenameTableIntent intent)
    {
        var source = TableExists(intent.Name, intent.Schema);
        var sourceOccupied = ObjectExists(intent.Name, intent.Schema);
        if ((intent.NewName ?? intent.Name) == intent.Name
            && EffectiveSchema(intent.NewSchema ?? intent.Schema) == EffectiveSchema(intent.Schema))
        {
            // WHY: An explicitly repeated identity is not an occupied rename
            // target. Its existing table already satisfies the postcondition;
            // never invoke sp_rename or TRANSFER for this no-op contract.
            return Plan(
                $"CASE WHEN NOT {sourceOccupied} THEN N'missing' WHEN {source} "
                + "THEN N'matching' ELSE N'different' END",
                Bit($"NOT {sourceOccupied} OR {source}"));
        }

        var target = ObjectExists(intent.NewName ?? intent.Name, intent.NewSchema ?? intent.Schema);
        // WHY: EF renames within the source schema before transferring the
        // renamed table. A free final name does not prove that intermediate
        // schema/name is available when both parts change.
        var intermediate = (intent.NewName ?? intent.Name) != intent.Name
            && EffectiveSchema(intent.NewSchema ?? intent.Schema) != EffectiveSchema(intent.Schema)
                ? " OR " + ObjectExists(intent.NewName ?? intent.Name, intent.Schema) : string.Empty;

        var dependent = $"EXISTS (SELECT 1 FROM sys.sql_expression_dependencies d "
            + $"WHERE d.referenced_id = {TableId(intent.Name, intent.Schema)} "
            + $"AND d.referencing_id <> {TableId(intent.Name, intent.Schema)})";

        return Plan(
            $"CASE WHEN NOT {sourceOccupied} THEN N'missing' "
            + $"WHEN NOT {source} OR {target}{intermediate} OR {dependent} "
            + "THEN N'different' ELSE N'matching' END",
            Bit($"NOT {sourceOccupied} AND {TableExists(
                intent.NewName ?? intent.Name, intent.NewSchema ?? intent.Schema)}")) with
        {
            RequiresExpressionDependencyRead = true,
        };
    }

    /// <summary>Compares table columns and bounded allowed or required constraint contracts.</summary>
    private string TableMatches(
        ExpectedTableDefinition definition,
        SafeMigrationExpectedTableConstraints? expectedTableConstraints
    )
    {
        var allowedPrimaryKeys = expectedTableConstraints?.AllowedPrimaryKeys
            ?? (definition.PrimaryKey is null ? [] : [definition.PrimaryKey]);

        var primaryMayBeAbsent = expectedTableConstraints?.PrimaryKeyMayBeAbsent
            ?? definition.PrimaryKey is null;

        var allowedUnique = expectedTableConstraints?.AllowedUniqueConstraints
            ?? definition.UniqueConstraints;

        var requiredUnique = expectedTableConstraints?.RequiredUniqueConstraints
            ?? definition.UniqueConstraints;

        var allowedChecks = expectedTableConstraints?.AllowedCheckConstraints
            ?? definition.CheckConstraints;

        var requiredChecks = expectedTableConstraints?.RequiredCheckConstraints
            ?? definition.CheckConstraints;

        var allowedForeignKeys = expectedTableConstraints?.AllowedForeignKeys
            ?? definition.ForeignKeys;

        var requiredForeignKeys = expectedTableConstraints?.RequiredForeignKeys
            ?? definition.ForeignKeys;

        var conditions = new List<string>
        {
            TableExists(definition.Table, definition.Schema),
            $"(SELECT COUNT(*) FROM sys.columns c WHERE c.object_id = {TableId(definition.Table, definition.Schema)}) "
            + $"= {definition.Columns.Count.ToString(CultureInfo.InvariantCulture)}",
            TableColumnsMatch(definition),
            NoUnexpectedConstraints(definition.Table, definition.Schema, "sys.key_constraints",
                "constraint_object.type = 'PK'",
                allowedPrimaryKeys.Select(static value => value.Name)),
            NoUnexpectedConstraints(definition.Table, definition.Schema, "sys.key_constraints",
                "constraint_object.type = 'UQ'",
                allowedUnique.Select(static value => value.Name)),
            NoUnexpectedConstraints(definition.Table, definition.Schema, "sys.check_constraints", "1 = 1",
                allowedChecks.Select(static value => value.Name)),
            NoUnexpectedConstraints(definition.Table, definition.Schema, "sys.foreign_keys", "1 = 1",
                allowedForeignKeys.Select(static value => value.Name)),
        };

        foreach (var primaryKey in allowedPrimaryKeys)
        {
            var matching = KeyMatches(definition.Table, definition.Schema, primaryKey.Name,
                primaryKey.Columns, "PK");

            conditions.Add(primaryMayBeAbsent
                ? $"(NOT {KeyExists(definition.Table, definition.Schema, primaryKey.Name, "PK")} OR {matching})"
                : matching);
        }

        foreach (var unique in allowedUnique)
        {
            var matching = KeyMatches(definition.Table, definition.Schema, unique.Name, unique.Columns, "UQ");
            conditions.Add(requiredUnique.Any(value => value.Name == unique.Name)
                ? matching
                : $"(NOT {KeyExists(definition.Table, definition.Schema, unique.Name, "UQ")} OR {matching})");
        }

        foreach (var foreignKey in allowedForeignKeys)
        {
            var matching = ForeignKeyMatches(foreignKey);
            conditions.Add(requiredForeignKeys.Any(value => value.Name == foreignKey.Name)
                ? matching
                : $"(NOT {ForeignKeyExists(definition.Table, definition.Schema, foreignKey.Name)} OR {matching})");
        }

        foreach (var check in allowedChecks)
        {
            var matching = CheckMatches(check);
            conditions.Add(requiredChecks.Any(value => value.Name == check.Name)
                ? matching
                : $"(NOT {CheckExists(definition.Table, definition.Schema, check.Name)} OR {matching})");
        }

        return $"({string.Join(" AND ", conditions)})";
    }

    private string NoUnexpectedConstraints(
        string table,
        string? schema,
        string catalog,
        string filter,
        IEnumerable<string> allowedNames
    )
    {
        var names = allowedNames.ToArray();
        var allowed = names.Length == 0
            ? "1 = 0"
            : $"constraint_object.name IN ({string.Join(", ", names.Select(Literal))})";

        return $"NOT EXISTS (SELECT 1 FROM {catalog} constraint_object "
            + $"WHERE constraint_object.parent_object_id = {TableId(table, schema)} "
            + $"AND {filter} AND NOT ({allowed}))";
    }
}
