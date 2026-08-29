namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    [Fact]
    public void ProjectionAllowsUniqueIndexAfterNullableColumnConvergence()
    {
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
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.PrerequisiteMissing));

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void ProjectionAllowsNonUniqueIndexAfterRequiredColumnsConverge()
    {
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
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.PrerequisiteMissing));

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    [Fact]
    public void ProjectionCarriesSuccessfulColumnRepairIntoFollowingOperations()
    {
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

        projection.Observe(ensure, ensureAnalysis, ensureDecision);

        var index = new SafeMigrationOperation(
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ix_items_value",
                    "items",
                    [new ExpectedIndexKeyDefinition(column: "value")])),
            SafeMigrationPolicy.ThrowIfDifferent);

        var indexAnalysis = projection.Project(index, Live(SafeMigrationObservedState.PrerequisiteMissing));

        Assert.Equal(SafeMigrationAction.Repair, ensureDecision.Action);
        Assert.Equal(SafeMigrationObservedState.Missing, indexAnalysis.ObservedState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectionAllowsUniqueIndexAfterProvableNullDefault(
        bool structuredDefault
    )
    {
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
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.PrerequisiteMissing));

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ProjectionKeepsUnprovenUniqueIndexPrerequisiteBlocked(
        bool nullable,
        bool hasDefault
    )
    {
        var projection = new SafeMigrationPreflightProjection();
        var key = new ExpectedColumnDefinition(
            "external_id",
            typeof(int),
            nullable,
            "integer",
            defaultValue: hasDefault ? SafeMigrationDefaultValue.Literal(0) : null);

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
        var analysis = projection.Project(operation, live);

        Assert.Same(live, analysis);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
    }

    [Fact]
    public void ProjectionKeepsUnknownIndexColumnPrerequisiteBlocked()
    {
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
        var analysis = projection.Project(operation, live);

        Assert.Same(live, analysis);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ProjectionKeepsComputedOrNullsNotDistinctUniqueIndexBlocked(
        bool computed,
        bool nullsNotDistinct
    )
    {
        var projection = new SafeMigrationPreflightProjection();
        var externalId = new ExpectedColumnDefinition(
            "external_id",
            typeof(int),
            isNullable: true,
            storeType: "integer",
            computedExpression: computed ? SafeMigrationSql.Literal(0) : null);

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
                nullsDistinct: nullsNotDistinct ? false : null));

        var operation = new SafeMigrationOperation(index, SafeMigrationPolicy.ThrowIfDifferent);
        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);
        var analysis = projection.Project(operation, live);

        Assert.Same(live, analysis);
    }

    [Fact]
    public void ProjectionCarriesImmutableDefinitionsAcrossRenameSequences()
    {
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
        Apply(projection, new RenameColumnIntent("id", "legacy_parent", "canonical_id"));

        AssertMatching(projection, new EnsureColumnIntent("legacy_parent", Column("canonical_id")));
        AssertMatching(
            projection,
            new EnsureCheckConstraintIntent(
                ExpectedCheckConstraintDefinition.FromExpression(
                    "ck_parent_id",
                    "legacy_parent",
                    ParentPredicate("canonical_id"))));
        AssertMatching(
            projection,
            new EnsureForeignKeyIntent(
                new ExpectedForeignKeyDefinition(
                    "fk_child_parent",
                    "child",
                    ["parent_id"],
                    "legacy_parent",
                    ["canonical_id"])));

        Apply(projection, new RenameIndexIntent("ix_parent_id", "legacy_parent", "ix_parent_canonical_id"));
        AssertMatching(
            projection,
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ix_parent_canonical_id",
                    "legacy_parent",
                    [new ExpectedIndexKeyDefinition(column: "canonical_id")],
                    structuredFilter: Positive("canonical_id"))));

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

        AssertMatching(projection, new EnsureTableIntent(finalParent, SafeMigrationTableMode.StrictDefinition));
        AssertMatching(
            projection,
            new EnsureForeignKeyIntent(
                new ExpectedForeignKeyDefinition(
                    "fk_child_parent",
                    "child",
                    ["parent_id"],
                    "canonical_parent",
                    ["canonical_id"])));
    }

    [Fact]
    public void ProjectionDoesNotRewriteOpaqueSqlAcrossColumnRename()
    {
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
            checkConstraints: [new ExpectedCheckConstraintDefinition("ck_items_id", "items", "id > 0"),]);

        Apply(projection, new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition));
        Apply(projection, new RenameColumnIntent("id", "items", "canonical_id"));

        AssertProjectedState(
            projection,
            new EnsureCheckConstraintIntent(
                new ExpectedCheckConstraintDefinition("ck_items_id", "items", "canonical_id > 0")),
            SafeMigrationObservedState.Different);
        AssertProjectedState(
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

    [Fact]
    public void ProjectionAppliesEveryConstraintAndIndexDropSequence()
    {
        var projection = new SafeMigrationPreflightProjection();
        var table = new ExpectedTableDefinition(
            "items",
            [Column("id"), Column("parent_id"),],
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"]),
            uniqueConstraints: [new ExpectedUniqueConstraintDefinition("uq_items_parent", "items", ["parent_id"]),],
            checkConstraints: [new ExpectedCheckConstraintDefinition("ck_items_id", "items", "id > 0"),],
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
        AssertMatching(projection, new EnsurePrimaryKeyIntent(table.PrimaryKey!));
        AssertProjectedState(
            projection,
            new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk_other", "items", ["id"])),
            SafeMigrationObservedState.Different);

        Apply(projection, new DropIndexIntent(index.Name, index.Table));
        Apply(projection, new DropPrimaryKeyIntent("pk_items", "items"));
        Apply(projection, new DropUniqueConstraintIntent("uq_items_parent", "items"));
        Apply(projection, new DropCheckConstraintIntent("ck_items_id", "items"));
        Apply(projection, new DropForeignKeyIntent("fk_items_parent", "items"));

        AssertProjectedState(projection, new EnsureIndexIntent(index), SafeMigrationObservedState.Missing);
        AssertProjectedState(
            projection,
            new EnsurePrimaryKeyIntent(table.PrimaryKey!),
            SafeMigrationObservedState.Missing);
        AssertProjectedState(
            projection,
            new EnsureUniqueConstraintIntent(table.UniqueConstraints[0]),
            SafeMigrationObservedState.Missing);
        AssertProjectedState(
            projection,
            new EnsureCheckConstraintIntent(table.CheckConstraints[0]),
            SafeMigrationObservedState.Missing);
        AssertProjectedState(
            projection,
            new EnsureForeignKeyIntent(table.ForeignKeys[0]),
            SafeMigrationObservedState.Missing);

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

        Assert.Equal(SafeMigrationAction.Repair, alterDecision.Action);

        projection.Observe(alterOperation, alterAnalysis, alterDecision);
        AssertMatching(projection, new EnsureColumnIntent("items", targetParentColumn));
        Apply(projection, new DropColumnIntent(targetParentColumn.Name, "items"));
        Apply(projection, new DropTableIntent("items"));
        Apply(projection, new DropTableIntent("items"));
    }

    private static void AssertProjectedState(
        SafeMigrationPreflightProjection projection,
        SafeMigrationIntent intent,
        SafeMigrationObservedState expected
    )
    {
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Different));

        Assert.Equal(expected, analysis.ObservedState);
    }
}
