namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    /// <summary>Guards native unqualified resolution before catalog scope or row queries can be evaluated.</summary>
    /// <param name="intent">The immutable operation and its authored dependencies.</param>
    /// <returns>TRUE for fully qualified operations, otherwise an exact current-schema ownership predicate.</returns>
    public string BuildObjectIdentityGuard(SafeMigrationIntent intent)
    {
        if (intent is not (EnsureTableIntent or EnsureForeignKeyIntent or DeleteModelManagedDataIntent
            or DropIndexIntent or RenameIndexIntent))
        {
            var owner = PostgreSqlSafeMigrationSchemaIdentity.GetOwner(intent);

            return owner is { Table: not null, Schema: null }
                ? BuildSingleOwnerIdentityGuard(owner.Table,
                    PostgreSqlSafeMigrationSchemaIdentity.GetOptionalAbsentNativeBinding(intent, null)) : "TRUE";
        }

        var tables = new HashSet<string>(StringComparer.Ordinal);
        PostgreSqlSafeMigrationSchemaIdentity.CollectUnqualifiedTables(intent, tables);

        return BuildObjectIdentityGuard(tables,
            PostgreSqlSafeMigrationSchemaIdentity.GetOptionalAbsentNativeBinding(intent, null));
    }

    /// <summary>Rechecks surviving native names, excluding sources intentionally consumed by the baseline.</summary>
    /// <param name="intent">The immutable operation whose apply/repair branch will execute.</param>
    /// <param name="initialGuard">The existing guard reused for operations without consumed relation names.</param>
    /// <returns>The fresh operation-local native binding proof for execution postflight.</returns>
    public string BuildPostExecutionObjectIdentityGuard(SafeMigrationIntent intent, string initialGuard)
    {
        if (intent is not (DropTableIntent or RenameTableIntent or DropIndexIntent or RenameIndexIntent
            or EnsureIndexIntent))
        {
            return initialGuard;
        }

        var tables = new HashSet<string>(StringComparer.Ordinal);
        PostgreSqlSafeMigrationSchemaIdentity.CollectUnqualifiedTables(intent, tables);
        PostgreSqlSafeMigrationSchemaIdentity.AdvancePostExecutionBindings(intent, tables);

        // WHY: A legitimate drop/rename can expose an existing fallback with
        // the old source name. The final catalog still checks the frozen current
        // schema, while surviving owners and renamed destinations must retain
        // their original native scope without a newly introduced TEMP shadow.

        return BuildObjectIdentityGuard(tables);
    }

    /// <summary>Builds the same current-schema contract for a deduplicated connected table family.</summary>
    /// <param name="tables">The exact unqualified native relation bindings.</param>
    /// <param name="optionalAbsentBinding">The source whose local absence cannot execute against a fallback.</param>
    /// <returns>The catalog-only identity guard.</returns>
    public string BuildObjectIdentityGuard(HashSet<string> tables, string? optionalAbsentBinding = null)
    {
        if (tables.Count == 0)
        {
            return "TRUE";
        }

        if (tables.Count == 1)
        {
            // WHY: Most guarded operations have one owner. Avoid an ordered
            // enumerable and join buffer for this common classification path.
            foreach (var table in tables)
            {
                return BuildSingleOwnerIdentityGuard(table, optionalAbsentBinding);
            }
        }

        var predicates = tables.Order(StringComparer.Ordinal)
            .Select(table => BuildNativeOwnerPredicate(table, optionalAbsentBinding));

        // WHY: Catalog predicates intentionally scope null qualifiers to the
        // active creation schema, while native DDL can resolve a fallback or
        // temporary relation. Absence is valid for CREATE, but a fallback must
        // not let catalog and execution operate on different physical owners.

        return "(current_schema() IS NOT NULL AND " + string.Join(" AND ", predicates) + ")";
    }

    /// <summary>Uses exact quoted native resolution without changing PostgreSQL identifier case.</summary>
    private string BuildNativeOwnerPredicate(string table, string? optionalAbsentBinding)
    {
        var native = "NOT EXISTS (SELECT 1 FROM pg_catalog.pg_class identity_relation "
            + "JOIN pg_catalog.pg_namespace identity_namespace "
            + "ON identity_namespace.oid = identity_relation.relnamespace "
            + $"WHERE identity_relation.oid = pg_catalog.to_regclass({Literal(Qualified(table, null))}) "
            + "AND identity_namespace.nspname <> current_schema())";

        return StringComparer.Ordinal.Equals(table, optionalAbsentBinding)
            ? $"(CASE WHEN {RelationExists(table, null)} THEN ({native}) ELSE TRUE END)" : native;
    }

    /// <summary>Combines the creation-schema prerequisite and native lookup for one exact owner.</summary>
    private string BuildSingleOwnerIdentityGuard(string table, string? optionalAbsentBinding = null)
        => "(current_schema() IS NOT NULL AND " + BuildNativeOwnerPredicate(table, optionalAbsentBinding) + ")";

    /// <summary>Builds an exact table plan with runtime-selected current-schema transition alternatives.</summary>
    /// <param name="operation">The EnsureTable operation being generated without a live connection.</param>
    /// <param name="exact">The contract which keeps every authored qualifier distinct.</param>
    /// <param name="alternatives">Only connected table-family aliases which can describe this physical owner.</param>
    /// <returns>The plan whose current-schema branch is selected by the server, never an authored guess.</returns>
    public PostgreSqlSafeMigrationRuntimePlan BuildWithTableIdentityContracts(
        SafeMigrationOperation operation,
        SafeMigrationExpectedTableConstraints? exact,
        IReadOnlyList<(string Schema, SafeMigrationExpectedTableConstraints? Contract)> alternatives
    )
    {
        var plan = Build(operation, exact, includeAnalysisEvidence: false, includeTransitionEvidence: true);
        if (plan.IsStaticallyUnsupported || alternatives.Count == 0)
        {
            return plan;
        }

        var state = new StringBuilder("CASE");
        var postcondition = new StringBuilder("CASE");
        foreach (var (schema, contract) in alternatives)
        {
            var alternative = Build(operation, contract, includeAnalysisEvidence: false,
                includeTransitionEvidence: true);
            var condition = "current_schema() = " + Literal(schema);
            state.Append(" WHEN ").Append(condition).Append(" THEN (").Append(alternative.StateExpression)
                .Append(')');
            postcondition.Append(" WHEN ").Append(condition).Append(" THEN (").Append(alternative.Postcondition)
                .Append(')');
        }

        return plan with
        {
            StateExpression = state.Append(" ELSE (").Append(plan.StateExpression).Append(") END").ToString(),
            Postcondition = postcondition.Append(" ELSE (").Append(plan.Postcondition).Append(") END").ToString(),
        };
    }

    /// <summary>Requires the runtime current schema not to activate a statically invalid alias transition.</summary>
    /// <param name="schemas">Candidate schemas whose normalized index or constraint stream is invalid.</param>
    /// <returns>The exact, quoted predicate retaining valid foreign-schema operation streams.</returns>
    public string BuildRejectedAliasGuard(IReadOnlyList<string> schemas)
        => schemas.Count == 0 ? "TRUE"
            : "(" + string.Join(" AND ", schemas.Select(schema =>
                "current_schema() IS DISTINCT FROM " + Literal(schema))) + ")";

    /// <summary>Requires a connected native owner proof only when this qualifier is the active schema.</summary>
    /// <param name="schema">The authored candidate, not an assumed connection identity.</param>
    /// <param name="guard">The exact native owner proof for its connected unqualified bindings.</param>
    /// <returns>The conditional guard, retaining distinct foreign schemas.</returns>
    public string BuildConditionalAliasGuard(string schema, string guard)
        => "(CASE WHEN current_schema() = " + Literal(schema) + " THEN (" + guard + ") ELSE TRUE END)";
}
