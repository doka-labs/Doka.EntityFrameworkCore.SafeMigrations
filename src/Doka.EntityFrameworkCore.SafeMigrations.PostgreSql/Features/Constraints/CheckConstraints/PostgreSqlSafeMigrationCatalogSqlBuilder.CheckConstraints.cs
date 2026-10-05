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
            requireLocalIdentity: false),
        semanticCandidates: CheckMatchQuery(
            intent.Definition,
            $"co.conname <> {Literal(intent.Definition.Name)}"));

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
    ) => $"EXISTS ({CheckMatchQuery(definition, namePredicate, requireLocalIdentity)})";

    private string CheckMatchQuery(
        ExpectedCheckConstraintDefinition definition,
        string namePredicate,
        bool requireLocalIdentity = true
    ) => ConstraintRowsWithoutName(definition.Table, definition.Schema, 'c')
        + $" AND {namePredicate}"
        + CheckFacets(ExpectedCheckExpressionMatches(definition), requireLocalIdentity);

    /// <summary>Builds the check-constraint facet predicates around one expression comparison.</summary>
    /// <remarks>
    /// The expression comparison is generated per constraint, so it is supplied by the caller while
    /// the surrounding facets stay defined once for the single-constraint and the correlated set form.
    /// </remarks>
    /// <param name="expressionMatch">The expression comparison applied to the candidate row.</param>
    /// <param name="requireLocalIdentity">Whether local ownership is part of the identity.</param>
    /// <returns>The facet predicates, each prefixed with AND.</returns>
    private static string CheckFacets(
        string expressionMatch,
        bool requireLocalIdentity = true
    ) => (requireLocalIdentity ? LocalConstraintIdentity() : string.Empty)
        + " AND co.convalidated AND NOT co.connoinherit"
        + " AND COALESCE((to_jsonb(co) ->> 'conenforced')::boolean, TRUE)"
        + $" AND {expressionMatch}";

    /// <summary>Builds the catalog comparison for one expected check expression.</summary>
    /// <param name="definition">The expected check constraint.</param>
    /// <returns>The comparison against the catalog expression.</returns>
    private string ExpectedCheckExpressionMatches(
        ExpectedCheckConstraintDefinition definition
    ) => definition.Expression is not null
        ? ExpressionMatches("pg_catalog.pg_get_expr(co.conbin, co.conrelid)", definition.Expression)
        : ExpressionMatches("pg_catalog.pg_get_expr(co.conbin, co.conrelid)", definition.Sql!);

    /// <summary>Verifies every required check constraint of one table through three catalog scans.</summary>
    /// <remarks>
    /// WHY: The per-constraint form opens three correlated catalog scans for each required check.
    /// The expression comparison is generated per constraint and cannot be carried as a value, so
    /// the scans are hoisted and only the comparison stays per item as a CASE arm. A predicate can
    /// evaluate to NULL, so the absent test coalesces to FALSE to keep the mismatch verdict.
    /// </remarks>
    /// <param name="table">The table owning the required checks.</param>
    /// <param name="schema">The owning schema, or null for the current schema.</param>
    /// <param name="required">The required check constraints, all owned by that table.</param>
    /// <returns>A predicate that is true when every required check constraint is satisfied.</returns>
    private string AllRequiredCheckConstraintsSatisfied(
        string table,
        string? schema,
        ExpectedCheckConstraintDefinition[] required
    )
    {
        var rows = new List<string>(required.Length);
        var arms = new List<string>(required.Length);
        for (var index = 0; index < required.Length; index++)
        {
            var ordinal = (index + 1).ToString(CultureInfo.InvariantCulture);
            var definition = required[index];
            rows.Add($"({ordinal}, CAST({Literal(definition.Name)} AS pg_catalog.name))");
            arms.Add($"WHEN {ordinal} THEN {ExpectedCheckExpressionMatches(definition)}");
        }

        var semantics = CheckFacets($"CASE required.ordinal {string.Join(" ", arms)} END");

        var rowSource = ConstraintRowsWithoutName(table, schema, 'c');
        var exact = $"EXISTS ({rowSource} AND co.conname = required.conname{semantics})";
        var exists = $"EXISTS ({rowSource} AND co.conname = required.conname)";
        var alias = $"EXISTS ({rowSource} AND co.conname <> required.conname{semantics})";

        return $"NOT EXISTS (SELECT 1 FROM (VALUES {string.Join(", ", rows)}) "
            + "AS required(ordinal, conname) WHERE NOT COALESCE("
            + $"({exact}) OR (NOT ({exists}) AND ({alias})), FALSE))";
    }

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
