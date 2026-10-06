namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer : ISafeMigrationProjectedColumnAnalyzer
{
    SafeMigrationProviderAnalysis ISafeMigrationProjectedColumnAnalyzer.ValidateProjectedAlterColumn(
        AlterColumnIntent intent,
        ExpectedColumnDefinition source,
        SafeMigrationProjectedAlterColumnContext context,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis
    )
    {
        if (!context.HasCompleteTable)
        {
            if (SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(source, intent.Definition)
                && context.CanReuseLiveProof)
            {
                return projectedAnalysis.RepairCapability == SafeMigrationRepairCapability.Safe
                    ? projectedAnalysis.WithRepairPreservesPhysicalKeys()
                    : projectedAnalysis;
            }

            if (context.PreservesLiveValueDomain
                && !context.HasDataMutation
                && !context.HasForeignKeyDependency
                && HasSafeRetainedAlterValueDomain(intent, source, context.Table, liveAnalysis))
            {
                // WHY: A certified widening does not populate the larger
                // domain. The immutable original declaration remains a row
                // proof only while Core certifies every intervening step.
                return new SafeMigrationProviderAnalysis(
                    SafeMigrationObservedState.Different,
                    SafeMigrationRepairCapability.Safe,
                    postconditionSatisfied: false,
                    "projected_retained_column_domain_safe",
                    SafeMigrationOperationalImpact.TableRewritePossible,
                    differences: null)
                {
                    RepairPreservesLiveValueDomain = true,
                }.WithRepairPreservesPhysicalKeys();
            }

            // WHY: Independent source-bound proofs do not compose by themselves:
            // earlier VARCHAR targets also consume row and composite-key bytes.
            // Recheck their cumulative shape without repeating row scans.
            return liveAnalysis.ObservedState == SafeMigrationObservedState.Different
                && liveAnalysis.RepairCapability == SafeMigrationRepairCapability.Safe
                && !(context.HasDataMutation && liveAnalysis.RequiresLiveDataProof)
                && !context.HasForeignKeyDependency
                && HasSafeCumulativeAlterPhysicalShape(intent, context.Table, liveAnalysis)
                    ? liveAnalysis.WithRepairPreservesPhysicalKeys()
                    : DifferentProjectedTransition();
        }

        if (SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(source, intent.Definition))
        {
            return projectedAnalysis.RepairCapability == SafeMigrationRepairCapability.Safe
                ? projectedAnalysis.WithRepairPreservesPhysicalKeys()
                : projectedAnalysis;
        }

        if (!context.ProvesEmpty
            || context.HasForeignKeyDependency
            || !MySqlProjectedColumnTransition.IsSafe(
                intent, source, context.Table, liveAnalysis.IndexPhysicalEnvironment, _typeMappingSource,
                context.CanReuseCreationCharacterSet))
        {
            return DifferentProjectedTransition();
        }

        return new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe,
            postconditionSatisfied: false,
            "projected_column_transition_safe",
            SafeMigrationOperationalImpact.TableRewritePossible,
            differences: null).WithRepairPreservesPhysicalKeys();
    }

    private static SafeMigrationProviderAnalysis DifferentProjectedTransition() => new(
        SafeMigrationObservedState.Different,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        "projected_column_transition_unproven");
}
