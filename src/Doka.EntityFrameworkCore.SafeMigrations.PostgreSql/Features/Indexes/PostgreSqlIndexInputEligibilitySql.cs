namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

/// <summary>Mirrors native index input and access-method selection using bounded catalog facts only.</summary>
internal static class PostgreSqlIndexInputEligibilitySql
{
    /// <summary>Qualifies an exact native input without invoking access-method, cast or support functions.</summary>
    /// <param name="input">The trusted native input and literal-encoded method/opclass identities.</param>
    /// <returns>Nullable Boolean SQL: true is proven supported, false disproven, null unproven.</returns>
    internal static string Render(PostgreSqlIndexInputSql input)
    {
        // WHY: Native GetDefaultOpClass reduces domains, prefers an exact
        // default, then a unique binary-compatible preferred category, then a
        // unique compatible default. A visible-opclass or builtin-type allowlist
        // would not preserve native array, enum, domain or VARCHAR semantics.
        var classFilter = input.OperatorClassName is null
            ? "opc.opcdefault"
            : "opc.opcname = " + input.OperatorClassName
                + (input.OperatorClassSchema is null
                    ? " AND pg_catalog.pg_opclass_is_visible(opc.oid)"
                    : " AND opc.opcnamespace = (SELECT n.oid FROM pg_catalog.pg_namespace n "
                        + "WHERE n.nspname = " + input.OperatorClassSchema + ")");

        var binary = BinaryCoercionPredicate("original.oid", "base_type", "target.oid");
        var baseBinary = BinaryCoercionPredicate("base_type.oid", "base_type", "target.oid");
        var supported = input.OperatorClassName is not null
            ? "COUNT(*) FILTER (WHERE compatible) = 1"
            : "COUNT(*) FILTER (WHERE exact) = 1 OR "
                + "(COUNT(*) FILTER (WHERE exact) = 0 AND "
                + "(COUNT(*) FILTER (WHERE preferred AND compatible) = 1 OR "
                + "(COUNT(*) FILTER (WHERE preferred AND compatible) = 0 "
                + "AND COUNT(*) FILTER (WHERE compatible) = 1)))";

        var binding = input.OperatorClassName is null ? baseBinary : binary;
        var methodFlags = MethodFlagsPredicate(input.RequiredFlags);

        // WHY: IsBinaryCoercible unwraps the source only. The recursive walk
        // follows just this requested native domain chain (and the element's
        // domain chain for RECORD[]), never a full type-catalog traversal.
        const string domainProjection = "t.oid, t.typtype, t.typbasetype, t.typcategory, "
            + "t.typelem, t.typsubscript, t.typrelid";

        const string elementProjection = "e.oid, e.typtype, e.typbasetype, e.typrelid";

        return "(WITH RECURSIVE domain_types AS ("
            + "SELECT " + domainProjection + " FROM pg_catalog.pg_type t WHERE t.oid = (" + input.NativeType + ") "
            + "UNION SELECT " + domainProjection + " FROM domain_types d "
            + "JOIN pg_catalog.pg_type t ON t.oid = d.typbasetype "
            + "WHERE d.typtype = 'd'), base_types AS ("
            + "SELECT * FROM domain_types WHERE typtype <> 'd'), element_types AS ("
            + "SELECT " + elementProjection + " FROM base_types b "
            + "JOIN pg_catalog.pg_type e ON e.oid = b.typelem "
            + "UNION SELECT " + elementProjection + " FROM element_types d "
            + "JOIN pg_catalog.pg_type e ON e.oid = d.typbasetype "
            + "WHERE d.typtype = 'd'), method AS ("
            + "SELECT am.oid, am.amhandler FROM pg_catalog.pg_am am WHERE am.amname = " + input.Method
            + " AND am.amtype = 'i'), candidates AS ("
            + "SELECT opc.oid, opc.opcintype = base_type.oid AS exact, "
            + "target.typispreferred AND target.typcategory = base_type.typcategory AS preferred, "
            + binding + " AS compatible FROM method am "
            + "JOIN pg_catalog.pg_opclass opc ON opc.opcmethod = am.oid "
            + "JOIN pg_catalog.pg_type target ON target.oid = opc.opcintype "
            + "CROSS JOIN base_types base_type "
            + "JOIN pg_catalog.pg_type original ON original.oid = (" + input.NativeType + ") "
            + "WHERE " + classFilter + "), resolved AS (SELECT (" + supported + ") AS supported FROM candidates) "
            + "SELECT CASE WHEN NOT EXISTS (SELECT 1 FROM method) THEN FALSE "
            + "WHEN (SELECT " + methodFlags + " FROM method am) IS FALSE THEN FALSE "
            + "WHEN (SELECT " + methodFlags + " FROM method am) IS NULL THEN NULL::boolean "
            + "WHEN (" + input.NativeType + ") = 0 THEN TRUE "
            + "WHEN (SELECT COUNT(*) FROM base_types) <> 1 THEN NULL::boolean "
            + "ELSE (SELECT supported FROM resolved) END)";
    }

