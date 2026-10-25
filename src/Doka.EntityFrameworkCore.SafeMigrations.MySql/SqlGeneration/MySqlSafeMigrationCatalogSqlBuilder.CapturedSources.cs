namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    /// <summary>Qualifies native identifier roots before captured SQL can enter a data or executable probe.</summary>
    private MySqlSafeMigrationRuntimePlan ApplyCapturedSourceIdentifierQualification(
        SafeMigrationIntent intent,
        MySqlSafeMigrationRuntimePlan plan,
        bool isMariaDb
    )
    {
        HashSet<string>? roots = null;
        switch (intent)
        {
            case EnsureColumnIntent column:
                Collect(column.Definition.CapturedComputedSql);
                break;
            case AlterColumnIntent column:
                Collect(column.Definition.CapturedComputedSql);
                Collect(column.OldDefinition?.CapturedComputedSql);
                break;
            case EnsureCheckConstraintIntent check:
                Collect(check.Definition.CapturedSql);
                break;
            case EnsureTableIntent table:
                foreach (var column in table.Definition.Columns)
                {
                    Collect(column.CapturedComputedSql);
                }

                foreach (var check in table.Definition.CheckConstraints)
                {
                    Collect(check.CapturedSql);
                }

                break;
        }

        if (roots is null
            || roots.Count == 0
            || plan.IsStaticallyUnsupported)
        {
            return plan;
        }

        IReadOnlyList<MySqlSafeMigrationIdentifierReferenceProof> proofs = [];
        string qualified;
        if (isMariaDb)
        {
            var captured = new MySqlSafeMigrationIdentifierReferenceProof[roots.Count];
            var scope = Guid
                .NewGuid()
                .ToString("N", CultureInfo.InvariantCulture);

            var ordinal = 0;
            foreach (var root in roots)
            {
                captured[ordinal] = new MySqlSafeMigrationIdentifierReferenceProof(
                    MySqlSafeMigrationIdentifierReferenceProof.MarkerPrefix
                    + ordinal.ToString(CultureInfo.InvariantCulture)
                    + "__",
                    root,
                    scope);

                ordinal++;
            }

            proofs = captured;
            qualified = string.Join(
                " AND ",
                captured.Select(proof =>
                    "(NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.KEYWORDS k WHERE k.WORD = "
                    + Literal(proof.RootIdentifier)
                    + $") OR ({proof.Marker}))"));
        }
        else
        {
            qualified = "NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.KEYWORDS k WHERE k.RESERVED = 1 AND "
                + $"k.WORD IN ({string.Join(", ", roots.Select(Literal))}))";
        }

        var ordinaryFailure = plan.StateEvaluationGuardFailureExpression ?? "NULL";
        var ordinaryCode = plan.ClassificationCodeExpression ?? "NULL";

        // WHY: The shared parser proves token roles, not native reservedness.
        // Only unquoted reference heads require this qualification: a reserved
        // terminal after a dot is legal. MariaDB exposes no RESERVED bit, so
        // its suspect heads use a fresh native parse-only certificate instead.
        // The existing guard batch runs before source-containing row SQL is
        // submitted; runtime uses the same predicate before lazy PREPARE.
        return plan with
        {
            StateEvaluationGuardExpression = $"({qualified}) AND ({plan.StateEvaluationGuardExpression})",
            StateEvaluationGuardFailureExpression =
            $"CASE WHEN NOT ({qualified}) THEN 'unsupported' " + $"ELSE ({ordinaryFailure}) END",
            ClassificationCodeExpression =
            $"CASE WHEN NOT ({qualified}) "
            + "THEN 'captured_expression_identifier_keyword_unverified' "
            + $"ELSE ({ordinaryCode}) END",
            Postcondition = $"({qualified}) AND ({plan.Postcondition})",
            RepairPrecondition = $"({qualified}) AND ({plan.RepairPrecondition})",
            RequiresLazyStateEvaluation = true,
            IdentifierReferenceProofs = proofs,
            CapturedSourceSyntaxExpression = isMariaDb ? null : qualified,
        };

        void Collect(
            string? source
        )
        {
            if (source is null
                || !SafeMigrationSqlExpressionParser.TryCollectUnquotedColumnRootIdentifiers(source, out var names))
            {
                return;
            }

            foreach (var name in names)
            {
                roots ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                roots.Add(name);
            }
        }
    }
}
