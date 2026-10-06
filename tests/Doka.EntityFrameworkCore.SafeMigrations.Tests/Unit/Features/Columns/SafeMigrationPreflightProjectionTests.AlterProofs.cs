namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>A matching shape cannot erase NULL rows observed by the provider.</summary>
    [Fact]
    public void ProjectedAlterRetainsLiveNullabilityBlocker()
    {
        // Arrange
        var oldColumn = new ExpectedColumnDefinition("value", typeof(int), isNullable: true, storeType: "int");
        var projection = new SafeMigrationPreflightProjection();
        ObserveAccepted(projection,
            new EnsureTableIntent(new ExpectedTableDefinition("items", [oldColumn]),
                SafeMigrationTableMode.StrictDefinition),
            SafeMigrationObservedState.Matching);
        var operation = new SafeMigrationOperation(
            new AlterColumnIntent("items", Column("value"), oldColumn), SafeMigrationPolicy.RepairIfSafe);

        // Act
        var result = projection.Project(operation, Live(SafeMigrationObservedState.DataBlocked));

        // Assert
        Assert.Equal(SafeMigrationObservedState.DataBlocked, result.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.None, result.RepairCapability);
    }

    /// <summary>An accepted empty table discharges row evidence, but only until DML.</summary>
    /// <param name="mutate">Whether a prior operation invalidates the empty proof.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectedAlterUsesVersionedEmptyProof(bool mutate)
    {
        // Arrange
        var oldColumn = new ExpectedColumnDefinition("value", typeof(int), isNullable: true, storeType: "int");
        var projection = new SafeMigrationPreflightProjection();
        Apply(projection,
            new EnsureTableIntent(new ExpectedTableDefinition("items", [oldColumn]),
                SafeMigrationTableMode.StrictDefinition));
        var operation = new SafeMigrationOperation(
            new AlterColumnIntent("items", Column("value"), oldColumn), SafeMigrationPolicy.RepairIfSafe);

        if (mutate)
        {
            projection.ObserveProviderPostcondition(new InsertDataOperation
            {
                Table = "unrelated",
                Columns = ["value"],
                Values = new object?[,] { { 1 } },
            });
        }

        // Act
        var result = projection.Project(operation, Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(mutate ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
            result.RepairCapability);
        Assert.Equal(mutate ? SafeMigrationObservedState.PrerequisiteMissing : SafeMigrationObservedState.Different,
            result.ObservedState);
    }

    /// <summary>Provider capability remains bound to the exact reviewed projected source.</summary>
    [Fact]
    public void ProjectedAlterRejectsProviderProofForAnotherOldDefinition()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        Apply(projection,
            new EnsureTableIntent(new ExpectedTableDefinition("items", [VarcharColumn(30)]),
                SafeMigrationTableMode.StrictDefinition));
        var operation = new SafeMigrationOperation(
            new AlterColumnIntent("items", VarcharColumn(100), VarcharColumn(20)), SafeMigrationPolicy.RepairIfSafe);

        // Act
        var result = projection.Project(operation, AlterProof());

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, result.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.None, result.RepairCapability);
    }

    /// <summary>Accepted trigger-capable backfills invalidate proofs on other tables.</summary>
    /// <param name="mutatesData">Whether the provider identifies an UPDATE in the repair.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedAlterInvalidatesRowsOnlyForProviderProvenMutation(bool mutatesData)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var backfill = new SafeMigrationOperation(
            new AlterColumnIntent("other", VarcharColumn(50), VarcharColumn(20)), SafeMigrationPolicy.RepairIfSafe);
        var live = AlterProof(mutatesData: mutatesData);
        var result = projection.Project(backfill, live);
        var decision = SafeMigrationDecisionPlanner.Plan(backfill.Intent.Kind, result.ObservedState,
            backfill.Policy, result.RepairCapability);
        var later = new SafeMigrationOperation(
            new AlterColumnIntent("items", VarcharColumn(10), VarcharColumn(200)), SafeMigrationPolicy.RepairIfSafe);

        // Act
        projection.Observe(backfill, live, result, decision);
        var laterResult = projection.Project(later, AlterProof(requiresRows: true));

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, decision.Action);
        Assert.Equal(
            mutatesData ? SafeMigrationObservedState.PrerequisiteMissing : SafeMigrationObservedState.Different,
            laterResult.ObservedState);
        Assert.Equal(mutatesData ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
            laterResult.RepairCapability);
    }

    /// <summary>Source existence alone does not remove a live rename destination.</summary>
    /// <param name="dropTarget">Whether an earlier accepted drop proves destination absence.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingConvergenceCannotOverrideRenameDestinationConflict(bool dropTarget)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        ObserveAccepted(projection,
            new EnsureTableIntent(new ExpectedTableDefinition("items", [Column("id")]),
                SafeMigrationTableMode.ConvergenceContainer), SafeMigrationObservedState.Matching);
        if (dropTarget)
        {
            ObserveAccepted(projection, new DropTableIntent("renamed"), SafeMigrationObservedState.Matching);
        }

        var rename = new SafeMigrationOperation(new RenameTableIntent("items", "renamed"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var result = projection.Project(rename, Live(SafeMigrationObservedState.Different));

        // Assert
        Assert.Equal(dropTarget ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            result.ObservedState);
    }

    /// <summary>Exact target replay consumes rather than carries historical row evidence.</summary>
    [Fact]
    public void AcceptedAlterTargetRemainsMatchingAfterUnrelatedDml()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var operation = new SafeMigrationOperation(
            new AlterColumnIntent("items", VarcharColumn(10), VarcharColumn(200)), SafeMigrationPolicy.RepairIfSafe);
        var live = AlterProof(requiresRows: true);
        var result = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, result.ObservedState,
            operation.Policy, result.RepairCapability);
        projection.Observe(operation, live, result, decision);
        projection.ObserveProviderPostcondition(new DeleteDataOperation
        {
            Table = "other",
            KeyColumns = ["id"],
            KeyValues = new object[,] { { 1 } },
        });

        // Act
        var replay = projection.Project(operation, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, replay.ObservedState);
        Assert.False(replay.RequiresLiveDataProof);
    }

    private static SafeMigrationProviderAnalysis AlterProof(
        bool requiresRows = false,
        bool mutatesData = false
    ) =>
        new(SafeMigrationObservedState.Different, SafeMigrationRepairCapability.Safe, false, "alter_proof")
        {
            RequiresLiveDataProof = requiresRows,
            RepairMutatesData = mutatesData,
        };

    /// <summary>Unknown destination presence cannot authorize a projected new-source rename.</summary>
    /// <param name="destinationExists">The independent provider observation, when available.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void NewTableRenameRequiresIndependentDestinationAbsence(bool? destinationExists)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", [Column("id")]),
            SafeMigrationTableMode.ConvergenceContainer));
        var operation = new SafeMigrationOperation(new RenameTableIntent("items", "renamed"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, false, "missing")
        {
            RenameTargetExists = destinationExists,
        };

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.Equal(destinationExists == false ? SafeMigrationObservedState.Matching
            : destinationExists == true ? SafeMigrationObservedState.Different
            : SafeMigrationObservedState.PrerequisiteMissing, result.ObservedState);
    }

    /// <summary>Ordinary EF backfills can fire triggers, but pure width changes cannot.</summary>
    /// <param name="backfill">Whether the ordinary operation may issue an UPDATE.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryAlterInvalidatesRowProofOnlyWhenItMayBackfill(bool backfill)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var operation = new AlterColumnOperation
        {
            Table = "other",
            Name = "value",
            ClrType = typeof(int),
            ColumnType = "int",
            IsNullable = false,
            DefaultValue = backfill ? 1 : null,
            OldColumn = new AddColumnOperation
            {
                ClrType = typeof(int),
                ColumnType = "int",
                IsNullable = backfill,
            },
        };

        var later = new SafeMigrationOperation(
            new AlterColumnIntent("items", VarcharColumn(10), VarcharColumn(200)), SafeMigrationPolicy.RepairIfSafe);

        // Act
        projection.ObserveProviderPostcondition(operation);
        var result = projection.Project(later, AlterProof(requiresRows: true));

        // Assert
        Assert.Equal(backfill ? SafeMigrationObservedState.PrerequisiteMissing : SafeMigrationObservedState.Different,
            result.ObservedState);
    }

    /// <summary>Original value domains require an unbroken certified history and a fresh row epoch.</summary>
    /// <param name="certified">Whether the provider certified the accepted repair.</param>
    /// <param name="unknownPriorChange">Whether an ordinary operation already invalidated the source domain.</param>
    /// <param name="mutateRows">Whether a later data operation invalidated the captured rows.</param>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void ProjectedAlterValueDomainRequiresCertifiedFreshHistory(
        bool certified,
        bool unknownPriorChange,
        bool mutateRows
    )
    {
        // Arrange
        var provider = new CapturingProjectedColumnAnalyzer();
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider);
        var source = new ExpectedColumnDefinition("value", typeof(string), false, "longtext");
        if (unknownPriorChange)
        {
            projection.ObserveProviderPostcondition(new AlterColumnOperation
            {
                Table = "items",
                Name = "value",
                ClrType = typeof(string),
                ColumnType = "varchar(200)",
                MaxLength = 200,
            });
        }

        var ensure = new SafeMigrationOperation(new EnsureColumnIntent("items", source),
            SafeMigrationPolicy.RepairIfSafe);
        var repair = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe, false, "lossless_repair")
        {
            RepairPreservesLiveValueDomain = certified,
        };

        var decision = SafeMigrationDecisionPlanner.Plan(ensure.Intent.Kind, repair.ObservedState,
            ensure.Policy, repair.RepairCapability);
        projection.Observe(ensure, repair, repair, decision);
        ObserveAccepted(projection, ensure.Intent, SafeMigrationObservedState.Matching);
        if (mutateRows)
        {
            projection.ObserveProviderPostcondition(new InsertDataOperation
            {
                Table = "other",
                Columns = ["id"],
                Values = new object?[,] { { 1 } },
            });
        }

        var alter = new SafeMigrationOperation(new AlterColumnIntent("items", VarcharColumn(500), source),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        projection.Project(alter, Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.NotNull(provider.Context);
        Assert.Equal(certified && !unknownPriorChange && !mutateRows,
            provider.Context.Value.PreservesLiveValueDomain);
    }

    /// <summary>Every accepted model-managed DML kind can fire triggers, but replay does not mutate rows.</summary>
    /// <param name="kind">The model-managed mutation kind.</param>
    /// <param name="apply">Whether the decision executes DML or is an exact replay.</param>
    [Theory]
    [InlineData("insert", true)]
    [InlineData("update", true)]
    [InlineData("delete", true)]
    [InlineData("insert", false)]
    [InlineData("update", false)]
    [InlineData("delete", false)]
    public void ModelManagedMutationInvalidatesGlobalProofOnlyWhenApplied(
        string kind,
        bool apply
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        ModelManagedDataIntent intent = kind switch
        {
            "insert" => new EnsureModelManagedDataIntent("other", ["id"], ["int"], ["id"], ["int"],
                new object?[,] { { 1 } }, null, null),
            "update" => new UpdateModelManagedDataIntent("other", ["id"], ["int"], new object?[,] { { 1 } },
                ["value"], ["int"], new object?[,] { { 1 } }, new object?[,] { { 2 } }, null, null),
            _ => new DeleteModelManagedDataIntent("other", ["id"], ["int"], new object?[,] { { 1 } },
                ["id"], ["int"], new object?[,] { { 1 } }, null, null),
        };

        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var state = apply
            ? kind == "insert" ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.TransitionReady
            : kind == "delete" ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Matching;
        var live = Live(state);
        var decision = SafeMigrationDecisionPlanner.Plan(intent.Kind, state, operation.Policy, live.RepairCapability);
        var later = new SafeMigrationOperation(new AlterColumnIntent("items", VarcharColumn(10), VarcharColumn(200)),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        projection.Observe(operation, live, live, decision);
        var result = projection.Project(later, AlterProof(requiresRows: true));

        // Assert
        Assert.Equal(apply ? SafeMigrationAction.Apply : SafeMigrationAction.NoOp, decision.Action);
        Assert.Equal(apply ? SafeMigrationObservedState.PrerequisiteMissing : SafeMigrationObservedState.Different,
            result.ObservedState);
    }

    /// <summary>Own-table completeness advances only if no earlier external mutation invalidated it.</summary>
    /// <param name="priorMutation">Whether another operation already invalidated the initial empty-table proof.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModelManagedCompletenessCannotReviveAfterPriorOtherMutation(bool priorMutation)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", [Column("id")]),
            SafeMigrationTableMode.StrictDefinition));
        if (priorMutation)
        {
            projection.ObserveProviderPostcondition(new InsertDataOperation
            {
                Table = "other",
                Columns = ["id"],
                Values = new object?[,] { { 1 } },
            });
        }

        var first = new SafeMigrationOperation(new EnsureModelManagedDataIntent("items", ["id"], ["int"],
            ["id"], ["int"], new object?[,] { { 1 } }, null, null), SafeMigrationPolicy.ThrowIfDifferent);
        var firstLive = Live(SafeMigrationObservedState.Missing);
        projection.Observe(first, firstLive, firstLive, SafeMigrationDecisionPlanner.Plan(first.Intent.Kind,
            firstLive.ObservedState, first.Policy, firstLive.RepairCapability));
        var second = new SafeMigrationOperation(new EnsureModelManagedDataIntent("items", ["id"], ["int"],
            ["id"], ["int"], new object?[,] { { 2 } }, null, null), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var result = projection.Project(second, Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(
            priorMutation ? SafeMigrationObservedState.PrerequisiteMissing : SafeMigrationObservedState.Missing,
            result.ObservedState);
    }

    /// <summary>Borrowed views are repeatable within a callback and advance only after accepted mutation.</summary>
    /// <param name="complete">Whether Core owns the entire table or only accepted prerequisite facts.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectedAlterViewIsStableWithinEachSynchronousCallback(bool complete)
    {
        // Arrange
        var provider = new CapturingProjectedColumnAnalyzer();
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider);
        var source = new ExpectedColumnDefinition("value", typeof(int), isNullable: true, storeType: "int");
        var target = Column("value");
        var primaryKey = new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"]);
        var unique = new ExpectedUniqueConstraintDefinition("uq_items", "items", ["id"]);
        if (complete)
        {
            Apply(projection, new EnsureTableIntent(
                new ExpectedTableDefinition("items", [Column("id"), source],
                    primaryKey: primaryKey, uniqueConstraints: [unique]),
                SafeMigrationTableMode.StrictDefinition));
        }
        else
        {
            ObserveAccepted(projection, new EnsureColumnIntent("items", Column("id")),
                SafeMigrationObservedState.Matching);
            ObserveAccepted(projection, new EnsureColumnIntent("items", source), SafeMigrationObservedState.Matching);
            ObserveAccepted(projection, new EnsurePrimaryKeyIntent(primaryKey), SafeMigrationObservedState.Matching);
            ObserveAccepted(projection, new EnsureUniqueConstraintIntent(unique), SafeMigrationObservedState.Matching);
        }

        ObserveAccepted(projection, new EnsureIndexIntent(new ExpectedIndexDefinition(
            "ix_items", "items", [new ExpectedIndexKeyDefinition("id")])), SafeMigrationObservedState.Matching);
        var first = new SafeMigrationOperation(new AlterColumnIntent("items", target, source),
            SafeMigrationPolicy.RepairIfSafe);

        var later = new SafeMigrationOperation(new AlterColumnIntent("items",
            new ExpectedColumnDefinition("value", typeof(int), false, "int", comment: "changed"), target),
            SafeMigrationPolicy.RepairIfSafe);

        var live = AlterProof();

        // Act
        var firstResult = projection.Project(first, live);
        var decision = SafeMigrationDecisionPlanner.Plan(first.Intent.Kind, firstResult.ObservedState,
            first.Policy, firstResult.RepairCapability);

        var firstFacts = provider.Context;
        projection.Observe(first, live, firstResult, decision);
        var laterResult = projection.Project(later, live);
        var laterFacts = provider.Context;

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, decision.Action);
        Assert.Equal(SafeMigrationRepairCapability.Safe, laterResult.RepairCapability);
        Assert.NotNull(firstFacts);
        Assert.NotNull(laterFacts);
        Assert.Equal(complete, firstFacts.Value.HasCompleteTable);
        Assert.Equal(complete, laterFacts.Value.HasCompleteTable);
        Assert.True(firstFacts.Value.RepeatedEnumerationMatches);
        Assert.True(laterFacts.Value.RepeatedEnumerationMatches);
        Assert.True(firstFacts.Value.ValueIsNullable);
        Assert.False(laterFacts.Value.ValueIsNullable);
        Assert.Equal(2, firstFacts.Value.ColumnCount);
        Assert.Equal(2, laterFacts.Value.ColumnCount);
        Assert.Equal(1, firstFacts.Value.UniqueConstraintCount);
        Assert.Equal(1, laterFacts.Value.UniqueConstraintCount);
        Assert.Equal(1, firstFacts.Value.IndexCount);
        Assert.Equal(1, laterFacts.Value.IndexCount);
        Assert.Equal("pk_items", firstFacts.Value.PrimaryKeyName);
        Assert.Equal("pk_items", laterFacts.Value.PrimaryKeyName);
    }

    /// <summary>Copies scalar observations before the synchronous borrowed-view callback returns.</summary>
    private sealed class CapturingProjectedColumnAnalyzer : ISafeMigrationProjectedColumnAnalyzer
    {
        /// <summary>Gets copied observations from the most recent validation callback.</summary>
        public CapturedProjectedColumnFacts? Context { get; private set; }

        /// <inheritdoc />
        public SafeMigrationProviderAnalysis ValidateProjectedAlterColumn(
            AlterColumnIntent intent,
            ExpectedColumnDefinition source,
            SafeMigrationProjectedAlterColumnContext context,
            SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis
        )
        {
            var columns = context.Table.Columns.ToArray();
            var uniqueConstraints = context.Table.UniqueConstraints.ToArray();
            var indexes = context.Table.Indexes.ToArray();
            var primaryKey = context.Table.PrimaryKey;

            // WHY: The view and deferred enumerables expire with this callback.
            // Only copied scalar facts may survive until the test's Assert section.
            Context = new CapturedProjectedColumnFacts(
                context.PreservesLiveValueDomain,
                context.CanReuseCreationCharacterSet,
                context.HasCompleteTable,
                context.HasForeignKeyDependency,
                columns.SequenceEqual(context.Table.Columns)
                    && uniqueConstraints.SequenceEqual(context.Table.UniqueConstraints)
                    && indexes.SequenceEqual(context.Table.Indexes)
                    && ReferenceEquals(primaryKey, context.Table.PrimaryKey),
                columns.FirstOrDefault(static column => column.Name == "value")?.IsNullable,
                columns.Length,
                uniqueConstraints.Length,
                indexes.Length,
                primaryKey?.Name);

            return projectedAnalysis;
        }
    }

    /// <summary>Retains no borrowed table, enumerable, or projection reference after validation.</summary>
    /// <param name="PreservesLiveValueDomain">Whether the prior transition retained its captured value domain.</param>
    /// <param name="CanReuseCreationCharacterSet">Whether captured creation defaults remain applicable.</param>
    /// <param name="HasCompleteTable">Whether Core owns the entire accepted table shape.</param>
    /// <param name="HasForeignKeyDependency">Whether a projected foreign key depends on the changing column.</param>
    /// <param name="RepeatedEnumerationMatches">Whether repeated callback reads returned the same facts.</param>
    /// <param name="ValueIsNullable">The changing column's nullability, or null if it was not captured.</param>
    /// <param name="ColumnCount">The number of accepted columns.</param>
    /// <param name="UniqueConstraintCount">The number of accepted unique constraints.</param>
    /// <param name="IndexCount">The number of accepted indexes.</param>
    /// <param name="PrimaryKeyName">The accepted primary key name, or null if absent.</param>
    private readonly record struct CapturedProjectedColumnFacts(
        bool PreservesLiveValueDomain,
        bool CanReuseCreationCharacterSet,
        bool HasCompleteTable,
        bool HasForeignKeyDependency,
        bool RepeatedEnumerationMatches,
        bool? ValueIsNullable,
        int ColumnCount,
        int UniqueConstraintCount,
        int IndexCount,
        string? PrimaryKeyName
    );
}
