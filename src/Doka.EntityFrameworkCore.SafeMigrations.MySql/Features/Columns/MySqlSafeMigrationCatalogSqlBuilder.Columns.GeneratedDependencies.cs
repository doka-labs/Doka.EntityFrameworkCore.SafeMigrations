namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    /// <summary>Requires exact surviving native membership before a dependency-sensitive column change.</summary>
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
        var owner = BuildRequiredColumnAddOwnerMatches("c.TABLE_SCHEMA", "DATABASE()")
            + " AND "
            + BuildRequiredColumnAddOwnerMatches("c.TABLE_NAME", Literal(table));

        var generated = "c.GENERATION_EXPRESSION IS NOT NULL AND c.GENERATION_EXPRESSION <> ''";
        if (isDrop)
        {
            // WHY: Removing a generated member itself is legal. Only members
            // surviving this exact DROP can retain a dependency on its source.
            generated += " AND c.COLUMN_NAME <> " + Literal(source);
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

        // WHY: A separately certified whole model can complement an authored
        // prefix, never dilute a proven positive dependency or union members
        // from two incomplete shapes into a fabricated complete native set.
        var guard = $"(NOT ({sourceExists})) OR (({permit}) AND NOT ({dependent}))";
        var codePrefix = isDrop ? "generated_column_drop_" : "generated_column_rename_";

        // WHY: MySQL validates generated dependencies only at ALTER execution,
        // not at PREPARE. A token search cannot certify an opaque native
        // expression. The authored prefix is conditional evidence: every live
        // surviving generated member must match its full expected contract
        // before its parsed dependency can classify this single-column DDL.
        // An authored reference alone cannot describe drifted native SQL. This
        // catalog-only decision belongs in the ordinary state expression, so
        // runtime cannot bypass it and analysis needs no extra guard batch.
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
}
