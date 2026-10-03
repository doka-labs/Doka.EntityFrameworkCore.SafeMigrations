namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Requires an explicit ordered proof after a provider rename.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpaqueProviderTransitionRequiresAnExplicitDependencyProof(bool proven)
    {
        // Arrange
        var proof = Live(SafeMigrationObservedState.Missing);
        var validator = new DependencyProbe(opaqueReplacement: proven ? proof : null);
        var projection = ProjectionAfterDependencyRename(validator);

        // Act
        var analysis = projection.Project(DependencyTableOperation(), Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.Equal(1, validator.OpaqueValidationCount);
        Assert.Equal(proven ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.PrerequisiteMissing,
            analysis.ObservedState);
        if (proven)
        {
            Assert.Same(proof, analysis);
        }
    }

    /// <summary>An opaque SQL effect or invariant rejection cannot be cleared by a provider rename proof.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProviderDependencyProofDoesNotOverrideSqlOrInvariantRejection(bool opaqueSql)
    {
        // Arrange
        var validator = new DependencyProbe(opaqueReplacement: Live(SafeMigrationObservedState.Missing));
        var projection = ProjectionAfterDependencyRename(validator);
        var live = Live(SafeMigrationObservedState.Missing);
        if (opaqueSql)
        {
            projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1;" });
        }
        else
        {
            live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Unsupported,
                SafeMigrationRepairCapability.None, postconditionSatisfied: false, "invariant_unsupported")
            {
                IsInvariantUnsupported = true,
            };
        }

        // Act
        var analysis = projection.Project(DependencyTableOperation(), live);

        // Assert
        Assert.Equal(0, validator.OpaqueValidationCount);
        Assert.Equal(opaqueSql ? SafeMigrationObservedState.PrerequisiteMissing : SafeMigrationObservedState.Unsupported,
            analysis.ObservedState);
    }

    /// <summary>Lets a provider reject dependencies after neutral projection accepts a shape.</summary>
    [Fact]
    public void ProviderDependencyValidationRunsAfterNeutralProjection()
    {
        // Arrange
        var rejected = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Unsupported,
            SafeMigrationRepairCapability.None,
            postconditionSatisfied: false,
            "provider_dependency_conflict");

        var validator = new DependencyProbe(rejected);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: validator);
        var operation = DependencyTableOperation();

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.Same(rejected, analysis);
        Assert.Equal(1, validator.ValidationCount);
        Assert.Equal(SafeMigrationObservedState.Missing, validator.LastProjectedState);
    }

    /// <summary>Preserves invariant rejection without consulting a mutable dependency graph.</summary>
    [Fact]
    public void InvariantRejectionPrecedesProviderDependencyValidation()
    {
        // Arrange
        var validator = new DependencyProbe();
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: validator);
        var live = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Unsupported,
            SafeMigrationRepairCapability.None,
            postconditionSatisfied: false,
            "invariant_unsupported")
        {
            IsInvariantUnsupported = true,
        };

        // Act
        var analysis = projection.Project(DependencyTableOperation(), live);

        // Assert
        Assert.Same(live, analysis);
        Assert.Equal(0, validator.ValidationCount);
    }

    /// <summary>Advances the provider graph only for an accepted decision.</summary>
    [Theory]
    [InlineData(SafeMigrationObservedState.Missing, 1)]
    [InlineData(SafeMigrationObservedState.Matching, 1)]
    [InlineData(SafeMigrationObservedState.Different, 0)]
    public void ProviderDependencyObservationRequiresAcceptedDecision(
        SafeMigrationObservedState state,
        int expectedObservations)
    {
        // Arrange
        var validator = new DependencyProbe();
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: validator);
        var operation = DependencyTableOperation();
        var analysis = Live(state);
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            state,
            operation.Policy,
            analysis.RepairCapability);

        // Act
        projection.Observe(operation, analysis, analysis, decision);

        // Assert
        Assert.Equal(expectedObservations, validator.ObservationCount);
    }

    /// <summary>Notifies the provider before ordinary SQL invalidates generic projected evidence.</summary>
    [Fact]
    public void OrdinarySqlNotifiesProviderDependencyState()
    {
        // Arrange
        var validator = new DependencyProbe();
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: validator);
        var operation = new SqlOperation { Sql = "SELECT 1;" };

        // Act
        projection.ObserveProviderPostcondition(operation);

        // Assert
        Assert.Same(operation, validator.LastProviderOperation);
        Assert.True(projection.HasOpaqueSqlPostcondition);
    }

    /// <summary>Exposes new-table proof together with the data-mutation invalidation bit.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectedTableSourceDoesNotTreatCreatedTableAsPermanentlyEmpty(bool mutateData)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var operation = DependencyTableOperation();
        var analysis = Live(SafeMigrationObservedState.Missing);
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        projection.Observe(operation, analysis, analysis, decision);
        if (mutateData)
        {
            projection.ObserveProviderPostcondition(new InsertDataOperation
            {
                Table = "dependency_items",
                Columns = ["Id"],
                Values = new object[,] { { 1 } },
            });
        }

        // Act
        var found = ((ISafeMigrationProjectedTableSource)projection).TryGetProjectedTableState(
            "dependency_items",
            schema: null,
            out var state);

        // Assert
        Assert.True(found);
        Assert.True(state.IsNewlyCreated);
        Assert.Equal(mutateData, state.HasDataMutation);
        Assert.False(state.PrimaryKeyWasDropped);
    }

    private static SafeMigrationOperation DependencyTableOperation() => new(
        new EnsureTableIntent(
            new ExpectedTableDefinition(
                "dependency_items",
                [new ExpectedColumnDefinition("Id", typeof(int), isNullable: false, storeType: "int")]),
            SafeMigrationTableMode.StrictDefinition),
        SafeMigrationPolicy.ThrowIfDifferent);

    private static SafeMigrationPreflightProjection ProjectionAfterDependencyRename(DependencyProbe validator)
    {
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: validator);
        var operation = new SafeMigrationOperation(new RenameTableIntent("parent", "renamed_parent"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var analysis = Live(SafeMigrationObservedState.Matching);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy, analysis.RepairCapability);

        projection.Observe(operation, analysis, analysis, decision);

        return projection;
    }

    private sealed class DependencyProbe(
        SafeMigrationProviderAnalysis? replacement = null,
        SafeMigrationProviderAnalysis? opaqueReplacement = null) : ISafeMigrationProjectedDependencyAnalyzer
    {
        public int OpaqueValidationCount { get; private set; }

        public int ValidationCount { get; private set; }

        public int ObservationCount { get; private set; }

        public SafeMigrationObservedState? LastProjectedState { get; private set; }

        public MigrationOperation? LastProviderOperation { get; private set; }

        public SafeMigrationProviderAnalysis? ValidateOpaqueProviderPostcondition(
            SafeMigrationOperation operation,
            SafeMigrationProviderAnalysis liveAnalysis,
            ISafeMigrationProjectedColumnSource columns)
        {
            OpaqueValidationCount++;

            return opaqueReplacement;
        }

        public SafeMigrationProviderAnalysis ValidateProjectedOperation(
            SafeMigrationOperation operation,
            SafeMigrationProviderAnalysis projectedAnalysis,
            ISafeMigrationProjectedColumnSource columns)
        {
            ValidationCount++;
            LastProjectedState = projectedAnalysis.ObservedState;

            return replacement ?? projectedAnalysis;
        }

        public void ObserveAcceptedOperation(
            SafeMigrationOperation operation,
            SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis analysis,
            SafeMigrationDecision decision)
            => ObservationCount++;

        public void ObserveProviderOperation(MigrationOperation operation)
            => LastProviderOperation = operation;
    }
}
