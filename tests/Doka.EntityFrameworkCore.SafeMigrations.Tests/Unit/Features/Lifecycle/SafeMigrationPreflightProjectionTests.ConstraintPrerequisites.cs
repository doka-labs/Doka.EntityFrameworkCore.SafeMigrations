namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>
    /// Combines verified parent keys with NULL-preserving new child members without rereading stale row evidence.
    /// </summary>
    [Fact]
    public void ForeignKeyUsesAcceptedColumnAndKeyPrerequisitesOnExistingTables()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var parentId = Column("id");
        var childId = Column("id");
        var parentReference = new ExpectedColumnDefinition(
            "parent_id",
            typeof(int),
            isNullable: true,
            storeType: "int");

        ObserveAccepted(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition("parents", [parentId]),
                SafeMigrationTableMode.ConvergenceContainer),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new EnsureColumnIntent("parents", parentId),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new EnsurePrimaryKeyIntent(
                new ExpectedPrimaryKeyDefinition("pk_parents", "parents", ["id"])),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition("children", [childId, parentReference]),
                SafeMigrationTableMode.ConvergenceContainer),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new EnsureColumnIntent("children", childId),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new EnsureColumnIntent("children", parentReference),
            SafeMigrationObservedState.Missing);

        var operation = new SafeMigrationOperation(
            new EnsureForeignKeyIntent(
                new ExpectedForeignKeyDefinition(
                    "fk_children_parents",
                    "children",
                    ["parent_id"],
                    "parents",
                    ["id"])),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(
            operation,
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Uses the empty-table proof required by a missing nonnullable member to approve its following primary key.
    /// </summary>
    [Fact]
    public void PrimaryKeyUsesEmptyTableProofFromRequiredColumnConvergence()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var id = Column("id");

        ObserveAccepted(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition("items", [id]),
                SafeMigrationTableMode.ConvergenceContainer),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new EnsureColumnIntent("items", id),
            SafeMigrationObservedState.Missing);

        // Act
        var analysis = Project(
            projection,
            new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"])),
            SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Approves an ordinary unique key when a new nullable member contributes only nonconflicting NULL values.
    /// </summary>
    [Fact]
    public void UniqueConstraintUsesNullPreservingColumnConvergence()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var id = Column("id");
        var externalId = NullableColumn("external_id");

        ObserveColumnsOnExistingTable(projection, "items", id, externalId);

        // Act
        var analysis = Project(
            projection,
            new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("uq_items_external_id", "items", ["external_id"])),
            SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Carries a proven empty table across required-column convergence into the following structured CHECK.
    /// </summary>
    [Fact]
    public void CheckConstraintUsesEmptyTableProofFromRequiredColumnConvergence()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var id = Column("id");

        ObserveAccepted(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition("items", [id]),
                SafeMigrationTableMode.ConvergenceContainer),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new EnsureColumnIntent("items", id),
            SafeMigrationObservedState.Missing);

        // Act
        var analysis = Project(
            projection,
            new EnsureCheckConstraintIntent(
                ExpectedCheckConstraintDefinition.FromExpression(
                    "ck_items_id",
                    "items",
                    Positive("id"))),
            SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Accepts a composite FK only when the verified parent candidate key preserves its declared endpoint order.
    /// </summary>
    [Fact]
    public void CompositeForeignKeyUsesAcceptedUniqueConstraintInDeclaredOrder()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var parentTenant = Column("tenant_id");
        var parentId = Column("id");
        var childId = Column("id");
        var childTenant = Column("tenant_id");
        var childParent = NullableColumn("parent_id");

        ObserveColumnsOnExistingTable(projection, "parents", parentTenant, parentId);
        ObserveAccepted(
            projection,
            new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition(
                    "uq_parents_tenant_id_id",
                    "parents",
                    ["tenant_id", "id"])),
            SafeMigrationObservedState.Matching);

        ObserveColumnsOnExistingTable(projection, "children", childId, childTenant, childParent);

        // Act
        var analysis = Project(
            projection,
            ForeignKey(
                "fk_children_parents",
                ["tenant_id", "parent_id"],
                ["tenant_id", "id"]),
            SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    /// <summary>
    /// Retains the original prerequisite rejection when the parent key reverses the FK endpoint order.
    /// </summary>
    [Fact]
    public void CompositeForeignKeyWithReversedPrincipalKeyOrderRemainsBlocked()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var parentTenant = Column("tenant_id");
        var parentId = Column("id");
        var childId = Column("id");
        var childTenant = Column("tenant_id");
        var childParent = NullableColumn("parent_id");

        ObserveColumnsOnExistingTable(projection, "parents", parentTenant, parentId);
        ObserveAccepted(
            projection,
            new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition(
                    "uq_parents_id_tenant_id",
                    "parents",
                    ["id", "tenant_id"])),
            SafeMigrationObservedState.Matching);

        ObserveColumnsOnExistingTable(projection, "children", childId, childTenant, childParent);

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(
                ForeignKey(
                    "fk_children_parents",
                    ["tenant_id", "parent_id"],
                    ["tenant_id", "id"]),
                SafeMigrationPolicy.ThrowIfDifferent),
            live);

        // Assert
        Assert.Same(live, analysis);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
    }

    /// <summary>
    /// Allows the missing FK only while required-child-column convergence proves that no dependent rows exist.
    /// </summary>
    [Fact]
    public void ForeignKeyUsesEmptyDependentTableProofForRequiredColumn()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var parentId = Column("id");
        var childId = Column("id");
        var parentReference = Column("parent_id");

        ObserveParentWithPrimaryKey(projection, parentId);
        ObserveAccepted(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition("children", [childId, parentReference]),
                SafeMigrationTableMode.ConvergenceContainer),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new EnsureColumnIntent("children", childId),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new EnsureColumnIntent("children", parentReference),
            SafeMigrationObservedState.Missing);

        // Act
        var analysis = Project(
            projection,
            ForeignKey(),
            SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Does not invent a parent candidate key from column existence or a nullable child value domain.
    /// </summary>
    [Fact]
    public void ForeignKeyWithoutAcceptedPrincipalKeyRemainsBlocked()
    {
        // Arrange
        var projection = ProjectionWithNullableForeignKeyColumn();

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(ForeignKey(), SafeMigrationPolicy.ThrowIfDifferent),
            live);

        // Assert
        Assert.Same(live, analysis);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
    }

    /// <summary>
    /// Keeps the native prerequisite rejection when child bigint storage differs from the verified int parent.
    /// </summary>
    [Fact]
    public void ForeignKeyWithIncompatibleProjectedStorageRemainsBlocked()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var parentId = Column("id");
        var childId = Column("id");
        var parentReference = new ExpectedColumnDefinition(
            "parent_id",
            typeof(long),
            isNullable: true,
            storeType: "bigint");

        ObserveParentWithPrimaryKey(projection, parentId);
        ObserveColumnsOnExistingTable(projection, "children", childId, parentReference);

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(ForeignKey(), SafeMigrationPolicy.ThrowIfDifferent),
            live);

        // Assert
        Assert.Same(live, analysis);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
    }

    /// <summary>
    /// Does not treat a nullable declaration as an all-NULL proof when its new column has a non-NULL default.
    /// </summary>
    [Fact]
    public void ForeignKeyWithNonNullDefaultOnNewColumnRemainsBlocked()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var parentId = Column("id");
        var childId = Column("id");
        var parentReference = new ExpectedColumnDefinition(
            "parent_id",
            typeof(int),
            isNullable: true,
            storeType: "int",
            defaultValue: SafeMigrationDefaultValue.Literal(0));

        ObserveParentWithPrimaryKey(projection, parentId);
        ObserveColumnsOnExistingTable(projection, "children", childId, parentReference);

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(ForeignKey(), SafeMigrationPolicy.ThrowIfDifferent),
            live);

        // Assert
        Assert.Same(live, analysis);
    }

    /// <summary>
    /// Invalidates previously inferred child-row safety across an intervening provider-owned data mutation.
    /// </summary>
    [Fact]
    public void ProviderDataMutationInvalidatesProjectedForeignKeyRowProof()
    {
        // Arrange
        var projection = ProjectionWithNullableForeignKeyColumn();
        var parentKey = new EnsurePrimaryKeyIntent(
            new ExpectedPrimaryKeyDefinition("pk_parents", "parents", ["id"]));

        ObserveAccepted(projection, parentKey, SafeMigrationObservedState.Matching);

        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "audit",
                Columns = ["id"],
                Values = new object?[,] { { 1 } },
            });

        // Act
        var analysis = Project(
            projection,
            ForeignKey(),
            SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_data_state_unknown", analysis.Code);
    }

    /// <summary>
    /// Recognizes an accepted FK shape under another authored name without scheduling a second physical object.
    /// </summary>
    [Fact]
    public void AcceptedForeignKeySemanticAliasProjectsAsMatching()
    {
        // Arrange
        var projection = ProjectionWithNullableForeignKeyColumn();
        var parentKey = new EnsurePrimaryKeyIntent(
            new ExpectedPrimaryKeyDefinition("pk_parents", "parents", ["id"]));

        ObserveAccepted(projection, parentKey, SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, ForeignKey(), SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var analysis = Project(
            projection,
            ForeignKey("fk_children_parents_alias"),
            SafeMigrationObservedState.Matching);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.True(analysis.PostconditionSatisfied);
    }

    /// <summary>
    /// Keeps referential actions in FK identity even after the same endpoints have already been accepted.
    /// </summary>
    [Fact]
    public void AcceptedForeignKeyWithChangedReferentialActionProjectsAsDifferent()
    {
        // Arrange
        var projection = ProjectionWithNullableForeignKeyColumn();
        var parentKey = new EnsurePrimaryKeyIntent(
            new ExpectedPrimaryKeyDefinition("pk_parents", "parents", ["id"]));

        ObserveAccepted(projection, parentKey, SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, ForeignKey(), SafeMigrationObservedState.PrerequisiteMissing);

        var changed = new EnsureForeignKeyIntent(
            new ExpectedForeignKeyDefinition(
                "fk_children_parents",
                "children",
                ["parent_id"],
                "parents",
                ["id"],
                onDelete: ReferentialAction.Cascade));

        // Act
        var analysis = Project(
            projection,
            changed,
            SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
    }

    /// <summary>Recognizes the accepted typed CHECK predicate independently from its authored semantic alias.</summary>
    [Fact]
    public void AcceptedStructuredCheckConstraintSemanticAliasProjectsAsMatching()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var id = Column("id");
        var constraint = ExpectedCheckConstraintDefinition.FromExpression(
            "ck_items_id",
            "items",
            Positive("id"));

        ObserveColumnsOnExistingTable(projection, "items", id);
        ObserveAccepted(
            projection,
            new EnsureCheckConstraintIntent(constraint),
            SafeMigrationObservedState.Matching);

        var alias = ExpectedCheckConstraintDefinition.FromExpression(
            "ck_items_id_alias",
            "items",
            Positive("id"));

        // Act
        var analysis = Project(
            projection,
            new EnsureCheckConstraintIntent(alias),
            SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.True(analysis.PostconditionSatisfied);
    }

    /// <summary>Retains the candidate on code when a separate physical unique key on id is removed.</summary>
    [Fact]
    public void DroppingUnrelatedUniqueConstraintPreservesForeignKeyCandidateKey()
    {
        // Arrange
        var projection = ProjectionWithUniqueForeignKeyCandidate();

        ObserveAccepted(
            projection,
            new DropUniqueConstraintIntent("uq_parents_id", "parents"),
            SafeMigrationObservedState.Matching);

        // Act
        var analysis = Project(
            projection,
            ForeignKey(
                columns: ["parent_id"],
                principalColumns: ["code"]),
            SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    /// <summary>
    /// Retains independently verified candidate-key lifetime after dropping another equal physical shape.
    /// </summary>
    [Fact]
    public void DroppingOneOfTwoPhysicalUniqueConstraintsPreservesTheRemainingCandidateKey()
    {
        // Arrange
        var projection = ProjectionWithUniqueForeignKeyCandidate();

        ObserveAccepted(
            projection,
            new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("uq_parents_code_alias", "parents", ["code"])),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new DropUniqueConstraintIntent("uq_parents_code", "parents"),
            SafeMigrationObservedState.Matching);

        // Act
        var analysis = Project(
            projection,
            ForeignKey(
                columns: ["parent_id"],
                principalColumns: ["code"]),
            SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    /// <summary>
    /// Invalidates every alias bound to the removed physical candidate rather than treating names as new lifetimes.
    /// </summary>
    [Fact]
    public void DroppingPhysicalUniqueConstraintInvalidatesItsSemanticAlias()
    {
        // Arrange
        var projection = ProjectionWithUniqueForeignKeyCandidate();

        ObserveAccepted(
            projection,
            new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("uq_parents_code_alias", "parents", ["code"])),
            SafeMigrationObservedState.Matching,
            "uq_parents_code");

        ObserveAccepted(
            projection,
            new DropUniqueConstraintIntent("uq_parents_code", "parents"),
            SafeMigrationObservedState.Matching);

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(
                ForeignKey(
                    columns: ["parent_id"],
                    principalColumns: ["code"]),
                SafeMigrationPolicy.ThrowIfDifferent),
            live);

        // Assert
        Assert.Same(live, analysis);
    }

    /// <summary>
    /// Projects a removed physical unique key as missing even when the old catalog still reports its alias matching.
    /// </summary>
    [Fact]
    public void DroppedUniqueConstraintOverridesStaleMatchingAnalysisForItsAlias()
    {
        // Arrange
        var projection = ProjectionWithUniqueForeignKeyCandidate();
        var alias = new EnsureUniqueConstraintIntent(
            new ExpectedUniqueConstraintDefinition("uq_parents_code_alias", "parents", ["code"]));

        ObserveAccepted(
            projection,
            alias,
            SafeMigrationObservedState.Matching,
            "uq_parents_code");

        ObserveAccepted(
            projection,
            new DropUniqueConstraintIntent("uq_parents_code", "parents"),
            SafeMigrationObservedState.Matching);

        // Act
        var analysis = Project(
            projection,
            alias,
            SafeMigrationObservedState.Matching);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Prevents stale native Matching evidence from reviving an explicitly dropped primary-key lifetime.
    /// </summary>
    /// <param name="replacementName">The exact physical name or authored semantic alias of the replacement key.</param>
    [Theory]
    [InlineData("pk_items")]
    [InlineData("pk_items_alias")]
    public void DroppedPrimaryKeyOverridesStaleMatchingAnalysis(
        string replacementName
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var physical = new EnsurePrimaryKeyIntent(
            new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"]));

        ObserveColumnsOnExistingTable(projection, "items", Column("id"));
        ObserveAccepted(projection, physical, SafeMigrationObservedState.Matching);
        ObserveAccepted(
            projection,
            new DropPrimaryKeyIntent("pk_items", "items"),
            SafeMigrationObservedState.Matching);

        // Act
        var analysis = Project(
            projection,
            new EnsurePrimaryKeyIntent(
                new ExpectedPrimaryKeyDefinition(replacementName, "items", ["id"])),
            SafeMigrationObservedState.Matching);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Makes the accepted exact-name unique-key removal authoritative over the earlier native catalog result.
    /// </summary>
    [Fact]
    public void DroppedUniqueConstraintOverridesStaleMatchingAnalysisForItsExactName()
    {
        // Arrange
        var projection = ProjectionWithUniqueForeignKeyCandidate();
        var definition = new EnsureUniqueConstraintIntent(
            new ExpectedUniqueConstraintDefinition("uq_parents_code", "parents", ["code"]));

        ObserveAccepted(
            projection,
            new DropUniqueConstraintIntent("uq_parents_code", "parents"),
            SafeMigrationObservedState.Matching);

        var live = Live(SafeMigrationObservedState.Matching, "uq_parents_code");

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(definition, SafeMigrationPolicy.ThrowIfDifferent),
            live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Makes an accepted CHECK removal authoritative while retaining its structured predicate identity.
    /// </summary>
    [Fact]
    public void DroppedCheckConstraintOverridesStaleMatchingAnalysisForItsExactName()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var definition = new EnsureCheckConstraintIntent(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_items_id",
                "items",
                Positive("id")));

        ObserveColumnsOnExistingTable(projection, "items", Column("id"));
        ObserveAccepted(projection, definition, SafeMigrationObservedState.Matching);
        ObserveAccepted(
            projection,
            new DropCheckConstraintIntent("ck_items_id", "items"),
            SafeMigrationObservedState.Matching);

        var live = Live(SafeMigrationObservedState.Matching, "ck_items_id");

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(definition, SafeMigrationPolicy.ThrowIfDifferent),
            live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>Does not reuse native Matching evidence for an FK whose exact physical lifetime was removed.</summary>
    [Fact]
    public void DroppedForeignKeyOverridesStaleMatchingAnalysisForItsExactName()
    {
        // Arrange
        var projection = ProjectionWithNullableForeignKeyColumn();
        var definition = ForeignKey();

        ObserveAccepted(
            projection,
            new EnsurePrimaryKeyIntent(
                new ExpectedPrimaryKeyDefinition("pk_parents", "parents", ["id"])),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(projection, definition, SafeMigrationObservedState.Matching);
        ObserveAccepted(
            projection,
            new DropForeignKeyIntent("fk_children_parents", "children"),
            SafeMigrationObservedState.Matching);

        var live = Live(SafeMigrationObservedState.Matching, "fk_children_parents");

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(definition, SafeMigrationPolicy.ThrowIfDifferent),
            live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Projects the exact dropped index as missing instead of replaying a stale matching index snapshot.
    /// </summary>
    [Fact]
    public void DroppedIndexOverridesStaleMatchingAnalysisForItsExactName()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var definition = new EnsureIndexIntent(
            new ExpectedIndexDefinition(
                "ix_items_external_id",
                "items",
                [new ExpectedIndexKeyDefinition(column: "external_id")]));

        ObserveColumnsOnExistingTable(projection, "items", Column("id"), NullableColumn("external_id"));
        ObserveAccepted(projection, definition, SafeMigrationObservedState.Matching);
        ObserveAccepted(
            projection,
            new DropIndexIntent("ix_items_external_id", "items"),
            SafeMigrationObservedState.Matching);

        var live = Live(SafeMigrationObservedState.Matching, "ix_items_external_id");

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(definition, SafeMigrationPolicy.ThrowIfDifferent),
            live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Rejects unique-key replacement after provider DML consumes the earlier post-drop row-safety proof.
    /// </summary>
    [Fact]
    public void DataMutationAfterDropInvalidatesUniqueConstraintReplacementProof()
    {
        // Arrange
        var projection = ProjectionWithUniqueForeignKeyCandidate();
        var definition = new EnsureUniqueConstraintIntent(
            new ExpectedUniqueConstraintDefinition("uq_parents_code", "parents", ["code"]));

        ObserveAccepted(
            projection,
            new DropUniqueConstraintIntent("uq_parents_code", "parents"),
            SafeMigrationObservedState.Matching);

        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "audit",
                Columns = ["id"],
                Values = new object?[,] { { 1 } },
            });

        var live = Live(SafeMigrationObservedState.Matching, "uq_parents_code");

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(definition, SafeMigrationPolicy.ThrowIfDifferent),
            live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_data_state_unknown", analysis.Code);
    }

    /// <summary>Retains a primary-key removal even without an earlier complete table or key projection.</summary>
    [Fact]
    public void StandalonePrimaryKeyDropOverridesStaleMatchingAnalysis()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var definition = new EnsurePrimaryKeyIntent(
            new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"]));

        ObserveAccepted(
            projection,
            new DropPrimaryKeyIntent("pk_items", "items"),
            SafeMigrationObservedState.Matching);

        // Act
        var analysis = Project(
            projection,
            definition,
            SafeMigrationObservedState.Matching);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>Uses a verified physical native alias to resolve a standalone unique-key removal precisely.</summary>
    [Fact]
    public void StandaloneUniqueConstraintDropOverridesStaleSemanticAliasAnalysis()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var definition = new EnsureUniqueConstraintIntent(
            new ExpectedUniqueConstraintDefinition("uq_items_code_alias", "items", ["code"]));

        ObserveAccepted(
            projection,
            new DropUniqueConstraintIntent("uq_items_code", "items"),
            SafeMigrationObservedState.Matching);

        var live = Live(SafeMigrationObservedState.Matching, "uq_items_code");

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(definition, SafeMigrationPolicy.ThrowIfDifferent),
            live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Rejects an unresolved unique-key alias rather than assigning it a guessed physical removal identity.
    /// </summary>
    [Fact]
    public void UnresolvedConstraintDropMakesAmbiguousLiveAliasFailClosed()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var definition = new EnsureUniqueConstraintIntent(
            new ExpectedUniqueConstraintDefinition("uq_items_code_alias", "items", ["code"]));

        ObserveAccepted(
            projection,
            new DropUniqueConstraintIntent("uq_items_code", "items"),
            SafeMigrationObservedState.Matching);

        // Act
        var analysis = Project(
            projection,
            definition,
            SafeMigrationObservedState.Matching);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", analysis.Code);
    }

    /// <summary>Rejects the old FK replacement proof after its parent candidate key has been invalidated.</summary>
    [Fact]
    public void PrincipalKeyMutationMakesStaleForeignKeyReplacementFailClosed()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var definition = ForeignKey();

        ObserveAccepted(
            projection,
            new DropPrimaryKeyIntent("pk_parents", "parents"),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new DropForeignKeyIntent("fk_children_parents", "children"),
            SafeMigrationObservedState.Matching);

        var live = Live(SafeMigrationObservedState.Matching, "fk_children_parents");

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(definition, SafeMigrationPolicy.ThrowIfDifferent),
            live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", analysis.Code);
    }

    /// <summary>Allows FK recreation only after the parent candidate key is separately accepted again.</summary>
    [Fact]
    public void RecreatedPrincipalKeyAuthorizesStaleForeignKeyReplacement()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var primaryKey = new EnsurePrimaryKeyIntent(
            new ExpectedPrimaryKeyDefinition("pk_parents", "parents", ["id"]));

        var foreignKey = ForeignKey();

        ObserveAccepted(
            projection,
            new DropPrimaryKeyIntent("pk_parents", "parents"),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(projection, primaryKey, SafeMigrationObservedState.Matching);
        ObserveAccepted(
            projection,
            new DropForeignKeyIntent("fk_children_parents", "children"),
            SafeMigrationObservedState.Matching);

        var live = Live(SafeMigrationObservedState.Matching, "fk_children_parents");

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(foreignKey, SafeMigrationPolicy.ThrowIfDifferent),
            live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Recognizes an index recreated under its semantic alias as the one surviving physical lifetime.
    /// </summary>
    [Fact]
    public void RecreatedSemanticIndexPreventsASecondPhysicalCreation()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var physical = Index("ix_items_code");
        var alias = Index("ix_items_code_alias");

        ObserveColumnsOnExistingTable(projection, "items", Column("code"));
        ObserveAccepted(projection, physical, SafeMigrationObservedState.Matching);
        ObserveAccepted(
            projection,
            new DropIndexIntent("ix_items_code", "items"),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(projection, alias, SafeMigrationObservedState.Missing);

        // Act
        var analysis = Project(
            projection,
            physical,
            SafeMigrationObservedState.Matching);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.True(analysis.PostconditionSatisfied);
    }

    /// <summary>
    /// Rejects a historical index result after an unbound rename whose final physical identity is unknown.
    /// </summary>
    [Fact]
    public void UnresolvedIndexRenameMakesLaterLiveStateFailClosed()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        ObserveAccepted(
            projection,
            new RenameIndexIntent("ix_items_code", "items", "ix_items_code_renamed"),
            SafeMigrationObservedState.Matching);

        // Act
        var analysis = Project(
            projection,
            Index("ix_items_code"),
            SafeMigrationObservedState.Matching);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", analysis.Code);
    }

    /// <summary>Does not turn an unbound semantic NoOp into the physical parent candidate required by an FK.</summary>
    [Fact]
    public void UnresolvedUniqueConstraintNoOpDoesNotAuthorizeForeignKey()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        ObserveColumnsOnExistingTable(projection, "parents", Column("id"), Column("code"));
        ObserveAccepted(
            projection,
            new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("uq_parents_code_alias", "parents", ["code"])),
            SafeMigrationObservedState.Matching,
            matchedObjectName: null);

        ObserveColumnsOnExistingTable(projection, "children", Column("id"), NullableColumn("parent_id"));

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(
                ForeignKey(
                    columns: ["parent_id"],
                    principalColumns: ["code"]),
                SafeMigrationPolicy.ThrowIfDifferent),
            live);

        // Assert
        Assert.Same(live, analysis);
    }

    /// <summary>
    /// Invalidates the earlier semantic CHECK alias when its separately verified physical object is removed.
    /// </summary>
    [Fact]
    public void DroppingPhysicalCheckConstraintInvalidatesItsSemanticAlias()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var physical = ExpectedCheckConstraintDefinition.FromExpression(
            "ck_items_id",
            "items",
            Positive("id"));

        ObserveColumnsOnExistingTable(projection, "items", Column("id"));
        ObserveAccepted(
            projection,
            new EnsureCheckConstraintIntent(physical),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new EnsureCheckConstraintIntent(
                ExpectedCheckConstraintDefinition.FromExpression(
                    "ck_items_id_alias",
                    "items",
                    Positive("id"))),
            SafeMigrationObservedState.Matching,
            "ck_items_id");

        ObserveAccepted(
            projection,
            new DropCheckConstraintIntent("ck_items_id", "items"),
            SafeMigrationObservedState.Matching);

        var live = Live(SafeMigrationObservedState.Matching, "ck_items_id");

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(
                new EnsureCheckConstraintIntent(
                    ExpectedCheckConstraintDefinition.FromExpression(
                        "ck_items_id_alias",
                        "items",
                        Positive("id"))),
                SafeMigrationPolicy.ThrowIfDifferent),
            live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>Invalidates the earlier semantic FK alias when the bound physical FK lifetime is dropped.</summary>
    [Fact]
    public void DroppingPhysicalForeignKeyInvalidatesItsSemanticAlias()
    {
        // Arrange
        var projection = ProjectionWithNullableForeignKeyColumn();

        ObserveAccepted(
            projection,
            new EnsurePrimaryKeyIntent(
                new ExpectedPrimaryKeyDefinition("pk_parents", "parents", ["id"])),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            ForeignKey(),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            ForeignKey("fk_children_parents_alias"),
            SafeMigrationObservedState.Matching,
            "fk_children_parents");

        ObserveAccepted(
            projection,
            new DropForeignKeyIntent("fk_children_parents", "children"),
            SafeMigrationObservedState.Matching);

        // Act
        var analysis = Project(
            projection,
            ForeignKey("fk_children_parents_alias"),
            SafeMigrationObservedState.Matching);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    /// <summary>Invalidates the earlier semantic index alias when its bound physical object is dropped.</summary>
    [Fact]
    public void DroppingPhysicalIndexInvalidatesItsSemanticAlias()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var physical = new EnsureIndexIntent(
            new ExpectedIndexDefinition(
                "ix_items_external_id",
                "items",
                [new ExpectedIndexKeyDefinition(column: "external_id")]));

        ObserveColumnsOnExistingTable(projection, "items", Column("id"), NullableColumn("external_id"));
        ObserveAccepted(projection, physical, SafeMigrationObservedState.Matching);
        ObserveAccepted(
            projection,
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ix_items_external_id_alias",
                    "items",
                    [new ExpectedIndexKeyDefinition(column: "external_id")])),
            SafeMigrationObservedState.Matching,
            "ix_items_external_id");

        ObserveAccepted(
            projection,
            new DropIndexIntent("ix_items_external_id", "items"),
            SafeMigrationObservedState.Matching);

        // Act
        var analysis = Project(
            projection,
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ix_items_external_id_alias",
                    "items",
                    [new ExpectedIndexKeyDefinition(column: "external_id")])),
            SafeMigrationObservedState.Matching);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    /// <summary>Carries exact physical index lifetime through rename before invalidating all aliases on drop.</summary>
    [Fact]
    public void RenameThenDropOfPhysicalIndexInvalidatesEarlierSemanticAlias()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var definition = new ExpectedIndexDefinition(
            "ix_items_external_id",
            "items",
            [new ExpectedIndexKeyDefinition(column: "external_id")]);

        ObserveColumnsOnExistingTable(projection, "items", Column("id"), NullableColumn("external_id"));
        ObserveAccepted(
            projection,
            new EnsureIndexIntent(definition),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ix_items_external_id_alias",
                    "items",
                    definition.Keys)),
            SafeMigrationObservedState.Matching,
            "ix_items_external_id");

        ObserveAccepted(
            projection,
            new RenameIndexIntent(
                "ix_items_external_id",
                "items",
                "ix_items_external_id_renamed"),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new DropIndexIntent("ix_items_external_id_renamed", "items"),
            SafeMigrationObservedState.Matching);

        // Act
        var analysis = Project(
            projection,
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ix_items_external_id_alias",
                    "items",
                    definition.Keys)),
            SafeMigrationObservedState.Matching);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    /// <summary>Preserves the native prerequisite rejection after removing the FK target candidate key.</summary>
    [Fact]
    public void DroppingForeignKeyCandidateKeyInvalidatesProjection()
    {
        // Arrange
        var projection = ProjectionWithUniqueForeignKeyCandidate();

        ObserveAccepted(
            projection,
            new DropUniqueConstraintIntent("uq_parents_code", "parents"),
            SafeMigrationObservedState.Matching);

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(
                ForeignKey(
                    columns: ["parent_id"],
                    principalColumns: ["code"]),
                SafeMigrationPolicy.ThrowIfDifferent),
            live);

        // Assert
        Assert.Same(live, analysis);
    }

    /// <summary>
    /// Treats an accepted index shape as converged under an equivalent authored alias without a second creation.
    /// </summary>
    [Fact]
    public void AcceptedIndexSemanticAliasProjectsAsMatching()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var id = Column("id");
        var externalId = NullableColumn("external_id");
        var index = new EnsureIndexIntent(
            new ExpectedIndexDefinition(
                "ix_items_external_id",
                "items",
                [new ExpectedIndexKeyDefinition(column: "external_id")]));

        ObserveColumnsOnExistingTable(projection, "items", id, externalId);
        ObserveAccepted(projection, index, SafeMigrationObservedState.PrerequisiteMissing);

        var alias = new EnsureIndexIntent(
            new ExpectedIndexDefinition(
                "ix_items_external_id_alias",
                "items",
                [new ExpectedIndexKeyDefinition(column: "external_id")]));

        // Act
        var analysis = Project(
            projection,
            alias,
            SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.True(analysis.PostconditionSatisfied);
    }

    private static SafeMigrationPreflightProjection ProjectionWithNullableForeignKeyColumn()
    {
        var projection = new SafeMigrationPreflightProjection();

        ObserveColumnsOnExistingTable(projection, "parents", Column("id"));
        ObserveColumnsOnExistingTable(projection, "children", Column("id"), NullableColumn("parent_id"));

        return projection;
    }

    private static SafeMigrationPreflightProjection ProjectionWithUniqueForeignKeyCandidate()
    {
        var projection = new SafeMigrationPreflightProjection();

        ObserveColumnsOnExistingTable(projection, "parents", Column("id"), Column("code"));
        ObserveAccepted(
            projection,
            new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("uq_parents_id", "parents", ["id"])),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(
            projection,
            new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("uq_parents_code", "parents", ["code"])),
            SafeMigrationObservedState.Matching);

        ObserveColumnsOnExistingTable(projection, "children", Column("id"), NullableColumn("parent_id"));

        return projection;
    }

    private static void ObserveParentWithPrimaryKey(
        SafeMigrationPreflightProjection projection,
        ExpectedColumnDefinition parentId
    )
    {
        ObserveColumnsOnExistingTable(projection, "parents", parentId);
        ObserveAccepted(
            projection,
            new EnsurePrimaryKeyIntent(
                new ExpectedPrimaryKeyDefinition("pk_parents", "parents", [parentId.Name])),
            SafeMigrationObservedState.Matching);
    }

    private static void ObserveColumnsOnExistingTable(
        SafeMigrationPreflightProjection projection,
        string table,
        params ExpectedColumnDefinition[] columns
    )
    {
        ObserveAccepted(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition(table, columns),
                SafeMigrationTableMode.ConvergenceContainer),
            SafeMigrationObservedState.Matching);

        foreach (var column in columns)
        {
            ObserveAccepted(
                projection,
                new EnsureColumnIntent(table, column),
                column.IsNullable
                    ? SafeMigrationObservedState.Missing
                    : SafeMigrationObservedState.Matching);
        }
    }

    private static SafeMigrationProviderAnalysis Project(
        SafeMigrationPreflightProjection projection,
        SafeMigrationIntent intent,
        SafeMigrationObservedState liveState
    ) => projection.Project(
        new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent),
        Live(liveState));

    private static EnsureForeignKeyIntent ForeignKey(
        string name = "fk_children_parents",
        IReadOnlyList<string>? columns = null,
        IReadOnlyList<string>? principalColumns = null
    ) => new(
        new ExpectedForeignKeyDefinition(
            name,
            "children",
            columns ?? ["parent_id"],
            "parents",
            principalColumns ?? ["id"]));

    private static EnsureIndexIntent Index(
        string name
    ) => new(
        new ExpectedIndexDefinition(
            name,
            "items",
            [new ExpectedIndexKeyDefinition(column: "code")]));

    private static ExpectedColumnDefinition NullableColumn(
        string name
    ) => new(name, typeof(int), isNullable: true, storeType: "int");
}