    /// <summary>Maps source-pinned handler identities, including legitimate aliases of those handlers.</summary>
    private static string MethodFlagsPredicate(PostgreSqlIndexRequiredFlags required)
    {
        if (required == PostgreSqlIndexRequiredFlags.None)
        {
            return "TRUE";
        }

        // WHY: pg_indexam_has_property invokes the AM's handler/amproperty.
        // Metadata inspection must not execute arbitrary extension code. The
        // pinned native builtin OIDs define the handler's flags, not its name;
        // a custom method alias of the same builtin handler retains those flags.
        var mask = (int)(required & ~PostgreSqlIndexRequiredFlags.NullsNotDistinct);
        if ((required & PostgreSqlIndexRequiredFlags.NullsNotDistinct) != 0)
        {
            mask |= (int)PostgreSqlIndexRequiredFlags.Unique;
        }

        var nativeMask = mask.ToString(CultureInfo.InvariantCulture);
        var profile = "CASE am.amhandler WHEN 330 THEN 15 WHEN 331 THEN 0 WHEN 332 THEN 12 "
            + "WHEN 333 THEN 4 WHEN 334 THEN 8 WHEN 335 THEN 4 ELSE NULL::integer END";

        return "(((" + profile + ") & " + nativeMask + ") = " + nativeMask + ")"
            + ((required & PostgreSqlIndexRequiredFlags.NullsNotDistinct) == 0 ? string.Empty
                : " AND pg_catalog.current_setting('server_version_num')::integer >= 150000");
    }

    /// <summary>Uses directional native binary coercion, not transitive casts or executable conversions.</summary>
    private static string BinaryCoercionPredicate(string original, string baseAlias, string target)
    {
        var array = baseAlias + ".typelem <> 0 AND " + baseAlias + ".typsubscript = 6179";

        // WHY: These are the source-pinned IsBinaryCoercible polymorphic
        // cases. All remaining conversions require one direct implicit binary
        // pg_cast edge; cast functions, assignment and I/O are not proofs.
        return "(" + original + " = " + target + " OR " + baseAlias + ".oid = " + target
            + " OR " + target + " IN (2276, 2283, 5077) "
            + "OR ((" + array + ") AND " + target + " IN (2277, 5078)) "
            + "OR (NOT (" + array + ") AND " + target + " IN (2776, 5079)) "
            + "OR (" + baseAlias + ".typtype = 'e' AND " + target + " = 3500) "
            + "OR (" + baseAlias + ".typtype = 'r' AND " + target + " IN (3831, 5080)) "
            + "OR (" + baseAlias + ".typtype = 'm' AND " + target + " IN (4537, 4538)) "
            + "OR (" + baseAlias + ".typrelid <> 0 AND " + target + " = 2249) "
            + "OR ((" + array + ") AND " + target + " = 2287 AND EXISTS "
            + "(SELECT 1 FROM element_types e WHERE e.typtype <> 'd' AND e.typrelid <> 0)) "
            + "OR EXISTS (SELECT 1 FROM pg_catalog.pg_cast cv WHERE cv.castsource = " + baseAlias + ".oid "
            + "AND cv.casttarget = " + target + " AND cv.castmethod = 'b' AND cv.castcontext = 'i'))";
    }
}

/// <summary>The immutable literal-encoded native input requested by one actual index key.</summary>
/// <param name="NativeType">The trusted native type OID expression; zero checks method flags only.</param>
/// <param name="Method">The SQL literal for the exact native access-method name.</param>
/// <param name="OperatorClassName">The optional SQL literal for an explicit operator-class name.</param>
/// <param name="OperatorClassSchema">The optional SQL literal for its explicit namespace.</param>
/// <param name="RequiredFlags">Independently requested AM capabilities, not inferred comparator behavior.</param>
internal readonly record struct PostgreSqlIndexInputSql(
    string NativeType,
    string Method,
    string? OperatorClassName,
    string? OperatorClassSchema,
    PostgreSqlIndexRequiredFlags RequiredFlags);

/// <summary>Native index flags whose identity can be proven without calling an AM handler.</summary>
[Flags]
internal enum PostgreSqlIndexRequiredFlags
{
    None = 0,
    Order = 1,
    Unique = 2,
    MultipleKeys = 4,
    Include = 8,
    NullsNotDistinct = 16,
}
