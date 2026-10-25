namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>A certified sibling removal does not erase an independent nullable-only native proof.</summary>
    [Fact]
    public void CertifiedSiblingColumnDropRetainsIndependentNullabilityProof()
    {
        // Arrange
        var provider = new IndependentColumnDropProbe();
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider);
        ObserveAccepted(projection, new DropPrimaryKeyIntent("pk_items", "items"),
            SafeMigrationObservedState.Matching);

        projection.CurrentOperationOrdinal = 1;
        ObserveAccepted(projection, new DropColumnIntent("legacy", "items"),
            SafeMigrationObservedState.Matching);

        var source = new ExpectedColumnDefinition("reference", typeof(int), true, "int");
        var operation = new SafeMigrationOperation(new AlterColumnIntent("items", Column("reference"), source),
            SafeMigrationPolicy.RepairIfSafe);

        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe, false, "independent_native_nullability");

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.Same(live, result);
        Assert.True(provider.ColumnQueries > 0);
        Assert.True(((ISafeMigrationProjectedTableSource)projection).TryGetProjectedTableState(
            "items", null, out var state));
        Assert.True(state.HasUnknownStructure);
        Assert.True(state.PrimaryKeyWasDropped);
    }

    /// <summary>Certified DROP uncertainty reaches only an explicitly opted-in provider key proof.</summary>
    [Fact]
    public void CertifiedSiblingColumnDropConsultsReplacementKeyProofWithoutInventingMissing()
    {
        // Arrange
        var provider = new IndependentColumnDropProbe();
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider,
            projectedKeyAnalyzer: provider);
        ObserveAccepted(projection, new DropPrimaryKeyIntent("pk_items", "items"),
            SafeMigrationObservedState.Matching);
        projection.CurrentOperationOrdinal = 1;
        ObserveAccepted(projection, new DropColumnIntent("legacy", "items"),
            SafeMigrationObservedState.Matching);
        var operation = PrimaryKeyReplacementOperation();
        var live = Live(SafeMigrationObservedState.Different);

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.Same(provider.KeyResult, result);
        Assert.Equal(1, provider.KeyQueries);
        Assert.Same(live, provider.KeyLive);
        Assert.NotNull(provider.KeyProjected);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, provider.KeyProjected.ObservedState);
        Assert.False(provider.KeyProjected.PostconditionSatisfied);
    }

    /// <summary>Only both independent source and key-provider opt-ins admit a key callback.</summary>
    /// <param name="certifiesDrop">Whether the provider certifies surviving column sources after DROP.</param>
    /// <param name="validatesKey">Whether the provider accepts the special unproven key callback.</param>
    /// <param name="dropPrimary">Whether the accepted stream removed the original primary key.</param>
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void IndependentColumnDropCannotBypassMissingProviderOrPrimaryKeyEvidence(
        bool certifiesDrop,
        bool validatesKey,
        bool dropPrimary
    )
    {
        // Arrange
        var provider = new IndependentColumnDropProbe(certifiesDrop, validatesKey);
        var projection = IndependentDropProjection(provider, dropPrimary);

        // Act
        var result = projection.Project(PrimaryKeyReplacementOperation(), Live(SafeMigrationObservedState.Different));

        // Assert
        Assert.Equal("projected_structure_state_unknown", result.Code);
        Assert.Equal(0, provider.KeyQueries);
    }

    /// <summary>Opaque, ordinary-DDL and data effects cannot revive an original removal certificate.</summary>
    /// <param name="boundary">The intervening operation that expires the narrowly certified lineage.</param>
    [Theory]
    [InlineData("sql")]
    [InlineData("insert")]
    [InlineData("rename")]
    [InlineData("provider-add")]
    public void IndependentColumnDropLineageExpiresAtUncertifiedBoundaries(string boundary)
    {
        // Arrange
        var provider = new IndependentColumnDropProbe();
        var projection = IndependentDropProjection(provider);
        MigrationOperation mutation = boundary switch
        {
            "sql" => new SqlOperation { Sql = "SELECT 1;" },
            "insert" => new InsertDataOperation
            {
                Table = "items", Columns = ["reference"], Values = new object[,] { { 1 } },
            },
            "rename" => new RenameColumnOperation { Table = "items", Name = "legacy", NewName = "renamed" },
            _ => new AddColumnOperation { Table = "items", Name = "other", ClrType = typeof(int) },
        };

        // Act
        projection.ObserveProviderPostcondition(mutation);
        var result = projection.Project(PrimaryKeyReplacementOperation(), Live(SafeMigrationObservedState.Different));

        // Assert
        Assert.Equal("projected_structure_state_unknown", result.Code);
        Assert.Equal(0, provider.KeyQueries);
    }

    /// <summary>The real DROP position survives a new member but never certifies that member's future values.</summary>
    [Fact]
    public void IndependentColumnDropOriginSurvivesNewColumnWithoutMintingRowEvidence()
    {
        // Arrange
        var provider = new IndependentColumnDropProbe();
        var projection = IndependentDropProjection(provider);
        projection.CurrentOperationOrdinal = 7;
        var add = new SafeMigrationOperation(new EnsureColumnIntent("items", Column("new_id")),
            SafeMigrationPolicy.ThrowIfDifferent);
        var live = Live(SafeMigrationObservedState.Missing);
        var decision = SafeMigrationDecisionPlanner.Plan(add.Intent.Kind, live.ObservedState,
            add.Policy, live.RepairCapability);
        var columns = (ISafeMigrationProjectedColumnSource)projection;

        // Act
        projection.Observe(add, live, live, decision);
        var hasTable = ((ISafeMigrationProjectedTableSource)projection).TryGetProjectedTableState(
            "items", null, out var state);

        // Assert
        Assert.True(hasTable);
        Assert.Equal(1, state.ColumnDropOriginOrdinal);
        Assert.True(state.HasUnknownStructure);
        Assert.True(state.PrimaryKeyWasDropped);
        Assert.False(columns.PreservesLiveColumnValues("items", null, "new_id"));
        Assert.True(columns.PreservesLiveColumnValues("items", null, "reference"));
        Assert.False(columns.PreservesLiveColumnValues("items", null, "legacy"));
        Assert.Equal(7, columns.GetColumnValueMutationOrigin("items", null, "new_id"));
        Assert.Null(columns.GetColumnValueMutationOrigin("items", "archive", "new_id"));
    }

    /// <summary>Matching a first observed existing member retains original row proof without adding values.</summary>
    [Fact]
    public void IndependentColumnDropMatchingEnsureRetainsExistingValueProof()
    {
        // Arrange
        var provider = new IndependentColumnDropProbe();
        var projection = IndependentDropProjection(provider);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", Column("id")),
            SafeMigrationPolicy.ThrowIfDifferent);
        var live = Live(SafeMigrationObservedState.Matching);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, live.ObservedState,
            operation.Policy, live.RepairCapability);

        // Act
        projection.Observe(operation, live, live, decision);
        var columns = (ISafeMigrationProjectedColumnSource)projection;

        // Assert
        Assert.True(columns.PreservesLiveColumnValues("items", null, "id"));
        Assert.Null(columns.GetColumnValueMutationOrigin("items", null, "id"));
    }

    /// <summary>A certified non-data nullable-only repair preserves values after first NoOp capture.</summary>
    [Fact]
    public void IndependentColumnDropNullabilityRepairRetainsOriginalValues()
    {
        // Arrange
        var provider = new IndependentColumnDropProbe();
        var projection = IndependentDropProjection(provider);
        var source = NullableColumn("reference");
        var observed = new SafeMigrationOperation(new EnsureColumnIntent("items", source),
            SafeMigrationPolicy.ThrowIfDifferent);
        var matching = Live(SafeMigrationObservedState.Matching);
        projection.Observe(observed, matching, matching, SafeMigrationDecisionPlanner.Plan(
            observed.Intent.Kind, matching.ObservedState, observed.Policy, matching.RepairCapability));
        var operation = new SafeMigrationOperation(new AlterColumnIntent("items", Column("reference"), source),
            SafeMigrationPolicy.RepairIfSafe);
        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe, false, "independent_native_nullability");
        var result = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, result.ObservedState,
            operation.Policy, result.RepairCapability);

        // Act
        projection.Observe(operation, live, result, decision);
        var columns = (ISafeMigrationProjectedColumnSource)projection;

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, decision.Action);
        Assert.True(columns.PreservesLiveColumnValues("items", null, "reference"));
        Assert.Null(columns.GetColumnValueMutationOrigin("items", null, "reference"));
    }

    /// <summary>Known native data or capability failures remain independent of unrelated member removal.</summary>
    /// <param name="state">The independently captured native rejection.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.DataBlocked)]
    [InlineData(SafeMigrationObservedState.Unsupported)]
    public void IndependentColumnDropRetainsIndependentColumnRejection(SafeMigrationObservedState state)
    {
        // Arrange
        var provider = new IndependentColumnDropProbe();
        var projection = IndependentDropProjection(provider);
        var operation = new SafeMigrationOperation(new AlterColumnIntent("items", Column("reference"),
            NullableColumn("reference")), SafeMigrationPolicy.RepairIfSafe);
        var live = Live(state);

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.Same(live, result);
        Assert.Equal(0, provider.ColumnQueries);
    }

    /// <summary>Source reuse after DROP cannot widen a separately captured physical column contract.</summary>
    [Fact]
    public void IndependentColumnDropCannotReuseStaleWideningProof()
    {
        // Arrange
        var provider = new IndependentColumnDropProbe();
        var projection = IndependentDropProjection(provider);
        var source = NullableColumn("reference");
        var target = new ExpectedColumnDefinition("reference", typeof(int), false, "bigint");
        var operation = new SafeMigrationOperation(new AlterColumnIntent("items", target, source),
            SafeMigrationPolicy.RepairIfSafe);
        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe, false, "stale_widening");

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.Equal("projected_structure_state_unknown", result.Code);
        Assert.True(provider.ColumnQueries > 0);
        Assert.Equal(SafeMigrationRepairCapability.None, result.RepairCapability);
    }

    /// <summary>Index and UNIQUE requests reach the explicit unproven provider boundary after certified DROP.</summary>
    /// <param name="unique">Whether the operation creates a UNIQUE constraint rather than an ordinary index.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndependentColumnDropKeyFamilyDoesNotReuseHistoricalMatching(bool unique)
    {
        // Arrange
        var provider = new IndependentColumnDropProbe();
        var projection = IndependentDropProjection(provider);
        SafeMigrationIntent intent = unique
            ? new EnsureUniqueConstraintIntent(new ExpectedUniqueConstraintDefinition("uq_reference", "items",
                ["reference"]))
            : new EnsureIndexIntent(new ExpectedIndexDefinition("ix_reference", "items",
                [new ExpectedIndexKeyDefinition("reference")]));
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var live = Live(SafeMigrationObservedState.Matching);

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.Same(provider.KeyResult, result);
        Assert.Equal(1, provider.KeyQueries);
        Assert.Same(live, provider.KeyLive);
        Assert.Equal("projected_key_after_column_drop_unproven", provider.KeyProjected?.Code);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, provider.KeyProjected?.ObservedState);
    }

    /// <summary>Provider opt-in cannot restore a key member that the ordered stream explicitly removed.</summary>
    [Fact]
    public void IndependentColumnDropCannotAskProviderToRestoreDroppedKeyMember()
    {
        // Arrange
        var provider = new IndependentColumnDropProbe();
        var projection = IndependentDropProjection(provider);
        var operation = new SafeMigrationOperation(new EnsurePrimaryKeyIntent(
            new ExpectedPrimaryKeyDefinition("pk_items", "items", ["legacy"])),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var result = projection.Project(operation, Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal("projected_structure_state_unknown", result.Code);
        Assert.Equal(0, provider.KeyQueries);
    }

    /// <summary>Unknown ordinals never fabricate provenance; the first real stream position remains valid.</summary>
    /// <param name="ordinal">The complete-stream position, or the sentinel before a runner binds it.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void IndependentColumnDropOriginRequiresRealStreamPosition(int ordinal)
    {
        // Arrange
        var provider = new IndependentColumnDropProbe();
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider,
            projectedKeyAnalyzer: provider);
        ObserveAccepted(projection, new DropPrimaryKeyIntent("pk_items", "items"),
            SafeMigrationObservedState.Matching);
        projection.CurrentOperationOrdinal = ordinal;

        // Act
        ObserveAccepted(projection, new DropColumnIntent("legacy", "items"),
            SafeMigrationObservedState.Matching);
        var hasTable = ((ISafeMigrationProjectedTableSource)projection).TryGetProjectedTableState(
            "items", null, out var state);

        // Assert
        Assert.True(hasTable);
        Assert.Equal(ordinal < 0 ? null : ordinal, state.ColumnDropOriginOrdinal);
    }

    /// <summary>A new member keeps its own value origin even when no sibling DROP occurred.</summary>
    [Fact]
    public void NewColumnValueOriginIsIndependentOfColumnDropHistory()
    {
        // Arrange
        var provider = new IndependentColumnDropProbe();
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider)
        {
            CurrentOperationOrdinal = 0,
        };

        var add = new SafeMigrationOperation(new EnsureColumnIntent("items", Column("new_id")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var missing = Live(SafeMigrationObservedState.Missing);
        var decision = SafeMigrationDecisionPlanner.Plan(add.Intent.Kind, missing.ObservedState,
            add.Policy, missing.RepairCapability);

        // Act
        projection.Observe(add, missing, missing, decision);
        ISafeMigrationProjectedColumnSource columns = projection;

        // Assert
        Assert.Equal(0, columns.GetColumnValueMutationOrigin("items", null, "new_id"));
        Assert.False(columns.PreservesLiveColumnValues("items", null, "new_id"));
    }

    /// <summary>A previously completed key removal remains proven absence when an interrupted stream resumes.</summary>
    [Fact]
    public void IndependentColumnDropResumesAfterAlreadyMissingPrimaryKey()
    {
        // Arrange
        var provider = new IndependentColumnDropProbe();
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider,
            projectedKeyAnalyzer: provider);

        ObserveAccepted(projection, new DropPrimaryKeyIntent("pk_items", "items"),
            SafeMigrationObservedState.Missing);

        projection.CurrentOperationOrdinal = 1;
        ObserveAccepted(projection, new DropColumnIntent("legacy", "items"),
            SafeMigrationObservedState.Matching);

        // Act
        var result = projection.Project(PrimaryKeyReplacementOperation(), Live(SafeMigrationObservedState.Missing));
        var hasTable = ((ISafeMigrationProjectedTableSource)projection).TryGetProjectedTableState(
            "items", null, out var state);

        // Assert
        Assert.Same(provider.KeyResult, result);
        Assert.Equal(1, provider.KeyQueries);
        Assert.True(hasTable);
        Assert.True(state.PrimaryKeyWasDropped);
    }

    /// <summary>A completed removal prefix needs no fabricated structural mutation to reuse live rows.</summary>
    [Fact]
    public void MissingRemovalPrefixRetainsUnchangedNativeMemberValues()
    {
        // Arrange
        var provider = new IndependentColumnDropProbe();
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider,
            projectedKeyAnalyzer: provider);

        ObserveAccepted(projection, new DropPrimaryKeyIntent("pk_items", "items"),
            SafeMigrationObservedState.Missing);

        ObserveAccepted(projection, new DropColumnIntent("legacy", "items"),
            SafeMigrationObservedState.Missing);

        ObserveAccepted(projection, new EnsureColumnIntent("items", Column("id")),
            SafeMigrationObservedState.Matching);

        // Act
        var columns = (ISafeMigrationProjectedColumnSource)projection;
        var unchanged = columns.PreservesLiveColumnValues("items", null, "id");

        // Assert
        Assert.True(unchanged);
        Assert.Null(columns.GetColumnValueMutationOrigin("items", null, "id"));
        Assert.True(((ISafeMigrationProjectedTableSource)projection).TryGetProjectedTableState(
            "items", null, out var state));
        Assert.True(state.PrimaryKeyWasDropped);
        Assert.False(state.HasUnknownStructure);
        Assert.Null(state.ColumnDropOriginOrdinal);
    }

    /// <summary>Existence-only acceptance of a different key is not a removal or absence certificate.</summary>
    [Fact]
    public void IndependentColumnDropDoesNotTreatDifferentNoOpAsPrimaryKeyAbsence()
    {
        // Arrange
        var provider = new IndependentColumnDropProbe();
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider,
            projectedKeyAnalyzer: provider);
        var operation = new SafeMigrationOperation(new DropPrimaryKeyIntent("pk_items", "items"),
            SafeMigrationPolicy.ExistenceOnly);
        var different = Live(SafeMigrationObservedState.Different);
        var noOp = new SafeMigrationDecision(SafeMigrationAction.NoOp, "test_existence_only");
        projection.CurrentOperationOrdinal = 1;

        // Act
        projection.Observe(operation, different, different, noOp);
        ObserveAccepted(projection, new DropColumnIntent("legacy", "items"),
            SafeMigrationObservedState.Matching);
        var result = projection.Project(PrimaryKeyReplacementOperation(), Live(SafeMigrationObservedState.Different));

        // Assert
        Assert.Equal("projected_structure_state_unknown", result.Code);
        Assert.Equal(0, provider.KeyQueries);
    }

    /// <summary>Generic DROP requalification cannot replace an independent generated-member proof.</summary>
    /// <param name="kind">An ordinary member or one of the two generated-expression representations.</param>
    [Theory]
    [InlineData("ordinary")]
    [InlineData("structured")]
    [InlineData("opaque")]
    [InlineData("unaccepted")]
    public void IndependentColumnDropIndexPreservesGeneratedQualificationBoundary(string kind)
    {
        // Arrange
        var provider = new IndependentColumnDropProbe();
        var projection = IndependentDropProjection(provider);
        var column = new ExpectedColumnDefinition("derived", typeof(int), true, "int",
            computedColumnSql: kind == "opaque" ? "reference + 1" : null,
            computedExpression: kind == "structured" ? SafeMigrationSql.Literal(1) : null);
        if (kind != "unaccepted")
        {
            ObserveAccepted(projection, new EnsureColumnIntent("items", column), SafeMigrationObservedState.Missing);
        }
        var operation = new SafeMigrationOperation(new EnsureIndexIntent(new ExpectedIndexDefinition(
            "ix_derived", "items", [new ExpectedIndexKeyDefinition("derived")])),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var result = projection.Project(operation, Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(kind == "ordinary" ? 1 : 0, provider.KeyQueries);
        if (kind == "ordinary")
        {
            Assert.Same(provider.KeyResult, result);
        }
        else
        {
            Assert.Equal("projected_structure_state_unknown", result.Code);
        }
    }

    /// <summary>A native prerequisite failure requires bounded member evidence before generic key proof.</summary>
    /// <param name="capturesOriginalMember">Whether native physical evidence contains the surviving member.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndependentColumnDropIndexRequiresBoundedMissingPrerequisites(bool capturesOriginalMember)
    {
        // Arrange
        var provider = new IndependentColumnDropProbe();
        var projection = IndependentDropProjection(provider);
        ObserveAccepted(projection, new EnsureColumnIntent("items", NullableColumn("new_id")),
            SafeMigrationObservedState.Missing);
        var operation = new SafeMigrationOperation(new EnsureIndexIntent(new ExpectedIndexDefinition(
            "ix_mixed", "items", [new ExpectedIndexKeyDefinition("new_id"),
                new ExpectedIndexKeyDefinition("reference")])), SafeMigrationPolicy.ThrowIfDifferent);
        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);
        if (capturesOriginalMember)
        {
            live = live.WithIndexPhysicalEnvironment(new SafeMigrationIndexPhysicalEnvironment(3072,
                new Dictionary<string, SafeMigrationIndexColumnPhysicalShape>(StringComparer.Ordinal)
                {
                    ["reference"] = new(null, 0, 4, false),
                }));
        }

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.Equal(capturesOriginalMember ? 1 : 0, provider.KeyQueries);
        Assert.Equal(capturesOriginalMember ? provider.KeyResult.Code : "projected_structure_state_unknown",
            result.Code);
    }

    /// <summary>Only completion of an owned existing staging member retains its original values.</summary>
    /// <param name="resumesStaging">Whether native classification certifies an owned staging member.</param>
    /// <param name="previouslyAdded">Whether an earlier operation invalidated that member's live values.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OwnedStagingResumeRetainsOnlyUnchangedOriginalMemberValues(bool resumesStaging, bool previouslyAdded)
    {
        // Arrange
        var provider = new IndependentColumnDropProbe();
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider);
        ObserveAccepted(projection, new DropPrimaryKeyIntent("pk_items", "items"),
            SafeMigrationObservedState.Matching);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", Column("id")),
            SafeMigrationPolicy.ThrowIfDifferent);
        projection.CurrentOperationOrdinal = 2;
        if (previouslyAdded)
        {
            ObserveAccepted(projection, operation.Intent, SafeMigrationObservedState.Missing);
        }

        var missing = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, false, "column_atomic_add_resume")
        {
            RequiredColumnAddResumesOwnedStaging = resumesStaging,
        };
        projection.CurrentOperationOrdinal = 3;
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, missing.ObservedState,
            operation.Policy, missing.RepairCapability);

        // Act
        projection.Observe(operation, missing, missing, decision);
        var columns = (ISafeMigrationProjectedColumnSource)projection;

        // Assert
        var preservesValues = resumesStaging && !previouslyAdded;
        Assert.Equal(preservesValues, columns.PreservesLiveColumnValues("items", null, "id"));
        Assert.Equal(preservesValues ? null : 3, columns.GetColumnValueMutationOrigin("items", null, "id"));
        Assert.Null(columns.GetColumnValueMutationOrigin("items", "other", "id"));
    }

    /// <summary>Owned-staging source preservation cannot outlive later opaque or row mutations.</summary>
    /// <param name="opaque">Whether the later mutation is opaque SQL rather than typed row insertion.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnedStagingResumeValueProofExpiresAtDataBoundaries(bool opaque)
    {
        // Arrange
        var provider = new IndependentColumnDropProbe();
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", Column("id")),
            SafeMigrationPolicy.ThrowIfDifferent);
        var missing = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, false, "column_atomic_add_resume")
        {
            RequiredColumnAddResumesOwnedStaging = true,
        };
        projection.Observe(operation, missing, missing, SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind,
            missing.ObservedState, operation.Policy, missing.RepairCapability));
        var columns = (ISafeMigrationProjectedColumnSource)projection;
        var originalValuesPreserved = columns.PreservesLiveColumnValues("items", null, "id");
        MigrationOperation boundary = opaque ? new SqlOperation { Sql = "SELECT 1;" }
            : new InsertDataOperation { Table = "items", Columns = ["id"], Values = new object[,] { { 1 } } };

        // Act
        projection.ObserveProviderPostcondition(boundary);
        var preservesValues = columns.PreservesLiveColumnValues("items", null, "id");

        // Assert
        Assert.True(originalValuesPreserved);
        Assert.False(preservesValues);
        Assert.Null(columns.GetColumnValueMutationOrigin("items", null, "id"));
    }

    /// <summary>Builds only accepted native removal facts, not a synthesized complete table.</summary>
    private static SafeMigrationPreflightProjection IndependentDropProjection(
        IndependentColumnDropProbe provider,
        bool dropPrimary = true
    )
    {
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider,
            projectedKeyAnalyzer: provider);
        if (dropPrimary)
        {
            ObserveAccepted(projection, new DropPrimaryKeyIntent("pk_items", "items"),
                SafeMigrationObservedState.Matching);
        }

        projection.CurrentOperationOrdinal = 1;
        ObserveAccepted(projection, new DropColumnIntent("legacy", "items"),
            SafeMigrationObservedState.Matching);

        return projection;
    }

    /// <summary>Models narrow native certificates without treating complete physical tables as known.</summary>
    private sealed class IndependentColumnDropProbe(bool certifiesDrop = true, bool validatesKey = true)
        : ISafeMigrationProjectedColumnAnalyzer,
        ISafeMigrationProjectedKeyAnalyzer
    {
        /// <summary>Gets native exact-column certificate queries.</summary>
        public int ColumnQueries { get; private set; }

        /// <summary>Gets independent provider-key validation queries.</summary>
        public int KeyQueries { get; private set; }

        /// <inheritdoc />
        public bool SharesUniqueConstraintAndIndexIdentity => false;

        /// <inheritdoc />
        public bool PrimaryKeyUsesNameIndependentIdentity => true;

        /// <inheritdoc />
        public bool CanValidateAfterColumnDrops => validatesKey;

        /// <summary>Gets the exact native result returned by this key proof.</summary>
        public SafeMigrationProviderAnalysis KeyResult { get; } = Live(SafeMigrationObservedState.Missing);

        /// <summary>Gets the original native observation received by the proof.</summary>
        public SafeMigrationProviderAnalysis? KeyLive { get; private set; }

        /// <summary>Gets the unproven ordered observation received by the proof.</summary>
        public SafeMigrationProviderAnalysis? KeyProjected { get; private set; }

        /// <inheritdoc />
        public bool PreservesIntrinsicColumnSources(SafeMigrationIntent intent)
            => intent is DropPrimaryKeyIntent || (certifiesDrop && intent is DropColumnIntent);

        /// <inheritdoc />
        public bool CanReuseLiveColumnProofAfterKeyChanges(AlterColumnIntent intent,
            SafeMigrationProviderAnalysis liveAnalysis)
        {
            ColumnQueries++;

            return intent.OldDefinition is { } source
                && source.IsNullable != intent.Definition.IsNullable
                && SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(source, intent.Definition)
                && !liveAnalysis.RepairMutatesData;
        }

        /// <inheritdoc />
        public SafeMigrationProviderAnalysis ValidateProjectedAlterColumn(AlterColumnIntent intent,
            ExpectedColumnDefinition source, SafeMigrationProjectedAlterColumnContext context,
            SafeMigrationProviderAnalysis liveAnalysis, SafeMigrationProviderAnalysis projectedAnalysis)
            => projectedAnalysis;

        /// <inheritdoc />
        public SafeMigrationProviderAnalysis ValidateProjectedPrimaryKey(EnsurePrimaryKeyIntent intent,
            ISafeMigrationProjectedColumnSource columns, SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis)
        {
            KeyQueries++;
            KeyLive = liveAnalysis;
            KeyProjected = projectedAnalysis;

            return KeyResult;
        }

        /// <inheritdoc />
        public SafeMigrationProviderAnalysis ValidateProjectedIndex(EnsureIndexIntent intent,
            ISafeMigrationProjectedColumnSource columns, SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis)
        {
            KeyQueries++;
            KeyLive = liveAnalysis;
            KeyProjected = projectedAnalysis;

            return KeyResult;
        }

        /// <inheritdoc />
        public SafeMigrationProviderAnalysis ValidateProjectedUniqueConstraint(EnsureUniqueConstraintIntent intent,
            ISafeMigrationProjectedColumnSource columns, SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis)
        {
            KeyQueries++;
            KeyLive = liveAnalysis;
            KeyProjected = projectedAnalysis;

            return KeyResult;
        }
    }
}
