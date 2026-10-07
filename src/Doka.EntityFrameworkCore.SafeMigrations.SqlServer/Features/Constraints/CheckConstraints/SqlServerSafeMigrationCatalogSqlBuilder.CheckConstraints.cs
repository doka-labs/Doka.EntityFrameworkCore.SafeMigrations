namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    private SqlServerSafeMigrationRuntimePlan BuildEnsureCheck(
        EnsureCheckConstraintIntent intent,
        bool orderedCapture = false
    )
    {
        var definition = intent.Definition;
        var table = TableExists(definition.Table, definition.Schema);
        var occupied = ConstraintNameOccupiedInSchema(definition.Schema, definition.Name);

        if (orderedCapture)
        {
            occupied = $"({occupied}) AND NOT ({CheckExists(definition.Table, definition.Schema, definition.Name)})";
        }

        var matching = orderedCapture ? "1 = 0" : CheckMatches(definition);
        var expression = GetStructuredCheckExpression(definition);
        var safe = expression is not null && IsSafeIntegerCheckPredicate(expression);
        var columns = new HashSet<string>(StringComparer.Ordinal);

        if (safe)
        {
            SafeMigrationSqlExpressionInspector.CollectIdentifiers(expression!, columns);
        }

        var support = safe ? BuildIntegerCheckColumnProof(definition, columns) : "1 = 1";
        var canRead = $"COALESCE(HAS_PERMS_BY_NAME({Literal(QualifiedTable(definition.Table, definition.Schema))}, "
            + "N'OBJECT', N'SELECT'), 0) = 1";

        var unsafeRows = $"EXISTS (SELECT TOP (1) 1 FROM {QualifiedTable(definition.Table, definition.Schema)}"
            + (safe ? $" WHERE NOT ({CheckExpression(definition)})" : string.Empty) + ")";

        var classificationCode = $"CASE WHEN NOT ({canRead}) THEN N'check_row_data_unproven' "
            + (orderedCapture ? string.Empty : $"WHEN {matching} OR ({occupied}) THEN NULL ")
            + $"WHEN NOT ({support}) THEN N'check_column_type_unproven' ELSE NULL END";

        // WHY: A CHECK rejects FALSE, not UNKNOWN. NOT(predicate) finds only
        // those failures. Rendering alone proves neither nonthrowing evaluation
        // nor physical operand types, so opaque or unsafe expressions retain
        // the empty-only proof and every row probe has an outer permission gate.

        return Plan(
            $"CASE WHEN NOT {table} THEN N'prerequisite_missing' "
            + $"WHEN {matching} THEN N'matching' WHEN {occupied} THEN N'different' "
            + $"WHEN {unsafeRows} THEN N'data_blocked' ELSE N'missing' END",
            Bit(matching)) with
        {
            RequiresDelayedBinding = true,
            RequiresLiveDataProof = true,
            PrerequisiteExpression = Bit(TableAndColumnsExist(definition.Table, definition.Schema,
                columns.Order(StringComparer.Ordinal).ToArray())),
            StateEvaluationGuardExpression = Bit(orderedCapture ? $"({canRead}) AND ({support})"
                : $"({canRead}) AND ({matching} OR ({occupied}) OR ({support}))"),
            StateEvaluationGuardFailureExpression = "N'unsupported'",
            ClassificationCodeExpression = classificationCode,
            PrerequisiteFailureCodeExpression = $"CASE WHEN NOT {table} THEN N'check_prerequisite_missing' "
                + $"ELSE ({classificationCode}) END",
            PostApplySql = BuildCheckStampSql(definition),
            MatchedObjectNameExpression = $"CASE WHEN {matching} THEN {Literal(definition.Name)} ELSE NULL END",
        };
    }

    /// <summary>Validates a replacement predicate without erasing another owner's name occupancy.</summary>
    /// <param name="intent">The authored CHECK candidate, retaining the opaque empty-only boundary.</param>
    /// <returns>A guarded row proof requiring an accepted local drop before reuse.</returns>
    internal SqlServerSafeMigrationRuntimePlan BuildCheckPredicateCapturePlan(
        EnsureCheckConstraintIntent intent
    )
        => BuildEnsureCheck(intent, orderedCapture: true) with
        {
            PhysicalTableSupportExpression = BuildPhysicalTableSupportExpression(intent),
            DefaultValueSupportExpression = BuildDefaultValueSupportExpression(intent),
        };

    private SqlServerSafeMigrationRuntimePlan BuildDropCheck(
        DropCheckConstraintIntent intent
    )
    {
        var occupied = ConstraintNameExists(intent.Table, intent.Schema, intent.Name);
        var checkExists = CheckExists(intent.Table, intent.Schema, intent.Name);

        return Plan(
            $"CASE WHEN NOT {occupied} THEN N'missing' WHEN {checkExists} "
            + "THEN N'matching' ELSE N'different' END",
            Bit($"NOT {occupied}"));
    }

    private string CheckMatches(
        ExpectedCheckConstraintDefinition definition
    )
    {
        var fingerprint = ContractFingerprint("check", definition.Schema, definition.Table,
            definition.Name, CheckExpression(definition));

        return "EXISTS (SELECT 1 FROM sys.check_constraints cc "
            + $"WHERE cc.parent_object_id = {TableId(definition.Table, definition.Schema)} "
            + $"AND cc.name = {Literal(definition.Name)} AND cc.is_disabled = 0 "
            + "AND cc.is_not_trusted = 0 AND cc.is_not_for_replication = 0 "
            + "AND EXISTS (SELECT 1 FROM sys.extended_properties ep "
            + "WHERE ep.class = 1 AND ep.major_id = cc.object_id AND ep.minor_id = 0 "
            + "AND ep.name = N'Doka:SafeMigrations:Contract' "
            + $"AND CONVERT(nvarchar(64), ep.value) = {Literal(fingerprint)} "
            + "COLLATE Latin1_General_100_BIN2))";
    }

    private string BuildCheckStampSql(
        ExpectedCheckConstraintDefinition definition
    )
    {
        var fingerprint = ContractFingerprint("check", definition.Schema, definition.Table,
            definition.Name, CheckExpression(definition));

        var check = CheckExists(definition.Table, definition.Schema, definition.Name);

        return $"IF NOT {check} THROW 51004, N'SafeMigrations check contract was not created', 1; "
            + "EXEC sys.sp_addextendedproperty "
            + "@name = N'Doka:SafeMigrations:Contract', "
            + $"@value = {Literal(fingerprint)}, "
            + "@level0type = N'SCHEMA', "
            + $"@level0name = {Literal(EffectiveSchema(definition.Schema))}, "
            + "@level1type = N'TABLE', "
            + $"@level1name = {Literal(definition.Table)}, "
            + "@level2type = N'CONSTRAINT', "
            + $"@level2name = {Literal(definition.Name)}";
    }

    private string CheckExpression(
        ExpectedCheckConstraintDefinition definition
    )
        => definition.Sql ?? (definition.Expression is { } expression
            ? _expressionRenderer.Render(expression)
            : null)
                ?? throw new InvalidOperationException("A check constraint has no expression.");

    private string CheckExists(
        string table,
        string? schema,
        string name
    )
        => $"EXISTS (SELECT 1 FROM sys.check_constraints cc WHERE cc.parent_object_id = {TableId(table, schema)} "
            + $"AND cc.name = {Literal(name)})";
}
