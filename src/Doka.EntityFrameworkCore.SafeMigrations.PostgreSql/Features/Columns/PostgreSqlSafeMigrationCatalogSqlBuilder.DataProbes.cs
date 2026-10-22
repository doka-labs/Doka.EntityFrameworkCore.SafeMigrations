namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    /// <summary>Builds only narrowing evidence for the analyzer's discovery and bounded eligibility phases.</summary>
    /// <param name="operation">The immutable authored operation.</param>
    /// <param name="includeTransitionEvidence">Whether this phase needs the physical transition predicate.</param>
    /// <returns>The full plan's complete data-probe record, or null when no probe is authorized.</returns>
    internal PostgreSqlSafeMigrationDataProbe? BuildDataProbe(
        SafeMigrationOperation operation,
        bool includeTransitionEvidence = false)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (RequiresFullDataProbePlan(operation.Intent))
        {
            // WHY: Source and generated-expression binders can replace a full
            // plan with Unsupported after initial capability checks. They own
            // that authority; do not duplicate or bypass it in this fast path.
            return Build(operation, includeTransitionEvidence: includeTransitionEvidence).DataProbe;
        }

        var intent = SafeMigrationCapturedExpressionNormalization.NormalizeIntent(operation.Intent, true);
        if (operation.GetAnnotations().Any() || GetUnsupportedNativeFeature(intent) is not null)
        {
            return null;
        }

        var repairRequested = operation.Policy == SafeMigrationPolicy.RepairIfSafe;

        // WHY: Discovery consumes only this immutable proof, not classifier,
        // matching, diagnostics or lock SQL. The same transition selectors below
        // also feed the final full plan, preserving its eligibility authority.
        return intent switch
        {
            EnsureColumnIntent value => BuildEnsureColumnRepairTransition(value,
                EnsureColumnRepairCapability(value, repairRequested), includeTransitionEvidence).DataProbe,
            AlterColumnIntent value => BuildAlterColumnRepairTransition(value, repairRequested,
                IsSameTypeAlterRepair(value, repairRequested), includeTransitionEvidence).DataProbe,
            _ => null,
        };
    }

    /// <summary>Leaves every computed or captured old/target contract on its existing complete binding path.</summary>
    private static bool RequiresFullDataProbePlan(SafeMigrationIntent intent) => intent switch
    {
        EnsureColumnIntent value => HasComputedDataProbeSource(value.Definition),
        AlterColumnIntent value => HasComputedDataProbeSource(value.Definition)
            || value.OldDefinition is { } old && HasComputedDataProbeSource(old),
        _ => false,
    };

    /// <summary>Recognizes source-bearing forms before normalization without parsing or rendering them twice.</summary>
    private static bool HasComputedDataProbeSource(ExpectedColumnDefinition definition) =>
        definition.ComputedColumnSql is not null || definition.ComputedExpression is not null
        || definition.CapturedComputedSql is not null;

    /// <summary>Shares the complete provider capability gate used by final EnsureColumn classification.</summary>
    private static SafeMigrationRepairCapability EnsureColumnRepairCapability(
        EnsureColumnIntent intent,
        bool repairRequested) => repairRequested
        && PostgreSqlSafeMigrationColumnMetadata.CanSafelyConverge(intent.Definition)
            ? SafeMigrationRepairCapability.Safe
            : SafeMigrationRepairCapability.None;

    /// <summary>Shares EnsureColumn transition selection without constructing unrelated plan SQL.</summary>
    private PostgreSqlColumnRepairTransition BuildEnsureColumnRepairTransition(
        EnsureColumnIntent intent,
        SafeMigrationRepairCapability repairCapability,
        bool includeTransitionEvidence) => repairCapability == SafeMigrationRepairCapability.Safe
            ? BuildColumnRepairTransition(intent.Table, intent.Schema, intent.Definition, includeTransitionEvidence)
            : PostgreSqlColumnRepairTransition.None;

    /// <summary>Shares the original intrinsic contract check used by final AlterColumn classification.</summary>
    private static bool IsSameTypeAlterRepair(AlterColumnIntent intent, bool repairRequested) => repairRequested
        && intent.OldDefinition is not null
        && SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(intent.OldDefinition, intent.Definition);

    /// <summary>Shares the original VARCHAR-only AlterColumn transition selection and exact source contract.</summary>
    private PostgreSqlColumnRepairTransition BuildAlterColumnRepairTransition(
        AlterColumnIntent intent,
        bool repairRequested,
        bool sameTypeRepair,
        bool includeTransitionEvidence) => repairRequested
        && !sameTypeRepair
        && intent.OldDefinition is not null
        && CanAlterVarcharLength(intent.OldDefinition, intent.Definition)
            ? BuildColumnRepairTransition(intent.Table, intent.Schema, intent.Definition,
                includeTransitionEvidence, intent.OldDefinition)
            : PostgreSqlColumnRepairTransition.None;
}
