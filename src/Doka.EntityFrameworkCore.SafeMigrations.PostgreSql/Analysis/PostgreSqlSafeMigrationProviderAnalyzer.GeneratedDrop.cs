namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationProviderAnalyzer
{
    private HashSet<SafeMigrationOperation>? _nativeGeneratedDropDependencies;
    private HashSet<SafeMigrationOperation>? _nativeGeneratedDropCapabilityRefusals;
    private bool _generatedDropCatalogReadable;

    /// <summary>Retains native refusal until accepted current membership disproves the generated dependency.</summary>
    private SafeMigrationProviderAnalysis? QualifyProjectedGeneratedSourceDrop(
        SafeMigrationOperation operation, SafeMigrationProviderAnalysis analysis,
        ISafeMigrationProjectedColumnSource columns)
    {
        if (operation.Intent is not DropColumnIntent drop
            || analysis.ObservedState == SafeMigrationObservedState.Missing
            || analysis.ObservedState == SafeMigrationObservedState.Unsupported
            || columns is not ISafeMigrationProjectedTableSource tables)
        {
            return null;
        }

        if (_nativeGeneratedDropCapabilityRefusals?.Contains(operation) == true)
        {
            // WHY: Accepted source presence proves no catalog privilege,
            // explicit LOCK authority or independently qualified owner epoch.
            // A new owner cannot invent those facts either; a fresh analysis
            // or runtime check must establish them without implicit grants.
            return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Unsupported,
                SafeMigrationRepairCapability.None, false, "generated_column_drop_unproven");
        }

        var dependency = tables.HasProjectedGeneratedColumnDependency(
            drop.Table, drop.Schema, drop.Name, StringComparer.Ordinal);
        var nativeDependency = _nativeGeneratedDropDependencies?.Contains(operation) == true
            || analysis.Code == "generated_column_drop_dependency";
        if (dependency == true || nativeDependency && dependency is null)
        {
            // WHY: A sparse accepted source can replace its initial Different
            // state with Matching. That does not erase a physical generated
            // dependent; only complete current accepted membership can do so.
            return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
                SafeMigrationRepairCapability.None, false, "generated_column_drop_dependency");
        }

        return dependency == false && nativeDependency
            ? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
                SafeMigrationRepairCapability.None, false, "projected_generated_column_drop_safe")
            : null;
    }

    /// <summary>
    /// Retains original-operation dependency and capability refusals without another catalog inventory.
    /// </summary>
    private static (
        HashSet<SafeMigrationOperation>? Dependencies,
        HashSet<SafeMigrationOperation>? CapabilityRefusals) CaptureNativeGeneratedDropEvidence(
        IReadOnlyList<SafeMigrationOperation> operations, IReadOnlyList<SafeMigrationProviderAnalysis> results)
    {
        HashSet<SafeMigrationOperation>? captured = null;
        HashSet<SafeMigrationOperation>? capabilities = null;
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            if (operations[ordinal].Intent is DropColumnIntent
                && results[ordinal].Code == "generated_column_drop_dependency")
            {
                (captured ??= new HashSet<SafeMigrationOperation>(ReferenceEqualityComparer.Instance))
                    .Add(operations[ordinal]);
            }
            else if (operations[ordinal].Intent is DropColumnIntent
                && results[ordinal].ObservedState == SafeMigrationObservedState.Unsupported
                && results[ordinal].Code == "generated_column_drop_unproven")
            {
                (capabilities ??= new HashSet<SafeMigrationOperation>(ReferenceEqualityComparer.Instance))
                    .Add(operations[ordinal]);
            }
        }

        return (captured, capabilities);
    }

    /// <summary>Consumes the exact private DROP certificate without exposing its transport code to reports.</summary>
    internal static bool DecodeGeneratedDropCompleteness(
        SafeMigrationIntent intent, SafeMigrationObservedState state, ref string code)
    {
        if (code != PostgreSqlSafeMigrationCatalogSqlBuilder.EmptyGeneratedDropSetCode)
        {
            return false;
        }

        if (intent is not DropColumnIntent || state != SafeMigrationObservedState.Matching)
        {
            throw new InvalidOperationException(
                "The PostgreSQL classifier returned invalid generated DROP completeness evidence.");
        }

        code = ClassificationCode(state);

        return true;
    }
}
