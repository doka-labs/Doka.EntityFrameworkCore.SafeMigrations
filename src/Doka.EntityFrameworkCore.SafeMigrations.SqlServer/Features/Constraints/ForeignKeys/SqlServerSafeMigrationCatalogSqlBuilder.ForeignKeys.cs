namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    private SqlServerSafeMigrationRuntimePlan BuildEnsureForeignKey(EnsureForeignKeyIntent intent)
    {
        var definition = intent.Definition;
        var occupied = ConstraintNameOccupiedInSchema(definition.Schema, definition.Name);
        var matching = ForeignKeyMatches(definition);
        var joins = string.Join(" AND ", definition.Columns.Select((column, ordinal) =>
            $"doka_dependent.{Delimited(column)} = doka_principal.{Delimited(definition.PrincipalColumns[ordinal])}"));

        var required = string.Join(" AND ", definition.Columns.Select(column =>
            $"doka_dependent.{Delimited(column)} IS NOT NULL"));

        var orphan = $"EXISTS (SELECT TOP (1) 1 FROM {QualifiedTable(definition.Table, definition.Schema)} "
            + "doka_dependent LEFT JOIN "
            + $"{QualifiedTable(definition.PrincipalTable, definition.PrincipalSchema)} doka_principal "
            + $"ON {joins} WHERE {required} "
            + $"AND doka_principal.{Delimited(definition.PrincipalColumns[0])} IS NULL)";

        var preamble = SqlServerForeignKeySafety.BuildCatalogPreamble([definition], Literal);
        var topologyGuard = preamble is null ? string.Empty
            : $"WHEN NOT ({SqlServerForeignKeySafety.GraphSafePredicate}) THEN N'prerequisite_missing' ";

        return Plan(
            $"CASE WHEN NOT {TableExists(definition.Table, definition.Schema)} THEN N'prerequisite_missing' "
            + $"WHEN {matching} THEN N'matching' WHEN {occupied} THEN N'different' "
            + $"WHEN {orphan} THEN N'data_blocked' " + topologyGuard + "ELSE N'missing' END",
            Bit(matching)) with
        {
            RequiresDelayedBinding = true,
            CatalogPreambleSql = preamble,
            // WHY: A later accepted FK drop can remove an immutable live
            // topology conflict. This code distinguishes that bounded repair
            // from missing storage prerequisites; reaching it also proves
            // the preceding orphan probe found no invalid rows.
            ClassificationCodeExpression = preamble is null ? null
                : $"CASE WHEN NOT ({SqlServerForeignKeySafety.GraphSafePredicate}) "
                    + "THEN N'foreign_key_cascade_topology' ELSE NULL END",
            MatchedObjectNameExpression = $"CASE WHEN {matching} THEN {Literal(definition.Name)} ELSE NULL END",
        };
    }

    /// <summary>Proves parent keys, physical storage, and action prerequisites before any orphan probe.</summary>
    private string ForeignKeyPrerequisites(ExpectedForeignKeyDefinition definition)
    {
        if (definition.Columns.Count > 32)
        {
            return "1 = 0";
        }

        var dependent = TableId(definition.Table, definition.Schema);
        var principal = TableId(definition.PrincipalTable, definition.PrincipalSchema);
        var values = string.Join(", ", definition.Columns.Select((column, ordinal) =>
            $"({ordinal + 1}, {Literal(column)}, {Literal(definition.PrincipalColumns[ordinal])})"));

        var targetKey = "EXISTS (SELECT 1 FROM sys.indexes i "
            + $"WHERE i.object_id = {principal} AND i.is_unique = 1 AND i.is_disabled = 0 "
            + "AND i.is_hypothetical = 0 AND i.has_filter = 0 "
            + $"AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id = i.object_id "
            + $"AND ic.index_id = i.index_id AND ic.key_ordinal > 0) = {definition.Columns.Count} "
            + $"AND NOT EXISTS (SELECT 1 FROM (VALUES {values}) expected(ordinal, dependent, principal) "
            + "WHERE NOT EXISTS (SELECT 1 FROM sys.index_columns ic "
            + "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id "
            + "WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id "
            + "AND ic.key_ordinal = expected.ordinal AND c.name = expected.principal)))";

        var types = $"NOT EXISTS (SELECT 1 FROM (VALUES {values}) expected(ordinal, dependent, principal) "
            + "JOIN sys.columns dc ON dc.object_id = " + dependent + " AND dc.name = expected.dependent "
            + "JOIN sys.columns pc ON pc.object_id = " + principal + " AND pc.name = expected.principal "
            + "WHERE dc.system_type_id <> pc.system_type_id "
            + "OR dc.max_length <> pc.max_length OR dc.precision <> pc.precision "
            + "OR dc.scale <> pc.scale OR ISNULL(dc.collation_name, N'') <> ISNULL(pc.collation_name, N'') "
            + (definition.OnDelete == ReferentialAction.SetNull
                || definition.OnUpdate == ReferentialAction.SetNull
                ? "OR dc.is_nullable = 0 "
                : string.Empty)
            + ")";

        // WHY: SQL Server requires a real candidate key and physically
        // compatible columns; row probes cannot make malformed FKs safe.

        return $"({TableAndColumnsExist(definition.Table, definition.Schema, definition.Columns)}) "
            + $"AND ({TableAndColumnsExist(
                definition.PrincipalTable, definition.PrincipalSchema, definition.PrincipalColumns)}) "
            + $"AND ({IndexKeysWithinLimit(definition.Table, definition.Schema, definition.Columns, 900)}) "
            + $"AND ({IndexKeysWithinLimit(
                definition.PrincipalTable, definition.PrincipalSchema, definition.PrincipalColumns, 900)}) "
            + $"AND {targetKey} AND {types} AND ({SqlServerForeignKeySafety.BuildPhysicalPredicate(
                definition, Literal)})";
    }

    /// <summary>Proves authored inline FK arity and byte width before live or projected parent validation.</summary>
    /// <param name="table">The authored dependent table.</param>
    /// <param name="foreignKey">The dependent and principal column contract.</param>
    /// <returns>Whether both authored sides are within SQL Server's 32-column and 900-byte bounds.</returns>
    internal bool InlineForeignKeyPhysicalWidthIsSupported(
        ExpectedTableDefinition table,
        ExpectedForeignKeyDefinition foreignKey
    )
    {
        if (!InlineKeyWidthIsSupported(table, foreignKey.Columns, 900))
        {
            return false;
        }

        if (foreignKey.PrincipalTable != table.Table
            || (foreignKey.PrincipalSchema ?? "dbo") != (table.Schema ?? "dbo"))
        {
            return true;
        }

        // WHY: A self-reference has no live principal catalog yet. Its
        // authored parent storage must not evade the same physical limit.

        return foreignKey.PrincipalColumns.All(name => table.Columns.Any(column => column.Name == name))
            && InlineKeyWidthIsSupported(table, foreignKey.PrincipalColumns, 900);
    }

    /// <summary>Proves inline parent storage and a candidate key before creating the dependent table.</summary>
    /// <param name="table">The authored dependent table.</param>
    /// <param name="foreignKey">One inline foreign-key definition.</param>
    /// <returns>A catalog-only Boolean predicate, with no reference to absent dependent rows.</returns>
    internal string BuildInlineForeignKeyPrerequisite(
        ExpectedTableDefinition table,
        ExpectedForeignKeyDefinition foreignKey
    )
    {
        if (!InlineForeignKeyPhysicalWidthIsSupported(table, foreignKey))
        {
            return "1 = 0";
        }

        if (foreignKey.PrincipalTable == table.Table
            && (foreignKey.PrincipalSchema ?? "dbo") == (table.Schema ?? "dbo"))
        {
            var hasKey = table.PrimaryKey?.Columns.SequenceEqual(foreignKey.PrincipalColumns) == true
                || table.UniqueConstraints.Any(key => key.Columns.SequenceEqual(foreignKey.PrincipalColumns));

            var compatible = foreignKey.Columns.Select((name, ordinal) =>
                ColumnStorageEquals(table.Columns.First(column => column.Name == name),
                    table.Columns.First(column => column.Name == foreignKey.PrincipalColumns[ordinal])))
                .All(static value => value);

            return hasKey && compatible ? "1 = 1" : "1 = 0";
        }

        var principal = TableId(foreignKey.PrincipalTable, foreignKey.PrincipalSchema);
        var values = string.Join(", ", foreignKey.PrincipalColumns.Select((column, ordinal) =>
            $"({ordinal + 1}, {Literal(column)})"));

        var targetKey = "EXISTS (SELECT 1 FROM sys.indexes i "
            + $"WHERE i.object_id = {principal} AND i.is_unique = 1 AND i.is_disabled = 0 "
            + "AND i.is_hypothetical = 0 AND i.has_filter = 0 "
            + $"AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id = i.object_id "
            + $"AND ic.index_id = i.index_id AND ic.key_ordinal > 0) = {foreignKey.PrincipalColumns.Count} "
            + $"AND NOT EXISTS (SELECT 1 FROM (VALUES {values}) expected(ordinal, principal) "
            + "WHERE NOT EXISTS (SELECT 1 FROM sys.index_columns ic "
            + "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id "
            + "WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id "
            + "AND ic.key_ordinal = expected.ordinal AND c.name = expected.principal)))";

        var predicates = new List<string>(foreignKey.Columns.Count + 3)
        {
            TableAndColumnsExist(foreignKey.PrincipalTable, foreignKey.PrincipalSchema, foreignKey.PrincipalColumns),
            IndexKeysWithinLimit(
                foreignKey.PrincipalTable, foreignKey.PrincipalSchema, foreignKey.PrincipalColumns, 900),
            targetKey,
        };

        for (var ordinal = 0; ordinal < foreignKey.Columns.Count; ordinal++)
        {
            var column = table.Columns.First(value => value.Name == foreignKey.Columns[ordinal]);
            if (!TryGetColumnType(column, out var type))
            {
                return "1 = 0";
            }

            var conditions = new List<string>
            {
                $"ty.name = {Literal(type.Name)}",
                "ty.is_user_defined = 0",
                "ty.is_assembly_type = 0",
            };

            if (type.Length is { } length)
            {
                conditions.Add($"c.max_length = {length.ToString(CultureInfo.InvariantCulture)}");
            }

            if (type.Precision is { } precision)
            {
                conditions.Add($"c.precision = {precision.ToString(CultureInfo.InvariantCulture)}");
            }

            if (type.Scale is { } scale)
            {
                conditions.Add($"c.scale = {scale.ToString(CultureInfo.InvariantCulture)}");
            }

            if (type.IsCharacter)
            {
                conditions.Add("c.collation_name = " + (column.Collation is null
                    ? "CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))"
                    : Literal(column.Collation.Name)));
            }

            predicates.Add("EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id "
                + $"WHERE c.object_id = {principal} AND c.name = {Literal(foreignKey.PrincipalColumns[ordinal])} "
                + "AND " + string.Join(" AND ", conditions) + ")");
        }

        return string.Join(" AND ", predicates);
    }

    /// <summary>Compares inferred physical FK column storage without nullability or default facets.</summary>
    /// <param name="left">The dependent column contract.</param>
    /// <param name="right">The principal column contract.</param>
    /// <returns>Whether both contracts resolve to compatible physical SQL Server storage.</returns>
    internal bool ColumnStorageEquals(
        ExpectedColumnDefinition left,
        ExpectedColumnDefinition right
    )
        => TryGetColumnType(left, out var leftType) && TryGetColumnType(right, out var rightType)
            && leftType == rightType && Equals(left.Collation, right.Collation);

    private SqlServerSafeMigrationRuntimePlan BuildDropForeignKey(DropForeignKeyIntent intent)
    {
        var occupied = ConstraintNameExists(intent.Table, intent.Schema, intent.Name);
        var foreignKey = ForeignKeyExists(intent.Table, intent.Schema, intent.Name);

        return Plan(
            $"CASE WHEN NOT {occupied} THEN N'missing' WHEN {foreignKey} "
            + "THEN N'matching' ELSE N'different' END",
            Bit($"NOT {occupied}"));
    }

    private string ForeignKeyMatches(ExpectedForeignKeyDefinition definition)
    {
        var id = TableId(definition.Table, definition.Schema);
        var principal = TableId(definition.PrincipalTable, definition.PrincipalSchema);
        var values = string.Join(", ", definition.Columns.Select((column, index) =>
            $"({index + 1}, {Literal(column)}, {Literal(definition.PrincipalColumns[index])})"));

        return "EXISTS (SELECT 1 FROM sys.foreign_keys fk "
            + $"WHERE fk.parent_object_id = {id} AND fk.name = {Literal(definition.Name)} "
            + $"AND fk.referenced_object_id = {principal} AND fk.is_disabled = 0 "
            + "AND fk.is_not_trusted = 0 AND fk.is_not_for_replication = 0 "
            + $"AND fk.delete_referential_action_desc = {Literal(ReferentialActionName(definition.OnDelete))} "
            + $"AND fk.update_referential_action_desc = {Literal(ReferentialActionName(definition.OnUpdate))} "
            + "AND (SELECT COUNT(*) FROM sys.foreign_key_columns fkc "
            + $"WHERE fkc.constraint_object_id = fk.object_id) = {definition.Columns.Count} "
            + $"AND NOT EXISTS (SELECT 1 FROM (VALUES {values}) expected(ordinal, dependent, principal) "
            + "WHERE NOT EXISTS (SELECT 1 FROM sys.foreign_key_columns fkc "
            + "JOIN sys.columns dc ON dc.object_id = fkc.parent_object_id AND dc.column_id = fkc.parent_column_id "
            + "JOIN sys.columns pc ON pc.object_id = fkc.referenced_object_id "
            + "AND pc.column_id = fkc.referenced_column_id "
            + "WHERE fkc.constraint_object_id = fk.object_id "
            + "AND fkc.constraint_column_id = expected.ordinal "
            + "AND dc.name = expected.dependent AND pc.name = expected.principal)))";
    }

    private static string ReferentialActionName(ReferentialAction action) => action switch
    {
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET_NULL",
        ReferentialAction.SetDefault => "SET_DEFAULT",
        ReferentialAction.NoAction or ReferentialAction.Restrict => "NO_ACTION",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private string ForeignKeyExists(
        string table,
        string? schema,
        string name
    )
        => $"EXISTS (SELECT 1 FROM sys.foreign_keys fk WHERE fk.parent_object_id = {TableId(table, schema)} "
            + $"AND fk.name = {Literal(name)})";
}
