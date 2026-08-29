namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private SafeMigrationProviderAnalysis Project(
        EnsureIndexIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
            return AnalyzeDefinition(
                table.Indexes,
                intent.Definition.Name,
                intent.Definition,
                SafeMigrationDefinitionEquivalence.Index);
        }

        return CanProjectMissingIndex(intent, liveAnalysis)
            ? Analysis(SafeMigrationObservedState.Missing)
            : liveAnalysis;
    }

    private SafeMigrationProviderAnalysis Project(
        DropIndexIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => TryGet(intent.Table, intent.Schema, out var table)
        ? Analysis(
            table.Indexes.ContainsKey(intent.Name)
                ? SafeMigrationObservedState.Matching
                : SafeMigrationObservedState.Missing)
        : liveAnalysis;

    private SafeMigrationProviderAnalysis Project(
        RenameIndexIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => TryGet(intent.Table, intent.Schema, out var table)
        ? Analysis(
            !table.Indexes.ContainsKey(intent.Name)
                ? SafeMigrationObservedState.Missing
                : table.Indexes.ContainsKey(intent.NewName)
                    ? SafeMigrationObservedState.Different
                    : SafeMigrationObservedState.Matching)
        : liveAnalysis;

    private void Observe(
        EnsureIndexIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
            table.Indexes[intent.Definition.Name] = intent.Definition;
        }
    }

    private void Observe(
        DropIndexIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.Indexes.Remove(intent.Name);
        }
    }

    private void Observe(
        RenameIndexIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.RenameIndex(intent.Name, intent.NewName);
        }
    }

    private sealed partial class ProjectedTable
    {
        public void RenameIndex(
            string source,
            string target
        )
        {
            if (Indexes.Remove(source, out var index))
            {
                Indexes[target] = Copy(index, name: target);
            }
        }
    }

    private bool CanProjectMissingIndex(
        EnsureIndexIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (liveAnalysis.ObservedState != SafeMigrationObservedState.PrerequisiteMissing
            || !_prerequisites.TryGetValue(
                new TableKey(intent.Definition.Table, intent.Definition.Schema),
                out var prerequisites))
        {
            return false;
        }

        var requiredColumns = SafeMigrationPrerequisiteColumns.Local(intent);
        if (requiredColumns.Any(column => !prerequisites.Columns.ContainsKey(column)))
        {
            return false;
        }

        if (prerequisites.NewlyCreated
            || !intent.Definition.Unique)
        {
            return true;
        }

        if (intent.Definition.NullsDistinct == false)
        {
            return false;
        }

        // A unique index over pre-existing rows is safe only when an earlier
        // operation added a nullable, non-computed key without a non-null
        // default. Every old row then receives NULL, so existing rows cannot
        // collide while providers retain their ordinary NULL-distinct rules.
        return intent.Definition.Keys
            .Where(static key => key.Column is not null)
            .Select(key => prerequisites.Columns[key.Column!])
            .Any(static column => column is
            {
                AddedToExistingTable: true,
                IsNullable: true,
                PreservesNullForExistingRows: true,
                IsComputed: false,
            });
    }

    private static bool PreservesNullForExistingRows(
        SafeMigrationDefaultValue defaultValue
    ) => defaultValue.Kind == SafeMigrationDefaultValueKind.None
        || defaultValue.IsNullLiteral
        || defaultValue is
        {
            Kind: SafeMigrationDefaultValueKind.Sql,
            StructuredExpression: SafeMigrationSqlLiteralExpression { Value: null, },
        };
}
