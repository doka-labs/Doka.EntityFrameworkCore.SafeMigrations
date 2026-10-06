namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationModelManagedDataTests
{
    /// <summary>Unconfined managed writes revoke earlier rows while retaining their own guarded result.</summary>
    /// <param name="kind">The managed write family.</param>
    /// <param name="knownExistingTable">Whether the write target has an existing-table structural proof.</param>
    [Theory]
    [InlineData("ensure", false)]
    [InlineData("update", false)]
    [InlineData("delete", false)]
    [InlineData("ensure", true)]
    [InlineData("update", true)]
    [InlineData("delete", true)]
    public void SideEffectsManagedMutationRevokesEarlierRowsAndKeepsCurrentGuard(
        string kind,
        bool knownExistingTable
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        Accept(projection, RoleEnsure(), SafeMigrationObservedState.Matching);

        if (knownExistingTable)
        {
            Accept(projection,
                Operation(new EnsureTableIntent(
                    new ExpectedTableDefinition("other_roles", RoleTable().Columns),
                    SafeMigrationTableMode.ConvergenceContainer)),
                SafeMigrationObservedState.Matching);
        }

        var mutation = SideEffectsManagedOperation(kind, "other_roles");
        var version = projection.ModelManagedDataChangeVersion;

        // Act
        ObserveAppliedMutation(projection, mutation,
            kind == "ensure" ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.TransitionReady);

        var earlier = projection.Project(RoleEnsure(), Live(SafeMigrationObservedState.Matching));
        var current = projection.Project(mutation, Live(SafeMigrationObservedState.Different));

        // Assert
        AssertSideEffectsUnknown(earlier);
        Assert.Equal(kind == "delete" ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Matching,
            current.ObservedState);
        Assert.True(current.PostconditionSatisfied);
        Assert.False(current.IsModelManagedProjectionUnknown);
        Assert.True(projection.ModelManagedDataChangeVersion > version);
    }

    /// <summary>Every stale live row classification becomes unknown after an unrelated data mutation.</summary>
    /// <param name="kind">The operation consuming immutable live evidence.</param>
    /// <param name="liveState">The stale classification captured before the preceding write.</param>
    [Theory]
    [InlineData("ensure", SafeMigrationObservedState.Matching)]
    [InlineData("ensure", SafeMigrationObservedState.Missing)]
    [InlineData("ensure", SafeMigrationObservedState.Different)]
    [InlineData("ensure", SafeMigrationObservedState.DataBlocked)]
    [InlineData("ensure", SafeMigrationObservedState.TransitionReady)]
    [InlineData("update", SafeMigrationObservedState.Matching)]
    [InlineData("update", SafeMigrationObservedState.Missing)]
    [InlineData("update", SafeMigrationObservedState.Different)]
    [InlineData("update", SafeMigrationObservedState.DataBlocked)]
    [InlineData("update", SafeMigrationObservedState.TransitionReady)]
    [InlineData("delete", SafeMigrationObservedState.Matching)]
    [InlineData("delete", SafeMigrationObservedState.Missing)]
    [InlineData("delete", SafeMigrationObservedState.Different)]
    [InlineData("delete", SafeMigrationObservedState.DataBlocked)]
    [InlineData("delete", SafeMigrationObservedState.TransitionReady)]
    public void SideEffectsStaleLiveClassificationsRequireRuntimeValidation(
        string kind,
        SafeMigrationObservedState liveState
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var operation = SideEffectsManagedOperation(kind, "roles");
        var live = Live(liveState);

        projection.ObserveProviderPostcondition(SideEffectsProviderMutation("insert"));

        // Act
        var projected = projection.Project(operation, live);

        // Assert
        AssertSideEffectsUnknown(projected);
        Assert.NotSame(live, projected);
    }

    /// <summary>An update proves its key and new values, not untouched columns from before its triggers.</summary>
    [Fact]
    public void SideEffectsGuardedUpdateDoesNotCopyUnmodifiedColumns()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var original = Operation(new EnsureModelManagedDataIntent(
            "roles", ["id"], ["int"], ["id", "name", "description"],
            ["int", "varchar(64)", "varchar(256)"],
            new object?[,] { { 1, "administrator", "Built-in role" } }, schema: null, uniqueKeys: null));

        Accept(projection, original, SafeMigrationObservedState.Matching);

        var keyOnly = Operation(new EnsureModelManagedDataIntent(
            "roles", ["id"], ["int"], ["id"], ["int"],
            new object?[,] { { 1 } }, schema: null, uniqueKeys: null));

        var untouchedColumn = Operation(new EnsureModelManagedDataIntent(
            "roles", ["id"], ["int"], ["id", "description"], ["int", "varchar(256)"],
            new object?[,] { { 1, "Built-in role" } }, schema: null, uniqueKeys: null));

        // Act
        ObserveAppliedMutation(projection, RoleUpdate(), SafeMigrationObservedState.TransitionReady);

        var guarded = projection.Project(RoleEnsure(1, "owner", false), Live(SafeMigrationObservedState.Different));
        var key = projection.Project(keyOnly, Live(SafeMigrationObservedState.Missing));
        var untouched = projection.Project(untouchedColumn, Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, guarded.ObservedState);
        Assert.True(guarded.PostconditionSatisfied);
        Assert.Equal(SafeMigrationObservedState.Matching, key.ObservedState);
        Assert.True(key.PostconditionSatisfied);
        AssertSideEffectsUnknown(untouched);
    }

    /// <summary>Deferred writes revoke earlier facts without fabricating a future write's postcondition.</summary>
    [Fact]
    public void SideEffectsDeferredWriteRevokesProofWithoutInventingRows()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        Accept(projection, RoleEnsure(), SafeMigrationObservedState.Matching);

        var version = projection.ModelManagedDataChangeVersion;

        // Act
        projection.ObserveDeferredModelManagedMutation();

        var earlier = projection.Project(RoleEnsure(), Live(SafeMigrationObservedState.Matching));
        var unwitnessed = projection.Project(RoleEnsure("other_roles"), Live(SafeMigrationObservedState.Matching));

        // Assert
        AssertSideEffectsUnknown(earlier);
        AssertSideEffectsUnknown(unwitnessed);
        Assert.True(projection.ModelManagedDataChangeVersion > version);
    }

    /// <summary>A confined insert preserves earlier rows and completeness for other keys in its new table.</summary>
    [Fact]
    public void SideEffectsConfinedInsertPreservesEarlierRowsAndMissingKeys()
    {
        // Arrange
        var projection = ProjectionWithNewRoleTable();

        ObserveAppliedMutation(projection, RoleEnsure(), SafeMigrationObservedState.PrerequisiteMissing);

        var version = projection.ModelManagedDataChangeVersion;

        // Act
        ObserveAppliedMutation(projection, RoleEnsure(2, "member", false),
            SafeMigrationObservedState.PrerequisiteMissing);

        var earlier = projection.Project(RoleEnsure(), Live(SafeMigrationObservedState.Different));
        var missing = projection.Project(RoleEnsure(3, "auditor", false), Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, earlier.ObservedState);
        Assert.True(earlier.PostconditionSatisfied);
        Assert.Equal(SafeMigrationObservedState.Missing, missing.ObservedState);
        Assert.False(missing.IsModelManagedProjectionUnknown);
        Assert.True(projection.ModelManagedDataChangeVersion > version);
    }

    /// <summary>Rejected and no-op managed operations cannot simulate trigger effects or advance evidence.</summary>
    /// <param name="state">The immutable classification of the operation that does not execute.</param>
    /// <param name="expectedAction">The corresponding non-executing planner action.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Matching, SafeMigrationAction.NoOp)]
    [InlineData(SafeMigrationObservedState.Different, SafeMigrationAction.RejectDifferent)]
    [InlineData(SafeMigrationObservedState.DataBlocked, SafeMigrationAction.RejectDataBlocked)]
    [InlineData(SafeMigrationObservedState.PrerequisiteMissing, SafeMigrationAction.RejectPrerequisiteMissing)]
    public void SideEffectsNonExecutingManagedOperationsPreserveEarlierProofs(
        SafeMigrationObservedState state,
        SafeMigrationAction expectedAction
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        Accept(projection, RoleEnsure(), SafeMigrationObservedState.Matching);

        var operation = RoleEnsure("other_roles");
        var live = Live(state);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, state, operation.Policy);
        var version = projection.ModelManagedDataChangeVersion;

        // Act
        projection.Observe(operation, live, live, decision);

        var earlier = projection.Project(RoleEnsure(), Live(SafeMigrationObservedState.Different));

        // Assert
        Assert.Equal(expectedAction, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Matching, earlier.ObservedState);
        Assert.True(earlier.PostconditionSatisfied);
        Assert.Equal(version, projection.ModelManagedDataChangeVersion);
    }

    /// <summary>Without preceding writes, real provider conflicts remain authoritative rejections.</summary>
    /// <param name="kind">The managed operation family.</param>
    /// <param name="state">The unchanged conflict classification.</param>
    [Theory]
    [InlineData("ensure", SafeMigrationObservedState.Different)]
    [InlineData("ensure", SafeMigrationObservedState.DataBlocked)]
    [InlineData("update", SafeMigrationObservedState.Different)]
    [InlineData("update", SafeMigrationObservedState.DataBlocked)]
    [InlineData("delete", SafeMigrationObservedState.Different)]
    [InlineData("delete", SafeMigrationObservedState.DataBlocked)]
    public void SideEffectsUnchangedLiveConflictsRemainRejected(
        string kind,
        SafeMigrationObservedState state
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var operation = SideEffectsManagedOperation(kind, "roles");
        var live = Live(state);

        // Act
        var projected = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind, projected.ObservedState, operation.Policy, projected.RepairCapability);

        // Assert
        Assert.Same(live, projected);
        Assert.False(projected.IsModelManagedProjectionUnknown);
        Assert.Equal(state == SafeMigrationObservedState.DataBlocked
            ? SafeMigrationAction.RejectDataBlocked
            : SafeMigrationAction.RejectDifferent, decision.Action);
        Assert.Equal(0, projection.ModelManagedDataChangeVersion);
    }

    /// <summary>All typed provider writes discard managed exact-row facts in unrelated tables.</summary>
    /// <param name="kind">The provider DML family.</param>
    [Theory]
    [InlineData("insert")]
    [InlineData("update")]
    [InlineData("delete")]
    public void SideEffectsProviderMutationRevokesEarlierExactRows(string kind)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        Accept(projection, RoleEnsure(), SafeMigrationObservedState.Matching);

        var version = projection.ModelManagedDataChangeVersion;

        // Act
        projection.ObserveProviderPostcondition(SideEffectsProviderMutation(kind));

        var projected = projection.Project(RoleEnsure(), Live(SafeMigrationObservedState.Matching));

        // Assert
        AssertSideEffectsUnknown(projected);
        Assert.True(projection.ModelManagedDataChangeVersion > version);
    }

    /// <summary>Only repairs certified as mutating data revoke unrelated exact-row facts.</summary>
    /// <param name="mutatesData">Whether the accepted repair can execute row writes.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SideEffectsRepairInvalidatesRowsOnlyWhenItMutatesData(bool mutatesData)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        Accept(projection, RoleEnsure(), SafeMigrationObservedState.Matching);

        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent("other_roles",
                new ExpectedColumnDefinition("name", typeof(string), false, "varchar(64)")),
            SafeMigrationPolicy.RepairIfSafe);

        var analysis = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different, SafeMigrationRepairCapability.Safe, false, "repair_test")
        {
            RepairMutatesData = mutatesData,
        };

        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind, analysis.ObservedState, operation.Policy, analysis.RepairCapability);

        var version = projection.ModelManagedDataChangeVersion;

        // Act
        projection.Observe(operation, analysis, analysis, decision);

        var projected = projection.Project(RoleEnsure(), Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, decision.Action);

        if (mutatesData)
        {
            AssertSideEffectsUnknown(projected);
            Assert.True(projection.ModelManagedDataChangeVersion > version);
        }
        else
        {
            Assert.Equal(SafeMigrationObservedState.Matching, projected.ObservedState);
            Assert.True(projected.PostconditionSatisfied);
            Assert.Equal(version, projection.ModelManagedDataChangeVersion);
        }
    }

    /// <summary>Global mutations revoke candidate-key row proofs as well as exact-row classifications.</summary>
    /// <param name="kind">The unconfined managed write family.</param>
    [Theory]
    [InlineData("ensure")]
    [InlineData("update")]
    [InlineData("delete")]
    public void SideEffectsManagedMutationRevokesEarlierUniqueKeyProof(string kind)
    {
        // Arrange
        var projection = ProjectionWithNewRoleTable();

        ObserveAppliedMutation(projection, RoleEnsure(1, "administrator", true),
            SafeMigrationObservedState.PrerequisiteMissing);

        var index = Operation(new EnsureIndexIntent(new ExpectedIndexDefinition(
            "ux_roles_name", "roles", [new ExpectedIndexKeyDefinition(column: "name")], unique: true)));

        var mutation = SideEffectsManagedOperation(kind, "other_roles");

        // Act
        ObserveAppliedMutation(projection, mutation,
            kind == "ensure" ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.TransitionReady);

        var projected = projection.Project(index, Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
        Assert.Equal("projected_data_state_unknown", projected.Code);
        Assert.False(projected.PostconditionSatisfied);
    }

    /// <summary>Direct absence beats a stale dependency blockage, both before and after a guarded deletion.</summary>
    /// <param name="acceptedDeletion">Whether the absence comes from a deletion rather than fresh creation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SideEffectsCurrentAbsenceDoesNotResurrectOldDependencyBlockage(bool acceptedDeletion)
    {
        // Arrange
        var projection = acceptedDeletion ? new SafeMigrationPreflightProjection() : ProjectionWithNewRoleTable();
        var deletion = SideEffectsParentDeletion();

        if (acceptedDeletion)
        {
            ObserveAppliedMutation(projection, deletion, SafeMigrationObservedState.TransitionReady);
        }

        var live = EvidenceAnalysis(SafeMigrationObservedState.DataBlocked,
            [SafeMigrationModelManagedRowState.Source], [1]);

        // Act
        var projected = projection.Project(deletion, live);
        var decision = SafeMigrationDecisionPlanner.Plan(
            deletion.Intent.Kind, projected.ObservedState, deletion.Policy, projected.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.True(projected.PostconditionSatisfied);
        Assert.False(projected.IsModelManagedProjectionUnknown);
        Assert.Equal(SafeMigrationAction.NoOp, decision.Action);
    }

    /// <summary>Exact parent contents cannot prove that incoming rows permit a guarded deletion.</summary>
    /// <param name="parentWasInserted">Whether the current parent proof follows an accepted data mutation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SideEffectsPresentParentCannotOverrideIncomingDependencyBlockage(bool parentWasInserted)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        if (parentWasInserted)
        {
            ObserveAppliedMutation(projection, RoleEnsure(), SafeMigrationObservedState.Missing);
        }
        else
        {
            Accept(projection, RoleEnsure(), SafeMigrationObservedState.Matching);
        }

        var parentProof = projection.Project(RoleEnsure(), Live(SafeMigrationObservedState.Different));
        var deletion = SideEffectsParentDeletion();
        var live = EvidenceAnalysis(SafeMigrationObservedState.DataBlocked,
            [SafeMigrationModelManagedRowState.Source], [1]);

        // Act
        var projected = projection.Project(deletion, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, parentProof.ObservedState);
        Assert.True(parentProof.PostconditionSatisfied);

        if (parentWasInserted)
        {
            AssertSideEffectsUnknown(projected);
            Assert.Null(projected.ModelManagedDataEvidence);
            Assert.True(projection.ModelManagedDataChangeVersion > 0);
        }
        else
        {
            Assert.Same(live, projected);
            Assert.Equal(SafeMigrationObservedState.DataBlocked, projected.ObservedState);
            Assert.False(projected.IsModelManagedProjectionUnknown);
            Assert.Equal(0, projection.ModelManagedDataChangeVersion);
        }
    }

    /// <summary>A confined insert invalidates only dependency evidence naming its written child table.</summary>
    /// <param name="insertedChildIsReferenced">Whether the parent deletion names the inserted child table.</param>
    /// <param name="parentHasExactProof">Whether an earlier no-op proved the exact existing parent row.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SideEffectsConfinedChildInsertInvalidatesOnlyReferencedDependencyEvidence(
        bool insertedChildIsReferenced,
        bool parentHasExactProof
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        if (parentHasExactProof)
        {
            Accept(projection, RoleEnsure(), SafeMigrationObservedState.Matching);
        }

        var childTable = insertedChildIsReferenced ? "user_roles" : "unrelated_roles";
        var definition = new ExpectedTableDefinition(childTable,
        [
            new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int"),
            new ExpectedColumnDefinition("role_id", typeof(int), isNullable: false, storeType: "int"),
        ]);

        Accept(projection,
            Operation(new EnsureTableIntent(definition, SafeMigrationTableMode.StrictDefinition)),
            SafeMigrationObservedState.Missing);

        var child = Operation(new EnsureModelManagedDataIntent(
            childTable, ["id"], ["int"], ["id", "role_id"], ["int", "int"],
            new object?[,] { { 11, 1 } }, schema: null, uniqueKeys: null));

        ObserveAppliedMutation(projection, child, SafeMigrationObservedState.PrerequisiteMissing);

        var deletion = SideEffectsParentDeletion();
        var live = EvidenceAnalysis(SafeMigrationObservedState.TransitionReady,
            [SafeMigrationModelManagedRowState.Source], [0]);

        // Act
        var projected = projection.Project(deletion, live);
        var childPostcondition = projection.Project(child, Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, childPostcondition.ObservedState);
        Assert.True(childPostcondition.PostconditionSatisfied);

        if (insertedChildIsReferenced)
        {
            AssertSideEffectsUnknown(projected);
            Assert.Null(projected.ModelManagedDataEvidence);
        }
        else
        {
            Assert.Same(live, projected);
            Assert.Equal(SafeMigrationObservedState.TransitionReady, projected.ObservedState);
            Assert.False(projected.IsModelManagedProjectionUnknown);
            Assert.Same(live.ModelManagedDataEvidence, projected.ModelManagedDataEvidence);
        }
    }

    /// <summary>Accepted incoming FKs must be represented by the parent deletion's dependency contract.</summary>
    /// <param name="dependencyIsModeled">Whether the deletion includes matching incoming-FK metadata.</param>
    /// <param name="replayedForeignKey">Whether the FK was already present instead of newly applied.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SideEffectsAcceptedIncomingForeignKeyRequiresModeledDeleteDependency(
        bool dependencyIsModeled,
        bool replayedForeignKey
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var foreignKey = Operation(new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            "fk_user_roles_roles", "user_roles", ["role_id"], "roles", ["id"])));

        SideEffectsObserveAcceptedForeignKey(projection, foreignKey, replayedForeignKey);

        var foreignKeyProof = projection.Project(foreignKey, Live(SafeMigrationObservedState.Different));
        var deletion = dependencyIsModeled
            ? SideEffectsParentDeletion()
            : SideEffectsManagedOperation("delete", "roles");
        var live = Live(SafeMigrationObservedState.TransitionReady);

        // Act
        var projected = projection.Project(deletion, live);
        var decision = SafeMigrationDecisionPlanner.Plan(
            deletion.Intent.Kind, projected.ObservedState, deletion.Policy, projected.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, foreignKeyProof.ObservedState);
        Assert.False(projected.IsModelManagedProjectionUnknown);
        Assert.False(projected.PostconditionSatisfied);

        if (dependencyIsModeled)
        {
            Assert.Same(live, projected);
            Assert.Equal(SafeMigrationObservedState.TransitionReady, projected.ObservedState);
            Assert.Equal(SafeMigrationAction.Apply, decision.Action);
        }
        else
        {
            Assert.Equal(SafeMigrationObservedState.Unsupported, projected.ObservedState);
            Assert.Equal("projected_model_managed_dependency_unmodeled", projected.Code);
            Assert.Equal(SafeMigrationAction.RejectUnsupported, decision.Action);
        }
    }

    /// <summary>Dropped or unrelated accepted FKs cannot add dependencies to the parent deletion contract.</summary>
    /// <param name="foreignKeyWasDropped">Whether the FK was removed instead of naming another principal.</param>
    /// <param name="replayedForeignKey">Whether the FK was already present instead of newly applied.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SideEffectsDroppedOrUnrelatedForeignKeyPreservesDeleteAnalysis(
        bool foreignKeyWasDropped,
        bool replayedForeignKey
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var principalTable = foreignKeyWasDropped ? "roles" : "unrelated_roles";
        var foreignKey = Operation(new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            "fk_user_roles_parent", "user_roles", ["role_id"], principalTable, ["id"])));

        SideEffectsObserveAcceptedForeignKey(projection, foreignKey, replayedForeignKey);

        var foreignKeyProof = projection.Project(foreignKey, Live(SafeMigrationObservedState.Different));

        if (foreignKeyWasDropped)
        {
            Accept(projection, Operation(new DropForeignKeyIntent("fk_user_roles_parent", "user_roles")),
                SafeMigrationObservedState.Matching);
        }

        var deletion = SideEffectsManagedOperation("delete", "roles");
        var live = Live(SafeMigrationObservedState.TransitionReady);

        // Act
        var projected = projection.Project(deletion, live);
        var decision = SafeMigrationDecisionPlanner.Plan(
            deletion.Intent.Kind, projected.ObservedState, deletion.Policy, projected.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, foreignKeyProof.ObservedState);
        Assert.Same(live, projected);
        Assert.Equal(SafeMigrationObservedState.TransitionReady, projected.ObservedState);
        Assert.False(projected.IsModelManagedProjectionUnknown);
        Assert.Equal(SafeMigrationAction.Apply, decision.Action);
    }

    /// <summary>Removed incoming-FK schema is a hard prerequisite failure even with exact parent absence.</summary>
    /// <param name="dropTable">Whether the child table is removed instead of its FK column.</param>
    /// <param name="parentIsAbsent">Whether the current parent row proof establishes absence.</param>
    /// <param name="providerDrop">Whether the schema removal is an EF provider operation.</param>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void SideEffectsMissingIncomingForeignKeySchemaCannotBeDeferred(
        bool dropTable,
        bool parentIsAbsent,
        bool providerDrop
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        Accept(projection,
            Operation(new EnsureTableIntent(RoleTable(), SafeMigrationTableMode.ConvergenceContainer)),
            SafeMigrationObservedState.Matching);

        if (providerDrop)
        {
            MigrationOperation removal = dropTable
                ? new DropTableOperation { Name = "user_roles" }
                : new DropColumnOperation { Table = "user_roles", Name = "role_id" };

            projection.ObserveProviderPostcondition(removal);
        }
        else
        {
            SafeMigrationIntent removal = dropTable
                ? new DropTableIntent("user_roles")
                : new DropColumnIntent("role_id", "user_roles");

            Accept(projection, Operation(removal), SafeMigrationObservedState.Matching);
        }

        var parentFact = parentIsAbsent ? SideEffectsManagedOperation("delete", "roles") : RoleEnsure();

        Accept(projection, parentFact,
            parentIsAbsent ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Matching);

        var parentProof = projection.Project(parentFact, Live(SafeMigrationObservedState.Different));
        var deletion = SideEffectsParentDeletion();
        var live = EvidenceAnalysis(SafeMigrationObservedState.TransitionReady,
            [SafeMigrationModelManagedRowState.Source], [0]);

        // Act
        var projected = projection.Project(deletion, live);
        var decision = SafeMigrationDecisionPlanner.Plan(
            deletion.Intent.Kind, projected.ObservedState, deletion.Policy, projected.RepairCapability);

        // Assert
        Assert.Equal(parentIsAbsent ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Matching,
            parentProof.ObservedState);
        Assert.True(parentProof.PostconditionSatisfied);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
        Assert.Equal("projected_prerequisite_missing", projected.Code);
        Assert.False(projected.IsModelManagedProjectionUnknown);
        Assert.False(projected.IsOpaqueProjectionUnknown);
        Assert.False(projected.PostconditionSatisfied);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, decision.Action);
    }

    /// <summary>Deleting a counted child does not certify the old parent dependency count after triggers.</summary>
    [Fact]
    public void SideEffectsChildDeletionCannotDischargeCountOnlyParentEvidence()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var child = Operation(new DeleteModelManagedDataIntent(
            "user_roles", ["id"], ["int"], new object?[,] { { 11 } },
            ["id", "role_id"], ["int", "int"], new object?[,] { { 11, 1 } },
            schema: null, foreignKeys: null));

        var parent = SideEffectsParentDeletion();

        var childLive = EvidenceAnalysis(SafeMigrationObservedState.TransitionReady,
            [SafeMigrationModelManagedRowState.Source], []);

        var childDecision = SafeMigrationDecisionPlanner.Plan(
            child.Intent.Kind, childLive.ObservedState, child.Policy, childLive.RepairCapability);

        projection.Observe(child, childLive, childLive, childDecision);

        var parentLive = EvidenceAnalysis(SafeMigrationObservedState.DataBlocked,
            [SafeMigrationModelManagedRowState.Source], [1]);

        // Act
        var projected = projection.Project(parent, parentLive);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, childDecision.Action);
        AssertSideEffectsUnknown(projected);
        Assert.Null(projected.ModelManagedDataEvidence);
    }

    /// <summary>A proven rename moves populated row knowledge without turning known rows into missing ones.</summary>
    /// <param name="providerRename">Whether the rename is an EF provider operation or a safe operation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SideEffectsPopulatedTableRenamePreservesExactRows(bool providerRename)
    {
        // Arrange
        var projection = ProjectionWithNewRoleTable();

        ObserveAppliedMutation(projection, RoleEnsure(), SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        if (providerRename)
        {
            projection.ObserveProviderPostcondition(new RenameTableOperation
            {
                Name = "roles", NewName = "application_roles",
            });
        }
        else
        {
            Accept(projection, Operation(new RenameTableIntent("roles", newName: "application_roles")),
                SafeMigrationObservedState.Matching);
        }

        var projected = projection.Project(RoleEnsure("application_roles"),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, projected.ObservedState);
        Assert.Equal("projected_matching", projected.Code);
        Assert.True(projected.PostconditionSatisfied);
    }

    /// <summary>Discarding populated row facts during a column rename cannot restore an empty-table proof.</summary>
    /// <param name="providerRename">Whether the rename is an EF provider operation or a safe operation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SideEffectsPopulatedColumnRenameCannotInventMissingRows(bool providerRename)
    {
        // Arrange
        var projection = ProjectionWithNewRoleTable();

        ObserveAppliedMutation(projection, RoleEnsure(), SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        if (providerRename)
        {
            projection.ObserveProviderPostcondition(new RenameColumnOperation
            {
                Table = "roles", Name = "name", NewName = "display_name",
            });
        }
        else
        {
            Accept(projection, Operation(new RenameColumnIntent("name", "roles", "display_name")),
                SafeMigrationObservedState.Matching);
        }

        var projected = projection.Project(RoleEnsureWithDisplayName(),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        AssertSideEffectsUnknown(projected);
    }

    /// <summary>Recreating a populated table yields a fresh absence proof without retaining its old rows.</summary>
    /// <param name="providerDrop">Whether the table is removed by an EF provider operation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SideEffectsDroppedAndRecreatedTableCannotRetainOldRows(bool providerDrop)
    {
        // Arrange
        var projection = ProjectionWithNewRoleTable();

        ObserveAppliedMutation(projection, RoleEnsure(), SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        if (providerDrop)
        {
            projection.ObserveProviderPostcondition(new DropTableOperation { Name = "roles" });
        }
        else
        {
            Accept(projection, Operation(new DropTableIntent("roles")), SafeMigrationObservedState.Matching);
        }

        projection.ObserveProviderPostcondition(ProviderRoleTable());

        var projected = projection.Project(RoleEnsure(), Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
        Assert.False(projected.PostconditionSatisfied);
        Assert.False(projected.IsModelManagedProjectionUnknown);
    }

    private static SafeMigrationOperation SideEffectsManagedOperation(
        string kind,
        string table
    ) => kind switch
    {
        "ensure" => RoleEnsure(table),
        "update" => Operation(new UpdateModelManagedDataIntent(
            table, ["id"], ["int"], new object?[,] { { 1 } }, ["name"], ["varchar(64)"],
            new object?[,] { { "administrator" } }, new object?[,] { { "owner" } },
            schema: null, uniqueKeys: null)),
        "delete" => Operation(new DeleteModelManagedDataIntent(
            table, ["id"], ["int"], new object?[,] { { 1 } }, ["id", "name"], ["int", "varchar(64)"],
            new object?[,] { { 1, "administrator" } }, schema: null, foreignKeys: null)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static void SideEffectsObserveAcceptedForeignKey(
        SafeMigrationPreflightProjection projection,
        SafeMigrationOperation operation,
        bool replayedForeignKey
    )
    {
        var definition = Assert.IsType<EnsureForeignKeyIntent>(operation.Intent).Definition;
        var live = new SafeMigrationProviderAnalysis(
            replayedForeignKey ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, replayedForeignKey, "test_live")
        {
            MatchedObjectName = replayedForeignKey ? definition.Name : null,
        };

        var projected = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind, projected.ObservedState, operation.Policy, projected.RepairCapability);

        Assert.Equal(replayedForeignKey ? SafeMigrationAction.NoOp : SafeMigrationAction.Apply, decision.Action);
        projection.Observe(operation, live, projected, decision);
    }

    private static SafeMigrationOperation SideEffectsParentDeletion() => Operation(new DeleteModelManagedDataIntent(
        "roles", ["id"], ["int"], new object?[,] { { 1 } },
        ["id", "name"], ["int", "varchar(64)"], new object?[,] { { 1, "administrator" } },
        schema: null,
        foreignKeys: [new ExpectedModelManagedDataForeignKeyDefinition("user_roles", ["role_id"], ["id"])]));

    private static MigrationOperation SideEffectsProviderMutation(string kind) => kind switch
    {
        "insert" => new InsertDataOperation
        {
            Table = "other_roles", Columns = ["id", "name"], ColumnTypes = ["int", "varchar(64)"],
            Values = new object?[,] { { 1, "owner" } },
        },
        "update" => new UpdateDataOperation
        {
            Table = "other_roles", KeyColumns = ["id"], KeyColumnTypes = ["int"],
            KeyValues = new object?[,] { { 1 } }, Columns = ["name"], ColumnTypes = ["varchar(64)"],
            Values = new object?[,] { { "owner" } },
        },
        "delete" => new DeleteDataOperation
        {
            Table = "other_roles", KeyColumns = ["id"], KeyColumnTypes = ["int"],
            KeyValues = new object?[,] { { 1 } },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static void AssertSideEffectsUnknown(SafeMigrationProviderAnalysis analysis)
    {
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_model_managed_data_state_unknown", analysis.Code);
        Assert.True(analysis.IsModelManagedProjectionUnknown);
        Assert.False(analysis.PostconditionSatisfied);
        Assert.Equal(SafeMigrationRepairCapability.None, analysis.RepairCapability);
    }
}
