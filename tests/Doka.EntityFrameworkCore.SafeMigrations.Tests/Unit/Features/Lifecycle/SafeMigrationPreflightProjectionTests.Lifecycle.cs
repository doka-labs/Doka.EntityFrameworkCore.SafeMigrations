namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
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
                    computedColumnSql: "id + 1",
                    isStored: true),
            ],
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_parent", "legacy_parent", ["id"]),
            checkConstraints:
            [
                new ExpectedCheckConstraintDefinition(
                    "ck_parent_id",
                    "legacy_parent",
                    "id > 0 AND note = 'id\\path' AND note = 'it''s id' AND \"id\" > 0 AND `id` > 0"),
            ]);

        var child = new ExpectedTableDefinition(
            "child",
            [
                Column("id"),
                Column("parent_id")
            ],
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
                    filter: "id > 0")));
        Apply(projection, new RenameColumnIntent("id", "legacy_parent", "canonical_id"));

        AssertMatching(projection, new EnsureColumnIntent("legacy_parent", Column("canonical_id")));
        AssertMatching(
            projection,
            new EnsureCheckConstraintIntent(
                new ExpectedCheckConstraintDefinition(
                    "ck_parent_id",
                    "legacy_parent",
                    "canonical_id > 0 AND note = 'id\\path' AND note = 'it''s id' "
                    + "AND \"canonical_id\" > 0 AND `canonical_id` > 0")));
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
                    filter: "canonical_id > 0")));

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
                    computedColumnSql: "canonical_id + 1",
                    isStored: true),
            ],
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_parent", "canonical_parent", ["canonical_id"]),
            checkConstraints:
            [
                new ExpectedCheckConstraintDefinition(
                    "ck_parent_id",
                    "canonical_parent",
                    "canonical_id > 0 AND note = 'id\\path' AND note = 'it''s id' "
                    + "AND \"canonical_id\" > 0 AND `canonical_id` > 0"),
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
    public void ProjectionAppliesEveryConstraintAndIndexDropSequence()
    {
        var projection = new SafeMigrationPreflightProjection();
        var table = new ExpectedTableDefinition(
            "items",
            [
                Column("id"),
                Column("parent_id"),
            ],
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"]),
            uniqueConstraints:
            [
                new ExpectedUniqueConstraintDefinition("uq_items_parent", "items", ["parent_id"]),
            ],
            checkConstraints:
            [
                new ExpectedCheckConstraintDefinition("ck_items_id", "items", "id > 0"),
            ],
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
