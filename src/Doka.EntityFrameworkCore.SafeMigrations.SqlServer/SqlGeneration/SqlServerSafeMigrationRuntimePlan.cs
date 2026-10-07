namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>Represents one SQL Server catalog classification and execution contract.</summary>
/// <param name="StateExpression">The scalar SQL expression returning a state name.</param>
/// <param name="Postcondition">The catalog-only predicate for the final state.</param>
/// <param name="RepairCapability">The provider-proven repair capability.</param>
/// <param name="RepairPrecondition">The predicate gating any repair.</param>
/// <param name="UnsupportedCode">The stable code for an unsupported contract.</param>
internal sealed record SqlServerSafeMigrationRuntimePlan(
    string StateExpression,
    string Postcondition,
    SafeMigrationRepairCapability RepairCapability,
    string RepairPrecondition,
    string? UnsupportedCode = null
)
{
    /// <summary>Identifies an immutable unsupported physical table engine.</summary>
    internal const string PhysicalTableUnsupportedCode = "physical_table_unproven";

    /// <summary>Identifies an unrepresentable typed default before any data or DDL evaluation.</summary>
    internal const string DefaultValueUnsupportedCode = "default_value_unrepresentable";

    /// <summary>Identifies an authored column collation absent from the active engine.</summary>
    internal const string ColumnCollationUnsupportedCode = "column_collation_unproven";

    /// <summary>Identifies a filtered-index predicate without a proven physical column and constant contract.</summary>
    internal const string IndexFilterUnsupportedCode = "index_filter_unproven";

    /// <summary>Gets the catalog-only physical-engine boundary checked before prerequisites or data.</summary>
    public string? PhysicalTableSupportExpression { get; init; }

    /// <summary>Gets the non-throwing scalar conversion proof shared by catalog and runtime guards.</summary>
    public string? DefaultValueSupportExpression { get; init; }

    /// <summary>Gets whether an authored collation must be proved before its scalar conversion can bind.</summary>
    public bool DefaultValueSupportRequiresDelayedBinding { get; init; }

    /// <summary>Gets the catalog-only authored column collation existence proof.</summary>
    public string? ColumnCollationSupportExpression { get; init; }

    /// <summary>Gets the catalog-only predicate-column and non-throwing constant conversion proof.</summary>
    public string? IndexFilterSupportExpression { get; init; }

    /// <summary>Gets a metadata-only column-addition layout failure code, or SQL NULL when admissible.</summary>
    public string? ColumnLayoutFailureExpression { get; init; }

    /// <summary>Gets the catalog-only prerequisite predicate.</summary>
    public string PrerequisiteExpression { get; init; } = "1";

    /// <summary>Gets a metadata-only failure code safe to evaluate before delayed state binding.</summary>
    public string? PrerequisiteFailureCodeExpression { get; init; }

    /// <summary>Gets the immediate postcondition for an applied operation.</summary>
    public string? ExecutionPostcondition { get; init; }

    /// <summary>Gets the catalog-only guard required before state evaluation.</summary>
    public string StateEvaluationGuardExpression { get; init; } = "1";

    /// <summary>Gets the state returned when the state-evaluation guard fails.</summary>
    public string? StateEvaluationGuardFailureExpression { get; init; }

    /// <summary>Gets an analysis-only guard using stable local parameters before delayed row binding.</summary>
    public string? AnalysisOuterStateGuardExpression { get; init; }

    /// <summary>Gets the local guarded failure classifier before delayed row binding.</summary>
    public string? AnalysisOuterStateGuardFailureExpression { get; init; }

    /// <summary>Gets an optional stable classification-code expression.</summary>
    public string? ClassificationCodeExpression { get; init; }

    /// <summary>Gets whether this operation can never produce a safe baseline.</summary>
    public bool IsStaticallyUnsupported { get; init; }

    /// <summary>Gets whether rows or validated authored facets require delayed SQL Server name binding.</summary>
    public bool RequiresDelayedBinding { get; init; }

    /// <summary>Gets whether classification reads the separately protected expression-dependency catalog.</summary>
    public bool RequiresExpressionDependencyRead { get; init; }

    /// <summary>Gets invocation-local source bindings retained only by analysis plans.</summary>
    public IReadOnlyList<SqlServerCatalogParameterValue> AnalysisParameters { get; init; } = [];

    /// <summary>Gets statement-local catalog setup executed in the same scope as state evaluation.</summary>
    public string? CatalogPreambleSql { get; init; }

    /// <summary>Gets SQL that stamps a freshly applied provider contract before postflight.</summary>
    public string? PostApplySql { get; init; }

    /// <summary>Gets the expected impact of an accepted repair.</summary>
    public SafeMigrationOperationalImpact RepairOperationalImpact { get; init; }
        = SafeMigrationOperationalImpact.Unknown;

    /// <summary>Gets bounded catalog-only difference evidence.</summary>
    public string? DiagnosticEvidenceExpression { get; init; }

    /// <summary>Gets a bounded constant difference when catalog SQL is unnecessary.</summary>
    public SafeMigrationFacetDifference? DifferentDifference { get; init; }

    /// <summary>Gets the resolved physical object name for a semantic match.</summary>
    public string? MatchedObjectNameExpression { get; init; }

    /// <summary>Gets compact model-managed row evidence.</summary>
    public string? ModelManagedRowEvidenceExpression { get; init; }

    /// <summary>Gets compact model-managed dependency counts.</summary>
    public string? ModelManagedDependencyCountsExpression { get; init; }

    /// <summary>Gets the expected number of row-state entries.</summary>
    public int ModelManagedRowCount { get; init; }

    /// <summary>Gets the expected number of dependency-count entries.</summary>
    public int ModelManagedDependencyCount { get; init; }

    /// <summary>Gets whether a nullable-to-required change requires row evidence.</summary>
    public bool MayRequireNullabilityDataProof { get; init; }

    /// <summary>Gets whether a nonmatching classification depends on a current row-safety proof.</summary>
    public bool RequiresLiveDataProof { get; init; }

    /// <summary>Renders the state expression without a separate data probe.</summary>
    /// <returns>The scalar state expression.</returns>
    public string RenderStateExpression() => StateExpression;

    /// <summary>Renders the repair predicate without a separate data probe.</summary>
    /// <returns>The repair predicate.</returns>
    public string RenderRepairPrecondition() => RepairPrecondition;

    /// <summary>Renders the optional provider classification code.</summary>
    /// <returns>The scalar code expression, or null.</returns>
    public string? RenderClassificationCodeExpression() => ClassificationCodeExpression;
}
