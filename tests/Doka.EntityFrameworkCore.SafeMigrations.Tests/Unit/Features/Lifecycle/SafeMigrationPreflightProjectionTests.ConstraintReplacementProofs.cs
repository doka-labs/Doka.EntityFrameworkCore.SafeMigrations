namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>A different replacement key can use an independently current empty-table proof.</summary>
    /// <param name="dropFirst">Whether the accepted removal precedes the empty-table certificate.</param>
    /// <param name="strictTable">Whether the existing table has a complete accepted definition.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PrimaryKeyReplacementUsesCurrentEmptyProofForAcceptedRequiredTarget(
        bool dropFirst,
        bool strictTable
    )
    {
        // Arrange
        var projection = ConstraintReplacementPrimaryKeyProjection(dropFirst, strictTable);
        var operation = ConstraintReplacementPrimaryKeyOperation();

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Different));
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
        Assert.Equal(SafeMigrationAction.Apply, decision.Action);
        Assert.False(analysis.IsOpaqueProjectionUnknown);
    }

    /// <summary>Independent empty proof does not bypass the provider's physical-key validation.</summary>
    /// <param name="providerState">The physical validator's independently captured result.</param>
    /// <param name="expectedAction">The planner action corresponding to that result.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Missing, SafeMigrationAction.Apply)]
    [InlineData(SafeMigrationObservedState.Unsupported, SafeMigrationAction.RejectUnsupported)]
    [InlineData(SafeMigrationObservedState.DataBlocked, SafeMigrationAction.RejectDataBlocked)]
    [InlineData(SafeMigrationObservedState.Different, SafeMigrationAction.RejectDifferent)]
    public void PrimaryKeyReplacementEmptyProofStillRunsPhysicalValidation(
        SafeMigrationObservedState providerState,
        SafeMigrationAction expectedAction
    )
    {
        // Arrange
        var providerResult = Live(providerState);
        var provider = new ConstraintReplacementKeyProbe(providerResult);
        var projection = ConstraintReplacementPrimaryKeyProjection(
            dropFirst: true,
            strictTable: false,
            provider);

        var operation = ConstraintReplacementPrimaryKeyOperation();
        var live = Live(SafeMigrationObservedState.Different);

        // Act
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        // Assert
        Assert.Same(providerResult, analysis);
        Assert.Equal(expectedAction, decision.Action);
        Assert.Equal(1, provider.ValidationCount);
        Assert.Same(live, provider.LiveAnalysis);
        Assert.Same(projection, provider.ColumnSource);
        Assert.NotNull(provider.ProjectedAnalysis);
        Assert.Equal(SafeMigrationObservedState.Missing, provider.ProjectedAnalysis.ObservedState);
        Assert.Equal("projected_missing", provider.ProjectedAnalysis.Code);
    }

    /// <summary>A current empty certificate does not erase an independent live rejection.</summary>
    /// <param name="liveState">The independent provider rejection.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Unsupported)]
    [InlineData(SafeMigrationObservedState.DataBlocked)]
    public void PrimaryKeyReplacementPreservesIndependentLiveBlocker(
        SafeMigrationObservedState liveState
    )
    {
        // Arrange
        var projection = ConstraintReplacementPrimaryKeyProjection(dropFirst: true, strictTable: false);
        var live = Live(liveState);

        // Act
        var analysis = projection.Project(ConstraintReplacementPrimaryKeyOperation(), live);

        // Assert
        Assert.Same(live, analysis);
    }

    /// <summary>Rejected, nullable, missing and defaulted additions cannot stand in for required-key proof.</summary>
    /// <param name="targetProof">The target-column evidence that must not authorize the replacement.</param>
    [Theory]
    [InlineData("missing")]
    [InlineData("nullable")]
    [InlineData("defaulted")]
    [InlineData("matching")]
    [InlineData("rejected")]
    [InlineData("nullable_with_empty_peer")]
    public void PrimaryKeyReplacementRefusesUnprovenTargetColumn(
        string targetProof
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        ObserveColumnsOnExistingTable(projection, "items", Column("id"));
        ObserveAccepted(projection, new DropPrimaryKeyIntent("pk_items", "items"),
            SafeMigrationObservedState.Matching);

        var target = targetProof is "nullable" or "nullable_with_empty_peer"
            ? NullableColumn("replacement_id")
            : targetProof == "defaulted"
                ? new ExpectedColumnDefinition("replacement_id", typeof(int), isNullable: false, storeType: "int",
                    defaultValue: SafeMigrationDefaultValue.Literal(0))
                : Column("replacement_id");

        if (targetProof == "nullable_with_empty_peer")
        {
            ObserveAccepted(projection, new EnsureColumnIntent("items", Column("empty_marker")),
                SafeMigrationObservedState.Missing);
        }

        if (targetProof == "rejected")
        {
            ObserveConstraintReplacementRejected(projection, new EnsureColumnIntent("items", target),
                SafeMigrationObservedState.DataBlocked);
        }
        else if (targetProof != "missing")
        {
            ObserveAccepted(projection, new EnsureColumnIntent("items", target),
                targetProof == "matching"
                    ? SafeMigrationObservedState.Matching
                    : SafeMigrationObservedState.Missing);
        }

        var operation = ConstraintReplacementPrimaryKeyOperation();

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Different));
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, decision.Action);
        Assert.False(analysis.PostconditionSatisfied);
    }

    /// <summary>Data mutation invalidates the otherwise sufficient empty-table replacement certificate.</summary>
    /// <param name="table">The table receiving the row mutation.</param>
    [Theory]
    [InlineData("items")]
    [InlineData("audit")]
    public void PrimaryKeyReplacementRefusesStaleEmptyProofAfterDataMutation(
        string table
    )
    {
        // Arrange
        var projection = ConstraintReplacementPrimaryKeyProjection(dropFirst: true, strictTable: false);

        ObserveConstraintReplacementDataMutation(projection, table);

        var operation = ConstraintReplacementPrimaryKeyOperation();

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Different));
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, decision.Action);
    }

    /// <summary>A rejected removal cannot authorize a different key despite accepted empty-table proof.</summary>
    [Fact]
    public void PrimaryKeyReplacementRequiresAcceptedDropDespiteCurrentEmptyProof()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        ObserveColumnsOnExistingTable(projection, "items", Column("id"));
        ObserveAccepted(projection, new EnsureColumnIntent("items", Column("replacement_id")),
            SafeMigrationObservedState.Missing);
        ObserveConstraintReplacementRejected(projection, new DropPrimaryKeyIntent("pk_items", "items"),
            SafeMigrationObservedState.Different);

        var live = Live(SafeMigrationObservedState.Different);

        // Act
        var analysis = projection.Project(ConstraintReplacementPrimaryKeyOperation(), live);

        // Assert
        Assert.Same(live, analysis);
    }

    /// <summary>Exact foreign-key removal can reuse independent null-preserving or empty row certificates.</summary>
    /// <param name="dropFirst">Whether removal precedes the dependent-column certificate.</param>
    /// <param name="emptyProof">Whether the dependent table is proven empty rather than null-preserving.</param>
    /// <param name="onDelete">The replacement delete action.</param>
    [Theory]
    [InlineData(false, false, ReferentialAction.Cascade)]
    [InlineData(true, false, ReferentialAction.Cascade)]
    [InlineData(false, false, ReferentialAction.SetNull)]
    [InlineData(true, false, ReferentialAction.SetNull)]
    [InlineData(false, true, ReferentialAction.Cascade)]
    [InlineData(true, true, ReferentialAction.Cascade)]
    public void ForeignKeyActionReplacementUsesCurrentIndependentPrerequisites(
        bool dropFirst,
        bool emptyProof,
        ReferentialAction onDelete
    )
    {
        // Arrange
        var projection = ConstraintReplacementForeignKeyProjection(dropFirst, emptyProof);
        var operation = ConstraintReplacementForeignKeyOperation(onDelete);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Different));
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
        Assert.Equal(SafeMigrationAction.Apply, decision.Action);
        Assert.False(analysis.IsOpaqueProjectionUnknown);
    }

    /// <summary>A complete newly created table also retains independent row proof after exact FK removal.</summary>
    [Fact]
    public void ForeignKeyActionReplacementUsesCompleteCreatedTableProof()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var parent = new ExpectedTableDefinition("parents", [Column("id")],
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_parents", "parents", ["id"]));

        var child = new ExpectedTableDefinition("children", [Column("id"), NullableColumn("parent_id")],
            foreignKeys: [ForeignKey().Definition]);

        Apply(projection, new EnsureTableIntent(parent, SafeMigrationTableMode.StrictDefinition));
        Apply(projection, new EnsureTableIntent(child, SafeMigrationTableMode.StrictDefinition));
        ObserveAccepted(projection, new DropForeignKeyIntent("fk_children_parents", "children"),
            SafeMigrationObservedState.Matching);

        // Act
        var analysis = projection.Project(ConstraintReplacementForeignKeyOperation(ReferentialAction.Cascade),
            Live(SafeMigrationObservedState.Different));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>SET NULL replacements require every dependent column to be optional even on empty tables.</summary>
    /// <param name="onUpdate">Whether the SET NULL action applies on update rather than deletion.</param>
    /// <param name="completeTable">Whether the table was created with a complete definition.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ForeignKeyActionReplacementRefusesSetNullOnRequiredColumn(
        bool onUpdate,
        bool completeTable
    )
    {
        // Arrange
        var projection = completeTable
            ? ConstraintReplacementCompleteRequiredForeignKeyProjection()
            : ConstraintReplacementForeignKeyProjection(dropFirst: true, emptyProof: true);

        var operation = ConstraintReplacementForeignKeyOperation(
            onUpdate ? ReferentialAction.NoAction : ReferentialAction.SetNull,
            onUpdate ? ReferentialAction.SetNull : ReferentialAction.NoAction);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Different));
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        // Assert
        Assert.NotEqual(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.NotEqual(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.Contains(decision.Action, new[]
        {
            SafeMigrationAction.RejectDifferent,
            SafeMigrationAction.RejectPrerequisiteMissing,
            SafeMigrationAction.RejectUnsupported,
        });
    }

    /// <summary>Exact removal does not supply missing structure, compatible storage or existing-row safety.</summary>
    /// <param name="proofGap">The independent prerequisite deliberately withheld.</param>
    [Theory]
    [InlineData("dependent_column")]
    [InlineData("principal_column")]
    [InlineData("candidate_key")]
    [InlineData("wrong_candidate_columns")]
    [InlineData("storage")]
    [InlineData("required_without_empty_proof")]
    [InlineData("nullable_default")]
    [InlineData("rejected_dependent_column")]
    public void ForeignKeyActionReplacementRefusesIndependentPrerequisiteGap(
        string proofGap
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        ObserveColumnsOnExistingTable(projection, "parents", Column("other_id"));

        if (proofGap != "principal_column")
        {
            ObserveAccepted(projection, new EnsureColumnIntent("parents", Column("id")),
                SafeMigrationObservedState.Matching);
        }

        if (proofGap != "candidate_key")
        {
            ObserveAccepted(projection, new EnsurePrimaryKeyIntent(
                    new ExpectedPrimaryKeyDefinition("pk_parents", "parents",
                        proofGap == "wrong_candidate_columns" ? ["other_id", "id"] : ["id"])),
                SafeMigrationObservedState.Matching);
        }

        ObserveColumnsOnExistingTable(projection, "children", Column("id"));
        ObserveAccepted(projection, new DropForeignKeyIntent("fk_children_parents", "children"),
            SafeMigrationObservedState.Matching);

        var dependent = proofGap == "storage"
            ? new ExpectedColumnDefinition("parent_id", typeof(long), isNullable: true, storeType: "bigint")
            : new ExpectedColumnDefinition("parent_id", typeof(int),
                isNullable: proofGap != "required_without_empty_proof", storeType: "int",
                defaultValue: proofGap == "nullable_default" ? SafeMigrationDefaultValue.Literal(0) : null);

        if (proofGap == "rejected_dependent_column")
        {
            ObserveConstraintReplacementRejected(projection, new EnsureColumnIntent("children", dependent),
                SafeMigrationObservedState.DataBlocked);
        }
        else if (proofGap != "dependent_column")
        {
            ObserveAccepted(projection, new EnsureColumnIntent("children", dependent),
                proofGap == "required_without_empty_proof"
                    ? SafeMigrationObservedState.Matching
                    : SafeMigrationObservedState.Missing);
        }

        var operation = ConstraintReplacementForeignKeyOperation(ReferentialAction.Cascade);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Different));
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        // Assert
        Assert.NotEqual(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.NotEqual(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.Contains(decision.Action, new[]
        {
            SafeMigrationAction.RejectDifferent,
            SafeMigrationAction.RejectPrerequisiteMissing,
        });
    }

    /// <summary>An exact dropped FK does not erase an independent unsupported or data-blocked result.</summary>
    /// <param name="liveState">The independent provider rejection.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Unsupported)]
    [InlineData(SafeMigrationObservedState.DataBlocked)]
    public void ForeignKeyActionReplacementPreservesIndependentLiveBlocker(
        SafeMigrationObservedState liveState
    )
    {
        // Arrange
        var projection = ConstraintReplacementForeignKeyProjection(dropFirst: true, emptyProof: false);
        var live = Live(liveState);

        // Act
        var analysis = projection.Project(ConstraintReplacementForeignKeyOperation(ReferentialAction.Cascade), live);

        // Assert
        Assert.Same(live, analysis);
    }

    /// <summary>Fresh emptiness supersedes old row blockers without bypassing DML or unsupported contracts.</summary>
    /// <param name="mutateAfterRecreation">Whether row mutation invalidates the fresh owner's empty proof.</param>
    /// <param name="liveState">The immutable provider result captured for the previous physical owner.</param>
    /// <param name="expectedState">The expected projected state for the current physical owner.</param>
    /// <param name="expectedAction">The expected planner action.</param>
    [Theory]
    [InlineData(false, SafeMigrationObservedState.DataBlocked, SafeMigrationObservedState.Missing,
        SafeMigrationAction.Apply)]
    [InlineData(true, SafeMigrationObservedState.DataBlocked, SafeMigrationObservedState.DataBlocked,
        SafeMigrationAction.RejectDataBlocked)]
    [InlineData(false, SafeMigrationObservedState.Unsupported, SafeMigrationObservedState.Unsupported,
        SafeMigrationAction.RejectUnsupported)]
    public void ForeignKeyReplacementRecreatedOwnerScopesImmutableDataBlocker(
        bool mutateAfterRecreation,
        SafeMigrationObservedState liveState,
        SafeMigrationObservedState expectedState,
        SafeMigrationAction expectedAction
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        ObserveParentWithPrimaryKey(projection, Column("id"));
        ObserveColumnsOnExistingTable(projection, "children", Column("id"), Column("parent_id"));
        ObserveAccepted(projection, new DropTableIntent("children"), SafeMigrationObservedState.Matching);
        Apply(projection, new EnsureTableIntent(
            new ExpectedTableDefinition("children", [Column("id"), Column("parent_id")]),
            SafeMigrationTableMode.StrictDefinition));

        if (mutateAfterRecreation)
        {
            projection.ObserveProviderPostcondition(new InsertDataOperation
            {
                Table = "children",
                Columns = ["id", "parent_id"],
                Values = new object[,] { { 1, 0 } },
            });
        }

        var operation = ConstraintReplacementForeignKeyOperation(ReferentialAction.Cascade);
        var live = Live(liveState);

        // Act
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        // Assert
        Assert.Equal(expectedState, analysis.ObservedState);
        Assert.Equal(expectedAction, decision.Action);
        Assert.True(((ISafeMigrationProjectedTableSource)projection).TryGetProjectedTableState(
            "children", schema: null, out var table));

        Assert.True(table.IsNewlyCreated);
        Assert.Equal(mutateAfterRecreation, table.HasDataMutation);

        if (expectedState != SafeMigrationObservedState.Missing)
        {
            Assert.Same(live, analysis);
        }
        else
        {
            Assert.Equal("projected_missing", analysis.Code);
            Assert.NotSame(live, analysis);
        }
    }

    /// <summary>FK-only removal leaves the physical table lifetime and its independent row blocker unchanged.</summary>
    [Fact]
    public void ForeignKeyReplacementUnchangedOwnerRetainsDataBlockerAfterExactDrop()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        ObserveParentWithPrimaryKey(projection, Column("id"));
        ObserveColumnsOnExistingTable(projection, "children", Column("id"), Column("parent_id"));
        ObserveAccepted(projection, new DropForeignKeyIntent("fk_children_parents", "children"),
            SafeMigrationObservedState.Matching);

        var operation = ConstraintReplacementForeignKeyOperation(ReferentialAction.Cascade);
        var live = Live(SafeMigrationObservedState.DataBlocked);

        // Act
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        // Assert
        Assert.Same(live, analysis);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, decision.Action);
        Assert.True(((ISafeMigrationProjectedTableSource)projection).TryGetProjectedTableState(
            "children", schema: null, out var table));

        Assert.False(table.IsNewlyCreated);
    }

    /// <summary>Neither a rejected drop nor removal of a different physical name authorizes replacement.</summary>
    /// <param name="dropOtherName">Whether an accepted drop names an unrelated FK rather than being rejected.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignKeyActionReplacementRequiresItsOwnAcceptedRemoval(
        bool dropOtherName
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        ObserveParentWithPrimaryKey(projection, Column("id"));
        ObserveColumnsOnExistingTable(projection, "children", Column("id"), NullableColumn("parent_id"));

        if (dropOtherName)
        {
            ObserveAccepted(projection, new DropForeignKeyIntent("fk_unrelated", "children"),
                SafeMigrationObservedState.Matching);
        }
        else
        {
            ObserveConstraintReplacementRejected(projection,
                new DropForeignKeyIntent("fk_children_parents", "children"), SafeMigrationObservedState.Different);
        }

        var live = Live(SafeMigrationObservedState.Different);

        // Act
        var analysis = projection.Project(ConstraintReplacementForeignKeyOperation(ReferentialAction.Cascade), live);

        // Assert
        Assert.Same(live, analysis);
    }

    /// <summary>Dependent, principal and unrelated row changes invalidate captured FK replacement proofs.</summary>
    /// <param name="table">The table receiving the mutation.</param>
    /// <param name="emptyProof">Whether the original row certificate proves emptiness or preserved nulls.</param>
    [Theory]
    [InlineData("children", false)]
    [InlineData("parents", false)]
    [InlineData("audit", false)]
    [InlineData("children", true)]
    [InlineData("parents", true)]
    [InlineData("audit", true)]
    public void ForeignKeyActionReplacementRefusesStaleRowProofAfterDataMutation(
        string table,
        bool emptyProof
    )
    {
        // Arrange
        var projection = ConstraintReplacementForeignKeyProjection(dropFirst: true, emptyProof);

        ObserveConstraintReplacementDataMutation(projection, table);

        var operation = ConstraintReplacementForeignKeyOperation(ReferentialAction.Cascade);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Different));
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, decision.Action);
    }

    /// <summary>A current independent certificate is required after removal of an accepted matching FK.</summary>
    [Fact]
    public void ForeignKeyActionReplacementCannotReuseImmutableMatchingRowEvidence()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        ObserveParentWithPrimaryKey(projection, Column("id"));
        ObserveColumnsOnExistingTable(projection, "children", Column("id"), Column("parent_id"));
        ObserveAccepted(projection, ForeignKey(), SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, new DropForeignKeyIntent("fk_children_parents", "children"),
            SafeMigrationObservedState.Matching);

        var operation = ConstraintReplacementForeignKeyOperation(ReferentialAction.Cascade);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Different));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.True(analysis.IsOpaqueProjectionUnknown);
    }

    /// <summary>Composite SET NULL requires ordered principal-key proof and every dependent column optional.</summary>
    /// <param name="reverseCandidate">Whether the accepted candidate key has the wrong declared order.</param>
    /// <param name="optionalPeer">Whether the second dependent column also permits SET NULL.</param>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ForeignKeyActionReplacementChecksCompositeCandidateOrderAndSetNullPeers(
        bool reverseCandidate,
        bool optionalPeer
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        ObserveColumnsOnExistingTable(projection, "parents", Column("tenant_id"), Column("id"));
        ObserveAccepted(projection, new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("uq_parents_pair", "parents",
                    reverseCandidate ? ["id", "tenant_id"] : ["tenant_id", "id"])),
            SafeMigrationObservedState.Matching);
        ObserveColumnsOnExistingTable(projection, "children", Column("id"));
        ObserveAccepted(projection, new EnsureColumnIntent("children",
                optionalPeer ? NullableColumn("tenant_id") : Column("tenant_id")),
            SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, new EnsureColumnIntent("children", NullableColumn("parent_id")),
            SafeMigrationObservedState.Missing);
        ObserveAccepted(projection, new DropForeignKeyIntent("fk_children_parents", "children"),
            SafeMigrationObservedState.Matching);

        var operation = new SafeMigrationOperation(new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
                "fk_children_parents", "children", ["tenant_id", "parent_id"], "parents", ["tenant_id", "id"],
                onUpdate: ReferentialAction.SetNull)),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Different));
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        // Assert
        if (!reverseCandidate && optionalPeer)
        {
            Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
            Assert.Equal(SafeMigrationAction.Apply, decision.Action);
        }
        else
        {
            Assert.NotEqual(SafeMigrationObservedState.Missing, analysis.ObservedState);
            Assert.NotEqual(SafeMigrationObservedState.Matching, analysis.ObservedState);
            Assert.Contains(decision.Action, new[]
            {
                SafeMigrationAction.RejectDifferent,
                SafeMigrationAction.RejectPrerequisiteMissing,
                SafeMigrationAction.RejectUnsupported,
            });
        }
    }

    /// <summary>Accepted exact FK endpoint and row proof survives removal only within its unchanged lifetime.</summary>
    /// <param name="replacementName">The exact name or new semantic alias requested after removal.</param>
    [Theory]
    [InlineData("fk_children_parents")]
    [InlineData("fk_new_alias")]
    public void ForeignKeyAcceptedCreationRetainsExactProofAfterDropWithOriginalMissing(
        string replacementName
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        ObserveAccepted(projection, ForeignKey(), SafeMigrationObservedState.Missing);
        ObserveAccepted(projection, new DropForeignKeyIntent("fk_children_parents", "children"),
            SafeMigrationObservedState.Missing);

        var operation = new SafeMigrationOperation(ForeignKey(replacementName), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Missing));
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy, analysis.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
        Assert.Equal(SafeMigrationAction.Apply, decision.Action);
        Assert.False(analysis.IsOpaqueProjectionUnknown);
    }

    /// <summary>Removed FK proof cannot promote original Missing after its accepted prerequisites change.</summary>
    /// <param name="mutation">The change that invalidates the retained endpoint or row certificate.</param>
    [Theory]
    [InlineData("data")]
    [InlineData("column")]
    [InlineData("principal_key")]
    [InlineData("principal_recreation")]
    [InlineData("action")]
    [InlineData("mapping")]
    [InlineData("rejected_creation")]
    public void ForeignKeyRemovedCreationProofRefusesChangedPrerequisites(
        string mutation
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        if (mutation == "rejected_creation")
        {
            ObserveConstraintReplacementRejected(projection, ForeignKey(), SafeMigrationObservedState.DataBlocked);
        }
        else
        {
            ObserveAccepted(projection, ForeignKey(), SafeMigrationObservedState.Missing);
        }

        ObserveAccepted(projection, new DropForeignKeyIntent("fk_children_parents", "children"),
            mutation == "rejected_creation"
                ? SafeMigrationObservedState.Matching
                : SafeMigrationObservedState.Missing);

        if (mutation == "data")
        {
            ObserveConstraintReplacementDataMutation(projection, "children");
        }
        else if (mutation == "column")
        {
            ObserveAccepted(projection, new DropColumnIntent("parent_id", "children"),
                SafeMigrationObservedState.Matching);
            ObserveAccepted(projection, new EnsureColumnIntent("children", NullableColumn("parent_id")),
                SafeMigrationObservedState.Missing);
        }
        else if (mutation == "principal_key")
        {
            ObserveAccepted(projection, new DropPrimaryKeyIntent("pk_parents", "parents"),
                SafeMigrationObservedState.Matching);
        }
        else if (mutation == "principal_recreation")
        {
            ObserveAccepted(projection, new DropTableIntent("parents"), SafeMigrationObservedState.Matching);
            Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("parents", [Column("id")],
                    primaryKey: new ExpectedPrimaryKeyDefinition("pk_parents", "parents", ["id"])),
                SafeMigrationTableMode.StrictDefinition));
        }

        var target = mutation == "action"
            ? new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition("fk_children_parents", "children",
                ["parent_id"], "parents", ["id"], onDelete: ReferentialAction.Cascade))
            : mutation == "mapping"
                ? ForeignKey(columns: ["alternate_id"])
                : ForeignKey();

        var operation = new SafeMigrationOperation(target, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Missing));
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy, analysis.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, decision.Action);
    }

    /// <summary>PK-column queries consume accepted standalone, inline, recreated and renamed graph state.</summary>
    /// <param name="lifetime">The accepted primary-key lifetime being queried.</param>
    /// <param name="expected">Whether the current owner still has the accepted primary-key column.</param>
    [Theory]
    [InlineData("standalone", true)]
    [InlineData("inline", true)]
    [InlineData("recreated", true)]
    [InlineData("renamed", true)]
    [InlineData("dropped", false)]
    [InlineData("rejected", null)]
    [InlineData("column_only", null)]
    [InlineData("complete_without_key", false)]
    [InlineData("different_key", false)]
    public void ProjectedPrimaryKeyColumnLookupUsesAuthoritativeCurrentGraph(
        string lifetime,
        bool? expected
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var primaryKey = new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"]);
        var definition = new ExpectedTableDefinition("items", [Column("id")],
            primaryKey: primaryKey);

        if (lifetime == "recreated")
        {
            ObserveAccepted(projection, new DropTableIntent("items"), SafeMigrationObservedState.Matching);
        }

        if (lifetime is "inline" or "recreated" or "renamed" or "complete_without_key")
        {
            Apply(projection, new EnsureTableIntent(lifetime == "complete_without_key"
                    ? new ExpectedTableDefinition("items", [Column("id")])
                    : definition,
                SafeMigrationTableMode.StrictDefinition));
        }
        else if (lifetime == "column_only")
        {
            ObserveColumnsOnExistingTable(projection, "items", Column("id"));
        }
        else if (lifetime == "rejected")
        {
            ObserveConstraintReplacementRejected(projection, new EnsurePrimaryKeyIntent(primaryKey),
                SafeMigrationObservedState.DataBlocked);
        }
        else
        {
            ObserveAccepted(projection, new EnsurePrimaryKeyIntent(primaryKey),
                SafeMigrationObservedState.Matching);
        }

        if (lifetime == "dropped")
        {
            ObserveAccepted(projection, new DropPrimaryKeyIntent("pk_items", "items"),
                SafeMigrationObservedState.Matching);
        }
        else if (lifetime == "different_key")
        {
            ObserveAccepted(projection, new DropPrimaryKeyIntent("pk_items", "items"),
                SafeMigrationObservedState.Matching);
            ObserveAccepted(projection, new EnsureColumnIntent("items", Column("other_id")),
                SafeMigrationObservedState.Missing);
            ObserveAccepted(projection, new EnsurePrimaryKeyIntent(
                    new ExpectedPrimaryKeyDefinition("pk_items_other", "items", ["other_id"])),
                SafeMigrationObservedState.Missing);
        }
        else if (lifetime == "renamed")
        {
            ObserveAccepted(projection, new RenameTableIntent("items", "renamed_items"),
                SafeMigrationObservedState.Matching);
        }

        var table = lifetime == "renamed" ? "renamed_items" : "items";
        var source = (ISafeMigrationProjectedColumnSource)projection;

        // Act
        var present = source.IsProjectedPrimaryKeyColumn(table, schema: null, "ID", StringComparer.OrdinalIgnoreCase);
        var otherTable = source.IsProjectedPrimaryKeyColumn(table.ToUpperInvariant(), schema: null, "id",
            StringComparer.OrdinalIgnoreCase);

        var otherColumn = source.IsProjectedPrimaryKeyColumn(table, schema: null, "absent_id",
            StringComparer.OrdinalIgnoreCase);

        // Assert
        Assert.Equal(expected, present);
        Assert.Null(otherTable);
        Assert.Equal(expected is null ? null : (bool?)false, otherColumn);
    }

    /// <summary>Accepted PK creation overrides immutable Missing for the singleton physical key.</summary>
    /// <param name="dropName">The authored name addressing the single physical primary key.</param>
    /// <param name="acceptKey">Whether primary-key creation is accepted rather than rejected.</param>
    [Theory]
    [InlineData("pk_items", true)]
    [InlineData("pk_alias", true)]
    [InlineData("pk_items", false)]
    public void PrimaryKeyPhysicalDropUsesAcceptedSingletonDespiteOriginalMissing(
        string dropName,
        bool acceptKey
    )
    {
        // Arrange
        // WHY: Ignoring the authored PK name is a MySQL singleton contract,
        // not the default identity of PostgreSQL, SQLite or SQL Server keys.
        var projection = ConstraintMemberProjection(completeTable: false,
            keyAnalyzer: new ReviewNameIndependentPrimaryKeyAnalyzer());
        var ensure = new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"]));

        if (acceptKey)
        {
            ObserveAccepted(projection, ensure, SafeMigrationObservedState.Missing);
        }
        else
        {
            ObserveConstraintReplacementRejected(projection, ensure, SafeMigrationObservedState.DataBlocked);
        }

        var operation = new SafeMigrationOperation(new DropPrimaryKeyIntent(dropName, "items"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Missing));
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy, analysis.RepairCapability);

        // Assert
        Assert.Equal(acceptKey ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Missing,
            analysis.ObservedState);
        Assert.Equal(acceptKey ? SafeMigrationAction.Apply : SafeMigrationAction.NoOp, decision.Action);
    }

    /// <summary>Accepted PK removal stays Missing despite stale Matching or another authored name.</summary>
    /// <param name="dropName">The later authored name addressing the single physical key.</param>
    [Theory]
    [InlineData("pk_items")]
    [InlineData("pk_alias")]
    public void PrimaryKeyPhysicalDropRetainsAcceptedSingletonTombstone(
        string dropName
    )
    {
        // Arrange
        var projection = ConstraintMemberProjection(completeTable: false);

        ObserveAccepted(projection,
            new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"])),
            SafeMigrationObservedState.Missing);
        ObserveAccepted(projection, new DropPrimaryKeyIntent("pk_items", "items"),
            SafeMigrationObservedState.Matching);

        var operation = new SafeMigrationOperation(new DropPrimaryKeyIntent(dropName, "items"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Matching));
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy, analysis.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, decision.Action);
    }

    /// <summary>FK member-name case folding does not change the physical table identity.</summary>
    /// <param name="ignoreForeignKeyCase">Whether provider FK names are case-insensitive.</param>
    /// <param name="completeTable">Whether the owner has a complete created-table projection.</param>
    /// <param name="sameTable">Whether the drop addresses the same case-sensitive physical table.</param>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void ForeignKeyPhysicalDropUsesMemberComparerWithoutFoldingTableIdentity(
        bool ignoreForeignKeyCase,
        bool completeTable,
        bool sameTable
    )
    {
        // Arrange
        var projection = ConstraintMemberProjection(completeTable,
            ignoreForeignKeyCase ? new ForeignKeyMemberIdentityNormalizer() : null);

        ObserveAccepted(projection, ConstraintMemberIntent("foreign", "fk_items_parents"),
            SafeMigrationObservedState.PrerequisiteMissing);

        var operation = new SafeMigrationOperation(
            new DropForeignKeyIntent("FK_ITEMS_PARENTS", sameTable ? "items" : "ITEMS"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Missing));
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy, analysis.RepairCapability);

        // Assert
        Assert.Equal(ignoreForeignKeyCase && sameTable
            ? SafeMigrationObservedState.Matching
            : SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal(ignoreForeignKeyCase && sameTable
            ? SafeMigrationAction.Apply
            : SafeMigrationAction.NoOp, decision.Action);
    }

    /// <summary>Inline FKs share member identity in created and existing strict-table state.</summary>
    /// <param name="createdTable">Whether the complete owner is newly created or accepted existing state.</param>
    /// <param name="ignoreForeignKeyCase">Whether physical FK names use case-insensitive identity.</param>
    /// <param name="sameTable">Whether the drop addresses the same case-sensitive table.</param>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void ForeignKeyInlinePhysicalDropUsesCreatedAndExistingStrictMemberIdentity(
        bool createdTable,
        bool ignoreForeignKeyCase,
        bool sameTable
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(objectIdentityNormalizer:
            ignoreForeignKeyCase ? new ForeignKeyMemberIdentityNormalizer() : null);

        var foreignKey = new ExpectedForeignKeyDefinition("fk_items_parents", "items", ["id"], "parents", ["id"]);
        var definition = new ExpectedTableDefinition("items", [Column("id")], foreignKeys: [foreignKey]);

        // WHY: This test addresses physical member casing, not shape-only table
        // aliases. Existing members therefore need independent actual-name proof.
        ObserveStrictTableWithPhysicalForeignKeyBindings(projection, definition, createdTable);

        var operation = new SafeMigrationOperation(
            new DropForeignKeyIntent("FK_ITEMS_PARENTS", sameTable ? "items" : "ITEMS"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = Live(SafeMigrationObservedState.Missing);

        // Act
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy, analysis.RepairCapability);

        projection.Observe(operation, live, analysis, decision);

        var exactDrop = Project(projection, new DropForeignKeyIntent("fk_items_parents", "items"),
            SafeMigrationObservedState.Matching);

        // Assert
        var removesPhysicalObject = ignoreForeignKeyCase && sameTable;

        Assert.Equal(removesPhysicalObject ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Missing,
            analysis.ObservedState);
        Assert.Equal(removesPhysicalObject ? SafeMigrationAction.Apply : SafeMigrationAction.NoOp, decision.Action);
        Assert.Equal(removesPhysicalObject ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Matching,
            exactDrop.ObservedState);
    }

    /// <summary>Local SET NULL lookup remains bounded and addresses only the current physical owner.</summary>
    /// <param name="createdTable">Whether the exact owner is newly created or accepted existing strict state.</param>
    /// <param name="renamed">Whether the newly created owner is subsequently renamed.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SetNullForeignKeyOwnerLookupDoesNotScanUnrelatedAcceptedOwners(
        bool createdTable,
        bool renamed
    )
    {
        // Arrange
        var normalizer = new CountingConstraintIdentityNormalizer();
        var projection = new SafeMigrationPreflightProjection(objectIdentityNormalizer: normalizer);
        var foreignKey = new ExpectedForeignKeyDefinition("fk_items_parents", "items", ["parent_id"],
            "parents", ["id"], schema: "scope", onDelete: ReferentialAction.SetNull);

        var definition = new ExpectedTableDefinition("items", [NullableColumn("parent_id")], schema: "scope",
            foreignKeys: [foreignKey]);

        ObserveAccepted(projection, new EnsureTableIntent(definition, SafeMigrationTableMode.StrictDefinition),
            createdTable ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Matching);

        if (renamed)
        {
            ObserveAccepted(projection, new RenameTableIntent("items", "renamed_items", schema: "scope"),
                SafeMigrationObservedState.Matching);
        }

        for (var index = 0; index < 128; index++)
        {
            var table = $"unrelated_{index}";

            ObserveAccepted(projection, new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
                    $"fk_unrelated_{index}", table, ["parent_id"], "parents", ["id"],
                    onDelete: ReferentialAction.SetNull)),
                SafeMigrationObservedState.Matching);
        }

        var source = (ISafeMigrationProjectedColumnSource)projection;
        var current = renamed ? "renamed_items" : "items";

        normalizer.Reset();

        // Act
        var traversedOwners = new bool[128];
        for (var index = 0; index < traversedOwners.Length; index++)
        {
            traversedOwners[index] = source.HasProjectedSetNullForeignKey($"unrelated_{index}", schema: null,
                "parent_id", StringComparer.OrdinalIgnoreCase);
        }

        var traversalNormalizations = normalizer.NormalizationCount;

        normalizer.Reset();

        var present = source.HasProjectedSetNullForeignKey(current, "scope", "PARENT_ID",
            StringComparer.OrdinalIgnoreCase);

        var otherOwner = source.HasProjectedSetNullForeignKey(current.ToUpperInvariant(), "scope", "parent_id",
            StringComparer.OrdinalIgnoreCase);

        var otherSchema = source.HasProjectedSetNullForeignKey(current, "other_scope", "parent_id",
            StringComparer.OrdinalIgnoreCase);

        var oldOwner = renamed && source.HasProjectedSetNullForeignKey("items", "scope", "parent_id",
            StringComparer.OrdinalIgnoreCase);

        // Assert
        Assert.All(traversedOwners, static value => Assert.True(value));
        Assert.True(traversalNormalizations > traversedOwners.Length);
        Assert.True(present);
        Assert.False(otherOwner);
        Assert.False(otherSchema);
        Assert.False(oldOwner);
        Assert.InRange(normalizer.NormalizationCount, 1, 63);
    }

    /// <summary>Case-equivalent physical removal invalidates every bound FK alias and presence certificate.</summary>
    /// <param name="completeTable">Whether the owner has a complete created-table projection.</param>
    /// <param name="ensureAlias">Whether the replacement uses the logical alias rather than the physical name.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ForeignKeyMemberComparerExpiresCaseVariantPhysicalAndAliasEvidence(
        bool completeTable,
        bool ensureAlias
    )
    {
        // Arrange
        var projection = ConstraintMemberProjection(completeTable, new ForeignKeyMemberIdentityNormalizer());

        ObserveAccepted(projection, ConstraintMemberIntent("foreign", "physical_key"),
            SafeMigrationObservedState.PrerequisiteMissing);
        ObserveAccepted(projection, ConstraintMemberIntent("foreign", "logical_alias"),
            SafeMigrationObservedState.Matching, "physical_key");
        ObserveAccepted(projection, new DropForeignKeyIntent("PHYSICAL_KEY", "items"),
            SafeMigrationObservedState.Matching);

        var ensure = new SafeMigrationOperation(
            ConstraintMemberIntent("foreign", ensureAlias ? "LOGICAL_ALIAS" : "physical_key"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(ensure, Live(SafeMigrationObservedState.Matching, "physical_key"));
        var repeatedDrop = Project(projection, new DropForeignKeyIntent("physical_key", "items"),
            SafeMigrationObservedState.Matching);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Null(analysis.MatchedObjectName);
        Assert.Equal(SafeMigrationObservedState.Missing, repeatedDrop.ObservedState);
    }

    /// <summary>Providers without an FK-specific comparer retain their existing identifier comparison rules.</summary>
    [Fact]
    public void ForeignKeyMemberComparerDefaultsToExistingProviderComparer()
    {
        // Arrange
        ISafeMigrationProviderObjectIdentityNormalizer normalizer =
            new ColumnStateOwnershipNormalizer(normalizeAliases: false);

        // Act
        var comparer = normalizer.ForeignKeyIdentifierComparer;

        // Assert
        Assert.Same(normalizer.IdentifierComparer, comparer);
        Assert.False(comparer.Equals("FK_NAME", "fk_name"));
    }

    /// <summary>Final writers fold only FK names, not case-sensitive owners, qualifiers or column names.</summary>
    /// <param name="ignoreForeignKeyCase">Whether provider FK names are case-insensitive.</param>
    /// <param name="laterTable">The later writer's case-sensitive table name.</param>
    /// <param name="laterSchema">The later writer's schema name.</param>
    [Theory]
    [InlineData(false, "items", null)]
    [InlineData(true, "items", null)]
    [InlineData(true, "ITEMS", null)]
    [InlineData(true, "items", "archive")]
    public void ForeignKeyPostflightMemberComparerPreservesOtherResourceIdentities(
        bool ignoreForeignKeyCase,
        string laterTable,
        string? laterSchema
    )
    {
        // Arrange
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer = ignoreForeignKeyCase
            ? new ForeignKeyMemberIdentityNormalizer()
            : null;

        MigrationOperation[] operations =
        [
            new SafeMigrationOperation(new DropForeignKeyIntent("FK_ITEMS_PARENTS", "items"),
                SafeMigrationPolicy.ThrowIfDifferent),
            new SafeMigrationOperation(new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
                    "fk_items_parents", laterTable, ["id"], "parents", ["id"], schema: laterSchema)),
                SafeMigrationPolicy.ThrowIfDifferent),
        ];

        MigrationOperation[] columns =
        [
            new SafeMigrationOperation(new DropColumnIntent("VALUE", "items"),
                SafeMigrationPolicy.ThrowIfDifferent),
            new SafeMigrationOperation(new EnsureColumnIntent("items", Column("value")),
                SafeMigrationPolicy.ThrowIfDifferent),
        ];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations, normalizer);
        var columnProjection = new SafeMigrationPostflightProjection(columns, normalizer);

        // Assert
        Assert.Equal(ignoreForeignKeyCase && laterTable == "items" && laterSchema is null,
            projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
        Assert.False(columnProjection.IsSuperseded(0));
    }

    /// <summary>Column final writers use member identity without folding owner or unrelated constraint names.</summary>
    /// <param name="ignoreColumnCase">Whether provider columns are case-insensitive.</param>
    /// <param name="earlierName">The first authored column name.</param>
    /// <param name="laterName">The final authored column name.</param>
    /// <param name="laterTable">The final writer's case-sensitive table name.</param>
    /// <param name="laterSchema">The final writer's schema name.</param>
    [Theory]
    [InlineData(true, "VALUE", "value", "items", null)]
    [InlineData(true, "value", "VALUE", "items", null)]
    [InlineData(false, "VALUE", "value", "items", null)]
    [InlineData(false, "value", "VALUE", "items", null)]
    [InlineData(true, "VALUE", "value", "ITEMS", null)]
    [InlineData(true, "value", "VALUE", "items", "archive")]
    public void ColumnPostflightMemberComparerPreservesOwnersAndOtherMemberNames(
        bool ignoreColumnCase,
        string earlierName,
        string laterName,
        string laterTable,
        string? laterSchema
    )
    {
        // Arrange
        var normalizer = new ForeignKeyMemberIdentityNormalizer(ignoreColumnCase);
        MigrationOperation[] operations =
        [
            new SafeMigrationOperation(new EnsureColumnIntent("items", Column(earlierName)),
                SafeMigrationPolicy.ThrowIfDifferent),
            new SafeMigrationOperation(new AlterColumnIntent(laterTable, NullableColumn(laterName), Column(laterName),
                    schema: laterSchema),
                SafeMigrationPolicy.RepairIfSafe),
        ];

        MigrationOperation[] uniqueConstraints =
        [
            new SafeMigrationOperation(new DropUniqueConstraintIntent("UQ_VALUE", "items"),
                SafeMigrationPolicy.ThrowIfDifferent),
            new SafeMigrationOperation(new EnsureUniqueConstraintIntent(
                    new ExpectedUniqueConstraintDefinition("uq_value", "items", ["id"])),
                SafeMigrationPolicy.ThrowIfDifferent),
        ];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations, normalizer);
        var uniqueProjection = new SafeMigrationPostflightProjection(uniqueConstraints, normalizer);

        // Assert
        Assert.Equal(ignoreColumnCase && laterTable == "items" && laterSchema is null,
            projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
        Assert.False(uniqueProjection.IsSuperseded(0));
    }

    /// <summary>Newly accepted physical constraints override immutable Missing, but semantic aliases do not.</summary>
    /// <param name="kind">The constraint family using accepted physical bookkeeping.</param>
    /// <param name="completeTable">Whether the owner has a complete created-table projection.</param>
    [Theory]
    [InlineData("foreign", false)]
    [InlineData("foreign", true)]
    [InlineData("unique", false)]
    [InlineData("unique", true)]
    [InlineData("check", false)]
    [InlineData("check", true)]
    public void ConstraintPhysicalDropDistinguishesNewObjectFromLogicalSemanticAlias(
        string kind,
        bool completeTable
    )
    {
        // Arrange
        var projection = ConstraintMemberProjection(completeTable);

        ObserveAccepted(projection, ConstraintMemberIntent(kind, "physical_key"),
            SafeMigrationObservedState.PrerequisiteMissing);

        var aliasEnsure = new SafeMigrationOperation(ConstraintMemberIntent(kind, "logical_alias"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var aliasLive = Live(SafeMigrationObservedState.Matching, "physical_key");
        var aliasNoOp = SafeMigrationDecisionPlanner.Plan(aliasEnsure.Intent.Kind, aliasLive.ObservedState,
            aliasEnsure.Policy, aliasLive.RepairCapability);

        projection.Observe(aliasEnsure, aliasLive, aliasLive, aliasNoOp);

        var physical = new SafeMigrationOperation(ConstraintMemberDrop(kind, "physical_key"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var alias = new SafeMigrationOperation(ConstraintMemberDrop(kind, "logical_alias"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var physicalAnalysis = projection.Project(physical, Live(SafeMigrationObservedState.Missing));
        var aliasAnalysis = projection.Project(alias, Live(SafeMigrationObservedState.Matching));
        var physicalDecision = SafeMigrationDecisionPlanner.Plan(physical.Intent.Kind, physicalAnalysis.ObservedState,
            physical.Policy, physicalAnalysis.RepairCapability);

        var aliasDecision = SafeMigrationDecisionPlanner.Plan(alias.Intent.Kind, aliasAnalysis.ObservedState,
            alias.Policy, aliasAnalysis.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, physicalAnalysis.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, physicalDecision.Action);
        Assert.Equal(SafeMigrationObservedState.Missing, aliasAnalysis.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, aliasDecision.Action);
    }

    /// <summary>Physical drops remain missing after observation despite stale live Matching analysis.</summary>
    /// <param name="kind">The constraint family using accepted physical bookkeeping.</param>
    [Theory]
    [InlineData("foreign")]
    [InlineData("unique")]
    [InlineData("check")]
    public void ConstraintPhysicalDropDoesNotReuseStalePresenceAfterAcceptedRemoval(
        string kind
    )
    {
        // Arrange
        var projection = ConstraintMemberProjection(completeTable: false);

        ObserveAccepted(projection, ConstraintMemberIntent(kind, "physical_key"),
            SafeMigrationObservedState.PrerequisiteMissing);
        ObserveAccepted(projection, ConstraintMemberDrop(kind, "physical_key"),
            SafeMigrationObservedState.Matching);

        // Act
        var analysis = Project(projection, ConstraintMemberDrop(kind, "physical_key"),
            SafeMigrationObservedState.Matching);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    /// <summary>Semantic FK Matching carries the new physical identity after the old owner is removed.</summary>
    /// <param name="completeTable">Whether the owner has a complete created-table projection.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignKeySemanticMatchingCarriesRecreatedPhysicalObjectName(
        bool completeTable
    )
    {
        // Arrange
        var projection = ConstraintMemberProjection(completeTable);

        ObserveAccepted(projection, ConstraintMemberIntent("foreign", "physical_key"),
            SafeMigrationObservedState.PrerequisiteMissing);
        ObserveAccepted(projection, new DropForeignKeyIntent("physical_key", "items"),
            SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, ConstraintMemberIntent("foreign", "physical_key"),
            SafeMigrationObservedState.Missing);

        var operation = new SafeMigrationOperation(ConstraintMemberIntent("foreign", "logical_alias"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Matching, "removed_old_alias"));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.Equal("physical_key", analysis.MatchedObjectName);
        Assert.True(analysis.PostconditionSatisfied);
    }

    /// <summary>Semantic FK candidates resolve deterministically while exact physical names retain priority.</summary>
    /// <param name="reverseInsertion">Whether physical candidates are accepted in reverse lexical order.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignKeySemanticMatchingSelectsDeterministicPhysicalCandidate(
        bool reverseInsertion
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var first = reverseInsertion ? "fk_z" : "fk_a";
        var second = reverseInsertion ? "fk_a" : "fk_z";
        var physicalDefinitions = new[]
        {
            ((EnsureForeignKeyIntent)ConstraintMemberIntent("foreign", first)).Definition,
            ((EnsureForeignKeyIntent)ConstraintMemberIntent("foreign", second)).Definition,
        };

        ObserveParentWithPrimaryKey(projection, Column("id"));
        // WHY: Deterministic selection requires two proven physical candidates;
        // semantic table matching by itself does not prove either authored symbol.
        ObserveStrictTableWithPhysicalForeignKeyBindings(projection,
            new ExpectedTableDefinition("items", [Column("id")], foreignKeys: physicalDefinitions),
            createdTable: false);

        // Act
        var semantic = Project(projection, ConstraintMemberIntent("foreign", "logical_alias"),
            SafeMigrationObservedState.Matching);
        var exact = Project(projection, ConstraintMemberIntent("foreign", "fk_z"),
            SafeMigrationObservedState.Matching);
        var firstDrop = Project(projection, new DropForeignKeyIntent("fk_a", "items"),
            SafeMigrationObservedState.Missing);
        var secondDrop = Project(projection, new DropForeignKeyIntent("fk_z", "items"),
            SafeMigrationObservedState.Missing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, semantic.ObservedState);
        Assert.Equal("fk_a", semantic.MatchedObjectName);
        Assert.Equal(SafeMigrationObservedState.Matching, exact.ObservedState);
        Assert.Equal("fk_z", exact.MatchedObjectName);
        Assert.Equal(SafeMigrationObservedState.Matching, firstDrop.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, secondDrop.ObservedState);
    }

    /// <summary>Untouched exact live matches record distinct physical FKs instead of semantic aliases.</summary>
    [Fact]
    public void ForeignKeyExactLivePhysicalIdentityExtendsUnchangedAcceptedGraph()
    {
        // Arrange
        var projection = ConstraintMemberProjection(completeTable: false);

        ObserveAccepted(projection, ConstraintMemberIntent("foreign", "fk_a"),
            SafeMigrationObservedState.Matching, "fk_a");

        var operation = new SafeMigrationOperation(ConstraintMemberIntent("foreign", "fk_b"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None, true, "existing_foreign_key_matching")
        {
            MatchedObjectName = "fk_b",
        };

        // Act
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy, analysis.RepairCapability);

        projection.Observe(operation, live, analysis, decision);

        var firstDrop = Project(projection, new DropForeignKeyIntent("fk_a", "items"),
            SafeMigrationObservedState.Missing);
        var secondDrop = Project(projection, new DropForeignKeyIntent("fk_b", "items"),
            SafeMigrationObservedState.Missing);

        // Assert
        Assert.Same(live, analysis);
        Assert.Equal("fk_b", analysis.MatchedObjectName);
        Assert.Equal(SafeMigrationAction.NoOp, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Matching, firstDrop.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, secondDrop.ObservedState);
    }

    /// <summary>Removed physical identities cannot return through immutable exact Matching.</summary>
    [Fact]
    public void ForeignKeyExactLivePhysicalIdentityCannotReviveAcceptedRemoval()
    {
        // Arrange
        var projection = ConstraintMemberProjection(completeTable: false);

        ObserveAccepted(projection, ConstraintMemberIntent("foreign", "fk_a"),
            SafeMigrationObservedState.Matching, "fk_a");
        ObserveAccepted(projection, new DropForeignKeyIntent("fk_b", "items"),
            SafeMigrationObservedState.Matching);

        var operation = new SafeMigrationOperation(ConstraintMemberIntent("foreign", "fk_b"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = Live(SafeMigrationObservedState.Matching, "fk_b");

        // Act
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy, analysis.RepairCapability);

        projection.Observe(operation, live, analysis, decision);

        var repeatedDrop = Project(projection, new DropForeignKeyIntent("fk_b", "items"),
            SafeMigrationObservedState.Matching);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.Equal("fk_a", analysis.MatchedObjectName);
        Assert.Equal(SafeMigrationObservedState.Missing, repeatedDrop.ObservedState);
    }

    /// <summary>Accepted physical definitions take priority over older immutable exact Matching.</summary>
    [Fact]
    public void ForeignKeyExactLivePhysicalIdentityCannotOverrideAcceptedChangedDefinition()
    {
        // Arrange
        var projection = ConstraintMemberProjection(completeTable: false);

        ObserveAccepted(projection, ConstraintMemberIntent("foreign", "fk_a"),
            SafeMigrationObservedState.Matching, "fk_a");
        ObserveAccepted(projection, new DropForeignKeyIntent("fk_b", "items"),
            SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
                "fk_b", "items", ["id"], "parents", ["id"], onDelete: ReferentialAction.Cascade)),
            SafeMigrationObservedState.PrerequisiteMissing);

        var operation = new SafeMigrationOperation(ConstraintMemberIntent("foreign", "fk_b"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Matching, "fk_b"));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
        Assert.Null(analysis.MatchedObjectName);
    }

    /// <summary>Supplies independently named physical FKs instead of inferring names from table shape.</summary>
    /// <param name="projection">The ordered physical projection receiving the fixture.</param>
    /// <param name="definition">The table whose test catalog contains these exact physical FK names.</param>
    /// <param name="createdTable">Whether DDL itself proves names instead of a catalog certificate.</param>
    private static void ObserveStrictTableWithPhysicalForeignKeyBindings(
        SafeMigrationPreflightProjection projection,
        ExpectedTableDefinition definition,
        bool createdTable
    )
    {
        var operation = new SafeMigrationOperation(
            new EnsureTableIntent(definition, SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = new SafeMigrationProviderAnalysis(
            createdTable ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None, false, "test_named_table")
        {
            TableConstraintIdentities = createdTable ? null : definition.ForeignKeys.Select(static constraint =>
                new SafeMigrationConstraintIdentityBinding(SafeMigrationDatabaseObjectKind.ForeignKey,
                    constraint.Name, constraint.Name)).ToArray(),
        };

        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy, analysis.RepairCapability);

        Assert.True(decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.NoOp);
        projection.Observe(operation, live, analysis, decision);
    }

    /// <summary>Builds independent current column/key proof without changing provider member identity.</summary>
    /// <param name="completeTable">Whether the owner has a complete created-table projection.</param>
    /// <param name="normalizer">The optional provider member identity rules.</param>
    /// <param name="keyAnalyzer">The optional provider-specific physical key identity contract.</param>
    private static SafeMigrationPreflightProjection ConstraintMemberProjection(
        bool completeTable,
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer = null,
        ISafeMigrationProjectedKeyAnalyzer? keyAnalyzer = null
    )
    {
        var projection = new SafeMigrationPreflightProjection(projectedKeyAnalyzer: keyAnalyzer,
            objectIdentityNormalizer: normalizer);

        if (completeTable)
        {
            Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("parents", [Column("id")],
                    primaryKey: new ExpectedPrimaryKeyDefinition("pk_parents", "parents", ["id"])),
                SafeMigrationTableMode.StrictDefinition));
            Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", [Column("id")]),
                SafeMigrationTableMode.StrictDefinition));
        }
        else
        {
            ObserveParentWithPrimaryKey(projection, Column("id"));
            ObserveColumnsOnExistingTable(projection, "items", Column("id"));
            ObserveAccepted(projection, new EnsureColumnIntent("items", Column("empty_marker")),
                SafeMigrationObservedState.Missing);
        }

        return projection;
    }

    /// <summary>Gets a constraint with stable semantics and an independently selected physical name.</summary>
    /// <param name="kind">The constraint family.</param>
    /// <param name="name">The requested constraint name.</param>
    private static SafeMigrationIntent ConstraintMemberIntent(
        string kind,
        string name
    ) => kind switch
    {
        "foreign" => new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(name, "items", ["id"],
            "parents", ["id"])),
        "unique" => new EnsureUniqueConstraintIntent(new ExpectedUniqueConstraintDefinition(name, "items", ["id"])),
        "check" => new EnsureCheckConstraintIntent(
            ExpectedCheckConstraintDefinition.FromExpression(name, "items", Positive("id"))),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Gets the exact physical drop intent for a constraint family.</summary>
    /// <param name="kind">The constraint family.</param>
    /// <param name="name">The requested physical name.</param>
    private static SafeMigrationIntent ConstraintMemberDrop(
        string kind,
        string name
    ) => kind switch
    {
        "foreign" => new DropForeignKeyIntent(name, "items"),
        "unique" => new DropUniqueConstraintIntent(name, "items"),
        "check" => new DropCheckConstraintIntent(name, "items"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Counts physical-identity normalization to detect unrelated-owner graph traversal.</summary>
    private sealed class CountingConstraintIdentityNormalizer : ISafeMigrationProviderObjectIdentityNormalizer
    {
        /// <summary>Gets the number of identifier and schema normalizations since the last reset.</summary>
        public int NormalizationCount { get; private set; }

        /// <summary>Gets the case-sensitive table and schema identity rule.</summary>
        public StringComparer IdentifierComparer => StringComparer.Ordinal;

        /// <summary>Resets the structural probe after its accepted graph has been arranged.</summary>
        public void Reset() => NormalizationCount = 0;

        /// <summary>Counts each physical identifier lookup without changing its identity.</summary>
        /// <param name="identifier">The requested physical identifier.</param>
        public string NormalizeIdentifier(string identifier)
        {
            NormalizationCount++;

            return identifier;
        }

        /// <summary>Counts schema lookup without changing its identity.</summary>
        /// <param name="schema">The requested schema.</param>
        public string? NormalizeSchema(string? schema)
        {
            // WHY: Table lookups compare identifiers directly but normalize
            // every schema, including NULL. This hook observes graph traversal.
            NormalizationCount++;

            return schema;
        }

        /// <summary>Does not simulate a physical identity rejection.</summary>
        /// <param name="analysis">The original provider analysis.</param>
        public bool IsObjectIdentityMismatch(SafeMigrationProviderAnalysis analysis) => false;
    }

    /// <summary>Models provider FK and column-member comparers without folding table or schema identifiers.</summary>
    /// <param name="ignoreColumnCase">Whether provider columns also use case-insensitive member identity.</param>
    private sealed class ForeignKeyMemberIdentityNormalizer(
        bool ignoreColumnCase = false
    ) : ISafeMigrationProviderObjectIdentityNormalizer
    {
        /// <summary>Gets the case-sensitive table and other constraint identity comparer.</summary>
        public StringComparer IdentifierComparer => StringComparer.Ordinal;

        /// <summary>Gets the selected physical column-member comparer.</summary>
        public StringComparer ColumnIdentifierComparer => ignoreColumnCase
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        /// <summary>Gets the case-insensitive physical FK name comparer.</summary>
        public StringComparer ForeignKeyIdentifierComparer => StringComparer.OrdinalIgnoreCase;

        /// <summary>Leaves case-sensitive identifiers unchanged.</summary>
        /// <param name="identifier">The physical identifier.</param>
        public string NormalizeIdentifier(string identifier) => identifier;

        /// <summary>Leaves schema qualifiers unchanged.</summary>
        /// <param name="schema">The physical qualifier.</param>
        public string? NormalizeSchema(string? schema) => schema;

        /// <summary>Does not simulate a provider identity rejection.</summary>
        /// <param name="analysis">The immutable provider analysis.</param>
        public bool IsObjectIdentityMismatch(SafeMigrationProviderAnalysis analysis) => false;
    }

    /// <summary>Builds a standalone key removal with an independent required-column empty-table certificate.</summary>
    /// <param name="dropFirst">Whether the removal precedes the required-column certificate.</param>
    /// <param name="strictTable">Whether the original table definition is completely accepted.</param>
    /// <param name="provider">The optional physical-key validator.</param>
    private static SafeMigrationPreflightProjection ConstraintReplacementPrimaryKeyProjection(
        bool dropFirst,
        bool strictTable,
        ISafeMigrationProjectedKeyAnalyzer? provider = null
    )
    {
        var projection = new SafeMigrationPreflightProjection(projectedKeyAnalyzer: provider);
        var definition = new ExpectedTableDefinition("items", [Column("id")],
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"]));

        ObserveAccepted(projection, new EnsureTableIntent(definition,
                strictTable ? SafeMigrationTableMode.StrictDefinition : SafeMigrationTableMode.ConvergenceContainer),
            SafeMigrationObservedState.Matching);

        if (!strictTable)
        {
            ObserveAccepted(projection, new EnsureColumnIntent("items", Column("id")),
                SafeMigrationObservedState.Matching);
        }

        if (dropFirst)
        {
            ObserveAccepted(projection, new DropPrimaryKeyIntent("pk_items", "items"),
                SafeMigrationObservedState.Matching);
        }

        ObserveAccepted(projection, new EnsureColumnIntent("items", Column("replacement_id")),
            SafeMigrationObservedState.Missing);

        if (!dropFirst)
        {
            ObserveAccepted(projection, new DropPrimaryKeyIntent("pk_items", "items"),
                SafeMigrationObservedState.Matching);
        }

        return projection;
    }

    /// <summary>Builds exact FK removal with independent dependent, principal and candidate-key proof.</summary>
    /// <param name="dropFirst">Whether removal precedes the dependent-column proof.</param>
    /// <param name="emptyProof">Whether a required addition supplies an empty-table proof.</param>
    private static SafeMigrationPreflightProjection ConstraintReplacementForeignKeyProjection(
        bool dropFirst,
        bool emptyProof
    )
    {
        var projection = new SafeMigrationPreflightProjection();

        ObserveParentWithPrimaryKey(projection, Column("id"));
        ObserveColumnsOnExistingTable(projection, "children", Column("id"));

        if (dropFirst)
        {
            ObserveAccepted(projection, new DropForeignKeyIntent("fk_children_parents", "children"),
                SafeMigrationObservedState.Matching);
        }

        ObserveAccepted(projection, new EnsureColumnIntent("children",
                emptyProof ? Column("parent_id") : NullableColumn("parent_id")),
            SafeMigrationObservedState.Missing);

        if (!dropFirst)
        {
            ObserveAccepted(projection, new DropForeignKeyIntent("fk_children_parents", "children"),
                SafeMigrationObservedState.Matching);
        }

        return projection;
    }

    /// <summary>Builds a complete newly created dependent table with a required FK column.</summary>
    private static SafeMigrationPreflightProjection ConstraintReplacementCompleteRequiredForeignKeyProjection()
    {
        var projection = new SafeMigrationPreflightProjection();
        var parent = new ExpectedTableDefinition("parents", [Column("id")],
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_parents", "parents", ["id"]));

        var child = new ExpectedTableDefinition("children", [Column("id"), Column("parent_id")],
            foreignKeys: [ForeignKey().Definition]);

        Apply(projection, new EnsureTableIntent(parent, SafeMigrationTableMode.StrictDefinition));
        Apply(projection, new EnsureTableIntent(child, SafeMigrationTableMode.StrictDefinition));
        ObserveAccepted(projection, new DropForeignKeyIntent("fk_children_parents", "children"),
            SafeMigrationObservedState.Matching);

        return projection;
    }

    /// <summary>Gets a key whose column semantics differ from the removed key.</summary>
    private static SafeMigrationOperation ConstraintReplacementPrimaryKeyOperation() => new(
        new EnsurePrimaryKeyIntent(
            new ExpectedPrimaryKeyDefinition("pk_items_replacement", "items", ["replacement_id"])),
        SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Gets an exact-name FK replacement with changed referential actions.</summary>
    /// <param name="onDelete">The requested delete action.</param>
    /// <param name="onUpdate">The requested update action.</param>
    private static SafeMigrationOperation ConstraintReplacementForeignKeyOperation(
        ReferentialAction onDelete,
        ReferentialAction onUpdate = ReferentialAction.NoAction
    ) => new(
        new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition("fk_children_parents", "children", ["parent_id"],
            "parents", ["id"], onUpdate: onUpdate, onDelete: onDelete)),
        SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Records a rejected operation without inventing an accepted prerequisite.</summary>
    /// <param name="projection">The projection receiving the rejected decision.</param>
    /// <param name="intent">The operation that must not become proof.</param>
    /// <param name="state">The rejecting provider state.</param>
    private static void ObserveConstraintReplacementRejected(
        SafeMigrationPreflightProjection projection,
        SafeMigrationIntent intent,
        SafeMigrationObservedState state
    )
    {
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var live = Live(state);
        var decision = SafeMigrationDecisionPlanner.Plan(intent.Kind, state, operation.Policy, live.RepairCapability);

        // WHY: Rejection is supplied directly so setup never projects its way into an accepted prerequisite.
        projection.Observe(operation, live, live, decision);
    }

    /// <summary>Invalidates previously captured row certificates using ordinary provider data mutation.</summary>
    /// <param name="projection">The projection receiving the mutation.</param>
    /// <param name="table">The physical table targeted by the mutation.</param>
    private static void ObserveConstraintReplacementDataMutation(
        SafeMigrationPreflightProjection projection,
        string table
    ) => projection.ObserveProviderPostcondition(new InsertDataOperation
    {
        Table = table,
        Columns = ["id"],
        Values = new object[,] { { 1 } },
    });

    /// <summary>Captures physical validation without generating any column or row certificate itself.</summary>
    /// <param name="result">The independently supplied provider result.</param>
    private sealed class ConstraintReplacementKeyProbe(
        SafeMigrationProviderAnalysis result
    ) : ISafeMigrationProjectedKeyAnalyzer
    {
        /// <summary>Gets whether unique constraints share physical identities with indexes.</summary>
        public bool SharesUniqueConstraintAndIndexIdentity => false;

        /// <summary>Gets the physical-validation call count.</summary>
        public int ValidationCount { get; private set; }

        /// <summary>Gets the immutable live analysis supplied to validation.</summary>
        public SafeMigrationProviderAnalysis? LiveAnalysis { get; private set; }

        /// <summary>Gets the Core analysis supplied to validation.</summary>
        public SafeMigrationProviderAnalysis? ProjectedAnalysis { get; private set; }

        /// <summary>Gets the accepted projected-column source supplied to validation.</summary>
        public ISafeMigrationProjectedColumnSource? ColumnSource { get; private set; }

        /// <summary>Captures the key-validation input and returns the independent physical result.</summary>
        /// <param name="intent">The requested primary key.</param>
        /// <param name="columns">The accepted projected-column source.</param>
        /// <param name="liveAnalysis">The immutable provider analysis.</param>
        /// <param name="projectedAnalysis">The Core replacement-proof result.</param>
        public SafeMigrationProviderAnalysis ValidateProjectedPrimaryKey(
            EnsurePrimaryKeyIntent intent,
            ISafeMigrationProjectedColumnSource columns,
            SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis
        )
        {
            ValidationCount++;
            LiveAnalysis = liveAnalysis;
            ProjectedAnalysis = projectedAnalysis;
            ColumnSource = columns;

            return result;
        }

        /// <summary>Leaves unrelated index validation unchanged.</summary>
        /// <param name="intent">The requested index.</param>
        /// <param name="columns">The accepted projected-column source.</param>
        /// <param name="liveAnalysis">The immutable provider analysis.</param>
        /// <param name="projectedAnalysis">The Core index result.</param>
        public SafeMigrationProviderAnalysis ValidateProjectedIndex(
            EnsureIndexIntent intent,
            ISafeMigrationProjectedColumnSource columns,
            SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis
        ) => projectedAnalysis;

        /// <summary>Leaves unrelated unique-constraint validation unchanged.</summary>
        /// <param name="intent">The requested unique constraint.</param>
        /// <param name="columns">The accepted projected-column source.</param>
        /// <param name="liveAnalysis">The immutable provider analysis.</param>
        /// <param name="projectedAnalysis">The Core unique-constraint result.</param>
        public SafeMigrationProviderAnalysis ValidateProjectedUniqueConstraint(
            EnsureUniqueConstraintIntent intent,
            ISafeMigrationProjectedColumnSource columns,
            SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis
        ) => projectedAnalysis;
    }
}
