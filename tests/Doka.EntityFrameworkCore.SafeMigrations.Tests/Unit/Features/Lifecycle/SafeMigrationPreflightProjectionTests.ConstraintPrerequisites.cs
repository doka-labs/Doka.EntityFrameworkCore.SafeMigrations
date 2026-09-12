namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    [Fact]
    public void ForeignKeyUsesAcceptedColumnAndKeyPrerequisitesOnExistingTables()
    {
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

        var analysis = projection.Project(
            operation,
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void PrimaryKeyUsesEmptyTableProofFromRequiredColumnConvergence()
    {
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

        var analysis = Project(
            projection,
            new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"])),
            SafeMigrationObservedState.PrerequisiteMissing);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void UniqueConstraintUsesNullPreservingColumnConvergence()
    {
        var projection = new SafeMigrationPreflightProjection();
        var id = Column("id");
        var externalId = NullableColumn("external_id");

        ObserveColumnsOnExistingTable(projection, "items", id, externalId);

        var analysis = Project(
            projection,
            new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("uq_items_external_id", "items", ["external_id"])),
            SafeMigrationObservedState.PrerequisiteMissing);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void CheckConstraintUsesEmptyTableProofFromRequiredColumnConvergence()
    {
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

        var analysis = Project(
            projection,
            new EnsureCheckConstraintIntent(
                ExpectedCheckConstraintDefinition.FromExpression(
                    "ck_items_id",
                    "items",
                    Positive("id"))),
            SafeMigrationObservedState.PrerequisiteMissing);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void CompositeForeignKeyUsesAcceptedUniqueConstraintInDeclaredOrder()
    {
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

        var analysis = Project(
            projection,
            ForeignKey(
                "fk_children_parents",
                ["tenant_id", "parent_id"],
                ["tenant_id", "id"]),
            SafeMigrationObservedState.PrerequisiteMissing);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    [Fact]
    public void CompositeForeignKeyWithReversedPrincipalKeyOrderRemainsBlocked()
    {
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
        var analysis = projection.Project(
            new SafeMigrationOperation(
                ForeignKey(
                    "fk_children_parents",
                    ["tenant_id", "parent_id"],
                    ["tenant_id", "id"]),
                SafeMigrationPolicy.ThrowIfDifferent),
            live);

        Assert.Same(live, analysis);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
    }

    [Fact]
    public void ForeignKeyUsesEmptyDependentTableProofForRequiredColumn()
    {
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

        var analysis = Project(
            projection,
            ForeignKey(),
            SafeMigrationObservedState.PrerequisiteMissing);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void ForeignKeyWithoutAcceptedPrincipalKeyRemainsBlocked()
    {
        var projection = ProjectionWithNullableForeignKeyColumn();

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);
        var analysis = projection.Project(
            new SafeMigrationOperation(ForeignKey(), SafeMigrationPolicy.ThrowIfDifferent),
            live);

        Assert.Same(live, analysis);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
    }

    [Fact]
    public void ForeignKeyWithIncompatibleProjectedStorageRemainsBlocked()
    {
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
        var analysis = projection.Project(
            new SafeMigrationOperation(ForeignKey(), SafeMigrationPolicy.ThrowIfDifferent),
            live);

        Assert.Same(live, analysis);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
    }

    [Fact]
    public void ForeignKeyWithNonNullDefaultOnNewColumnRemainsBlocked()
    {
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
        var analysis = projection.Project(
            new SafeMigrationOperation(ForeignKey(), SafeMigrationPolicy.ThrowIfDifferent),
            live);

        Assert.Same(live, analysis);
    }

    [Fact]
    public void ProviderDataMutationInvalidatesProjectedForeignKeyRowProof()
    {
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

        var analysis = Project(
            projection,
            ForeignKey(),
            SafeMigrationObservedState.PrerequisiteMissing);

        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_data_state_unknown", analysis.Code);
    }

    [Fact]
    public void AcceptedForeignKeySemanticAliasProjectsAsMatching()
    {
        var projection = ProjectionWithNullableForeignKeyColumn();
        var parentKey = new EnsurePrimaryKeyIntent(
            new ExpectedPrimaryKeyDefinition("pk_parents", "parents", ["id"]));

        ObserveAccepted(projection, parentKey, SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, ForeignKey(), SafeMigrationObservedState.PrerequisiteMissing);

        var analysis = Project(
            projection,
            ForeignKey("fk_children_parents_alias"),
            SafeMigrationObservedState.Matching);

        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.True(analysis.PostconditionSatisfied);
    }

    [Fact]
    public void AcceptedForeignKeyWithChangedReferentialActionProjectsAsDifferent()
    {
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

        var analysis = Project(
            projection,
            changed,
            SafeMigrationObservedState.PrerequisiteMissing);

        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
    }

    [Fact]
    public void AcceptedStructuredCheckConstraintSemanticAliasProjectsAsMatching()
    {
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

        var analysis = Project(
            projection,
            new EnsureCheckConstraintIntent(alias),
            SafeMigrationObservedState.PrerequisiteMissing);

        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.True(analysis.PostconditionSatisfied);
    }

    [Fact]
    public void DroppingUnrelatedUniqueConstraintPreservesForeignKeyCandidateKey()
    {
        var projection = ProjectionWithUniqueForeignKeyCandidate();

        ObserveAccepted(
            projection,
            new DropUniqueConstraintIntent("uq_parents_id", "parents"),
            SafeMigrationObservedState.Matching);

        var analysis = Project(
            projection,
            ForeignKey(
                columns: ["parent_id"],
                principalColumns: ["code"]),
            SafeMigrationObservedState.PrerequisiteMissing);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    [Fact]
    public void DroppingOneOfTwoPhysicalUniqueConstraintsPreservesTheRemainingCandidateKey()
    {
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

        var analysis = Project(
            projection,
            ForeignKey(
                columns: ["parent_id"],
                principalColumns: ["code"]),
            SafeMigrationObservedState.PrerequisiteMissing);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    [Fact]
    public void DroppingPhysicalUniqueConstraintInvalidatesItsSemanticAlias()
    {
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
        var analysis = projection.Project(
            new SafeMigrationOperation(
                ForeignKey(
                    columns: ["parent_id"],
                    principalColumns: ["code"]),
                SafeMigrationPolicy.ThrowIfDifferent),
            live);

        Assert.Same(live, analysis);
    }

    [Fact]
    public void DroppedUniqueConstraintOverridesStaleMatchingAnalysisForItsAlias()
    {
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

        var analysis = Project(
            projection,
            alias,
            SafeMigrationObservedState.Matching);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Theory]
    [InlineData("pk_items")]
    [InlineData("pk_items_alias")]
    public void DroppedPrimaryKeyOverridesStaleMatchingAnalysis(
        string replacementName
    )
    {
        var projection = new SafeMigrationPreflightProjection();
        var physical = new EnsurePrimaryKeyIntent(
            new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"]));

        ObserveColumnsOnExistingTable(projection, "items", Column("id"));
        ObserveAccepted(projection, physical, SafeMigrationObservedState.Matching);
        ObserveAccepted(
            projection,
            new DropPrimaryKeyIntent("pk_items", "items"),
            SafeMigrationObservedState.Matching);

        var analysis = Project(
            projection,
            new EnsurePrimaryKeyIntent(
                new ExpectedPrimaryKeyDefinition(replacementName, "items", ["id"])),
            SafeMigrationObservedState.Matching);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void DroppedUniqueConstraintOverridesStaleMatchingAnalysisForItsExactName()
    {
        var projection = ProjectionWithUniqueForeignKeyCandidate();
        var definition = new EnsureUniqueConstraintIntent(
            new ExpectedUniqueConstraintDefinition("uq_parents_code", "parents", ["code"]));

        ObserveAccepted(
            projection,
            new DropUniqueConstraintIntent("uq_parents_code", "parents"),
            SafeMigrationObservedState.Matching);

        var live = Live(SafeMigrationObservedState.Matching, "uq_parents_code");
        var analysis = projection.Project(
            new SafeMigrationOperation(definition, SafeMigrationPolicy.ThrowIfDifferent),
            live);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void DroppedCheckConstraintOverridesStaleMatchingAnalysisForItsExactName()
    {
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
        var analysis = projection.Project(
            new SafeMigrationOperation(definition, SafeMigrationPolicy.ThrowIfDifferent),
            live);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void DroppedForeignKeyOverridesStaleMatchingAnalysisForItsExactName()
    {
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
        var analysis = projection.Project(
            new SafeMigrationOperation(definition, SafeMigrationPolicy.ThrowIfDifferent),
            live);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void DroppedIndexOverridesStaleMatchingAnalysisForItsExactName()
    {
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
        var analysis = projection.Project(
            new SafeMigrationOperation(definition, SafeMigrationPolicy.ThrowIfDifferent),
            live);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void DataMutationAfterDropInvalidatesUniqueConstraintReplacementProof()
    {
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
        var analysis = projection.Project(
            new SafeMigrationOperation(definition, SafeMigrationPolicy.ThrowIfDifferent),
            live);

        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_data_state_unknown", analysis.Code);
    }

    [Fact]
    public void StandalonePrimaryKeyDropOverridesStaleMatchingAnalysis()
    {
        var projection = new SafeMigrationPreflightProjection();
        var definition = new EnsurePrimaryKeyIntent(
            new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"]));

        ObserveAccepted(
            projection,
            new DropPrimaryKeyIntent("pk_items", "items"),
            SafeMigrationObservedState.Matching);

        var analysis = Project(
            projection,
            definition,
            SafeMigrationObservedState.Matching);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void StandaloneUniqueConstraintDropOverridesStaleSemanticAliasAnalysis()
    {
        var projection = new SafeMigrationPreflightProjection();
        var definition = new EnsureUniqueConstraintIntent(
            new ExpectedUniqueConstraintDefinition("uq_items_code_alias", "items", ["code"]));

        ObserveAccepted(
            projection,
            new DropUniqueConstraintIntent("uq_items_code", "items"),
            SafeMigrationObservedState.Matching);

        var live = Live(SafeMigrationObservedState.Matching, "uq_items_code");
        var analysis = projection.Project(
            new SafeMigrationOperation(definition, SafeMigrationPolicy.ThrowIfDifferent),
            live);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void UnresolvedConstraintDropMakesAmbiguousLiveAliasFailClosed()
    {
        var projection = new SafeMigrationPreflightProjection();
        var definition = new EnsureUniqueConstraintIntent(
            new ExpectedUniqueConstraintDefinition("uq_items_code_alias", "items", ["code"]));

        ObserveAccepted(
            projection,
            new DropUniqueConstraintIntent("uq_items_code", "items"),
            SafeMigrationObservedState.Matching);

        var analysis = Project(
            projection,
            definition,
            SafeMigrationObservedState.Matching);

        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", analysis.Code);
    }

    [Fact]
    public void PrincipalKeyMutationMakesStaleForeignKeyReplacementFailClosed()
    {
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
        var analysis = projection.Project(
            new SafeMigrationOperation(definition, SafeMigrationPolicy.ThrowIfDifferent),
            live);

        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", analysis.Code);
    }

    [Fact]
    public void RecreatedPrincipalKeyAuthorizesStaleForeignKeyReplacement()
    {
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
        var analysis = projection.Project(
            new SafeMigrationOperation(foreignKey, SafeMigrationPolicy.ThrowIfDifferent),
            live);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void RecreatedSemanticIndexPreventsASecondPhysicalCreation()
    {
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

        var analysis = Project(
            projection,
            physical,
            SafeMigrationObservedState.Matching);

        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.True(analysis.PostconditionSatisfied);
    }

    [Fact]
    public void UnresolvedIndexRenameMakesLaterLiveStateFailClosed()
    {
        var projection = new SafeMigrationPreflightProjection();

        ObserveAccepted(
            projection,
            new RenameIndexIntent("ix_items_code", "items", "ix_items_code_renamed"),
            SafeMigrationObservedState.Matching);

        var analysis = Project(
            projection,
            Index("ix_items_code"),
            SafeMigrationObservedState.Matching);

        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", analysis.Code);
    }

    [Fact]
    public void UnresolvedUniqueConstraintNoOpDoesNotAuthorizeForeignKey()
    {
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
        var analysis = projection.Project(
            new SafeMigrationOperation(
                ForeignKey(
                    columns: ["parent_id"],
                    principalColumns: ["code"]),
                SafeMigrationPolicy.ThrowIfDifferent),
            live);

        Assert.Same(live, analysis);
    }

    [Fact]
    public void DroppingPhysicalCheckConstraintInvalidatesItsSemanticAlias()
    {
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
        var analysis = projection.Project(
            new SafeMigrationOperation(
                new EnsureCheckConstraintIntent(
                    ExpectedCheckConstraintDefinition.FromExpression(
                        "ck_items_id_alias",
                        "items",
                        Positive("id"))),
                SafeMigrationPolicy.ThrowIfDifferent),
            live);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void DroppingPhysicalForeignKeyInvalidatesItsSemanticAlias()
    {
        var projection = ProjectionWithNullableForeignKeyColumn();

        ObserveAccepted(
            projection,
            new EnsurePrimaryKeyIntent(
                new ExpectedPrimaryKeyDefinition("pk_parents", "parents", ["id"])),
            SafeMigrationObservedState.Matching);
        ObserveAccepted(
            projection,
            ForeignKey("fk_children_parents"),
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

        var analysis = Project(
            projection,
            ForeignKey("fk_children_parents_alias"),
            SafeMigrationObservedState.Matching);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    [Fact]
    public void DroppingPhysicalIndexInvalidatesItsSemanticAlias()
    {
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

        var analysis = Project(
            projection,
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ix_items_external_id_alias",
                    "items",
                    [new ExpectedIndexKeyDefinition(column: "external_id")])),
            SafeMigrationObservedState.Matching);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    [Fact]
    public void RenameThenDropOfPhysicalIndexInvalidatesEarlierSemanticAlias()
    {
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

        var analysis = Project(
            projection,
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ix_items_external_id_alias",
                    "items",
                    definition.Keys)),
            SafeMigrationObservedState.Matching);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    [Fact]
    public void DroppingForeignKeyCandidateKeyInvalidatesProjection()
    {
        var projection = ProjectionWithUniqueForeignKeyCandidate();

        ObserveAccepted(
            projection,
            new DropUniqueConstraintIntent("uq_parents_code", "parents"),
            SafeMigrationObservedState.Matching);

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);
        var analysis = projection.Project(
            new SafeMigrationOperation(
                ForeignKey(
                    columns: ["parent_id"],
                    principalColumns: ["code"]),
                SafeMigrationPolicy.ThrowIfDifferent),
            live);

        Assert.Same(live, analysis);
    }

    [Fact]
    public void AcceptedIndexSemanticAliasProjectsAsMatching()
    {
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

        var analysis = Project(
            projection,
            alias,
            SafeMigrationObservedState.PrerequisiteMissing);

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
