namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlProjectedForeignKeySupportingIndexesTests
{
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SurvivingNativeOrProjectedCoverageIsNotAKnownCollision(
        bool projected
    )
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true, projected ? [] : ["covering"]);
        if (projected)
        {
            Apply(indexes, new EnsureIndexIntent(Index("covering", "parent_id")));
        }

        // Act
        var result = indexes.HasKnownNameCollision(foreignKey);

        // Assert
        Assert.False(result);
    }

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
                indexes.ObserveProviderOperation(new CreateIndexOperation
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
                    foreignKey.Name, "Child", ["parent_id"], "parent", ["id"]);

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
        indexes.ObserveAcceptedOperation(operation, Analysis(state), new(action, code));
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void FutureIndexDoesNotParticipateInAnEarlierForeignKeyProof()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true);
        var future = Operation(new EnsureIndexIntent(Index("future_cover", "parent_id")));

        // Act
        var before = indexes.CanEnsure(foreignKey);
        indexes.ObserveAcceptedOperation(future, Analysis(), new(SafeMigrationAction.Apply, "ApplyMissing"));
        var after = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(before);
        Assert.True(after);
    }

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
            new(SafeMigrationAction.Repair, "RepairDifferent"));

        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(result);
    }

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
                column: "parent_id", nullOrder: SafeMigrationIndexNullOrder.First),
            _ => new ExpectedIndexKeyDefinition(column: "parent_id"),
        };

        var index = new ExpectedIndexDefinition(
            "new_cover",
            "child",
            [key],
            filter: shape == "filter" ? "parent_id IS NOT NULL" : null,
            includedColumns: shape == "included" ? ["other"] : null,
            method: shape == "hash" ? "HASH" : null);

        // Act
        Apply(indexes, new EnsureIndexIntent(index));
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(result);
    }

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
            "new_cover", "child", [new ExpectedIndexKeyDefinition(column: "parent_id", sortOrder: sortOrder)]);

        // Act
        Apply(indexes, new EnsureIndexIntent(index));
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.Equal(expected, result);
    }

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
            ? new EnsurePrimaryKeyIntent(new("pk_child", "child", ["parent_id"]))
            : new EnsureUniqueConstraintIntent(new("uq_child", "child", ["parent_id"]));

        // Act
        Apply(indexes, key);
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.True(result);
    }

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
            new AlterColumnIntent("child", target, source), SafeMigrationPolicy.RepairIfSafe);

        var analysis = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different, SafeMigrationRepairCapability.Safe, false, "CapturedSafeRepair");

        // Act
        indexes.ObserveAcceptedOperation(operation, analysis, new(SafeMigrationAction.Repair, "RepairDifferent"));
        var covering = indexes.CanEnsure(foreignKey);
        var unsafeRisk = indexes.HasUnsafeMultiValuedIndex(foreignKey);
        var unrelatedRisk = indexes.HasUnsafeMultiValuedIndex(ForeignKey("other_id"));

        // Assert
        Assert.True(covering);
        Assert.True(unsafeRisk);
        Assert.False(unrelatedRisk);
    }

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
            "parent_id", typeof(string), isNullable: false, storeType: "varchar(32)", maxLength: 32,
            collation: new SafeMigrationCollationIdentifier("utf8mb4_bin"));

        var annotatedColumn = new AlterColumnOperation { Table = "child", Name = "parent_id" };
        annotatedColumn["MySql:UnreviewedStorageFacet"] = "changed";
        var target = new ExpectedColumnDefinition(
            "parent_id", typeof(string), isNullable: true,
            storeType: difference == "Storage" ? "varchar(64)" : "varchar(32)",
            maxLength: difference == "Storage" ? 64 : 32,
            collation: new SafeMigrationCollationIdentifier(
                difference == "Collation" ? "utf8mb4_general_ci" : "utf8mb4_bin"))
        {
            ProviderAnnotations = difference == "Annotation"
                ? SafeMigrationProviderAnnotation.Capture(annotatedColumn)
                : [],
        };

        var operation = new SafeMigrationOperation(
            new AlterColumnIntent("child", target, difference == "MissingOldDefinition" ? null : source),
            SafeMigrationPolicy.RepairIfSafe);

        var analysis = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different, SafeMigrationRepairCapability.Safe, false, "CapturedSafeRepair");

        // Act
        indexes.ObserveAcceptedOperation(operation, analysis, new(SafeMigrationAction.Repair, "RepairDifferent"));
        var covering = indexes.CanEnsure(foreignKey);
        var unsafeRisk = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.False(covering);
        Assert.True(unsafeRisk);
    }

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
            SafeMigrationObservedState.Different, SafeMigrationRepairCapability.Safe, false, "CapturedSafeRepair");

        if (certified)
        {
            analysis = analysis.WithRepairPreservesPhysicalKeys();
        }

        // Act
        indexes.ObserveAcceptedOperation(operation, analysis, new(SafeMigrationAction.Repair, "RepairDifferent"));
        var covering = indexes.CanEnsure(foreignKey);
        var unsafeRisk = indexes.HasUnsafeMultiValuedIndex(foreignKey);
        var unrelatedRisk = indexes.HasUnsafeMultiValuedIndex(ForeignKey("other_id"));

        // Assert
        Assert.Equal(certified, covering);
        Assert.True(unsafeRisk);
        Assert.Equal(!certified, unrelatedRisk);
    }

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
            SafeMigrationObservedState.Different, SafeMigrationRepairCapability.Safe, false, "CapturedSafeRepair")
            .WithRepairPreservesPhysicalKeys();

        // Act
        indexes.ObserveAcceptedOperation(operation, analysis, new(SafeMigrationAction.Repair, "RepairDifferent"));
        var covering = indexes.CanEnsure(foreignKey);
        var unsafeRisk = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.False(covering);
        Assert.True(unsafeRisk);
    }

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
        var indexes = Capture(foreignKey, occupied: true, capturedCoverage ? ["covering"] : []);
        indexes.Capture(foreignKey, localNameOccupied: true, [], ["unsafe_mvi"]);
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent("child", new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true)),
            SafeMigrationPolicy.RepairIfSafe);

        var analysis = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different, SafeMigrationRepairCapability.Safe, false, "CapturedSafeRepair")
            .WithRepairPreservesPhysicalKeys();

        // Act
        indexes.ObserveAcceptedOperation(operation, analysis, new(action, "Nonexecuting"));
        var covering = indexes.CanEnsure(foreignKey);
        var unsafeRisk = indexes.HasUnsafeMultiValuedIndex(foreignKey);
        var unrelatedRisk = indexes.HasUnsafeMultiValuedIndex(ForeignKey("other_id"));

        // Assert
        Assert.Equal(capturedCoverage, covering);
        Assert.True(unsafeRisk);
        Assert.False(unrelatedRisk);
    }

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
            ? new RenameIndexOperation { Name = "covering", NewName = "other" }
            : new DropIndexOperation { Name = "covering" };

        // Act
        indexes.ObserveProviderOperation(operation);
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(result);
    }

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

    [Fact]
    public void PlainAcceptedTableCreationStartsAFreshIndexNamespace()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true, "old_cover");
        Apply(indexes, new DropTableIntent("child"));
        var table = new ExpectedTableDefinition(
            "child", [new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true)]);

        // Act
        Apply(indexes, new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition));
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.True(result);
    }

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
            [new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true),
                new ExpectedColumnDefinition("old_parent_id", typeof(int), isNullable: true)],
            uniqueConstraints: [new ExpectedUniqueConstraintDefinition(foreignKey.Name, "child", [inlineColumn])]);

        // Act
        Apply(indexes, new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition));
        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void RuntimeDeferredTableCreationCannotMintAFreshNamespace()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = Capture(foreignKey, occupied: true);
        var table = new ExpectedTableDefinition(
            "child", [new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true)]);

        var analysis = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Missing, SafeMigrationRepairCapability.None, false, "DeferredRows")
        {
            RequiresLiveDataProof = true,
        };

        // Act
        indexes.ObserveAcceptedOperation(
            Operation(new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition)),
            analysis,
            new(SafeMigrationAction.Apply, "ApplyMissing"));

        var result = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void TableRenameMovesThePhysicalLifetimeWithoutReusingTheOldOwner()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var renamed = new ExpectedForeignKeyDefinition(
            "fk_child_parent", "renamed_child", ["parent_id"], "parent", ["id"]);
        var indexes = Capture(foreignKey, occupied: true, "covering");
        Apply(indexes, new RenameTableIntent("child", "renamed_child"));

        // Act
        var oldOwner = indexes.CanEnsure(foreignKey);
        var newOwner = indexes.CanEnsure(renamed);

        // Assert
        Assert.False(oldOwner);
        Assert.True(newOwner);
    }

    [Fact]
    public void SchemaNormalizationAndIdentifierComparersMatchTheNativeDomains()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = new MySqlProjectedForeignKeySupportingIndexes(
            static schema => schema is null or "current_db" ? null : schema,
            isMariaDb: false);

        indexes.Capture(foreignKey, localNameOccupied: true, ["Covering"]);
        Apply(indexes, new DropIndexIntent("COVERING", "child", "current_db"));
        Apply(indexes, new EnsureIndexIntent(Index("fresh", "PARENT_ID")));
        var caseChangedTable = new ExpectedForeignKeyDefinition(
            "fk_child_parent", "Child", ["parent_id"], "parent", ["id"]);

        // Act
        var matchingOwner = indexes.CanEnsure(foreignKey);
        var differentOwner = indexes.CanEnsure(caseChangedTable);

        // Assert
        Assert.True(matchingOwner);
        Assert.False(differentOwner);
    }

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
        indexes.ObserveAcceptedOperation(operation, Analysis(), new(action, "NotApplied"));
        var result = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.True(result);
    }

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

    [Fact]
    public void UnknownProviderIndexMutationCannotReleaseUnsafeIndexRisk()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = CaptureUnsafe(foreignKey, "unsafe_mvi");
        Apply(indexes, new DropIndexIntent("unsafe_mvi", "child"));
        indexes.ObserveProviderOperation(new CreateIndexOperation
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

    [Fact]
    public void FreshAcceptedTableLifetimeClearsOldUnsafeAliases()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = CaptureUnsafe(foreignKey, "unsafe_mvi");
        indexes.ObserveProviderOperation(new SqlOperation { Sql = "SELECT 1" });
        Apply(indexes, new DropTableIntent("child"));
        var table = new ExpectedTableDefinition(
            "child", [new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true)]);

        // Act
        Apply(indexes, new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition));
        var result = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void MatchingTableNoOpCannotClearCapturedUnsafeAliases()
    {
        // Arrange
        var foreignKey = ForeignKey();
        var indexes = CaptureUnsafe(foreignKey, "unsafe_mvi");
        var table = new ExpectedTableDefinition(
            "child", [new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true)]);

        // Act
        indexes.ObserveAcceptedOperation(
            Operation(new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition)),
            Analysis(SafeMigrationObservedState.Matching),
            new(SafeMigrationAction.NoOp, "NoOpMatching"));

        var result = indexes.HasUnsafeMultiValuedIndex(foreignKey);

        // Assert
        Assert.True(result);
    }

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

    [Fact]
    public void SuppliedCaseInsensitiveOwnerProfileSharesAcceptedMixedCaseLifetimes()
    {
        // Arrange
        var foreignKey = new ExpectedForeignKeyDefinition(
            "fk_child_parent", "Child", ["parent_id"], "parent", ["id"]);

        var indexes = new MySqlProjectedForeignKeySupportingIndexes(
            static schema => schema, isMariaDb: false, static table => table.ToLowerInvariant());

        indexes.Capture(foreignKey, localNameOccupied: true, ["old_cover"], ["unsafe_mvi"]);
        Apply(indexes, new DropForeignKeyIntent(foreignKey.Name, "CHILD"));
        Apply(indexes, new DropIndexIntent("unsafe_mvi", "cHiLd"));
        Apply(indexes, new DropIndexIntent("old_cover", "CHILD"));

        // Act
        var beforeCover = indexes.CanEnsure(foreignKey);
        var remainingRisk = indexes.HasUnsafeMultiValuedIndex(foreignKey);
        Apply(indexes, new EnsureIndexIntent(new ExpectedIndexDefinition(
            "new_cover", "CHILD", [new ExpectedIndexKeyDefinition(column: "parent_id")])));

        var afterCover = indexes.CanEnsure(foreignKey);

        // Assert
        Assert.False(beforeCover);
        Assert.False(remainingRisk);
        Assert.True(afterCover);
    }

    private static ExpectedForeignKeyDefinition ForeignKey(
        params string[] columns
    ) => new("fk_child_parent", "child", columns.Length == 0 ? ["parent_id"] : columns,
        "parent", columns.Length == 0 ? ["id"] : columns);

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
        Operation(intent), Analysis(), new(SafeMigrationAction.Apply, "ApplyMissing"));
}
