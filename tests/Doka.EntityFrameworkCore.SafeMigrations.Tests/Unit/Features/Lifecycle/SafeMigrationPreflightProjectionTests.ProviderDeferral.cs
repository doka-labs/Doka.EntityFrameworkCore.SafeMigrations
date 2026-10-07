namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>A deferred key is not accepted, but a dependent FK must await the ordered runtime result.</summary>
    [Fact]
    public void DeferredProviderKey_RetainsConditionalDependencyOriginWithoutAcceptingKey()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var foreignKey = new SafeMigrationOperation(new EnsureForeignKeyIntent(
            new ExpectedForeignKeyDefinition("FK_children_parent", "children", ["ParentId"],
                "parents", ["Id"])), SafeMigrationPolicy.ThrowIfDifferent);

        var missing = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.PrerequisiteMissing,
            SafeMigrationRepairCapability.None, false, "foreign_key_prerequisite");

        // Act
        projection.ObserveDeferredProviderOperation(1);
        var analysis = projection.Project(foreignKey, missing);

        // Assert
        Assert.Equal("projected_provider_postcondition_unknown", analysis.Code);
        Assert.Equal(1, analysis.ProviderDeferredOriginOrdinal);
        Assert.False(analysis.PostconditionSatisfied);
        Assert.Equal(SafeMigrationRepairCapability.None, analysis.RepairCapability);
    }

    /// <summary>Later deferred effects cannot replace the first missing postcondition with invented certainty.</summary>
    [Fact]
    public void DeferredProviderBoundary_RetainsFirstConditionalOrigin()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var column = new SafeMigrationOperation(new EnsureColumnIntent("children",
            new ExpectedColumnDefinition("Id", typeof(int), false, "int")), SafeMigrationPolicy.ThrowIfDifferent);

        var matching = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None, true, "matching");

        // Act
        projection.ObserveDeferredProviderOperation(1);
        projection.ObserveDeferredProviderOperation(3);
        var analysis = projection.Project(column, matching);

        // Assert
        Assert.Equal(1, analysis.ProviderDeferredOriginOrdinal);
        Assert.False(analysis.PostconditionSatisfied);
    }

    /// <summary>Immutable supported catalog states cannot establish a postcondition after deferred structural DDL.</summary>
    /// <param name="state">The supported state captured before the ordered stream executes.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Missing)]
    [InlineData(SafeMigrationObservedState.Matching)]
    [InlineData(SafeMigrationObservedState.Different)]
    [InlineData(SafeMigrationObservedState.DataBlocked)]
    [InlineData(SafeMigrationObservedState.PrerequisiteMissing)]
    public void DeferredProviderBoundary_DoesNotReuseSupportedSnapshotStates(SafeMigrationObservedState state)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var column = new SafeMigrationOperation(new EnsureColumnIntent("values",
            new ExpectedColumnDefinition("Id", typeof(int), false, "int")), SafeMigrationPolicy.ThrowIfDifferent);

        var snapshot = new SafeMigrationProviderAnalysis(state,
            SafeMigrationRepairCapability.None, state == SafeMigrationObservedState.Matching, "snapshot_state");

        // Act
        projection.ObserveDeferredProviderOperation(1);
        var analysis = projection.Project(column, snapshot);

        // Assert
        Assert.Equal("projected_provider_postcondition_unknown", analysis.Code);
        Assert.Equal(1, analysis.ProviderDeferredOriginOrdinal);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.False(analysis.PostconditionSatisfied);
        Assert.Equal(SafeMigrationRepairCapability.None, analysis.RepairCapability);
    }

    /// <summary>Deferred safe DDL does not grant missing permissions or remove unsupported provider contracts.</summary>
    /// <param name="invariant">Whether the live provider also attached the invariant certificate.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredProviderBoundary_PreservesUnsupportedContracts(bool invariant)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var check = new SafeMigrationOperation(new EnsureCheckConstraintIntent(
            new ExpectedCheckConstraintDefinition("CK_values", "values", "Value>=0")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var unsupported = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Unsupported,
            SafeMigrationRepairCapability.None, false, "check_row_data_unproven")
        {
            IsInvariantUnsupported = invariant,
        };

        // Act
        projection.ObserveDeferredProviderOperation(1);
        var analysis = projection.Project(check, unsupported);

        // Assert
        Assert.Same(unsupported, analysis);
        Assert.Null(analysis.ProviderDeferredOriginOrdinal);
    }

    /// <summary>Authored SQL keeps its actual opaque origin instead of becoming a provider-DDL descendant.</summary>
    [Fact]
    public void DeferredProviderBoundary_DoesNotOverwriteActualSqlUncertainty()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var column = new SafeMigrationOperation(new EnsureColumnIntent("values",
            new ExpectedColumnDefinition("Id", typeof(int), false, "int")), SafeMigrationPolicy.ThrowIfDifferent);

        var matching = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None, true, "matching");

        // Act
        projection.ObserveDeferredProviderOperation(1);
        projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1;" });
        var analysis = projection.Project(column, matching);

        // Assert
        Assert.True(analysis.IsOpaqueProjectionUnknown);
        Assert.Null(analysis.ProviderDeferredOriginOrdinal);
        Assert.Equal("projected_structure_state_unknown", analysis.Code);
    }
}
