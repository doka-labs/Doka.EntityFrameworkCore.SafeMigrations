namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    private SqlServerSafeMigrationRuntimePlan BuildEnsureIndex(EnsureIndexIntent intent)
    {
        var definition = intent.Definition;
        var table = TableExists(definition.Table, definition.Schema);
        var exists = IndexExists(definition.Name, definition.Table, definition.Schema);
        var matching = IndexMatches(definition);
        var width = IndexKeysWithinLimit(definition.Table, definition.Schema,
            definition.Keys.Select(static key => key.Column!).ToArray(), 1700, allowComputed: true);

        var includes = IncludedIndexColumnsAreEligible(definition);
        var settings = ComputedIndexSettingsAreSupported(definition);
        var supported = $"({width}) AND ({includes}) AND ({settings})";

        var duplicate = definition.Unique ? DuplicateIndexKeyExists(definition) : "1 = 0";

        return Plan(
            $"CASE WHEN NOT {table} THEN N'prerequisite_missing' "
            + $"WHEN {matching} THEN N'matching' WHEN {exists} THEN N'different' "
            + $"WHEN NOT ({supported}) THEN N'unsupported' "
            + $"WHEN {duplicate} THEN N'data_blocked' ELSE N'missing' END",
            Bit(matching)) with
        {
            RequiresDelayedBinding = definition.Unique,
            // WHY: A dropped same-name index is absent in the projected stream,
            // but its target rows still need an independent uniqueness proof.
            ClassificationCodeExpression = definition.Unique
                ? $"CASE WHEN {exists} AND NOT ({matching}) THEN "
                    + $"CASE WHEN {duplicate} THEN N'index_replacement_data_blocked' "
                    + "ELSE N'key_replacement_data_safe' END ELSE NULL END"
                : null,
            StateEvaluationGuardExpression = Bit(supported),
            StateEvaluationGuardFailureExpression = "N'unsupported'",
            IndexFilterSupportExpression = BuildIndexFilterSupportExpression(definition),
            PostApplySql = HasIndexFilter(definition) ? BuildIndexStampSql(definition) : null,
            MatchedObjectNameExpression = $"CASE WHEN {matching} THEN {Literal(definition.Name)} ELSE NULL END",
        };
    }

    private SqlServerSafeMigrationRuntimePlan BuildDropIndex(DropIndexIntent intent)
    {
        var exists = IndexExists(intent.Name, intent.Table, intent.Schema);
        var independent = IndependentIndexExists(intent.Name, intent.Table, intent.Schema);
        var referenced = IndexIsReferenced(intent.Name, intent.Table, intent.Schema);

        return Plan(
            $"CASE WHEN NOT {exists} THEN N'missing' WHEN {independent} AND NOT {referenced} "
            + "THEN N'matching' ELSE N'different' END",
            Bit($"NOT {exists}")) with
        {
            // WHY: Ordered projection distinguishes a removed native physical
            // name from an unbound semantic alias without another catalog read.
            MatchedObjectNameExpression = $"(SELECT i.name FROM sys.indexes i "
                + $"WHERE i.object_id={TableId(intent.Table, intent.Schema)} AND i.name={Literal(intent.Name)})",
        };
    }

    private SqlServerSafeMigrationRuntimePlan BuildRenameIndex(RenameIndexIntent intent)
    {
        var source = IndexExists(intent.Name, intent.Table, intent.Schema);
        var independent = IndependentIndexExists(intent.Name, intent.Table, intent.Schema);
        var target = IndexExists(intent.NewName, intent.Table, intent.Schema);

        return Plan(
            $"CASE WHEN NOT {source} THEN N'missing' WHEN NOT {independent} OR {target} "
            + "THEN N'different' ELSE N'matching' END",
            Bit($"NOT {source} AND {target}"));
    }

    private string IndexMatches(ExpectedIndexDefinition definition)
    {
        var id = TableId(definition.Table, definition.Schema);
        var filter = HasIndexFilter(definition)
            ? "i.has_filter = 1 AND EXISTS (SELECT 1 FROM sys.extended_properties ep "
                + "WHERE ep.class = 7 AND ep.major_id = i.object_id AND ep.minor_id = i.index_id "
                + "AND ep.name = N'Doka:SafeMigrations:Contract' "
                + $"AND CONVERT(nvarchar(64), ep.value) = {Literal(IndexFilterFingerprint(definition))} "
                + "COLLATE Latin1_General_100_BIN2)"
            : "i.has_filter = 0";

        var values = string.Join(", ", definition.Keys.Select((key, index) =>
            $"({index + 1}, {Literal(key.Column!)}, "
                + $"{(key.SortOrder == SafeMigrationIndexSortOrder.Descending ? 1 : 0)})"));

        var included = definition.IncludedColumns.Count == 0
            ? "NOT EXISTS (SELECT 1 FROM sys.index_columns ic WHERE ic.object_id = i.object_id "
                + "AND ic.index_id = i.index_id AND ic.is_included_column = 1)"
            : $"(SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id = i.object_id "
                + $"AND ic.index_id = i.index_id AND ic.is_included_column = 1) = {definition.IncludedColumns.Count} "
                + "AND NOT EXISTS (SELECT 1 FROM sys.index_columns ic "
                + "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id "
                + "WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id "
                + "AND ic.is_included_column = 1 "
                + $"AND c.name NOT IN ({string.Join(", ", definition.IncludedColumns.Select(Literal))}))";

        return "EXISTS (SELECT 1 FROM sys.indexes i "
            + $"WHERE i.object_id = {id} AND i.name = {Literal(definition.Name)} "
            + "AND i.type = 2 AND i.is_primary_key = 0 AND i.is_unique_constraint = 0 "
            + $"AND i.is_disabled = 0 AND i.is_hypothetical = 0 AND i.ignore_dup_key = 0 AND {filter} "
            + $"AND i.is_unique = {(definition.Unique ? 1 : 0)} "
            + $"AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id = i.object_id "
            + $"AND ic.index_id = i.index_id AND ic.key_ordinal > 0) = {definition.Keys.Count} "
            + $"AND {included} "
            + $"AND NOT EXISTS (SELECT 1 FROM (VALUES {values}) expected(ordinal, name, descending) "
            + "WHERE NOT EXISTS (SELECT 1 FROM sys.index_columns ic "
            + "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id "
            + "WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id "
            + "AND ic.key_ordinal = expected.ordinal AND c.name = expected.name "
            + "AND ic.is_descending_key = expected.descending)))";
    }

    private string IndexKeysWithinLimit(
        string table,
        string? schema,
        IReadOnlyList<string> columns,
        int maximumBytes,
        bool allowComputed = false
    )
    {
        var values = string.Join(", ", columns.Select((column, ordinal) =>
            $"({ordinal + 1}, {Literal(column)})"));

        // WHY: Declared key width, rather than present row values, determines
        // whether a future insert could exceed the active physical key limit.

        return "EXISTS (SELECT 1 FROM (VALUES " + values + ") expected(ordinal, name) "
            + "JOIN sys.columns c ON c.object_id = " + TableId(table, schema)
            + " AND c.name = expected.name "
            + "JOIN sys.types ty ON ty.user_type_id = c.user_type_id "
            + "AND ty.is_user_defined = 0 AND ty.is_assembly_type = 0 "
            + "AND ty.name IN (N'bigint', N'binary', N'bit', N'char', N'date', N'datetime', "
            + "N'datetime2', N'datetimeoffset', N'decimal', N'float', N'int', N'money', "
            + "N'nchar', N'numeric', N'nvarchar', N'real', N'smalldatetime', N'smallint', "
            + "N'smallmoney', N'time', N'timestamp', N'tinyint', N'uniqueidentifier', "
            + "N'varbinary', N'varchar') "
            + "AND " + (allowComputed ? ComputedIndexColumnEligibility("c") : "c.is_computed = 0") + " "
            + $"HAVING COUNT(*) = {columns.Count} "
            + "AND MIN(CONVERT(int, c.max_length)) > 0 "
            + $"AND SUM(CONVERT(bigint, c.max_length)) <= {maximumBytes})";
    }

    /// <summary>Preserves the effective indexed-computed SET contract and SqlClient's implicit ARITHABORT.</summary>
    internal const string ComputedIndexSessionPredicate =
        "SESSIONPROPERTY(N'ANSI_NULLS')=1 AND SESSIONPROPERTY(N'ANSI_PADDING')=1 "
        + "AND SESSIONPROPERTY(N'ANSI_WARNINGS')=1 AND SESSIONPROPERTY(N'CONCAT_NULL_YIELDS_NULL')=1 "
        + "AND SESSIONPROPERTY(N'QUOTED_IDENTIFIER')=1 AND SESSIONPROPERTY(N'NUMERIC_ROUNDABORT')=0 "
        + "AND (SESSIONPROPERTY(N'ARITHABORT')=1 OR (SESSIONPROPERTY(N'ANSI_WARNINGS')=1 "
        + "AND (SELECT compatibility_level FROM sys.databases WHERE database_id=DB_ID())>=90))";

    /// <summary>Qualifies native key and INCLUDE admission without confusing their precision requirements.</summary>
    internal static string ComputedIndexColumnEligibility(string alias, bool included = false)
        => $"({alias}.is_computed=0 OR (COLUMNPROPERTY({alias}.object_id,{alias}.name,N'IsDeterministic')=1 "
            // WHY: Deterministic imprecise values may be included without
            // persistence. Keys additionally need native indexability and a
            // precise expression or the engine's persisted-value contract.
            + (included ? string.Empty
                : $"AND COLUMNPROPERTY({alias}.object_id,{alias}.name,N'IsIndexable')=1 "
                    + $"AND (COLUMNPROPERTY({alias}.object_id,{alias}.name,N'IsPrecise')=1 "
                    + $"OR EXISTS(SELECT 1 FROM sys.computed_columns cc WHERE cc.object_id={alias}.object_id "
                    + $"AND cc.column_id={alias}.column_id AND cc.is_persisted=1)) ")
            + "AND (" + ComputedIndexSessionPredicate + ")))";

    private string IncludedIndexColumnsAreEligible(ExpectedIndexDefinition definition)
    {
        if (definition.IncludedColumns.Count == 0)
        {
            return "1 = 1";
        }

        return "(SELECT COUNT(*) FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id "
            + $"WHERE c.object_id={TableId(definition.Table, definition.Schema)} AND c.name IN ("
            + string.Join(", ", definition.IncludedColumns.Select(Literal))
            + ") AND ty.is_user_defined=0 AND ty.is_assembly_type=0 "
            + "AND ty.name NOT IN (N'text',N'ntext',N'image') AND "
            + ComputedIndexColumnEligibility("c", included: true)
            + $")={definition.IncludedColumns.Count}";
    }

    private string ComputedIndexSettingsAreSupported(ExpectedIndexDefinition definition)
    {
        var columns = definition.Keys.Select(static key => key.Column!).Concat(definition.IncludedColumns);

        // WHY: Ordinary indexes keep their existing session contract. A computed
        // key or INCLUDE requires all native settings, without changing the caller's
        // session. ANSI_WARNINGS supplies effective ARITHABORT at compatibility 90+.
        return "(NOT EXISTS(SELECT 1 FROM sys.columns c WHERE c.object_id="
            + TableId(definition.Table, definition.Schema) + " AND c.is_computed=1 AND c.name IN ("
            + string.Join(", ", columns.Select(Literal)) + ")) OR (" + ComputedIndexSessionPredicate + "))";
    }

    private string DuplicateIndexKeyExists(ExpectedIndexDefinition definition)
    {
        var keys = string.Join(", ", definition.Keys.Select(key => Delimited(key.Column!)));
        var filter = GetIndexFilter(definition);
        var where = filter is null ? string.Empty : $" WHERE ({filter})";

        return $"EXISTS (SELECT TOP (1) 1 FROM {QualifiedTable(definition.Table, definition.Schema)}"
            + $"{where} GROUP BY {keys} HAVING COUNT_BIG(*) > 1)";
    }

    private static bool HasIndexFilter(ExpectedIndexDefinition definition)
        => definition.Filter is not null || definition.StructuredFilter is not null;

    private string? GetIndexFilter(ExpectedIndexDefinition definition)
        => definition.Filter ?? (definition.StructuredFilter is null
            ? null
            : _expressionRenderer.Render(definition.StructuredFilter));

    private string IndexFilterFingerprint(ExpectedIndexDefinition definition)
        => ContractFingerprint("filtered_index", definition.Schema, definition.Table,
            definition.Name, GetIndexFilter(definition)!);

    private string BuildIndexStampSql(ExpectedIndexDefinition definition)
    {
        var index = IndependentIndexExists(definition.Name, definition.Table, definition.Schema);
        var fingerprint = IndexFilterFingerprint(definition);
        var contractValue = Literal(fingerprint);
        var schemaName = EffectiveSchema(definition.Schema);
        var variable = SqlServerSqlStringLiteral.RequiresExpression(schemaName.AsSpan())
            || SqlServerSqlStringLiteral.RequiresExpression(definition.Table.AsSpan())
            || SqlServerSqlStringLiteral.RequiresExpression(definition.Name.AsSpan())
                ? "@doka_index_stamp_" + fingerprint : string.Empty;

        var schemaArgument = SqlServerSqlStringLiteral.ProcedureArgument(
            schemaName, Literal(schemaName), variable.Length == 0 ? string.Empty : variable + "_schema",
            out var schemaDeclaration);
        var tableArgument = SqlServerSqlStringLiteral.ProcedureArgument(
            definition.Table, Literal(definition.Table), variable.Length == 0 ? string.Empty : variable + "_table",
            out var tableDeclaration);
        var nameArgument = SqlServerSqlStringLiteral.ProcedureArgument(
            definition.Name, Literal(definition.Name), variable.Length == 0 ? string.Empty : variable + "_name",
            out var nameDeclaration);

        return $"IF NOT {index} THROW 51004, N'SafeMigrations index contract was not created', 1; "
            // WHY: Bind expression-rendered names before EXEC, without
            // changing ordinary literal arguments or their parameter order.
            + schemaDeclaration + tableDeclaration + nameDeclaration
            + "EXEC sys.sp_addextendedproperty "
            + "@name = N'Doka:SafeMigrations:Contract', "
            + $"@value = {contractValue}, "
            + "@level0type = N'SCHEMA', "
            + $"@level0name = {schemaArgument}, "
            + "@level1type = N'TABLE', "
            + $"@level1name = {tableArgument}, "
            + "@level2type = N'INDEX', "
            + $"@level2name = {nameArgument}";
    }

    private string IndexExists(
        string name,
        string table,
        string? schema
    )
        => $"EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id = {TableId(table, schema)} "
            + $"AND i.name = {Literal(name)})";

    private string IndependentIndexExists(
        string name,
        string table,
        string? schema
    )
        => $"EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id = {TableId(table, schema)} "
            + $"AND i.name = {Literal(name)} AND i.is_primary_key = 0 AND i.is_unique_constraint = 0)";

    private string IndexIsReferenced(
        string name,
        string table,
        string? schema
    )
        => "EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.foreign_keys fk "
            + "ON fk.referenced_object_id = i.object_id AND fk.key_index_id = i.index_id "
            + $"WHERE i.object_id = {TableId(table, schema)} AND i.name = {Literal(name)})";
}
