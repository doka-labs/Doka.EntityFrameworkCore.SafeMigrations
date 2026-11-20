namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Protects ordered support-index coverage, physical lifetimes, aliases, and unsafe-index risk.</summary>
public sealed class MySqlProjectedForeignKeySupportingIndexesTests
{
    /// <summary>
    /// Requires a complete covering index before an occupied automatic-index name can be considered safe.
    /// </summary>
    /// <param name="occupied">Whether the captured automatic-index namespace is already occupied.</param>
    /// <param name="expected">The independently specified result for this exact input case.</param>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void OccupiedAutomaticIndexNameNeedsACompleteCoveringIndex(
        bool occupied,
        bool expected
    )
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied);

        // Act
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>Classifies only an occupied captured name without valid coverage as a proven name collision.</summary>
    /// <param name="occupied">Whether the captured automatic-index namespace is already occupied.</param>
    /// <param name="expected">The independently specified result for this exact input case.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void OnlyAnOccupiedCapturedNameWithoutCoverageIsAKnownCollision(
        bool occupied,
        bool expected
    )
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied);

        // Act
        var result = indexes.HasKnownNameCollision(foreignKey);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Retains a known automatic-index collision after an accepted wrong-shape index occupies the captured name.
    /// </summary>
    [Fact]
    public void AcceptedWrongShapeIndexCreatesAKnownCollisionForTheCapturedRequest()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: false);
        Apply(indexes, new EnsureIndexIntent(Index(foreignKey.Name, "other_id")));

        // Act
        var result = indexes.HasKnownNameCollision(foreignKey);

        // Assert
        Assert.True(result);
    }

    /// <summary>Does not report a collision when independently proven native or projected coverage survives.</summary>
    /// <param name="projected">Whether the prerequisite shape comes from an accepted earlier operation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SurvivingNativeOrProjectedCoverageIsNotAKnownCollision(
        bool projected
    )
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(
            foreignKey,
            occupied: true,
            projected
                ? []
                : ["covering"]);

        if (projected)
        {
            Apply(indexes, new EnsureIndexIntent(Index("covering", "parent_id")));
        }

        // Act
        var result = indexes.HasKnownNameCollision(foreignKey);

        // Assert
        Assert.False(result);
    }

    /// <summary>Does not invent a known collision from an unproven owner state.</summary>
    /// <param name="boundary">The exact catalog or SQL boundary selected for this case.</param>
    [Theory]
    [InlineData("Opaque")]
    [InlineData("Unknown")]
    [InlineData("Dropped")]
    [InlineData("WrongOwner")]
    [InlineData("UncapturedShape")]
    public void UnprovenOwnerStateCannotAssertAKnownCollision(
        string boundary
    )
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true);
        var requested = foreignKey;
        switch (boundary)
        {
            case "Opaque":
                indexes.ObserveProviderOperation(new SqlOperation { Sql = "SELECT 1" });
                break;
            case "Unknown":
                indexes.ObserveProviderOperation(
                    new CreateIndexOperation
                    {
                        Table = "child",
                        Name = "unknown",
                        Columns = ["parent_id"],
                    });

                break;
            case "Dropped":
                Apply(indexes, new DropTableIntent("child"));
                break;
            case "WrongOwner":
                requested = new ExpectedForeignKeyDefinition(
                    foreignKey.Name,
                    "Child",
                    ["parent_id"],
                    "parent",
                    ["id"]);

                break;
            case "UncapturedShape":
                requested = ForeignKey("other_id");
                break;
        }

        // Act
        var result = indexes.HasKnownNameCollision(requested);

        // Assert
        Assert.False(result);
    }

    /// <summary>
    /// Does not resurrect dropped covering evidence when the same name is reused for a different shape.
    /// </summary>
    [Fact]
    public void DroppedCoveringAliasCannotBeResurrectedByWrongShapeNameReuse()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true, "covering");
        Apply(indexes, new DropIndexIntent("covering", "child"));
        Apply(indexes, new EnsureIndexIntent(Index("covering", "old_parent_id")));

        // Act
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(result);
    }

    /// <summary>Keeps independent alias coverage after a different alias is removed.</summary>
    [Fact]
    public void DroppingOneAliasPreservesAnIndependentSurvivingAlias()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true, "first", "second");
        Apply(indexes, new DropIndexIntent("first", "child"));

        // Act
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// Uses the requested ordered left prefix rather than unordered membership to prove support coverage.
    /// </summary>
    [Fact]
    public void AcceptedSupportingIndexUsesTheRequestedOrderedLeftPrefix()
    {
        // Arrange
        var foreignKey = ForeignKey("parent_id", "tenant_id");
        var indexes = Capture(foreignKey, occupied: true);
        Apply(indexes, new EnsureIndexIntent(Index("new_cover", "parent_id", "tenant_id", "extra")));

        // Act
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.True(result);
    }

    /// <summary>Rejects a same-name index whose key members are ordered differently from the required prefix.</summary>
    [Fact]
    public void SameNameWithWrongColumnOrderIsNotCoveringAuthority()
    {
        // Arrange
        var foreignKey = ForeignKey("parent_id", "tenant_id");
        var indexes = Capture(foreignKey, occupied: true);
        Apply(indexes, new EnsureIndexIntent(Index("new_cover", "tenant_id", "parent_id")));

        // Act
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(result);
    }

    /// <summary>Does not mint index coverage from a rejected or non-applying operation.</summary>
    /// <param name="action">The action or decision selected by the test case.</param>
    /// <param name="state">The selected preexisting native or projected lifecycle state.</param>
    /// <param name="code">The expected machine-readable classification code.</param>
    [Theory]
    [InlineData(SafeMigrationAction.NoOp, SafeMigrationObservedState.Matching, "NoOpMatching")]
    [InlineData(SafeMigrationAction.NoOp, SafeMigrationObservedState.Different, "NoOpExistsOnlyDifferent")]
    [InlineData(SafeMigrationAction.RejectDifferent, SafeMigrationObservedState.Different, "Rejected")]
    [InlineData(SafeMigrationAction.ValidateAtRuntime, SafeMigrationObservedState.Missing, "Deferred")]
    public void NonApplyingIndexCannotMintCoverage(
        SafeMigrationAction action,
        SafeMigrationObservedState state,
        string code
    )
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true);
        var operation = Operation(new EnsureIndexIntent(Index("new_cover", "parent_id")));

        // Act
        indexes.ObserveAcceptedOperation(operation, Analysis(state), new SafeMigrationDecision(action, code));
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(result);
    }

    /// <summary>Admits future index evidence only after its accepted operation ordinal has been reached.</summary>
    [Fact]
    public void FutureIndexDoesNotParticipateInAnEarlierForeignKeyProof()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true);
        var future = Operation(new EnsureIndexIntent(Index("future_cover", "parent_id")));

        // Act
        var before = indexes.CanEnsure(foreignKey);
        indexes.ObserveAcceptedOperation(
            future,
            Analysis(),
            new SafeMigrationDecision(SafeMigrationAction.Apply, "ApplyMissing"));

        var after = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(before);
        Assert.True(after);
    }

    /// <summary>
    /// Does not carry a renamed and subsequently dropped index lifetime into a wrong-shape name reuse.
    /// </summary>
    [Fact]
    public void RenamedThenDroppedAliasCannotSurviveWrongShapeReuse()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true, "first");
        Apply(indexes, new RenameIndexIntent("first", "child", "renamed"));
        Apply(indexes, new DropIndexIntent("renamed", "child"));
        Apply(indexes, new EnsureIndexIntent(Index("renamed", "other")));

        // Act
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(result);
    }

    /// <summary>Moves the existing physical coverage certificate with an accepted index rename.</summary>
    [Fact]
    public void AcceptedIndexRenameRetainsThePhysicalCoverageLifetime()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true, "first");
        Apply(indexes, new RenameIndexIntent("first", "child", "renamed"));
        Apply(indexes, new DropIndexIntent("first", "child"));

        // Act
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// Tracks name occupancy when an uncaptured index is renamed into the foreign-key support symbol.
    /// </summary>
    [Fact]
    public void RenamingAnUncapturedIndexIntoTheForeignKeySymbolOccupiesItsName()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: false);
        Apply(indexes, new RenameIndexIntent("uncaptured", "child", foreignKey.Name));

        // Act
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(result);
    }

    /// <summary>Revokes a captured physical index identity when its accepted repair replaces the shape.</summary>
    [Fact]
    public void IndexRepairRevokesTheCapturedPhysicalIdentity()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true, "covering");
        var operation = Operation(new EnsureIndexIntent(Index("covering", "other")));

        // Act
        indexes.ObserveAcceptedOperation(
            operation,
            Analysis(SafeMigrationObservedState.Different),
            new SafeMigrationDecision(SafeMigrationAction.Repair, "RepairDifferent"));

        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(result);
    }

    /// <summary>Does not establish coverage for an index whose physical key shape is unavailable.</summary>
    /// <param name="shape">The independently selected physical index or column shape.</param>
    [Theory]
    [InlineData("prefix")]
    [InlineData("expression")]
    [InlineData("filter")]
    [InlineData("hash")]
    [InlineData("null_order")]
    [InlineData("included")]
    public void UnprovenIndexShapesCannotMintCoverage(
        string shape
    )
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true);
        var key = shape switch
        {
            "prefix" => new ExpectedIndexKeyDefinition(column: "parent_id", prefixLength: 1),
            "expression" => new ExpectedIndexKeyDefinition(expression: "parent_id + 0"),
            "null_order" => new ExpectedIndexKeyDefinition(
                column: "parent_id",
                nullOrder: SafeMigrationIndexNullOrder.First),
            _ => new ExpectedIndexKeyDefinition(column: "parent_id"),
        };

        var index = new ExpectedIndexDefinition(
            "new_cover",
            "child",
            [key],
            filter: shape == "filter"
                ? "parent_id IS NOT NULL"
                : null,
            includedColumns: shape == "included"
                ? ["other"]
                : null,
            method: shape == "hash"
                ? "HASH"
                : null);

        // Act
        Apply(indexes, new EnsureIndexIntent(index));
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(result);
    }

    /// <summary>Uses the captured engine's ordinary B-tree direction rules for support coverage.</summary>
    /// <param name="isMariaDb">Whether the selected physical contract belongs to MariaDB rather than MySQL.</param>
    /// <param name="sortOrder">The authored native index direction.</param>
    /// <param name="expected">The independently specified result for this exact input case.</param>
    [Theory]
    [InlineData(false, SafeMigrationIndexSortOrder.ProviderDefault, true)]
    [InlineData(true, SafeMigrationIndexSortOrder.ProviderDefault, true)]
    [InlineData(false, SafeMigrationIndexSortOrder.Ascending, true)]
    [InlineData(true, SafeMigrationIndexSortOrder.Ascending, true)]
    [InlineData(false, SafeMigrationIndexSortOrder.Descending, false)]
    [InlineData(true, SafeMigrationIndexSortOrder.Descending, true)]
    public void AcceptedOrdinaryBtreeDirectionsRespectTheCapturedEngine(
        bool isMariaDb,
        SafeMigrationIndexSortOrder sortOrder,
        bool expected
    )
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = new MySqlProjectedForeignKeySupportingIndexes(static schema => schema, isMariaDb);

        indexes.Capture(foreignKey, localNameOccupied: true, []);
        var index = new ExpectedIndexDefinition(
            "new_cover",
            "child",
            [new ExpectedIndexKeyDefinition(column: "parent_id", sortOrder: sortOrder)]);

        // Act
        Apply(indexes, new EnsureIndexIntent(index));
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Allows accepted physical primary or unique keys to satisfy the requested ordered support prefix.
    /// </summary>
    /// <param name="kind">The independently selected object or transition family.</param>
    [Theory]
    [InlineData("primary")]
    [InlineData("unique")]
    public void AcceptedPhysicalKeysCanSupplyCoverage(
        string kind
    )
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true);
        SafeMigrationIntent key = kind == "primary"
            ? new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk_child", "child", ["parent_id"]))
            : new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("uq_child", "child", ["parent_id"]));

        // Act
        Apply(indexes, key);
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.True(result);
    }

    /// <summary>Uses the engine's physical PRIMARY identity when retiring a dropped primary key's coverage.</summary>
    [Fact]
    public void PrimaryKeyDropUsesItsNativePhysicalIndexName()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true, "PRIMARY");
        Apply(indexes, new DropPrimaryKeyIntent("authored_pk_alias", "child"));

        // Act
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(result);
    }

    /// <summary>Revokes coverage only for indexes affected by the changed column boundary.</summary>
    [Fact]
    public void AffectedColumnBoundaryRevokesOnlyItsCoverageCertificates()
    {
        // Arrange
        var first = ForeignKey();
        var second = new ExpectedForeignKeyDefinition("fk_other", "child", ["other"], "parent", ["id"]);
        var indexes = Capture(first, occupied: true, "first");
        indexes.Capture(second, localNameOccupied: true, ["second"]);
        Apply(indexes, new RenameColumnIntent("parent_id", "child", "renamed_parent_id"));

        // Act
        var affected = indexes.CanEnsure(first);
        var independent = indexes.CanEnsure(second);

        // Assert
        Assert.False(affected);
        Assert.True(independent);
    }

    /// <summary>
    /// Retains physical coverage and unsafe-index risk across a certified same-layout nullability change.
    /// </summary>
    /// <param name="sourceNullable">Whether the source column admits NULL values.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedSameLayoutNullabilityTransitionRetainsCapturedCoverageAndUnsafeRisk(
        bool sourceNullable
    )
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true, "covering");
        indexes.Capture(foreignKey, localNameOccupied: true, [], ["unsafe_mvi"]);
        Apply(indexes, new DropForeignKeyIntent(foreignKey.Name, "child"));
        var source = new ExpectedColumnDefinition("parent_id", typeof(int), sourceNullable, storeType: "int");
        var target = new ExpectedColumnDefinition("parent_id", typeof(int), !sourceNullable, storeType: "int");
        var operation = new SafeMigrationOperation(
            new AlterColumnIntent("child", target, source),
            SafeMigrationPolicy.RepairIfSafe);

        var analysis = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe,
            false,
            "CapturedSafeRepair");

        // Act
        indexes.ObserveAcceptedOperation(
            operation,
            analysis,
            new SafeMigrationDecision(SafeMigrationAction.Repair, "RepairDifferent"));

        var covering = indexes.CanEnsure(foreignKey);
        var unsafeRisk = indexes.HasUnsafeMultiValuedIndex(foreignKey);
        var unrelatedRisk = indexes.HasUnsafeMultiValuedIndex(ForeignKey("other_id"));

        // Assert
        Assert.True(covering);
        Assert.True(unsafeRisk);
        Assert.False(unrelatedRisk);
    }

    /// <summary>
    /// Revokes unproven coverage without pretending that the underlying unsafe-index risk disappeared.
    /// </summary>
    /// <param name="difference">The independently changed relational facet.</param>
    [Theory]
    [InlineData("Storage")]
    [InlineData("Collation")]
    [InlineData("Annotation")]
    [InlineData("MissingOldDefinition")]
    public void UnprovenNullabilityTransitionRevokesCapturedCoverageWithoutRetiringUnsafeRisk(
        string difference
    )
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true, "covering");
        indexes.Capture(foreignKey, localNameOccupied: true, [], ["unsafe_mvi"]);
        var source = new ExpectedColumnDefinition(
            "parent_id",
            typeof(string),
            isNullable: false,
            storeType: "varchar(32)",
            maxLength: 32,
            collation: new SafeMigrationCollationIdentifier("utf8mb4_bin"));

        var annotatedColumn = new AlterColumnOperation
        {
            Table = "child",
            Name = "parent_id",
            ["MySql:UnreviewedStorageFacet"] = "changed",
        };

        var target = new ExpectedColumnDefinition(
            "parent_id",
            typeof(string),
            isNullable: true,
            storeType: difference == "Storage"
                ? "varchar(64)"
                : "varchar(32)",
            maxLength: difference == "Storage"
                ? 64
                : 32,
            collation: new SafeMigrationCollationIdentifier(
                difference == "Collation"
                    ? "utf8mb4_general_ci"
                    : "utf8mb4_bin"))
        {
            ProviderAnnotations = difference == "Annotation"
                ? SafeMigrationProviderAnnotation.Capture(annotatedColumn)
                : [],
        };

        var operation = new SafeMigrationOperation(
            new AlterColumnIntent(
                "child",
                target,
                difference == "MissingOldDefinition"
                    ? null
                    : source),
            SafeMigrationPolicy.RepairIfSafe);

        var analysis = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe,
            false,
            "CapturedSafeRepair");

        // Act
        indexes.ObserveAcceptedOperation(
            operation,
            analysis,
            new SafeMigrationDecision(SafeMigrationAction.Repair, "RepairDifferent"));

        var covering = indexes.CanEnsure(foreignKey);
        var unsafeRisk = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.False(covering);
        Assert.True(unsafeRisk);
    }

    /// <summary>
    /// Requires the physical-key preservation certificate before carrying coverage through an ensure-column repair.
    /// </summary>
    /// <param name="certified">Whether physical-key preservation is independently certified.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedEnsureColumnRepairNeedsPhysicalKeyPreservationCertificate(
        bool certified
    )
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true, "covering");
        indexes.Capture(foreignKey, localNameOccupied: true, [], ["unsafe_mvi"]);
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent("child", new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true)),
            SafeMigrationPolicy.RepairIfSafe);

        var analysis = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe,
            false,
            "CapturedSafeRepair");

        if (certified)
        {
            analysis = analysis.WithRepairPreservesPhysicalKeys();
        }

        // Act
        indexes.ObserveAcceptedOperation(
            operation,
            analysis,
            new SafeMigrationDecision(SafeMigrationAction.Repair, "RepairDifferent"));

        var covering = indexes.CanEnsure(foreignKey);
        var unsafeRisk = indexes.HasUnsafeMultiValuedIndex(foreignKey);
        var unrelatedRisk = indexes.HasUnsafeMultiValuedIndex(ForeignKey("other_id"));

        // Assert
        Assert.Equal(certified, covering);
        Assert.True(unsafeRisk);
        Assert.Equal(!certified, unrelatedRisk);
    }

    /// <summary>Does not mint new coverage or retire unsafe risk merely because a column repair is certified.</summary>
    [Fact]
    public void CertifiedEnsureColumnRepairCannotMintCoverageOrRetireUnsafeRisk()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true);
        indexes.Capture(foreignKey, localNameOccupied: true, [], ["unsafe_mvi"]);
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent("child", new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true)),
            SafeMigrationPolicy.RepairIfSafe);

        var analysis = new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.Different,
                SafeMigrationRepairCapability.Safe,
                false,
                "CapturedSafeRepair")
            .WithRepairPreservesPhysicalKeys();

        // Act
        indexes.ObserveAcceptedOperation(
            operation,
            analysis,
            new SafeMigrationDecision(SafeMigrationAction.Repair, "RepairDifferent"));

        var covering = indexes.CanEnsure(foreignKey);
        var unsafeRisk = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.False(covering);
        Assert.True(unsafeRisk);
    }

    /// <summary>Does not consume a physical-key certificate for a repair that will not execute.</summary>
    /// <param name="action">The action or decision selected by the test case.</param>
    /// <param name="capturedCoverage">Whether independently captured native index coverage exists.</param>
    [Theory]
    [InlineData(SafeMigrationAction.NoOp, false)]
    [InlineData(SafeMigrationAction.NoOp, true)]
    [InlineData(SafeMigrationAction.RejectDifferent, false)]
    [InlineData(SafeMigrationAction.RejectDifferent, true)]
    [InlineData(SafeMigrationAction.ValidateAtRuntime, false)]
    [InlineData(SafeMigrationAction.ValidateAtRuntime, true)]
    public void NonexecutingEnsureColumnRepairCannotConsumePhysicalKeyCertificate(
        SafeMigrationAction action,
        bool capturedCoverage
    )
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(
            foreignKey,
            occupied: true,
            capturedCoverage
                ? ["covering"]
                : []);

        indexes.Capture(foreignKey, localNameOccupied: true, [], ["unsafe_mvi"]);
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent("child", new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true)),
            SafeMigrationPolicy.RepairIfSafe);

        var analysis = new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.Different,
                SafeMigrationRepairCapability.Safe,
                false,
                "CapturedSafeRepair")
            .WithRepairPreservesPhysicalKeys();

        // Act
        indexes.ObserveAcceptedOperation(operation, analysis, new SafeMigrationDecision(action, "Nonexecuting"));
        var covering = indexes.CanEnsure(foreignKey);
        var unsafeRisk = indexes.HasUnsafeMultiValuedIndex(foreignKey);
        var unrelatedRisk = indexes.HasUnsafeMultiValuedIndex(ForeignKey("other_id"));

        // Assert
        Assert.Equal(capturedCoverage, covering);
        Assert.True(unsafeRisk);
        Assert.False(unrelatedRisk);
    }

    /// <summary>Does not waive automatic-index name collision protection across opaque SQL.</summary>
    [Fact]
    public void OpaqueSqlCannotWaiveANameCollision()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: false, "covering");

        // Act
        indexes.ObserveProviderOperation(new SqlOperation { Sql = "SELECT 1" });
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(result);
    }

    /// <summary>Treats a provider index mutation without a known owner as an opaque coverage boundary.</summary>
    /// <param name="rename">Whether the captured physical index changes its alias before removal.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProviderIndexMutationWithoutAnOwnerIsAnOpaqueBoundary(
        bool rename
    )
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: false, "covering");
        MigrationOperation operation = rename
            ? new RenameIndexOperation
            {
                Name = "covering",
                NewName = "other",
            }
            : new DropIndexOperation { Name = "covering" };

        // Act
        indexes.ObserveProviderOperation(operation);
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(result);
    }

    /// <summary>
    /// Accounts for automatic-index namespace occupancy created by an uncaptured provider foreign key.
    /// </summary>
    [Fact]
    public void UncapturedProviderForeignKeyCanOccupyTheAutomaticIndexNamespace()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: false);
        var operation = new AddForeignKeyOperation
        {
            Name = foreignKey.Name,
            Table = "child",
            Columns = ["old_parent_id"],
            PrincipalTable = "parent",
            PrincipalColumns = ["id"],
        };

        // Act
        indexes.ObserveProviderOperation(operation);
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(result);
    }

    /// <summary>Ends old alias evidence when its table is dropped and a new owner reuses the name.</summary>
    [Fact]
    public void TableDropAndReuseCannotRetainTheOldAliasLifetime()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true, "covering");
        Apply(indexes, new DropTableIntent("child"));

        // Act
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(result);
    }

    /// <summary>Starts a fresh support-index namespace only after an accepted concrete table creation.</summary>
    [Fact]
    public void PlainAcceptedTableCreationStartsAFreshIndexNamespace()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true, "old_cover");
        Apply(indexes, new DropTableIntent("child"));
        var table = new ExpectedTableDefinition(
            "child",
            [new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true)]);

        // Act
        Apply(indexes, new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition));
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.True(result);
    }

    /// <summary>Preserves inline key occupancy and key shape when a fresh table is created.</summary>
    /// <param name="inlineColumn">Whether the prerequisite column is declared inline with its owner.</param>
    /// <param name="expected">The independently specified result for this exact input case.</param>
    [Theory]
    [InlineData("parent_id", true)]
    [InlineData("old_parent_id", false)]
    public void PlainTableCreationRetainsInlineKeyOccupancyAndShape(
        string inlineColumn,
        bool expected
    )
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true);
        var table = new ExpectedTableDefinition(
            "child",
            [
                new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true),
                new ExpectedColumnDefinition("old_parent_id", typeof(int), isNullable: true),
            ],
            uniqueConstraints: [new ExpectedUniqueConstraintDefinition(foreignKey.Name, "child", [inlineColumn])]);

        // Act
        Apply(indexes, new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition));
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>Does not invent a fresh index namespace from table creation deferred to runtime.</summary>
    [Fact]
    public void RuntimeDeferredTableCreationCannotMintAFreshNamespace()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true);
        var table = new ExpectedTableDefinition(
            "child",
            [new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true)]);

        var analysis = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None,
            false,
            "DeferredRows")
        {
            RequiresLiveDataProof = true,
        };

        // Act
        indexes.ObserveAcceptedOperation(
            Operation(new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition)),
            analysis,
            new SafeMigrationDecision(SafeMigrationAction.Apply, "ApplyMissing"));

        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(result);
    }

    /// <summary>
    /// Moves a table's physical index lifetime to the accepted rename destination without leaving an old-owner alias.
    /// </summary>
    [Fact]
    public void TableRenameMovesThePhysicalLifetimeWithoutReusingTheOldOwner()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var renamed = new ExpectedForeignKeyDefinition(
            "fk_child_parent",
            "renamed_child",
            ["parent_id"],
            "parent",
            ["id"]);

        var indexes = Capture(foreignKey, occupied: true, "covering");
        Apply(indexes, new RenameTableIntent("child", "renamed_child"));

        // Act
        var oldOwner = indexes.CanEnsure(foreignKey);
        var newOwner = indexes.CanEnsure(renamed);

        // Assert
        Assert.False(oldOwner);
        Assert.True(newOwner);
    }

    /// <summary>Uses the captured schema and identifier comparison rules for owner identity.</summary>
    [Fact]
    public void SchemaNormalizationAndIdentifierComparersMatchTheNativeDomains()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = new MySqlProjectedForeignKeySupportingIndexes(
            static schema => schema is null or "current_db"
                ? null
                : schema,
            isMariaDb: false);

        indexes.Capture(foreignKey, localNameOccupied: true, ["Covering"]);
        Apply(indexes, new DropIndexIntent("COVERING", "child", "current_db"));
        Apply(indexes, new EnsureIndexIntent(Index("fresh", "PARENT_ID")));
        var caseChangedTable = new ExpectedForeignKeyDefinition(
            "fk_child_parent",
            "Child",
            ["parent_id"],
            "parent",
            ["id"]);

        // Act
        var matchingOwner = indexes.CanEnsure(foreignKey);
        var differentOwner = indexes.CanEnsure(caseChangedTable);

        // Assert
        Assert.True(matchingOwner);
        Assert.False(differentOwner);
    }

    /// <summary>
    /// Does not resurrect an automatic support alias after the index that replaced it is itself dropped.
    /// </summary>
    [Fact]
    public void AutomaticallyCreatedAliasCannotBeResurrectedAfterItsReplacementIsDropped()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true, "automatic_cover");
        Apply(indexes, new EnsureIndexIntent(Index("replacement", "parent_id")));
        Apply(indexes, new DropIndexIntent("replacement", "child"));

        // Act
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(result);
    }

    /// <summary>Keeps a captured primary index outside ordinary automatic-index pruning.</summary>
    [Fact]
    public void CapturedPrimaryIndexSurvivesAutomaticIndexPruning()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true, "PRIMARY");
        Apply(indexes, new EnsureIndexIntent(Index("replacement", "parent_id")));
        Apply(indexes, new DropIndexIntent("replacement", "child"));

        // Act
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.True(result);
    }

    /// <summary>Keeps an explicitly authored physical index outside automatic support-index pruning.</summary>
    [Fact]
    public void ExplicitPhysicalIndexSurvivesAutomaticIndexPruning()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true);
        Apply(indexes, new EnsureIndexIntent(Index("first", "parent_id")));
        Apply(indexes, new EnsureIndexIntent(Index("second", "parent_id")));
        Apply(indexes, new DropIndexIntent("second", "child"));

        // Act
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.True(result);
    }

    /// <summary>Retains automatic-index name occupancy when only the foreign key is dropped.</summary>
    [Fact]
    public void DroppingTheForeignKeyDoesNotDropItsAutomaticIndexOccupancy()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true);
        Apply(indexes, new DropForeignKeyIntent(foreignKey.Name, foreignKey.Table));

        // Act
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(result);
    }

    /// <summary>Tracks an unsafe index independently of otherwise sufficient ordinary support coverage.</summary>
    [Fact]
    public void CapturedUnsafeIndexIsIndependentOfOrdinaryCoverage()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = CaptureUnsafe(foreignKey, "unsafe_mvi");
        Apply(indexes, new EnsureIndexIntent(Index("ordinary_cover", "parent_id")));

        // Act
        var result = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.True(result);
    }

    /// <summary>Retires a captured unsafe-index lifetime only after its exact accepted physical drop.</summary>
    [Fact]
    public void PreciseAcceptedDropRemovesTheCapturedUnsafeLifetime()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = CaptureUnsafe(foreignKey, "unsafe_mvi");
        Apply(indexes, new DropIndexIntent("unsafe_mvi", "child"));

        // Act
        var result = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.False(result);
    }

    /// <summary>Does not clear unsafe-index risk for a rejected or otherwise nonexecuting drop.</summary>
    /// <param name="action">The action or decision selected by the test case.</param>
    [Theory]
    [InlineData(SafeMigrationAction.NoOp)]
    [InlineData(SafeMigrationAction.RejectDifferent)]
    [InlineData(SafeMigrationAction.ValidateAtRuntime)]
    public void UnacceptedDropCannotReleaseAnUnsafeIndex(
        SafeMigrationAction action
    )
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = CaptureUnsafe(foreignKey, "unsafe_mvi");
        var operation = Operation(new DropIndexIntent("unsafe_mvi", "child"));

        // Act
        indexes.ObserveAcceptedOperation(operation, Analysis(), new SafeMigrationDecision(action, "NotApplied"));
        var result = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.True(result);
    }

    /// <summary>Does not use a future drop to clear risk before that operation's ordinal.</summary>
    [Fact]
    public void FutureDropDoesNotReleaseAnEarlierUnsafeIndex()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = CaptureUnsafe(foreignKey, "unsafe_mvi");
        var future = Operation(new DropIndexIntent("unsafe_mvi", "child"));

        // Act
        var result = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.True(result);
        Assert.IsType<DropIndexIntent>(future.Intent);
    }

    /// <summary>Does not clear unsafe-index risk by removing a different physical alias.</summary>
    [Fact]
    public void WrongPhysicalAliasDoesNotReleaseUnsafeIndexRisk()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = CaptureUnsafe(foreignKey, "unsafe_mvi");
        Apply(indexes, new DropIndexIntent("ordinary_cover", "child"));

        // Act
        var result = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// Keeps a reused unsafe alias conservative instead of treating name reuse as fresh safety evidence.
    /// </summary>
    [Fact]
    public void ReoccupiedUnsafeAliasRemainsConservativelyUnsafe()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = CaptureUnsafe(foreignKey, "unsafe_mvi");
        Apply(indexes, new DropIndexIntent("unsafe_mvi", "child"));
        Apply(indexes, new EnsureIndexIntent(Index("unsafe_mvi", "parent_id")));

        // Act
        var result = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.True(result);
    }

    /// <summary>Requires the currently renamed unsafe-index identity when proving its removal.</summary>
    /// <param name="dropAlias">The physical alias selected for removal.</param>
    /// <param name="expected">The independently specified result for this exact input case.</param>
    [Theory]
    [InlineData("unsafe_mvi", true)]
    [InlineData("renamed_mvi", false)]
    public void UnsafeRenameRequiresDroppingItsCurrentPhysicalAlias(
        string dropAlias,
        bool expected
    )
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = CaptureUnsafe(foreignKey, "unsafe_mvi");
        Apply(indexes, new RenameIndexIntent("unsafe_mvi", "child", "renamed_mvi"));
        Apply(indexes, new DropIndexIntent(dropAlias, "child"));

        // Act
        var result = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>Does not clear an independent unsafe alias when a different unsafe index is removed.</summary>
    [Fact]
    public void DroppingOneUnsafeAliasCannotReleaseAnother()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = CaptureUnsafe(foreignKey, "first_mvi", "second_mvi");
        Apply(indexes, new DropIndexIntent("first_mvi", "child"));

        // Act
        var result = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.True(result);
    }

    /// <summary>Keeps unsafe-index lifetimes separate for different owner tables.</summary>
    [Fact]
    public void UnsafeIndexDropOnAnotherOwnerCannotReleaseTheCapturedOwner()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = CaptureUnsafe(foreignKey, "unsafe_mvi");
        Apply(indexes, new DropIndexIntent("unsafe_mvi", "other_child"));

        // Act
        var result = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.True(result);
    }

    /// <summary>Does not assume an exact earlier drop proves an empty unsafe-index set across opaque SQL.</summary>
    [Fact]
    public void OpaqueBoundaryAfterExactDropCannotProveNoUnsafeIndex()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = CaptureUnsafe(foreignKey, "unsafe_mvi");
        Apply(indexes, new DropIndexIntent("unsafe_mvi", "child"));
        indexes.ObserveProviderOperation(new SqlOperation { Sql = "SELECT 1" });

        // Act
        var result = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.True(result);
    }

    /// <summary>Does not clear unsafe-index risk through an unknown provider mutation.</summary>
    [Fact]
    public void UnknownProviderIndexMutationCannotReleaseUnsafeIndexRisk()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = CaptureUnsafe(foreignKey, "unsafe_mvi");
        Apply(indexes, new DropIndexIntent("unsafe_mvi", "child"));
        indexes.ObserveProviderOperation(
            new CreateIndexOperation
            {
                Name = "new_index",
                Table = "child",
                Columns = ["parent_id"],
            });

        // Act
        var result = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.True(result);
    }

    /// <summary>Clears old unsafe aliases when an accepted fresh table starts a new physical lifetime.</summary>
    [Fact]
    public void FreshAcceptedTableLifetimeClearsOldUnsafeAliases()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = CaptureUnsafe(foreignKey, "unsafe_mvi");
        indexes.ObserveProviderOperation(new SqlOperation { Sql = "SELECT 1" });
        Apply(indexes, new DropTableIntent("child"));
        var table = new ExpectedTableDefinition(
            "child",
            [new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true)]);

        // Act
        Apply(indexes, new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition));
        var result = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.False(result);
    }

    /// <summary>Does not clear captured unsafe aliases when ensuring a table is only a matching no-op.</summary>
    [Fact]
    public void MatchingTableNoOpCannotClearCapturedUnsafeAliases()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = CaptureUnsafe(foreignKey, "unsafe_mvi");
        var table = new ExpectedTableDefinition(
            "child",
            [new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true)]);

        // Act
        indexes.ObserveAcceptedOperation(
            Operation(new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition)),
            Analysis(SafeMigrationObservedState.Matching),
            new SafeMigrationDecision(SafeMigrationAction.NoOp, "NoOpMatching"));

        var result = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.True(result);
    }

    /// <summary>Matches unsafe-index risk by its captured ordered ordinary-column prefix.</summary>
    [Fact]
    public void UnsafeIndexRiskUsesItsCapturedOrdinaryColumnPrefix()
    {
        // Arrange
        var foreignKey = ForeignKey("parent_id", "tenant_id");
        var unrelated = ForeignKey("other_id");
        var indexes = CaptureUnsafe(foreignKey, "unsafe_mvi");

        // Act
        var matching = indexes.HasUnsafeMultiValuedIndex(foreignKey);
        var different = indexes.HasUnsafeMultiValuedIndex(unrelated);

        // Assert
        Assert.True(matching);
        Assert.False(different);
    }

    /// <summary>
    /// Does not certify a different requested prefix after the unsafe index's member shape becomes unknown.
    /// </summary>
    [Fact]
    public void ChangedUnsafeIndexMembersCannotCertifyADifferentRequestedPrefix()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var renamed = ForeignKey("renamed_parent_id");
        var indexes = CaptureUnsafe(foreignKey, "unsafe_mvi");
        Apply(indexes, new RenameColumnIntent("parent_id", "child", "renamed_parent_id"));

        // Act
        var result = indexes.HasUnsafeMultiValuedIndex(renamed);

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// Allows an exact accepted physical drop to retire an unsafe index even after member shape is unknown.
    /// </summary>
    [Fact]
    public void PreciseDropStillRemovesAnUnsafeIndexAfterMemberShapeBecameUnknown()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var renamed = ForeignKey("renamed_parent_id");
        var indexes = CaptureUnsafe(foreignKey, "unsafe_mvi");
        Apply(indexes, new RenameColumnIntent("parent_id", "child", "renamed_parent_id"));
        Apply(indexes, new DropIndexIntent("unsafe_mvi", "child"));

        // Act
        var result = indexes.HasUnsafeMultiValuedIndex(renamed);

        // Assert
        Assert.False(result);
    }

    /// <summary>Ends unsafe-index lifetime at an exact accepted table drop.</summary>
    [Fact]
    public void PreciseAcceptedTableDropEndsItsUnsafeIndexLifetime()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = CaptureUnsafe(foreignKey, "unsafe_mvi");
        Apply(indexes, new DropTableIntent("child"));

        // Act
        var result = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.False(result);
    }

    /// <summary>Keeps differently cased unsafe owners separate under an ordinal owner profile.</summary>
    [Fact]
    public void IdentityOwnerProfileKeepsDifferentlyCasedUnsafeOwnersDistinct()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = CaptureUnsafe(foreignKey, "unsafe_mvi");
        Apply(indexes, new DropIndexIntent("unsafe_mvi", "Child"));

        // Act
        var result = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// Shares mixed-case accepted lifetimes only under the supplied verified case-insensitive owner profile.
    /// </summary>
    [Fact]
    public void SuppliedCaseInsensitiveOwnerProfileSharesAcceptedMixedCaseLifetimes()
    {
        // Arrange
        var foreignKey = new ExpectedForeignKeyDefinition(
            "fk_child_parent",
            "Child",
            ["parent_id"],
            "parent",
            ["id"]);

        var indexes = new MySqlProjectedForeignKeySupportingIndexes(
            static schema => schema,
            isMariaDb: false,
            static table => table.ToLowerInvariant());

        indexes.Capture(foreignKey, localNameOccupied: true, ["old_cover"], ["unsafe_mvi"]);
        Apply(indexes, new DropForeignKeyIntent(foreignKey.Name, "CHILD"));
        Apply(indexes, new DropIndexIntent("unsafe_mvi", "cHiLd"));
        Apply(indexes, new DropIndexIntent("old_cover", "CHILD"));

        // Act
        var beforeCover = indexes.CanEnsure(foreignKey);
        var remainingRisk = indexes.HasUnsafeMultiValuedIndex(foreignKey);
        Apply(
            indexes,
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "new_cover",
                    "CHILD",
                    [new ExpectedIndexKeyDefinition(column: "parent_id")])));

        var afterCover = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(beforeCover);
        Assert.False(remainingRisk);
        Assert.True(afterCover);
    }

    private static ExpectedForeignKeyDefinition ForeignKey(
        params string[] columns
    ) => new(
        "fk_child_parent",
        "child",
        columns.Length == 0
            ? ["parent_id"]
            : columns,
        "parent",
        columns.Length == 0
            ? ["id"]
            : columns);

    private static ExpectedIndexDefinition Index(
        string name,
        params string[] columns
    ) => new(name, "child", columns.Select(static column => new ExpectedIndexKeyDefinition(column: column)));

    private static MySqlProjectedForeignKeySupportingIndexes Capture(
        ExpectedForeignKeyDefinition foreignKey,
        bool occupied,
        params string[] aliases
    )
    {
        var indexes = new MySqlProjectedForeignKeySupportingIndexes(static schema => schema, isMariaDb: false);

        indexes.Capture(foreignKey, occupied, aliases);

        return indexes;
    }

    private static SafeMigrationOperation Operation(
        SafeMigrationIntent intent
    ) => new(intent, SafeMigrationPolicy.ThrowIfDifferent);

    private static MySqlProjectedForeignKeySupportingIndexes CaptureUnsafe(
        ExpectedForeignKeyDefinition foreignKey,
        params string[] aliases
    )
    {
        var indexes = Capture(foreignKey, occupied: false);

        indexes.Capture(foreignKey, localNameOccupied: false, [], unsafeIndexAliases: aliases);

        return indexes;
    }

    private static SafeMigrationProviderAnalysis Analysis(
        SafeMigrationObservedState state = SafeMigrationObservedState.Missing
    ) => new(state, SafeMigrationRepairCapability.None, false, "Captured");

    private static void Apply(
        MySqlProjectedForeignKeySupportingIndexes indexes,
        SafeMigrationIntent intent
    ) => indexes.ObserveAcceptedOperation(
        Operation(intent),
        Analysis(),
        new SafeMigrationDecision(SafeMigrationAction.Apply, "ApplyMissing"));
}
