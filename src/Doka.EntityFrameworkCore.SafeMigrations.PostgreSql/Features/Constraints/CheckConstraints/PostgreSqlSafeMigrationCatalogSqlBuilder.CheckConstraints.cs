namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    private PostgreSqlSafeMigrationRuntimePlan BuildEnsureCheck(
        EnsureCheckConstraintIntent intent
    ) => BuildEnsureConstraint(
        intent.Definition.Table,
        intent.Definition.Schema,
        intent.Definition.Name,
        'c',
        CheckMatches(intent.Definition),
        CheckConstraintDataBlocked(intent.Definition));

    private PostgreSqlSafeMigrationRuntimePlan BuildDropCheck(
        DropCheckConstraintIntent intent
    ) => BuildDropConstraint(intent.Table, intent.Schema, intent.Name, 'c');

    private string CheckConstraintDataBlocked(
        ExpectedCheckConstraintDefinition definition
    ) => $"EXISTS (SELECT 1 FROM {Qualified(definition.Table, definition.Schema)} "
        + $"WHERE NOT COALESCE(({definition.Sql}), TRUE) LIMIT 1)";

    private string CheckMatches(
        ExpectedCheckConstraintDefinition definition
    ) => ConstraintBase(definition.Table, definition.Schema, definition.Name, 'c')
        + $" AND co.convalidated AND {ExpressionMatches("pg_catalog.pg_get_expr(co.conbin, co.conrelid)", definition.Sql)})";
}
