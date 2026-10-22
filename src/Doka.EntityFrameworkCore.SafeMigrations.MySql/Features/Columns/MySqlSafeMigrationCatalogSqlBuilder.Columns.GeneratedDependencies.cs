namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    /// <summary>Certifies surviving native dependencies before a dependency-sensitive column change.</summary>
    private MySqlSafeMigrationRuntimePlan QualifyGeneratedColumnDependency(
        string table,
        string source,
        MySqlSafeMigrationRuntimePlan plan,
        string sourceExists,
        bool isMariaDb,
        MySqlGeneratedColumnDependencyContract? contract,
        MySqlGeneratedColumnDependencyContract? foldedContract,
        bool bindFoldedContract,
        bool isDrop,
        MySqlGeneratedColumnDependencyContract? modelCandidate,
        MySqlGeneratedColumnDependencyContract? foldedModelCandidate
    )
    {
        // WHY: Bare schema/table filters let native metadata lookup avoid
        // enumerating unrelated databases. They are coarse prefilters only;
        // the native-case binary proofs still decide the exact owner identity.
        var owner = "c.TABLE_SCHEMA = DATABASE() "
            + $"AND c.TABLE_NAME = {Literal(table)} AND "
            + BuildRequiredColumnAddOwnerMatches("c.TABLE_SCHEMA", "DATABASE()")
            + " AND "
            + BuildRequiredColumnAddOwnerMatches("c.TABLE_NAME", Literal(table));

        var generated = "c.GENERATION_EXPRESSION IS NOT NULL AND c.GENERATION_EXPRESSION <> ''";
        if (isDrop)
        {
            // WHY: Removing a generated member itself is legal. Only members
            // surviving this exact DROP can retain a dependency on its source.
            // Fold case only: catalog collation must not also discard an
            // accent-distinct surviving identifier as though it were removed.
            generated += " AND CAST(LOWER(c.COLUMN_NAME) AS BINARY) <> CAST(LOWER("
                + Literal(source) + ") AS BINARY)";
        }

        var count = $"(SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS c WHERE {owner} AND ({generated}))";
        var ordinaryCertificate = BuildCertificate(contract);
        var ordinaryModelCertificate = BuildCertificate(modelCandidate);
        var ordinaryPermit = Combine(
            contract?.ReferencesSource == false ? ordinaryCertificate : "FALSE",
            modelCandidate?.ReferencesSource == false ? ordinaryModelCertificate : "FALSE");

        var permit = Combine($"{count} = 0", ordinaryPermit);
        var dependent = Combine(
            contract?.ReferencesSource == true ? ordinaryCertificate : "FALSE",
            modelCandidate?.ReferencesSource == true ? ordinaryModelCertificate : "FALSE");

        if (bindFoldedContract)
        {
            var foldedCertificate = BuildCertificate(foldedContract);
            var foldedModelCertificate = BuildCertificate(foldedModelCandidate);
            var foldedPermit = Combine(
                foldedContract?.ReferencesSource == false ? foldedCertificate : "FALSE",
                foldedModelCandidate?.ReferencesSource == false ? foldedModelCertificate : "FALSE");

            var foldedDependent = Combine(
                foldedContract?.ReferencesSource == true ? foldedCertificate : "FALSE",
                foldedModelCandidate?.ReferencesSource == true ? foldedModelCertificate : "FALSE");

            permit = $"{count} = 0 OR CASE WHEN @@lower_case_table_names <> 0 THEN ({foldedPermit}) "
                + $"ELSE ({ordinaryPermit}) END";
            dependent = $"CASE WHEN @@lower_case_table_names <> 0 THEN "
                + $"({foldedDependent})"
                + $" ELSE ({dependent}) END";
        }

        if (isDrop && isMariaDb)
        {
            permit = Combine(permit, BuildMariaDbGeneratedDropSourceAbsence(table, source));
        }

        // WHY: A separately certified whole model can complement an authored
        // prefix, never dilute a proven positive dependency or union members
        // from two incomplete shapes into a fabricated complete native set.
        var guard = $"(NOT ({sourceExists})) OR (({permit}) AND NOT ({dependent}))";
        var codePrefix = isDrop ? "generated_column_drop_" : "generated_column_rename_";

        // WHY: Whole-model certificates require exact membership and facets;
        // the MariaDB fallback certifies only this source's absence, never a
        // model or an empty generated set. Both belong in the ordinary state
        // expression so runtime/scripts recheck the catalog without another
        // prerequisite batch. Native ALTER still rejects a dependent added
        // concurrently after this observation, without implicit CASCADE.
        return plan with
        {
            StateExpression = $"CASE WHEN NOT ({guard}) THEN 'unsupported' ELSE ({plan.StateExpression}) END",
            ClassificationCodeExpression = $"CASE WHEN NOT ({guard}) THEN CASE WHEN ({dependent}) "
            + $"THEN '{codePrefix}dependency' ELSE '{codePrefix}unproven' END ELSE NULL END",
            DropLeavesNoGeneratedColumnsExpression = isDrop ? $"{count} = 0" : null,
        };

        string BuildCertificate(
            MySqlGeneratedColumnDependencyContract? candidate
        )
        {
            if (candidate is null
                || !candidate.HasOnlyParsedExpressions)
            {
                return "FALSE";
            }

            var members = candidate.GeneratedColumns;
            if (members.Count == 0)
            {
                return $"{count} = 0";
            }

            var conditions = new string[members.Count + 1];
            conditions[0] = $"{count} = {members.Count.ToString(CultureInfo.InvariantCulture)}";
            // WHY: Store dependency predicates in their reserved slots without changing source-member order.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < members.Count; index++)
            {
                conditions[index + 1] = BuildColumnMatches(
                    table,
                    members[index],
                    isMariaDb,
                    requireNativeOwnerIdentity: true);
            }

            return string.Join(" AND ", conditions);
        }

        static string Combine(
            string left,
            string right
        ) => left == "FALSE"
            ? right
            : right == "FALSE"
                ? left
                : $"({left}) OR ({right})";
    }

    /// <summary>Proves a MariaDB source name absent from every surviving native generated expression.</summary>
    private string BuildMariaDbGeneratedDropSourceAbsence(
        string table,
        string source
    )
    {
        var sourceOwner = "native_source.TABLE_SCHEMA = DATABASE() "
            + $"AND native_source.TABLE_NAME = {Literal(table)} AND "
            + BuildRequiredColumnAddOwnerMatches("native_source.TABLE_SCHEMA", "DATABASE()")
            + " AND " + BuildRequiredColumnAddOwnerMatches("native_source.TABLE_NAME", Literal(table));

        var generatedOwner = "c.TABLE_SCHEMA = DATABASE() "
            + $"AND c.TABLE_NAME = {Literal(table)} "
            + "AND c.TABLE_SCHEMA = native_source.TABLE_SCHEMA AND c.TABLE_NAME = native_source.TABLE_NAME "
            + "AND " + BuildRequiredColumnAddOwnerMatches("c.TABLE_SCHEMA", "native_source.TABLE_SCHEMA")
            + " AND " + BuildRequiredColumnAddOwnerMatches("c.TABLE_NAME", "native_source.TABLE_NAME");

        const string name = "CAST(native_source.COLUMN_NAME AS BINARY)";
        const string expression = "CAST(c.GENERATION_EXPRESSION AS BINARY)";

        // WHY: MariaDB prints the bound physical Item_field name, not the
        // migration's casing. append_identifier either preserves it or doubles
        // the selected quote delimiter. Search all three byte representations:
        // no lexer, collation folding, SQL-mode-dependent literal or UDF call
        // can hide a real reference. Occurrences in literals/functions are
        // intentionally unproven, not interpreted as positive dependencies.
        // MySQL persists its expression before this binding is guaranteed;
        // its existing complete-certificate requirement is retained.
        var absent = $"LOCATE({name}, {expression}) = 0 "
            + $"AND LOCATE(REPLACE({name}, 0x60, 0x6060), {expression}) = 0 "
            + $"AND LOCATE(REPLACE({name}, 0x22, 0x2222), {expression}) = 0";

        // WHY: The explicit source existence prevents a vacuous proof. Native
        // generated membership is captured afresh, independently of optional
        // model facets. Only a true absence result authorizes the fallback.
        return "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS native_source "
            + $"WHERE {sourceOwner} AND native_source.COLUMN_NAME = {Literal(source)} "
            + $"AND CAST(LOWER(native_source.COLUMN_NAME) AS BINARY) = CAST(LOWER({Literal(source)}) AS BINARY) "
            + "AND NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c "
            + $"WHERE {generatedOwner} AND c.GENERATION_EXPRESSION IS NOT NULL "
            + "AND c.GENERATION_EXPRESSION <> '' "
            + "AND CAST(c.COLUMN_NAME AS BINARY) <> CAST(native_source.COLUMN_NAME AS BINARY) "
            + $"AND NOT COALESCE(({absent}), FALSE)))";
    }
}
