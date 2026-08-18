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
                new ExpectedCheckConstraintDefinition("ck_parent_id", "legacy_parent", "id > 0"),
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
                new ExpectedCheckConstraintDefinition("ck_parent_id", "legacy_parent", "canonical_id > 0")));
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
                new ExpectedCheckConstraintDefinition("ck_parent_id", "canonical_parent", "canonical_id > 0"),
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
}
