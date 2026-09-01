namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationExpectedCatalogTests
{
    [Fact]
    public void Catalog_FoldsGranularOperationsIntoFinalOwnedShape()
    {
        var table = new ExpectedTableDefinition(
            "items",
            [new ExpectedColumnDefinition("id", typeof(int), false)],
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"]),
            uniqueConstraints: [new ExpectedUniqueConstraintDefinition("uq_items_id", "items", ["id"]),]);

        IReadOnlyList<MigrationOperation> operations =
        [
            Envelope(new EnsureTableIntent(table, SafeMigrationTableMode.ConvergenceContainer)),
            Envelope(
                new EnsureColumnIntent("items", new ExpectedColumnDefinition("old_name", typeof(string), true))),
            Envelope(new RenameColumnIntent("old_name", "items", "name")),
            Envelope(
                new EnsureIndexIntent(
                    new ExpectedIndexDefinition(
                        "ix_old",
                        "items",
                        [new ExpectedIndexKeyDefinition(column: "name")],
                        unique: true))),
            Envelope(new RenameIndexIntent("ix_old", "items", "ix_name")),
            Envelope(
                new EnsureIndexIntent(
                    new ExpectedIndexDefinition(
                        "ix_non_unique",
                        "items",
                        [new ExpectedIndexKeyDefinition(column: "id")]))),
            Envelope(
                new EnsureIndexIntent(
                    new ExpectedIndexDefinition(
                        "ux_removed",
                        "items",
                        [new ExpectedIndexKeyDefinition(column: "id")],
                        unique: true))),
            Envelope(new DropIndexIntent("ux_removed", "items")),
            Envelope(new DropUniqueConstraintIntent("uq_items_id", "items")),
        ];

        var inventory = Assert.Single(SafeMigrationExpectedCatalog.Create(operations));

        Assert.Equal(["id", "name"], inventory.Columns.Order());
        Assert.Null(inventory.ColumnStoreTypes["id"]);
        Assert.Null(inventory.ColumnStoreTypes["name"]);
        Assert.Equal(["ix_name", "ix_non_unique"], inventory.Indexes.Order());
        Assert.Equal(["ix_name"], inventory.UniqueIndexes);
        Assert.Equal(
            SafeMigrationDatabaseObjectKind.PrimaryKey,
            Assert.Single(inventory.Constraints)
                .Value);
    }

    [Fact]
    public void Catalog_PreservesColumnStoreTypeAcrossRenameForProviderInventoryRules()
    {
        IReadOnlyList<MigrationOperation> operations =
        [
            Envelope(
                new EnsureTableIntent(
                    new ExpectedTableDefinition(
                        "documents",
                        [new ExpectedColumnDefinition("legacy_payload", typeof(string), false, "json")]),
                    SafeMigrationTableMode.StrictDefinition)),
            Envelope(new RenameColumnIntent("legacy_payload", "documents", "payload")),
        ];

        var inventory = Assert.Single(SafeMigrationExpectedCatalog.Create(operations));

        Assert.False(inventory.ColumnStoreTypes.ContainsKey("legacy_payload"));
        Assert.Equal("json", inventory.ColumnStoreTypes["payload"]);
    }

    [Fact]
    public void Catalog_AppliesTableSchemaAndDropTransitions()
    {
        var table = new ExpectedTableDefinition(
            "old_items",
            [new ExpectedColumnDefinition("id", typeof(int), false)],
            schema: "legacy");

        IReadOnlyList<MigrationOperation> operations =
        [
            Envelope(new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition)),
            Envelope(new RenameTableIntent("old_items", newName: "items", schema: "legacy", newSchema: "app")),
            Envelope(new DropSchemaIntent("app")),
        ];

        Assert.Empty(SafeMigrationExpectedCatalog.Create(operations));
    }
}
