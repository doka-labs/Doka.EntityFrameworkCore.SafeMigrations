namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies exact column-proof lifetime across accepted foreign-key-only removals.</summary>
public sealed class MySqlForeignKeyDropColumnProofTests
{
    /// <summary>A supported exact relaxation and an already-matching replay survive FK-only removals.</summary>
    /// <param name="matching">Whether the target already matches the immutable catalog.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedForeignKeyDropsRetainExactColumnCertificate(bool matching)
    {
        // Arrange
        using var context = CreateContext();
        var projection = CreateProjection(context);
        Accept(projection, new DropForeignKeyIntent("fk_reference", "items"));
        Accept(projection, new DropForeignKeyIntent("fk_other", "items"));
        var operation = Relaxation();
        var live = ColumnProof(matching);

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.Equal(matching ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            result.ObservedState);
        Assert.Equal(matching ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
            result.RepairCapability);
        Assert.Equal(matching, result.PostconditionSatisfied);
        Assert.Same(live, result);
    }

    /// <summary>Certified nullability changes retain unchanged sibling proofs without making keys unknown.</summary>
    /// <param name="nullableSource">Whether each live source requires a NULL-free tightening proof.</param>
    /// <param name="matchingSibling">Whether the second target already matches the immutable catalog.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ForeignKeyDropRetainsSiblingColumnCertificatesAfterAcceptedNullabilityChange(
        bool nullableSource,
        bool matchingSibling
    )
    {
        // Arrange
        using var context = CreateContext();
        var projection = CreateProjection(context);
        var index = new SafeMigrationOperation(new EnsureIndexIntent(new ExpectedIndexDefinition(
            "ix_left", "items", [new ExpectedIndexKeyDefinition("left_id")])),
            SafeMigrationPolicy.ThrowIfDifferent);
        var matchingIndex = ColumnProof(matching: true);
        projection.Observe(index, matchingIndex, matchingIndex, SafeMigrationDecisionPlanner.Plan(
            index.Intent.Kind, matchingIndex.ObservedState, index.Policy, matchingIndex.RepairCapability));
        Accept(projection, new DropForeignKeyIntent("fk_left", "items"));
        Accept(projection, new DropForeignKeyIntent("fk_right", "items"));
        var first = NullabilityChange("left_id", nullableSource);
        var firstLive = ColumnProof(requiresRows: nullableSource);
        var firstResult = projection.Project(first, firstLive);
        var firstDecision = SafeMigrationDecisionPlanner.Plan(first.Intent.Kind, firstResult.ObservedState,
            first.Policy, firstResult.RepairCapability);
        var sibling = NullabilityChange("right_id", nullableSource);
        var siblingLive = ColumnProof(matchingSibling, requiresRows: nullableSource);

        // Act
        projection.Observe(first, firstLive, firstResult, firstDecision);
        var result = projection.Project(sibling, siblingLive);

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, firstDecision.Action);
        Assert.Equal(matchingSibling ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            result.ObservedState);
        Assert.Equal(matchingSibling ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
            result.RepairCapability);
        Assert.Same(siblingLive, result);
    }

    /// <summary>A bounded accepted transition cannot authorize facet changes, stale rows or new dependencies.</summary>
    /// <param name="mutation">The independent reason a later source-bound proof is no longer applicable.</param>
    /// <param name="nullableSource">Whether the certified first operation tightens nullability.</param>
    [Theory]
    [InlineData("facet", false)]
    [InlineData("facet", true)]
    [InlineData("dependency", false)]
    [InlineData("dependency", true)]
    [InlineData("incoming-dependency", false)]
    [InlineData("retarget", false)]
    [InlineData("retarget", true)]
    [InlineData("data", false)]
    [InlineData("data", true)]
    [InlineData("backfill", true)]
    public void ForeignKeyDropNullabilityContinuationRetainsProofBoundaries(string mutation, bool nullableSource)
    {
        // Arrange
        using var context = CreateContext();
        var projection = CreateProjection(context);
        Accept(projection, new DropForeignKeyIntent("fk_left", "items"));
        var first = NullabilityChange("left_id", nullableSource);
        var firstLive = mutation == "backfill"
            ? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
                SafeMigrationRepairCapability.Safe, false, "captured_backfill") { RepairMutatesData = true }
            : ColumnProof(requiresRows: nullableSource);
        var firstResult = mutation == "backfill" ? firstLive : projection.Project(first, firstLive);
        var firstDecision = SafeMigrationDecisionPlanner.Plan(first.Intent.Kind, firstResult.ObservedState,
            first.Policy, firstResult.RepairCapability);
        projection.Observe(first, firstLive, firstResult, firstDecision);
        var later = NullabilityChange("right_id", nullableSource);

        switch (mutation)
        {
            case "facet":
                later = new SafeMigrationOperation(new AlterColumnIntent("items",
                    GuidColumn(!nullableSource, comment: "changed", name: "right_id"),
                    GuidColumn(nullableSource, name: "right_id")), SafeMigrationPolicy.RepairIfSafe);
                break;
            case "dependency":
                Accept(projection, new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
                    "fk_added", "items", ["right_id"], "parent", ["id"])));
                break;
            case "incoming-dependency":
                Accept(projection, new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
                    "fk_added", "child", ["id"], "items", ["right_id"])));
                break;
            case "retarget":
                later = new SafeMigrationOperation(new AlterColumnIntent("items",
                    GuidColumn(nullableSource, name: "left_id"), GuidColumn(!nullableSource, name: "left_id")),
                    SafeMigrationPolicy.RepairIfSafe);
                break;
            case "data":
                Invalidate(projection, "data");
                break;
        }

        // Act
        var result = projection.Project(later, ColumnProof(requiresRows: nullableSource));

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, firstDecision.Action);
        Assert.NotEqual(SafeMigrationRepairCapability.Safe, result.RepairCapability);
    }

    /// <summary>Only validated nonphysical backfill-marker changes may accompany a same-layout tightening.</summary>
    /// <param name="mutation">The independently changed metadata or source facet.</param>
    /// <param name="expected">Whether the source-bound NULL-free proof remains eligible.</param>
    [Theory]
    [InlineData("added-true", true)]
    [InlineData("added-false", true)]
    [InlineData("changed-true", true)]
    [InlineData("changed-false", true)]
    [InlineData("removed", true)]
    [InlineData("text-marker", false)]
    [InlineData("null-marker", false)]
    [InlineData("source-invalid", false)]
    [InlineData("unknown", false)]
    [InlineData("guid-format", false)]
    [InlineData("generation", false)]
    [InlineData("source-comment", false)]
    public void ForeignKeyDropTighteningAcceptsOnlyKnownBooleanReviewMarker(string mutation, bool expected)
    {
        // Arrange
        using var context = CreateContext();
        var projection = CreateProjection(context);
        Accept(projection, new DropForeignKeyIntent("fk_reference", "items"));
        var source = MarkedGuidColumn(nullable: true,
            includeMarker: mutation is "changed-true" or "changed-false" or "removed" or "source-invalid",
            marker: mutation == "source-invalid" ? "true" : mutation != "changed-true",
            comment: mutation == "source-comment" ? "changed" : null);
        var target = MarkedGuidColumn(nullable: false, includeMarker: mutation != "removed",
            marker: mutation == "text-marker" ? "true" : mutation == "null-marker" ? null
                : mutation is not "added-false" and not "changed-false",
            extraUnknown: mutation == "unknown",
            guidFormat: mutation == "guid-format" ? DokaMySqlGuidFormat.Binary16 : DokaMySqlGuidFormat.Char36,
            generation: mutation == "generation" ? MySqlValueGenerationStrategy.None
                : MySqlValueGenerationStrategy.ClientGuid);
        var operation = new SafeMigrationOperation(new AlterColumnIntent("items", target, source),
            SafeMigrationPolicy.RepairIfSafe);
        var live = ColumnProof(requiresRows: true);

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.Equal(expected ? SafeMigrationRepairCapability.Safe : SafeMigrationRepairCapability.None,
            result.RepairCapability);
    }

    /// <summary>Accepted source metadata may differ only in the supported boolean review marker.</summary>
    /// <param name="nullableSource">Whether the live source requires a NULL-free tightening proof.</param>
    /// <param name="acceptedMarker">Whether matching convergence captured the review marker.</param>
    /// <param name="mutation">The accepted source facet that must not be normalized away.</param>
    /// <param name="expected">Whether the independent current column proof remains reusable.</param>
    [Theory]
    [InlineData(false, true, "none", true)]
    [InlineData(false, false, "none", true)]
    [InlineData(true, true, "none", true)]
    [InlineData(true, false, "none", true)]
    [InlineData(false, true, "unknown", false)]
    [InlineData(true, true, "unknown", false)]
    [InlineData(false, true, "malformed-marker", false)]
    [InlineData(false, true, "comment", false)]
    [InlineData(false, true, "generation", false)]
    [InlineData(false, true, "nullability", false)]
    public void ForeignKeyDropUsesProviderQualifiedAcceptedSourceComparison(
        bool nullableSource,
        bool acceptedMarker,
        string mutation,
        bool expected
    )
    {
        // Arrange
        using var context = CreateContext();
        var projection = CreateProjection(context);
        var accepted = MarkedGuidColumn(mutation == "nullability" ? !nullableSource : nullableSource,
            includeMarker: acceptedMarker, marker: mutation == "malformed-marker" ? "true" : true,
            extraUnknown: mutation == "unknown", comment: mutation == "comment" ? "changed" : null,
            generation: mutation == "generation" ? MySqlValueGenerationStrategy.None
                : MySqlValueGenerationStrategy.ClientGuid);
        var ensure = new SafeMigrationOperation(new EnsureColumnIntent("items", accepted),
            SafeMigrationPolicy.ThrowIfDifferent);
        var ensureLive = ColumnProof(matching: true);
        var ensureResult = projection.Project(ensure, ensureLive);
        var ensureDecision = SafeMigrationDecisionPlanner.Plan(ensure.Intent.Kind, ensureResult.ObservedState,
            ensure.Policy, ensureResult.RepairCapability);
        projection.Observe(ensure, ensureLive, ensureResult, ensureDecision);
        var drop = new SafeMigrationOperation(new DropForeignKeyIntent("fk_reference", "items"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var dropLive = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None, false, "captured_foreign_key")
        {
            MatchedObjectName = "fk_reference",
        };

        var dropResult = projection.Project(drop, dropLive);
        var dropDecision = SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, dropResult.ObservedState,
            drop.Policy, dropResult.RepairCapability);
        projection.Observe(drop, dropLive, dropResult, dropDecision);
        var source = MarkedGuidColumn(nullableSource, includeMarker: !acceptedMarker, marker: true);
        var target = MarkedGuidColumn(!nullableSource, includeMarker: !acceptedMarker, marker: true);
        var operation = new SafeMigrationOperation(new AlterColumnIntent("items", target, source),
            SafeMigrationPolicy.RepairIfSafe);
        var live = ColumnProof(requiresRows: nullableSource);

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.Equal(SafeMigrationAction.NoOp, ensureDecision.Action);
        Assert.Equal(SafeMigrationAction.Apply, dropDecision.Action);
        Assert.Equal(expected ? SafeMigrationRepairCapability.Safe : SafeMigrationRepairCapability.None,
            result.RepairCapability);
        if (expected)
        {
            Assert.Same(live, result);
        }
    }

    /// <summary>A case variant cannot reuse original Matching after an accepted column nullability change.</summary>
    /// <param name="firstName">The column spelling used by the accepted tightening.</param>
    /// <param name="laterName">The physical column's alternate spelling used by the stale target.</param>
    [Theory]
    [InlineData("OWNER_ID", "owner_id")]
    [InlineData("Owner_Id", "OWNER_id")]
    public void ForeignKeyDropMixedCaseColumnHistoryCannotRestoreStaleMatching(string firstName, string laterName)
    {
        // Arrange
        using var context = CreateContext();
        var projection = CreateProjection(context);
        Accept(projection, new DropForeignKeyIntent("fk_owner", "items"));
        var first = NullabilityChange(firstName, nullableSource: true);
        var firstLive = ColumnProof(requiresRows: true);
        var firstResult = projection.Project(first, firstLive);
        var firstDecision = SafeMigrationDecisionPlanner.Plan(first.Intent.Kind, firstResult.ObservedState,
            first.Policy, firstResult.RepairCapability);
        projection.Observe(first, firstLive, firstResult, firstDecision);
        var later = NullabilityChange(laterName, nullableSource: false);
        var originalMatching = ColumnProof(matching: true);

        // Act
        var result = projection.Project(later, originalMatching);
        var decision = SafeMigrationDecisionPlanner.Plan(later.Intent.Kind, result.ObservedState,
            later.Policy, result.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, firstDecision.Action);
        Assert.NotEqual(SafeMigrationObservedState.Matching, result.ObservedState);
        Assert.False(result.PostconditionSatisfied);
        Assert.NotEqual(SafeMigrationAction.NoOp, decision.Action);
    }

    /// <summary>Provider source identity accepts column case while retaining exact known default metadata.</summary>
    /// <param name="defaultDrift">Whether the authored source omits an independently accepted literal default.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignKeyDropKnownSourceCaseComparisonRetainsDefaultContract(bool defaultDrift)
    {
        // Arrange
        using var context = CreateContext();
        var projection = CreateProjection(context);
        var accepted = GuidColumn(true, name: "owner_id",
            defaultValue: defaultDrift ? SafeMigrationDefaultValue.Literal(Guid.Empty) : null);
        var ensure = new SafeMigrationOperation(new EnsureColumnIntent("items", accepted),
            SafeMigrationPolicy.ThrowIfDifferent);
        var matching = ColumnProof(matching: true);
        var ensureResult = projection.Project(ensure, matching);
        var ensureDecision = SafeMigrationDecisionPlanner.Plan(ensure.Intent.Kind, ensureResult.ObservedState,
            ensure.Policy, ensureResult.RepairCapability);
        projection.Observe(ensure, matching, ensureResult, ensureDecision);
        Accept(projection, new DropForeignKeyIntent("fk_owner", "items"));
        var operation = NullabilityChange("OWNER_ID", nullableSource: true);
        var live = ColumnProof(requiresRows: true);

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.Equal(SafeMigrationAction.NoOp, ensureDecision.Action);
        Assert.Equal(defaultDrift ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
            result.RepairCapability);
    }

    /// <summary>Ensure consumes current provider evidence despite an unchanged earlier source declaration.</summary>
    /// <param name="nullableSource">The accepted live source nullability.</param>
    /// <param name="matching">Whether the reviewed target already matches the original catalog.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ForeignKeyDropEnsureReusesOnlyCurrentProviderColumnProof(bool nullableSource, bool matching)
    {
        // Arrange
        using var context = CreateContext();
        var projection = CreateProjection(context);
        var source = MarkedGuidColumn(nullableSource, includeMarker: true, marker: true);
        var initial = new SafeMigrationOperation(new EnsureColumnIntent("items", source),
            SafeMigrationPolicy.ThrowIfDifferent);
        var initialLive = ColumnProof(matching: true);
        var initialResult = projection.Project(initial, initialLive);
        var initialDecision = SafeMigrationDecisionPlanner.Plan(initial.Intent.Kind, initialResult.ObservedState,
            initial.Policy, initialResult.RepairCapability);
        projection.Observe(initial, initialLive, initialResult, initialDecision);
        Accept(projection, new DropForeignKeyIntent("fk_reference", "items"));
        var target = MarkedGuidColumn(matching ? nullableSource : !nullableSource,
            includeMarker: false, marker: null);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", target),
            SafeMigrationPolicy.RepairIfSafe);
        var live = ColumnProof(matching, requiresRows: nullableSource && !matching);

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.Equal(SafeMigrationAction.NoOp, initialDecision.Action);
        Assert.Equal(matching ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            result.ObservedState);
        Assert.Equal(matching ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
            result.RepairCapability);
        Assert.Equal(live.Code, result.Code);
        Assert.Equal(live.RequiresLiveDataProof, result.RequiresLiveDataProof);
        Assert.Equal(live.RepairMutatesData, result.RepairMutatesData);
        Assert.Equal(live.PostconditionSatisfied, result.PostconditionSatisfied);
        Assert.Equal(live.OperationalImpact, result.OperationalImpact);
        Assert.Equal(live.RepairPreservesLiveValueDomain, result.RepairPreservesLiveValueDomain);
        Assert.Equal(live.RequiredColumnAddResumesOwnedStaging, result.RequiredColumnAddResumesOwnedStaging);
        Assert.Same(live.IndexPhysicalEnvironment, result.IndexPhysicalEnvironment);
        Assert.Same(live.Differences, result.Differences);
        Assert.False(live.RepairPreservesIntrinsicColumnLayout);
        Assert.False(live.RepairPreservesPhysicalKeys);

        if (matching)
        {
            Assert.Same(live, result);
            Assert.False(result.RepairPreservesIntrinsicColumnLayout);
        }
        else
        {
            Assert.NotSame(live, result);
            Assert.True(result.RepairPreservesIntrinsicColumnLayout);
            Assert.True(result.RepairPreservesPhysicalKeys);
        }
    }

    /// <summary>Only a known same-layout non-data Ensure repair retains sibling column certificates.</summary>
    /// <param name="defaultChange">Whether the accepted repair also changes the original default contract.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignKeyDropEnsurePreservesSiblingProofOnlyForKnownNullabilityChange(bool defaultChange)
    {
        // Arrange
        using var context = CreateContext();
        var projection = CreateProjection(context);
        var source = GuidColumn(true, name: "left_id");
        var initial = new SafeMigrationOperation(new EnsureColumnIntent("items", source),
            SafeMigrationPolicy.ThrowIfDifferent);
        var matching = ColumnProof(matching: true);
        var initialResult = projection.Project(initial, matching);
        projection.Observe(initial, matching, initialResult, SafeMigrationDecisionPlanner.Plan(
            initial.Intent.Kind, initialResult.ObservedState, initial.Policy, initialResult.RepairCapability));
        Accept(projection, new DropForeignKeyIntent("fk_left", "items"));
        var target = GuidColumn(false, name: "left_id",
            defaultValue: defaultChange ? SafeMigrationDefaultValue.Literal(Guid.Empty) : null);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", target),
            SafeMigrationPolicy.RepairIfSafe);
        var live = ColumnProof(requiresRows: true);
        var result = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, result.ObservedState,
            operation.Policy, result.RepairCapability);

        // Act
        projection.Observe(operation, live, result, decision);
        var sibling = projection.Project(NullabilityChange("right_id", nullableSource: false), ColumnProof());

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, decision.Action);
        Assert.Equal(defaultChange ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
            sibling.RepairCapability);
    }

    /// <summary>Ensure cannot overwrite source mutations, opaque operations, stale rows or provider blockers.</summary>
    /// <param name="mutation">The invalidating event or independent provider refusal.</param>
    [Theory]
    [InlineData("data")]
    [InlineData("raw")]
    [InlineData("target")]
    [InlineData("unknown")]
    [InlineData("data-blocked")]
    [InlineData("unsupported")]
    [InlineData("unsupported-target")]
    public void ForeignKeyDropEnsureRejectsInvalidatedOrUnsupportedColumnProof(string mutation)
    {
        // Arrange
        using var context = CreateContext();
        var projection = CreateProjection(context);
        var source = GuidColumn(true);
        var initial = new SafeMigrationOperation(new EnsureColumnIntent("items", source),
            SafeMigrationPolicy.ThrowIfDifferent);
        var matching = ColumnProof(matching: true);
        var initialResult = projection.Project(initial, matching);
        projection.Observe(initial, matching, initialResult, SafeMigrationDecisionPlanner.Plan(
            initial.Intent.Kind, initialResult.ObservedState, initial.Policy, initialResult.RepairCapability));
        Accept(projection, new DropForeignKeyIntent("fk_reference", "items"));
        if (mutation is "data" or "raw")
        {
            Invalidate(projection, mutation);
        }

        if (mutation == "unknown")
        {
            projection.ObserveProviderPostcondition(new AlterTableOperation { Name = "items" });
        }

        if (mutation == "target")
        {
            var changed = new SafeMigrationOperation(new AlterColumnIntent("items",
                GuidColumn(false), source), SafeMigrationPolicy.RepairIfSafe);
            var changedLive = ColumnProof(requiresRows: true);
            var changedResult = projection.Project(changed, changedLive);
            projection.Observe(changed, changedLive, changedResult, SafeMigrationDecisionPlanner.Plan(
                changed.Intent.Kind, changedResult.ObservedState, changed.Policy, changedResult.RepairCapability));
        }

        var target = GuidColumn(mutation == "target", annotation: mutation == "unsupported-target");
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", target),
            SafeMigrationPolicy.RepairIfSafe);
        var live = mutation is "data-blocked" or "unsupported"
            ? new SafeMigrationProviderAnalysis(mutation == "data-blocked" ? SafeMigrationObservedState.DataBlocked
                : SafeMigrationObservedState.Unsupported, SafeMigrationRepairCapability.None, false, "provider_blocker")
            : ColumnProof(requiresRows: true);

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.NotEqual(SafeMigrationRepairCapability.Safe, result.RepairCapability);
        if (mutation is "data-blocked" or "unsupported")
        {
            Assert.Same(live, result);
        }
    }

    /// <summary>The first safe GUID backfill may repair, but its DML expires every later row proof.</summary>
    /// <param name="marker">Whether the supported target requires explicit backfill review.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignKeyDropAcceptsCapturedGuidBackfillWithoutRetainingLaterCertificates(bool marker)
    {
        // Arrange
        using var context = CreateContext();
        var projection = CreateProjection(context);
        Accept(projection, new DropForeignKeyIntent("fk_reference", "items"));
        var source = GuidColumn(true);
        var targetOperation = new AddColumnOperation
        {
            Name = "reference_id",
            Table = "items",
            ClrType = typeof(Guid),
            ColumnType = "char(36)",
            IsNullable = false,
            DefaultValue = Guid.Parse("00000000-0000-0000-0000-000000000001"),
        };

        if (marker)
        {
            targetOperation.AddAnnotation(MySqlSafeMigrationColumnMetadata.ExplicitBackfillAnnotation, true);
        }

        var target = SafeMigrationExpectedDefinitionFactory.From(targetOperation);
        var operation = new SafeMigrationOperation(new AlterColumnIntent("items", target, source),
            SafeMigrationPolicy.RepairIfSafe);
        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe, false, "captured_guid_backfill")
        {
            RepairMutatesData = true,
            RequiresLiveDataProof = true,
        };

        var result = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, result.ObservedState,
            operation.Policy, result.RepairCapability);
        var later = NullabilityChange("right_id", nullableSource: true);

        // Act
        projection.Observe(operation, live, result, decision);
        var laterResult = projection.Project(later, ColumnProof(requiresRows: true));

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, decision.Action);
        Assert.True(result.RepairMutatesData);
        Assert.Equal(1, projection.DataMutationVersion);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, laterResult.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.None, laterResult.RepairCapability);
    }

    /// <summary>A captured backfill cannot authorize unsupported target metadata or changed storage.</summary>
    /// <param name="mutation">The target contract difference outside the bounded literal-GUID backfill.</param>
    [Theory]
    [InlineData("unknown")]
    [InlineData("malformed-marker")]
    [InlineData("storage")]
    [InlineData("no-default")]
    [InlineData("after-sibling")]
    public void ForeignKeyDropGuidBackfillRetainsProviderContractBoundaries(string mutation)
    {
        // Arrange
        using var context = CreateContext();
        var projection = CreateProjection(context);
        Accept(projection, new DropForeignKeyIntent("fk_reference", "items"));
        SafeMigrationAction? siblingAction = null;
        if (mutation == "after-sibling")
        {
            var sibling = NullabilityChange("left_id", nullableSource: false);
            var siblingLive = ColumnProof();
            var siblingResult = projection.Project(sibling, siblingLive);
            var siblingDecision = SafeMigrationDecisionPlanner.Plan(
                sibling.Intent.Kind, siblingResult.ObservedState, sibling.Policy, siblingResult.RepairCapability);
            siblingAction = siblingDecision.Action;
            projection.Observe(sibling, siblingLive, siblingResult, siblingDecision);
        }

        var source = GuidColumn(true);
        var annotationSource = new AddColumnOperation();
        if (mutation == "unknown")
        {
            annotationSource.AddAnnotation("Test:Unknown", true);
        }

        if (mutation == "malformed-marker")
        {
            annotationSource.AddAnnotation(MySqlSafeMigrationColumnMetadata.ExplicitBackfillAnnotation, "true");
        }

        var target = new ExpectedColumnDefinition("reference_id", typeof(Guid), false,
            mutation == "storage" ? "binary(16)" : "char(36)",
            defaultValue: mutation == "no-default" ? null : SafeMigrationDefaultValue.Literal(Guid.Empty))
        {
            ProviderAnnotations = SafeMigrationProviderAnnotation.Capture(annotationSource),
        };

        var operation = new SafeMigrationOperation(new AlterColumnIntent("items", target, source),
            SafeMigrationPolicy.RepairIfSafe);
        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe, false, "captured_guid_backfill")
        {
            RepairMutatesData = true,
            RequiresLiveDataProof = true,
        };

        // Act
        var result = projection.Project(operation, live);

        // Assert
        if (mutation == "after-sibling")
        {
            Assert.Equal(SafeMigrationAction.Repair, siblingAction);
        }

        Assert.NotEqual(SafeMigrationRepairCapability.Safe, result.RepairCapability);
    }

    /// <summary>Repeated exact certificate reuse does not clone the column or projected table shape.</summary>
    [Fact]
    public void ForeignKeyDropColumnCertificateReuseAvoidsPerOperationShapeCopies()
    {
        // Arrange
        using var context = CreateContext();
        var projection = CreateProjection(context);
        Accept(projection, new DropForeignKeyIntent("fk_reference", "items"));
        var operation = Relaxation();
        var live = ColumnProof();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            projection.Project(operation, live);
        }

        // Act
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            projection.Project(operation, live);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // Assert
        Assert.InRange(allocated, 0, 1_024);
    }

    /// <summary>Authored source facets cannot substitute for a provider's current live source certificate.</summary>
    /// <param name="drift">The independently changed authored facet.</param>
    [Theory]
    [InlineData("source-missing")]
    [InlineData("source-type")]
    [InlineData("target-type")]
    [InlineData("target-length")]
    [InlineData("target-collation")]
    [InlineData("target-default")]
    [InlineData("target-comment")]
    [InlineData("target-annotation")]
    [InlineData("source-target-annotation")]
    [InlineData("same-nullability")]
    public void ForeignKeyDropDoesNotAuthorizeAnotherColumnContract(string drift)
    {
        // Arrange
        using var context = CreateContext();
        var projection = CreateProjection(context);
        Accept(projection, new DropForeignKeyIntent("fk_reference", "items"));
        var source = GuidColumn(false, storeType: drift == "source-type" ? "binary(16)" : "char(36)",
            annotation: drift == "source-target-annotation");
        var target = GuidColumn(drift != "same-nullability",
            storeType: drift == "target-type" ? "binary(16)" : "char(36)",
            maxLength: drift == "target-length" ? 36 : null,
            collation: drift == "target-collation" ? new SafeMigrationCollationIdentifier("ascii_bin") : null,
            defaultValue: drift == "target-default" ? SafeMigrationDefaultValue.Literal(Guid.Empty) : null,
            comment: drift == "target-comment" ? "changed" : null,
            annotation: drift is "target-annotation" or "source-target-annotation");

        var operation = new SafeMigrationOperation(new AlterColumnIntent("items", target,
            drift == "source-missing" ? null : source), SafeMigrationPolicy.RepairIfSafe);

        // Act
        var result = projection.Project(operation, ColumnProof());

        // Assert
        Assert.NotEqual(SafeMigrationRepairCapability.Safe, result.RepairCapability);
    }

    /// <summary>An accepted live source definition must agree with the independently reviewed old contract.</summary>
    [Fact]
    public void ForeignKeyDropDoesNotReplaceKnownSourceWithAuthoredOldDefinition()
    {
        // Arrange
        using var context = CreateContext();
        var projection = CreateProjection(context);
        var ensure = new SafeMigrationOperation(new EnsureColumnIntent("items",
            GuidColumn(false, storeType: "binary(16)")), SafeMigrationPolicy.ThrowIfDifferent);
        var matching = ColumnProof(matching: true);
        projection.Observe(ensure, matching, matching, SafeMigrationDecisionPlanner.Plan(
            ensure.Intent.Kind, matching.ObservedState, ensure.Policy, matching.RepairCapability));
        Accept(projection, new DropForeignKeyIntent("fk_reference", "items"));

        // Act
        var result = projection.Project(Relaxation(), ColumnProof());

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, result.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.None, result.RepairCapability);
    }

    /// <summary>Only accepted FK removals preserve the exact source lifetime; other history stays closed.</summary>
    /// <param name="mutation">The earlier mutation that invalidates the captured certificate.</param>
    /// <param name="beforeDrop">Whether the invalidating mutation preceded the accepted FK drop.</param>
    [Theory]
    [InlineData("data", false)]
    [InlineData("data", true)]
    [InlineData("raw", false)]
    [InlineData("table", false)]
    [InlineData("table", true)]
    [InlineData("column-drop", false)]
    [InlineData("column-alter", false)]
    [InlineData("column-rename", false)]
    [InlineData("key-drop", false)]
    [InlineData("index-drop", false)]
    [InlineData("foreign-key-add", false)]
    [InlineData("incoming-foreign-key", false)]
    public void ForeignKeyDropCannotReviveAnInvalidatedColumnCertificate(string mutation, bool beforeDrop)
    {
        // Arrange
        using var context = CreateContext();
        var projection = CreateProjection(context);
        if (beforeDrop)
        {
            Invalidate(projection, mutation);
        }

        Accept(projection, new DropForeignKeyIntent("fk_reference", "items"));
        if (!beforeDrop)
        {
            Invalidate(projection, mutation);
        }

        var operation = Relaxation();

        // Act
        var result = projection.Project(operation, ColumnProof());

        // Assert
        Assert.NotEqual(SafeMigrationRepairCapability.Safe, result.RepairCapability);
    }

    /// <summary>Independent provider rejections are not repaired by removing a foreign key.</summary>
    /// <param name="state">The independently observed provider blocker.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.DataBlocked)]
    [InlineData(SafeMigrationObservedState.Unsupported)]
    public void ForeignKeyDropRetainsIndependentColumnBlocker(SafeMigrationObservedState state)
    {
        // Arrange
        using var context = CreateContext();
        var projection = CreateProjection(context);
        Accept(projection, new DropForeignKeyIntent("fk_reference", "items"));
        var live = new SafeMigrationProviderAnalysis(state, SafeMigrationRepairCapability.None, false,
            "independent_column_blocker");

        // Act
        var result = projection.Project(Relaxation(), live);

        // Assert
        Assert.Equal(state, result.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.None, result.RepairCapability);
    }

    /// <summary>Builds the real provider projection without connecting to a server.</summary>
    private static SafeMigrationPreflightProjection CreateProjection(DbContext context)
    {
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        return new SafeMigrationPreflightProjection(
            providerOperationProjection: (ISafeMigrationProviderOperationProjection)analyzer,
            objectIdentityNormalizer: (ISafeMigrationProviderObjectIdentityNormalizer)analyzer,
            projectedColumnAnalyzer: (ISafeMigrationProjectedColumnAnalyzer)analyzer);
    }

    /// <summary>Records one accepted safe removal or explicitly supplied anonymous constraint.</summary>
    private static void Accept(SafeMigrationPreflightProjection projection, SafeMigrationIntent intent)
    {
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var live = new SafeMigrationProviderAnalysis(
            intent.Kind is SafeMigrationOperationKind.DropForeignKey
                or SafeMigrationOperationKind.DropPrimaryKey
                or SafeMigrationOperationKind.DropIndex
                or SafeMigrationOperationKind.DropColumn
                    ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, false, "accepted_operation")
        {
            MatchedObjectName = intent.ObjectName,
        };

        var decision = SafeMigrationDecisionPlanner.Plan(intent.Kind, live.ObservedState,
            operation.Policy, live.RepairCapability);
        projection.Observe(operation, live, live, decision);
    }

    /// <summary>Models unrelated or local effects that an immutable provider proof cannot cover.</summary>
    private static void Invalidate(SafeMigrationPreflightProjection projection, string mutation)
    {
        switch (mutation)
        {
            case "data":
                projection.ObserveProviderPostcondition(new InsertDataOperation
                {
                    Table = "other",
                    Columns = ["id"],
                    Values = new object?[,] { { 1 } },
                });
                break;
            case "raw":
                projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1" });
                break;
            case "table":
                projection.ObserveProviderPostcondition(new AlterTableOperation { Name = "items" });
                break;
            case "column-drop":
                Accept(projection, new DropColumnIntent("other", "items"));
                break;
            case "column-alter":
                projection.ObserveProviderPostcondition(new AlterColumnOperation
                {
                    Table = "items",
                    Name = "other",
                    ClrType = typeof(int),
                    ColumnType = "int",
                    IsNullable = true,
                });
                break;
            case "column-rename":
                projection.ObserveProviderPostcondition(new RenameColumnOperation
                {
                    Table = "items",
                    Name = "reference_id",
                    NewName = "renamed",
                });
                break;
            case "key-drop":
                Accept(projection, new DropPrimaryKeyIntent("pk_items", "items"));
                break;
            case "index-drop":
                Accept(projection, new DropIndexIntent("ix_other", "items"));
                break;
            case "foreign-key-add":
                Accept(projection, new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
                    "fk_added", "items", ["reference_id"], "parent", ["id"])));
                break;
            case "incoming-foreign-key":
                Accept(projection, new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
                    "fk_added", "child", ["id"], "items", ["reference_id"])));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }

    /// <summary>Creates the exact declaration pair used by the original source-bound proof.</summary>
    private static SafeMigrationOperation Relaxation() => new(
        new AlterColumnIntent("items", GuidColumn(true), GuidColumn(false)), SafeMigrationPolicy.RepairIfSafe);

    /// <summary>Creates an exact same-facet nullability change for one anonymous sibling column.</summary>
    private static SafeMigrationOperation NullabilityChange(string name, bool nullableSource) => new(
        new AlterColumnIntent("items", GuidColumn(!nullableSource, name: name), GuidColumn(nullableSource, name: name)),
        SafeMigrationPolicy.RepairIfSafe);

    /// <summary>Creates an anonymous GUID declaration with independently controllable drift.</summary>
    private static ExpectedColumnDefinition GuidColumn(
        bool nullable,
        string storeType = "char(36)",
        int? maxLength = null,
        SafeMigrationCollationIdentifier? collation = null,
        SafeMigrationDefaultValue? defaultValue = null,
        string? comment = null,
        bool annotation = false,
        string name = "reference_id"
    ) => new(name, typeof(Guid), nullable, storeType, maxLength: maxLength,
        collation: collation, comment: comment, defaultValue: defaultValue)
    {
        ProviderAnnotations = annotation ? DriftAnnotations() : [],
    };

    /// <summary>Captures anonymous provider metadata through the production immutability boundary.</summary>
    private static IReadOnlyList<SafeMigrationProviderAnnotation> DriftAnnotations()
    {
        var operation = new AddColumnOperation();
        operation.AddAnnotation("test:facet", 1);

        return SafeMigrationProviderAnnotation.Capture(operation);
    }

    /// <summary>Captures exact typed physical metadata separately from the nonphysical boolean review marker.</summary>
    private static ExpectedColumnDefinition MarkedGuidColumn(
        bool nullable,
        bool includeMarker,
        object? marker,
        bool extraUnknown = false,
        DokaMySqlGuidFormat guidFormat = DokaMySqlGuidFormat.Char36,
        MySqlValueGenerationStrategy generation = MySqlValueGenerationStrategy.ClientGuid,
        string? comment = null
    )
    {
        var operation = new AddColumnOperation
        {
            Name = "reference_id",
            Table = "items",
            ClrType = typeof(Guid),
            ColumnType = "char(36)",
            IsNullable = nullable,
            Comment = comment,
        };

        operation["Doka:MySql:GuidFormat"] = guidFormat;
        operation["Doka:MySql:ValueGenerationStrategy"] = generation;
        if (includeMarker)
        {
            operation.AddAnnotation(MySqlSafeMigrationColumnMetadata.ExplicitBackfillAnnotation, marker);
        }

        if (extraUnknown)
        {
            operation["Test:Unknown"] = true;
        }

        return SafeMigrationExpectedDefinitionFactory.From(operation);
    }

    /// <summary>Represents an exact independently captured live column certificate, not authored evidence.</summary>
    private static SafeMigrationProviderAnalysis ColumnProof(bool matching = false, bool requiresRows = false) => new(
        matching ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
        matching ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
        matching, "source_bound_column_proof")
    {
        RequiresLiveDataProof = requiresRows,
    };

    /// <summary>Creates an offline provider context for the synchronous projection callback.</summary>
    private static DbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DbContext>()
            .UseMySql("Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
                MySqlServerVersion.MySql(new Version(8, 4, 11)))
            .UseMySqlSafeMigrations()
            .Options;

        return new DbContext(options);
    }
}
