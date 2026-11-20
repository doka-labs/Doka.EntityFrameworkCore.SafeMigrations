namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>
    /// Does not turn a matching existence-only container into a complete strict table-definition proof.
    /// </summary>
    [Fact]
    public void MatchingConvergenceContainerPreservesFollowingLiveStrictAnalysis()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var table = new ExpectedTableDefinition("items", [Column("id")]);

        ObserveAccepted(
            projection,
            new EnsureTableIntent(table, SafeMigrationTableMode.ConvergenceContainer),
            SafeMigrationObservedState.Matching);

        var live = Live(SafeMigrationObservedState.Different);

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(
                new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition),
                SafeMigrationPolicy.ThrowIfDifferent),
            live);

        // Assert
        Assert.Same(live, analysis);
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
    }

    /// <summary>
    /// Retains an accepted unexpected member as a concrete strict-shape difference after container convergence.
    /// </summary>
    [Fact]
    public void AppliedUnexpectedColumnAfterConvergenceContainerProvesFollowingStrictDifference()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var table = new ExpectedTableDefinition("items", [Column("id")]);

        ObserveAccepted(
            projection,
            new EnsureTableIntent(table, SafeMigrationTableMode.ConvergenceContainer),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new EnsureColumnIntent("items", Column("added")),
            SafeMigrationObservedState.Missing);

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(
                new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition),
                SafeMigrationPolicy.ThrowIfDifferent),
            Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
        Assert.Equal("projected_different", analysis.Code);
    }

    /// <summary>
    /// Requires a fresh strict table proof after an accepted member repair changes the incomplete owner shape.
    /// </summary>
    [Fact]
    public void RepairedExpectedColumnAfterConvergenceContainerInvalidatesFollowingLiveStrictAnalysis()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var id = Column("id");
        var table = new ExpectedTableDefinition("items", [id]);

        ObserveAccepted(
            projection,
            new EnsureTableIntent(table, SafeMigrationTableMode.ConvergenceContainer),
            SafeMigrationObservedState.Matching);

        var repair = new SafeMigrationOperation(
            new EnsureColumnIntent("items", id),
            SafeMigrationPolicy.RepairIfSafe);

        var repairAnalysis = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe,
            postconditionSatisfied: false,
            "test_repair");

        var repairDecision = SafeMigrationDecisionPlanner.Plan(
            repair.Intent.Kind,
            repairAnalysis.ObservedState,
            repair.Policy,
            repairAnalysis.RepairCapability);

        projection.Observe(repair, repairAnalysis, repairAnalysis, repairDecision);

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(
                new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition),
                SafeMigrationPolicy.ThrowIfDifferent),
            Live(SafeMigrationObservedState.Different));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", analysis.Code);
    }

    /// <summary>
    /// Carries the all-NULL new-member proof into ordinary unique-index creation without guessing existing values.
    /// </summary>
    [Fact]
    public void ProjectionAllowsUniqueIndexAfterNullableColumnConvergence()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var id = Column("id");
        var email = new ExpectedColumnDefinition("email", typeof(string), isNullable: true, storeType: "text");
        var table = new ExpectedTableDefinition("users", [id, email]);

        ObserveAccepted(
            projection,
            new EnsureTableIntent(table, SafeMigrationTableMode.ConvergenceContainer),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new EnsureColumnIntent("users", id),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new EnsureColumnIntent("users", email),
            SafeMigrationObservedState.Missing);

        var index = new EnsureIndexIntent(
            new ExpectedIndexDefinition(
                "ux_users_email",
                "users",
                [new ExpectedIndexKeyDefinition(column: "email")],
                unique: true));

        var operation = new SafeMigrationOperation(index, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Uses accepted member existence to satisfy a nonunique index without requiring a row-uniqueness proof.
    /// </summary>
    [Fact]
    public void ProjectionAllowsNonUniqueIndexAfterRequiredColumnsConverge()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var id = Column("id");
        var table = new ExpectedTableDefinition("items", [id]);

        ObserveAccepted(
            projection,
            new EnsureTableIntent(table, SafeMigrationTableMode.ConvergenceContainer),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new EnsureColumnIntent("items", id),
            SafeMigrationObservedState.Missing);

        var index = new EnsureIndexIntent(
            new ExpectedIndexDefinition(
                "ix_items_id",
                "items",
                [new ExpectedIndexKeyDefinition(column: "id")]));

        var operation = new SafeMigrationOperation(index, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    /// <summary>
    /// Makes accepted column repair available to the following nonunique-index prerequisite.
    /// </summary>
    [Fact]
    public void ProjectionCarriesSuccessfulColumnRepairIntoFollowingOperations()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var value = new ExpectedColumnDefinition("value", typeof(string), isNullable: true, storeType: "text");
        var table = new ExpectedTableDefinition("items", [value]);

        ObserveAccepted(
            projection,
            new EnsureTableIntent(table, SafeMigrationTableMode.ConvergenceContainer),
            SafeMigrationObservedState.Matching);

        var ensure = new SafeMigrationOperation(
            new EnsureColumnIntent("items", value),
            SafeMigrationPolicy.RepairIfSafe);

        var ensureAnalysis = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe,
            postconditionSatisfied: false,
            "test_repair");

        var ensureDecision = SafeMigrationDecisionPlanner.Plan(
            ensure.Intent.Kind,
            ensureAnalysis.ObservedState,
            ensure.Policy,
            ensureAnalysis.RepairCapability);

        projection.Observe(ensure, ensureAnalysis, ensureAnalysis, ensureDecision);

        var index = new SafeMigrationOperation(
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ix_items_value",
                    "items",
                    [new ExpectedIndexKeyDefinition(column: "value")])),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var indexAnalysis = projection.Project(index, Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, ensureDecision.Action);
        Assert.Equal(SafeMigrationObservedState.Missing, indexAnalysis.ObservedState);
    }

    /// <summary>
    /// Recognizes both literal NULL and structured SQL NULL as nonconflicting new unique-key values.
    /// </summary>
    /// <param name="structuredDefault">
    /// Whether the proven NULL default is represented as a structured SQL literal.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectionAllowsUniqueIndexAfterProvableNullDefault(
        bool structuredDefault
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var externalId = new ExpectedColumnDefinition(
            "external_id",
            typeof(int),
            isNullable: true,
            storeType: "integer",
            defaultValue: structuredDefault
                ? SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(null))
                : SafeMigrationDefaultValue.Literal(null));

        var table = new ExpectedTableDefinition("items", [externalId]);

        ObserveAccepted(
            projection,
            new EnsureTableIntent(table, SafeMigrationTableMode.ConvergenceContainer),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new EnsureColumnIntent("items", externalId),
            SafeMigrationObservedState.Missing);

        var index = new EnsureIndexIntent(
            new ExpectedIndexDefinition(
                "ux_items_external_id",
                "items",
                [new ExpectedIndexKeyDefinition(column: "external_id")],
                unique: true));

        var operation = new SafeMigrationOperation(index, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    /// <summary>
    /// Retains the native rejection when a required or non-NULL-default member has no proven unique value domain.
    /// </summary>
    /// <param name="nullable">Whether the new index member permits NULL values.</param>
    /// <param name="hasDefault">Whether a non-NULL default populates the newly accepted member.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ProjectionKeepsUnprovenUniqueIndexPrerequisiteBlocked(
        bool nullable,
        bool hasDefault
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var key = new ExpectedColumnDefinition(
            "external_id",
            typeof(int),
            nullable,
            "integer",
            defaultValue: hasDefault
                ? SafeMigrationDefaultValue.Literal(0)
                : null);

        var table = new ExpectedTableDefinition("items", [key]);

        ObserveAccepted(
            projection,
            new EnsureTableIntent(table, SafeMigrationTableMode.ConvergenceContainer),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new EnsureColumnIntent("items", key),
            SafeMigrationObservedState.Missing);

        var index = new EnsureIndexIntent(
            new ExpectedIndexDefinition(
                "ux_items_external_id",
                "items",
                [new ExpectedIndexKeyDefinition(column: "external_id")],
                unique: true));

        var operation = new SafeMigrationOperation(index, SafeMigrationPolicy.ThrowIfDifferent);
        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var analysis = projection.Project(operation, live);

        // Assert
        Assert.Same(live, analysis);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
    }

    /// <summary>
    /// Does not infer an unknown indexed column from existence-only table convergence.
    /// </summary>
    [Fact]
    public void ProjectionKeepsUnknownIndexColumnPrerequisiteBlocked()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var table = new ExpectedTableDefinition("items", [Column("id")]);

        ObserveAccepted(
            projection,
            new EnsureTableIntent(table, SafeMigrationTableMode.ConvergenceContainer),
            SafeMigrationObservedState.Matching);

        var index = new EnsureIndexIntent(
            new ExpectedIndexDefinition(
                "ix_items_unknown",
                "items",
                [new ExpectedIndexKeyDefinition(column: "unknown")]));

        var operation = new SafeMigrationOperation(index, SafeMigrationPolicy.ThrowIfDifferent);
        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var analysis = projection.Project(operation, live);

        // Assert
        Assert.Same(live, analysis);
    }

    /// <summary>
    /// Rejects speculative uniqueness for computed values and NULLS NOT DISTINCT semantics.
    /// </summary>
    /// <param name="computed">
    /// Whether the indexed member is computed rather than a proven all-NULL ordinary column.
    /// </param>
    /// <param name="nullsNotDistinct">Whether duplicate NULL values conflict under the index contract.</param>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ProjectionKeepsComputedOrNullsNotDistinctUniqueIndexBlocked(
        bool computed,
        bool nullsNotDistinct
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var externalId = new ExpectedColumnDefinition(
            "external_id",
            typeof(int),
            isNullable: true,
            storeType: "integer",
            computedExpression: computed
                ? SafeMigrationSql.Literal(0)
                : null);

        var table = new ExpectedTableDefinition("items", [externalId]);

        ObserveAccepted(
            projection,
            new EnsureTableIntent(table, SafeMigrationTableMode.ConvergenceContainer),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new EnsureColumnIntent("items", externalId),
            SafeMigrationObservedState.Missing);

        var index = new EnsureIndexIntent(
            new ExpectedIndexDefinition(
                "ux_items_external_id",
                "items",
                [new ExpectedIndexKeyDefinition(column: "external_id")],
                unique: true,
                nullsDistinct: nullsNotDistinct
                    ? false
                    : null));

        var operation = new SafeMigrationOperation(index, SafeMigrationPolicy.ThrowIfDifferent);
        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var analysis = projection.Project(operation, live);

        // Assert
        Assert.Same(live, analysis);
    }

    /// <summary>
    /// Carries every typed member, predicate and FK role through column, index and table rename lifetimes.
    /// </summary>
    [Fact]
    public void ProjectionCarriesImmutableDefinitionsAcrossRenameSequences()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var parent = new ExpectedTableDefinition(
            "legacy_parent",
            [
                Column("id"),
                Column("note"),
                new ExpectedColumnDefinition(
                    "derived",
                    typeof(int),
                    isNullable: true,
                    storeType: "int",
                    isStored: true,
                    computedExpression: AddOne("id")),
            ],
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_parent", "legacy_parent", ["id"]),
            checkConstraints:
            [
                ExpectedCheckConstraintDefinition.FromExpression(
                    "ck_parent_id",
                    "legacy_parent",
                    ParentPredicate("id")),
            ]);

        var child = new ExpectedTableDefinition(
            "child",
            [Column("id"), Column("parent_id")],
            foreignKeys:
            [
                new ExpectedForeignKeyDefinition("fk_child_parent", "child", ["parent_id"], "legacy_parent", ["id"]),
            ]);

        Apply(projection, new EnsureTableIntent(parent, SafeMigrationTableMode.StrictDefinition));
        Apply(projection, new EnsureTableIntent(child, SafeMigrationTableMode.StrictDefinition));
        Apply(
            projection,
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ix_parent_id",
                    "legacy_parent",
                    [new ExpectedIndexKeyDefinition(column: "id")],
                    structuredFilter: Positive("id"))));

        List<SafeMigrationProviderAnalysis> observedStates = [];

        // Act
        Apply(projection, new RenameColumnIntent("id", "legacy_parent", "canonical_id"));

        observedStates.Add(Project(
            projection,
            new EnsureColumnIntent("legacy_parent", Column("canonical_id")),
            SafeMigrationObservedState.Missing));

        observedStates.Add(Project(
            projection,
            new EnsureCheckConstraintIntent(
                ExpectedCheckConstraintDefinition.FromExpression(
                    "ck_parent_id",
                    "legacy_parent",
                    ParentPredicate("canonical_id"))),
            SafeMigrationObservedState.Missing));

        observedStates.Add(Project(
            projection,
            new EnsureForeignKeyIntent(
                new ExpectedForeignKeyDefinition(
                    "fk_child_parent",
                    "child",
                    ["parent_id"],
                    "legacy_parent",
                    ["canonical_id"])),
            SafeMigrationObservedState.Missing));

        Apply(projection, new RenameIndexIntent("ix_parent_id", "legacy_parent", "ix_parent_canonical_id"));
        observedStates.Add(Project(
            projection,
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ix_parent_canonical_id",
                    "legacy_parent",
                    [new ExpectedIndexKeyDefinition(column: "canonical_id")],
                    structuredFilter: Positive("canonical_id"))),
            SafeMigrationObservedState.Missing));

        Apply(projection, new RenameTableIntent("legacy_parent", "canonical_parent"));
        var finalParent = new ExpectedTableDefinition(
            "canonical_parent",
            [
                Column("canonical_id"),
                Column("note"),
                new ExpectedColumnDefinition(
                    "derived",
                    typeof(int),
                    isNullable: true,
                    storeType: "int",
                    isStored: true,
                    computedExpression: AddOne("canonical_id")),
            ],
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_parent", "canonical_parent", ["canonical_id"]),
            checkConstraints:
            [
                ExpectedCheckConstraintDefinition.FromExpression(
                    "ck_parent_id",
                    "canonical_parent",
                    ParentPredicate("canonical_id")),
            ]);

        observedStates.Add(Project(
            projection,
            new EnsureTableIntent(finalParent, SafeMigrationTableMode.StrictDefinition),
            SafeMigrationObservedState.Missing));

        observedStates.Add(Project(
            projection,
            new EnsureForeignKeyIntent(
                new ExpectedForeignKeyDefinition(
                    "fk_child_parent",
                    "child",
                    ["parent_id"],
                    "canonical_parent",
                    ["canonical_id"])),
            SafeMigrationObservedState.Missing));

        // Assert
        Assert.Equal(6, observedStates.Count);
        Assert.All(observedStates,
            analysis => Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState));
    }

    /// <summary>
    /// Rejects historical existing-table shape evidence after a member rename without a complete current shape.
    /// </summary>
    [Fact]
    public void ExistingTableColumnRenameInvalidatesPreBatchStructureEvidence()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        ObserveAccepted(
            projection,
            new RenameColumnIntent("legacy_id", "items", "id"),
            SafeMigrationObservedState.Matching);

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(
                new EnsureColumnIntent("items", Column("id")),
                SafeMigrationPolicy.ThrowIfDifferent),
            Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", analysis.Code);
    }

    /// <summary>
    /// Carries accepted column existence into the following nonunique-index prerequisite instead of retaining stale
    /// absence.
    /// </summary>
    [Fact]
    public void AcceptedMissingColumnProjectsFollowingIndexAsMissing()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        ObserveAccepted(
            projection,
            new EnsureColumnIntent("items", Column("category_id")),
            SafeMigrationObservedState.Missing);

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(
                new EnsureIndexIntent(
                    new ExpectedIndexDefinition(
                        "ix_items_category_id",
                        "items",
                        [new ExpectedIndexKeyDefinition(column: "category_id")])),
                SafeMigrationPolicy.ThrowIfDifferent),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    /// <summary>
    /// Uses an accepted target-column removal to establish the following rename's unoccupied destination.
    /// </summary>
    [Fact]
    public void AcceptedTargetDropProjectsFollowingColumnRenameAsMatching()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        ObserveAccepted(
            projection,
            new DropColumnIntent("legacy", "items"),
            SafeMigrationObservedState.Matching);

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(
                new RenameColumnIntent("replacement", "items", "legacy"),
                SafeMigrationPolicy.ThrowIfDifferent),
            Live(SafeMigrationObservedState.Different));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
    }

    /// <summary>
    /// Rejects historical strict table shape evidence after renaming an incompletely observed existing owner.
    /// </summary>
    [Fact]
    public void ExistingTableRenameInvalidatesPreBatchStructureEvidence()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        ObserveAccepted(
            projection,
            new RenameTableIntent("legacy_items", "items"),
            SafeMigrationObservedState.Matching);

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(
                new EnsureTableIntent(
                    new ExpectedTableDefinition("items", [Column("id")]),
                    SafeMigrationTableMode.StrictDefinition),
                SafeMigrationPolicy.ThrowIfDifferent),
            Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", analysis.Code);
    }

    /// <summary>
    /// Keeps both computed and CHECK SQL opaque instead of textually replacing identifier-like substrings.
    /// </summary>
    [Fact]
    public void ProjectionDoesNotRewriteOpaqueSqlAcrossColumnRename()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var table = new ExpectedTableDefinition(
            "items",
            [
                Column("id"),
                new ExpectedColumnDefinition(
                    "derived",
                    typeof(int),
                    isNullable: true,
                    storeType: "int",
                    computedColumnSql: "id + 1",
                    isStored: true),
            ],
            checkConstraints: [new ExpectedCheckConstraintDefinition("ck_items_id", "items", "id > 0")]);

        Apply(projection, new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition));

        // Act
        Apply(projection, new RenameColumnIntent("id", "items", "canonical_id"));

        var check = Project(
            projection,
            new EnsureCheckConstraintIntent(
                new ExpectedCheckConstraintDefinition("ck_items_id", "items", "canonical_id > 0")),
            SafeMigrationObservedState.Different);

        var column = Project(
            projection,
            new EnsureColumnIntent(
                "items",
                new ExpectedColumnDefinition(
                    "derived",
                    typeof(int),
                    isNullable: true,
                    storeType: "int",
                    computedColumnSql: "canonical_id + 1",
                    isStored: true)),
            SafeMigrationObservedState.Different);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, check.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Different, column.ObservedState);
    }

    private static SafeMigrationSqlExpression AddOne(
        string column
    ) => SafeMigrationSql.Binary(
        SafeMigrationSql.Identifier(column),
        SafeMigrationSqlBinaryOperator.Add,
        SafeMigrationSql.Literal(1));

    private static SafeMigrationSqlExpression Positive(
        string column
    ) => SafeMigrationSql.Binary(
        SafeMigrationSql.Identifier(column),
        SafeMigrationSqlBinaryOperator.GreaterThan,
        SafeMigrationSql.Literal(0));

    private static SafeMigrationSqlExpression ParentPredicate(
        string column
    ) => SafeMigrationSql.Binary(
        Positive(column),
        SafeMigrationSqlBinaryOperator.And,
        SafeMigrationSql.Binary(
            SafeMigrationSql.Identifier("note"),
            SafeMigrationSqlBinaryOperator.Equal,
            SafeMigrationSql.Literal("id\\path and it's id")));

    /// <summary>
    /// Preserves removal and replay lifetimes before a following column repair, member removal and repeated table
    /// drop.
    /// </summary>
    [Fact]
    public void ProjectionAppliesEveryConstraintAndIndexDropSequence()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var table = new ExpectedTableDefinition(
            "items",
            [Column("id"), Column("parent_id")],
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"]),
            uniqueConstraints: [new ExpectedUniqueConstraintDefinition("uq_items_parent", "items", ["parent_id"])],
            checkConstraints: [new ExpectedCheckConstraintDefinition("ck_items_id", "items", "id > 0")],
            foreignKeys:
            [
                new ExpectedForeignKeyDefinition("fk_items_parent", "items", ["parent_id"], "parents", ["id"]),
            ]);

        var index = new ExpectedIndexDefinition(
            "ix_items_parent",
            "items",
            [new ExpectedIndexKeyDefinition(column: "parent_id")]);

        Apply(projection, new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition));
        Apply(projection, new EnsureIndexIntent(index));

        // Act
        var initialKey = Project(projection,
            new EnsurePrimaryKeyIntent(table.PrimaryKey!),
            SafeMigrationObservedState.Missing);

        var differentKey = Project(
            projection,
            new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk_other", "items", ["id"])),
            SafeMigrationObservedState.Different);

        Apply(projection, new DropIndexIntent(index.Name, index.Table));
        Apply(projection, new DropPrimaryKeyIntent("pk_items", "items"));
        Apply(projection, new DropUniqueConstraintIntent("uq_items_parent", "items"));
        Apply(projection, new DropCheckConstraintIntent("ck_items_id", "items"));
        Apply(projection, new DropForeignKeyIntent("fk_items_parent", "items"));

        var removedIndex = Project(projection, new EnsureIndexIntent(index), SafeMigrationObservedState.Different);
        var removedPrimaryKey = Project(
            projection,
            new EnsurePrimaryKeyIntent(table.PrimaryKey!),
            SafeMigrationObservedState.Different);

        var removedUnique = Project(
            projection,
            new EnsureUniqueConstraintIntent(table.UniqueConstraints[0]),
            SafeMigrationObservedState.Different);

        var removedCheck = Project(
            projection,
            new EnsureCheckConstraintIntent(table.CheckConstraints[0]),
            SafeMigrationObservedState.Different);

        var removedForeignKey = Project(
            projection,
            new EnsureForeignKeyIntent(table.ForeignKeys[0]),
            SafeMigrationObservedState.Different);

        Apply(projection, new DropIndexIntent(index.Name, index.Table));
        Apply(projection, new DropPrimaryKeyIntent("pk_items", "items"));
        Apply(projection, new DropUniqueConstraintIntent("uq_items_parent", "items"));
        Apply(projection, new DropCheckConstraintIntent("ck_items_id", "items"));
        Apply(projection, new DropForeignKeyIntent("fk_items_parent", "items"));

        var oldParentColumn = table.Columns[1];
        var targetParentColumn = new ExpectedColumnDefinition(
            oldParentColumn.Name,
            oldParentColumn.ClrType,
            oldParentColumn.IsNullable,
            oldParentColumn.StoreType,
            comment: "canonical");

        var alterIntent = new AlterColumnIntent("items", targetParentColumn, oldParentColumn);
        var alterOperation = new SafeMigrationOperation(alterIntent, SafeMigrationPolicy.RepairIfSafe);
        var alterAnalysis = projection.Project(alterOperation, Live(SafeMigrationObservedState.Missing));
        var alterDecision = SafeMigrationDecisionPlanner.Plan(
            alterIntent.Kind,
            alterAnalysis.ObservedState,
            alterOperation.Policy,
            alterAnalysis.RepairCapability);

        projection.Observe(alterOperation, alterAnalysis, alterAnalysis, alterDecision);
        var repairedColumn = Project(projection,
            new EnsureColumnIntent("items", targetParentColumn),
            SafeMigrationObservedState.Missing);

        Apply(projection, new DropColumnIntent(targetParentColumn.Name, "items"));
        Apply(projection, new DropTableIntent("items"));
        Apply(projection, new DropTableIntent("items"));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, initialKey.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Different, differentKey.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, removedIndex.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, removedPrimaryKey.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, removedUnique.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, removedCheck.ObservedState);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, removedForeignKey.ObservedState);
        Assert.Equal(SafeMigrationAction.Repair, alterDecision.Action);
        Assert.Equal(SafeMigrationObservedState.Matching, repairedColumn.ObservedState);
    }
}
