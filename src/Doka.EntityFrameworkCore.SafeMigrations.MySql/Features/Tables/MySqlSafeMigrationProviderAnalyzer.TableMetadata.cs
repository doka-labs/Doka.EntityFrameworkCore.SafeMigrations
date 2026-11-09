namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    /// <inheritdoc />
    SafeMigrationProviderAnalysis? ISafeMigrationProjectedDependencyAnalyzer.ValidateProjectedTableProviderMetadata(
        EnsureTableIntent intent,
        IReadOnlyList<SafeMigrationProviderAnnotation> acceptedAnnotations,
        bool newlyCreated,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (intent.Definition.ProviderAnnotations.Count == 0
            || MySqlSafeMigrationTableMetadata.IsExplicitInnoDb(acceptedAnnotations))
        {
            // WHY: An unannotated target has no engine requirement; two
            // certified InnoDB spellings have identical native engine meaning.
            return null;
        }

        if (!newlyCreated && liveAnalysis.ObservedState == SafeMigrationObservedState.Matching)
        {
            // WHY: A complete fresh Matching probe already checked this exact
            // live engine; subsequent safe member changes do not change it.
            return null;
        }

        var hasExplicitEngine = acceptedAnnotations.Any(static annotation =>
            StringComparer.Ordinal.Equals(annotation.Name, MySqlSafeMigrationTableMetadata.StorageEngineAnnotation));

        return new SafeMigrationProviderAnalysis(
            hasExplicitEngine
                ? SafeMigrationObservedState.Unsupported
                : SafeMigrationObservedState.PrerequisiteMissing,
            SafeMigrationRepairCapability.None,
            false,
            hasExplicitEngine
                ? "table_storage_engine_different"
                : "projected_table_storage_engine_unproven");
    }
}
