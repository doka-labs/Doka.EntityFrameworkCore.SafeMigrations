namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    /// <summary>Routes only unoccupied native identities after independent owner/member prerequisites passed.</summary>
    /// <param name="intent">The exact requested native index identity.</param>
    /// <returns>SQL NULL for independently occupied names, otherwise an internal metadata-fence request.</returns>
    internal string BuildIndexInputRoutingState(EnsureIndexIntent intent)
        => "CASE WHEN " + RelationNameExists(intent.Definition.Name, intent.Definition.Schema)
            + " THEN NULL::text ELSE 'index_input_required' END";

    /// <summary>Folds Missing-only input eligibility into the existing metadata prerequisite statement.</summary>
    /// <param name="intent">The independently authored index contract.</param>
    /// <param name="certificates">The optional statement-local catalog certificate interner.</param>
    /// <returns>The short-circuit state, or SQL NULL when ordinary classification remains authoritative.</returns>
    internal string BuildIndexInputPrerequisiteState(EnsureIndexIntent intent,
        PostgreSqlIndexPrerequisiteCertificates? certificates = null)
    {
        var definition = intent.Definition;
        var independent = IndexExists(definition.Name, definition.Schema)
            + " OR " + IndexMatches(definition, requireExpectedName: false, requireIndependentIdentity: false)
            + " OR " + RelationNameExists(definition.Name, definition.Schema);

        var supported = certificates is null ? IndexInputEligibility(definition)
            : certificates.Intern(definition, this);

        // WHY: A real index or occupied native identity retains its independent
        // Matching/Different classification. This branch inspects type/opclass
        // catalogs only when a CREATE would otherwise become executable.
        return "CASE WHEN " + independent + " THEN NULL::text ELSE CASE (" + supported
            + ") WHEN FALSE THEN 'index_input_operator_class' WHEN TRUE THEN NULL::text "
            + "ELSE 'index_input_binding_unproven' END END";
    }

    /// <summary>Creates one deduplicatable native binding request for an actual ordered key input.</summary>
    /// <param name="nativeType">The trusted scalar native type expression; zero checks only method flags.</param>
    /// <param name="definition">The index definition whose method and independently requested flags apply.</param>
    /// <param name="key">The physical key, or null for the method-only request.</param>
    /// <returns>The metadata-only nullable Boolean selection.</returns>
    internal string BuildIndexInputProof(string nativeType, ExpectedIndexDefinition definition,
        ExpectedIndexKeyDefinition? key)
    {
        var path = key?.OperatorClass?.Split('.', StringSplitOptions.None);
        if (path is { Length: < 1 or > 2 } || path?.Any(string.IsNullOrWhiteSpace) == true)
        {
            throw new NotSupportedException("An operator class must be an unqualified or schema-qualified name.");
        }

        return PostgreSqlIndexInputEligibilitySql.Render(new PostgreSqlIndexInputSql(nativeType,
            Literal(definition.Method ?? "btree"), path is null ? null : Literal(path[^1]),
            path is { Length: 2 } ? Literal(path[0]) : null, IndexRequiredFlags(definition)));
    }

    /// <summary>Recognizes physical inputs without substituting operands for a computed expression result.</summary>
    /// <param name="key">The immutable physical or explicitly structured key.</param>
    /// <returns>The exact physical member reference, or null for a genuinely computed expression.</returns>
    internal static PostgreSqlIndexPhysicalMember? IndexPhysicalMember(ExpectedIndexKeyDefinition key)
    {
        if (key.Column is { } column)
        {
            // WHY: Ordinary column keys need no identifier-array snapshot or
            // expression object just to qualify their native storage type.
            return new PostgreSqlIndexPhysicalMember(column, null, null, 1);
        }

        if (IndexStoredExpression(key) is not SafeMigrationSqlIdentifierExpression member)
        {
            return null;
        }

        return new PostgreSqlIndexPhysicalMember(member.Parts[^1],
            member.Parts.Count > 1 ? member.Parts[^2] : null,
            member.Parts.Count > 2 ? member.Parts[^3] : null, member.Parts.Count);
    }

    /// <summary>Captures only independently specified native access-method requirements.</summary>
    internal static PostgreSqlIndexRequiredFlags IndexRequiredFlags(ExpectedIndexDefinition definition)
    {
        var flags = (definition.Unique ? PostgreSqlIndexRequiredFlags.Unique : PostgreSqlIndexRequiredFlags.None)
            | (definition.Keys.Count > 1 ? PostgreSqlIndexRequiredFlags.MultipleKeys : 0)
            | (definition.IncludedColumns.Count > 0 ? PostgreSqlIndexRequiredFlags.Include : 0)
            | (definition.NullsDistinct == false ? PostgreSqlIndexRequiredFlags.NullsNotDistinct : 0);

        // WHY: The immutable key list is an Array.AsReadOnly snapshot. This
        // cache-key hot path must not allocate its LINQ enumerator merely to
        // detect one explicitly requested ordering flag.
        for (var ordinal = 0; ordinal < definition.Keys.Count; ordinal++)
        {
            var key = definition.Keys[ordinal];
            if (key.SortOrder != SafeMigrationIndexSortOrder.ProviderDefault
                || key.NullOrder != SafeMigrationIndexNullOrder.ProviderDefault)
            {
                flags |= PostgreSqlIndexRequiredFlags.Order;

                break;
            }
        }

        return flags;
    }

    /// <summary>Qualifies actual column OIDs; INCLUDE and genuinely computed keys are not type-substituted.</summary>
    internal string IndexInputEligibility(ExpectedIndexDefinition definition)
    {
        var inputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in definition.Keys)
        {
            if (IndexPhysicalMember(key) is not { } member)
            {
                continue;
            }

            var owner = IndexMemberOwnerPredicate(definition, member);
            var nativeType = "(SELECT a.atttypid FROM pg_catalog.pg_attribute a "
                + "JOIN pg_catalog.pg_class t ON t.oid = a.attrelid "
                + "JOIN pg_catalog.pg_namespace n ON n.oid = t.relnamespace "
                + "WHERE n.nspname = " + SchemaExpression(definition.Schema)
                + " AND t.relname = " + Literal(definition.Table) + " AND a.attname = " + Literal(member.Name)
                + " AND a.attnum > 0 AND NOT a.attisdropped AND (" + owner + "))";

            inputs.Add(BuildIndexInputProof(nativeType, definition, key));
        }

        return inputs.Count == 0 ? BuildIndexInputProof("0::oid", definition, key: null)
            : "(" + string.Join(" AND ", inputs) + ")";
    }

    /// <summary>Requires the structured member's qualifier to resolve to the actual owning index relation.</summary>
    internal string IndexMemberOwnerPredicate(ExpectedIndexDefinition definition,
        PostgreSqlIndexPhysicalMember member)
        => member.PartCount == 1 ? "TRUE"
            : member.PartCount == 2 ? Literal(member.Table!) + " = " + Literal(definition.Table)
            : member.PartCount == 3 ? Literal(member.Table!) + " = " + Literal(definition.Table)
                + " AND " + Literal(member.Schema!) + " = " + SchemaExpression(definition.Schema)
            : "FALSE";

    /// <summary>Stops native data SQL from binding before a Missing index's inputs are proven executable.</summary>
    private PostgreSqlSafeMigrationRuntimePlan AddIndexInputRuntimeGuard(
        PostgreSqlSafeMigrationRuntimePlan plan, ExpectedIndexDefinition definition, string satisfied,
        string independentConflict)
    {
        var eligible = IndexInputEligibility(definition);

        // WHY: PostgreSQL resolves GROUP BY operators when planning data SQL,
        // even behind CASE. A separate native IF selects Matching/Different or
        // rejects unavailable inputs before a unique-index probe is planned.
        return plan with
        {
            StateEvaluationGuardExpression = "NOT (" + satisfied + ") AND NOT (" + independentConflict
                + ") AND COALESCE((" + eligible + "), FALSE)",
            StateEvaluationGuardFailureExpression = "CASE WHEN " + satisfied + " THEN 'matching' "
                + "WHEN " + independentConflict + " THEN 'different' WHEN (" + eligible
                + ") IS FALSE THEN 'unsupported' ELSE 'prerequisite_missing' END",
        };
    }
}

/// <summary>The physical identity already held by a column key or structured identifier, without a snapshot.</summary>
/// <param name="Name">The exact native column name.</param>
/// <param name="Table">The optional explicitly authored owning-table qualifier.</param>
/// <param name="Schema">The optional explicitly authored owning-schema qualifier.</param>
/// <param name="PartCount">The number of original identifier parts, retaining unsupported longer paths.</param>
internal readonly record struct PostgreSqlIndexPhysicalMember(
    string Name, string? Table, string? Schema, int PartCount);
