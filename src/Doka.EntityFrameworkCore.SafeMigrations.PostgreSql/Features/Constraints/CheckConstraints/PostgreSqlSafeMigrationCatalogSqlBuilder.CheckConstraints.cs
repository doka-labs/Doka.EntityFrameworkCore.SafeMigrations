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
        CheckMatches(intent.Definition, requireExpectedName: true, requireLocalIdentity: true),
        CheckConstraintDataBlocked(intent.Definition),
        semanticAlias: CheckMatches(intent.Definition, requireExpectedName: false),
        nonCanonicalAlias: CheckMatches(
            intent.Definition,
            requireExpectedName: false,
            requireLocalIdentity: false));

    private PostgreSqlSafeMigrationRuntimePlan BuildDropCheck(
        DropCheckConstraintIntent intent
    ) => BuildDropConstraint(intent.Table, intent.Schema, intent.Name, 'c');

    private string CheckConstraintDataBlocked(
        ExpectedCheckConstraintDefinition definition
    )
    {
        var expression = definition.Sql ?? _expressionRenderer.Render(definition.Expression!);

        return $"EXISTS (SELECT 1 FROM {Qualified(definition.Table, definition.Schema)} "
            + $"WHERE NOT COALESCE(({expression}), TRUE) LIMIT 1)";
    }

    private string CheckMatches(
        ExpectedCheckConstraintDefinition definition,
        bool requireExpectedName = true,
        bool requireLocalIdentity = true
    ) => CheckMatches(
        definition,
        $"co.conname {(requireExpectedName ? "=" : "<>")} {Literal(definition.Name)}",
        requireLocalIdentity);

    private string CheckMatches(
        ExpectedCheckConstraintDefinition definition,
        string namePredicate,
        bool requireLocalIdentity = true
    ) => ConstraintBaseWithoutName(definition.Table, definition.Schema, 'c')
        + $" AND {namePredicate}"
        + (requireLocalIdentity ? LocalConstraintIdentity() : string.Empty)
        + " AND co.convalidated AND NOT co.connoinherit"
        + " AND COALESCE((to_jsonb(co) ->> 'conenforced')::boolean, TRUE)"
        + $" AND {(definition.Expression is not null
            ? ExpressionMatches("pg_catalog.pg_get_expr(co.conbin, co.conrelid)", definition.Expression)
            : ExpressionMatches("pg_catalog.pg_get_expr(co.conbin, co.conrelid)", definition.Sql!))})";

    private string CheckSatisfied(
        ExpectedCheckConstraintDefinition definition
    )
    {
        var exists = ConstraintExists(definition.Table, definition.Schema, definition.Name, 'c');
        var exact = CheckMatches(definition, requireExpectedName: true);
        var semanticAlias = CheckMatches(definition, requireExpectedName: false);

        return $"({exact}) OR (NOT ({exists}) AND ({semanticAlias}))";
    }
}
