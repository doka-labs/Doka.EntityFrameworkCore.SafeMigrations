namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    private HashSet<(string? Schema, string Table)>? _expiredGeneratedDependencyOwners;

    /// <summary>Rechecks native source changes against accepted members, never an authored future target.</summary>
    private SafeMigrationProviderAnalysis ValidateProjectedGeneratedDependency(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis projected,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (operation.Intent is not (RenameColumnIntent or DropColumnIntent)
            || (_generatedColumnDependencyIsMariaDb && operation.Intent is RenameColumnIntent)
            || projected.IsInvariantUnsupported
            || projected.ObservedState is not (SafeMigrationObservedState.Matching
                or SafeMigrationObservedState.Missing
                or SafeMigrationObservedState.PrerequisiteMissing
                or SafeMigrationObservedState.Unsupported))
        {
            return projected;
        }

        var (table, schema, source, isDrop) = operation.Intent switch
        {
            RenameColumnIntent rename => (rename.Table, rename.Schema, rename.Name, false),
            DropColumnIntent drop => (drop.Table, drop.Schema, drop.Name, true),
            _ => throw new UnreachableException(),
        };

        var prefix = isDrop ? "generated_column_drop_" : "generated_column_rename_";
        var dependency = (columns as ISafeMigrationProjectedTableSource)?.HasProjectedGeneratedColumnDependency(
            table,
            schema,
            source,
            StringComparer.OrdinalIgnoreCase);
        if (dependency == true)
        {
            return projected.ObservedState == SafeMigrationObservedState.Missing
                ? projected
                : Refused(prefix + "dependency");
        }

        if (dependency == false)
        {
            if (projected.ObservedState == SafeMigrationObservedState.Unsupported
                && IsGeneratedDependencyRefusal(projected.Code))
            {
                // WHY: The accepted prefix can remove the last dependent
                // member. Its complete generated-set proof supersedes only
                // this owned dynamic refusal, not unrelated native failures.
                if (isDrop)
                {
                    return new SafeMigrationProviderAnalysis(
                        SafeMigrationObservedState.Matching,
                        SafeMigrationRepairCapability.None,
                        false,
                        "projected_matching");
                }

                if (projected.RenameTargetExists is { } occupied)
                {
                    return new SafeMigrationProviderAnalysis(
                        occupied ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching,
                        SafeMigrationRepairCapability.None,
                        false,
                        occupied ? "projected_different" : "projected_matching");
                }
            }

            return projected;
        }

        if (_expiredGeneratedDependencyOwners?.Contains((NormalizeProofSchema(schema), table)) == true
            && projected.ObservedState == SafeMigrationObservedState.Matching)
        {
            // WHY: A native no-generated-member observation predates accepted
            // generated DDL. Sparse projection cannot turn that old absence
            // into a complete dependency certificate for the new owner state.
            return Refused(prefix + "unproven");
        }

        return projected;

        static SafeMigrationProviderAnalysis Refused(
            string code
        ) => new(SafeMigrationObservedState.Unsupported, SafeMigrationRepairCapability.None, false, code);
    }

    /// <summary>Identifies only dynamic native generated-set refusals that an accepted prefix may supersede.</summary>
    private static bool IsGeneratedDependencyRefusal(
        string code
    ) => code is "generated_column_rename_dependency"
        or "generated_column_rename_unproven"
        or "generated_column_drop_dependency"
        or "generated_column_drop_unproven";

    /// <summary>Expires only native generated-set evidence affected by an accepted structural transition.</summary>
    private void ObserveGeneratedDependency(
        SafeMigrationOperation operation,
        SafeMigrationDecision decision
    )
    {
        if (!_requiresGeneratedDependencyQualification
            || decision.Action is not (SafeMigrationAction.Apply or SafeMigrationAction.Repair))
        {
            return;
        }

        switch (operation.Intent)
        {
            case EnsureColumnIntent column when column.Definition.ComputedExpression is not null
                || column.Definition.ComputedColumnSql is not null:
                Expire(column.Table, column.Schema);
                break;
            case AlterColumnIntent column when column.Definition.ComputedExpression is not null
                || column.Definition.ComputedColumnSql is not null
                || column.OldDefinition?.ComputedExpression is not null
                || column.OldDefinition?.ComputedColumnSql is not null:
                Expire(column.Table, column.Schema);
                break;
            case DropTableIntent table:
                Expire(table.Table, table.Schema);
                break;
            case RenameTableIntent table:
                Expire(table.Name, table.Schema);
                Expire(table.NewName ?? table.Name, table.NewSchema ?? table.Schema);
                break;
        }

        return;

        void Expire(
            string table,
            string? schema
        ) => (_expiredGeneratedDependencyOwners ??= new(PhysicalOwnerComparer)).Add(
            (NormalizeProofSchema(schema), table));
    }
}
