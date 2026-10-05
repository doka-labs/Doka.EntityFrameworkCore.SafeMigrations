namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    private SqlServerSafeMigrationRuntimePlan BuildEnsureCheck(EnsureCheckConstraintIntent intent)
    {
        var definition = intent.Definition;
        var table = TableExists(definition.Table, definition.Schema);
        var occupied = ConstraintNameOccupiedInSchema(definition.Schema, definition.Name);
        var matching = CheckMatches(definition);
        var hasRows = $"EXISTS (SELECT TOP (1) 1 FROM {QualifiedTable(definition.Table, definition.Schema)})";

        // WHY: SQL Server validates a new CHECK against existing rows. Without
        // interpreting an arbitrary authored predicate, only an empty table
        // provides a provider-independent proof that creation cannot fail.

        return Plan(
            $"CASE WHEN NOT {table} THEN N'prerequisite_missing' "
            + $"WHEN {matching} THEN N'matching' WHEN {occupied} THEN N'different' "
            + $"WHEN {hasRows} THEN N'data_blocked' ELSE N'missing' END",
            Bit(matching)) with
        {
            RequiresDelayedBinding = true,
            PostApplySql = BuildCheckStampSql(definition),
            MatchedObjectNameExpression = $"CASE WHEN {matching} THEN {Literal(definition.Name)} ELSE NULL END",
        };
    }

    private SqlServerSafeMigrationRuntimePlan BuildDropCheck(DropCheckConstraintIntent intent)
    {
        var occupied = ConstraintNameExists(intent.Table, intent.Schema, intent.Name);
        var checkExists = CheckExists(intent.Table, intent.Schema, intent.Name);

        return Plan(
            $"CASE WHEN NOT {occupied} THEN N'missing' WHEN {checkExists} "
            + "THEN N'matching' ELSE N'different' END",
            Bit($"NOT {occupied}"));
    }

    private string CheckMatches(ExpectedCheckConstraintDefinition definition)
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

    private string BuildCheckStampSql(ExpectedCheckConstraintDefinition definition)
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

    private string CheckExpression(ExpectedCheckConstraintDefinition definition)
        => definition.Expression is { } expression
            ? _expressionRenderer.Render(expression)
            : definition.Sql
                ?? throw new InvalidOperationException("A check constraint has no expression.");

    private string CheckExists(
        string table,
        string? schema,
        string name
    )
        => $"EXISTS (SELECT 1 FROM sys.check_constraints cc WHERE cc.parent_object_id = {TableId(table, schema)} "
            + $"AND cc.name = {Literal(name)})";
}
