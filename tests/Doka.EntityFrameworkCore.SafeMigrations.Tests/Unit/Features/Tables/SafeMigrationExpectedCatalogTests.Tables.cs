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
        Assert.Equal(["ix_name", "ix_non_unique"], inventory.IndexDefinitions.Keys.Order());
        Assert.Equal(
            "name",
            inventory.IndexDefinitions["ix_name"].Keys[0].Column);
        Assert.Equal(
            SafeMigrationDatabaseObjectKind.PrimaryKey,
            Assert.Single(inventory.Constraints)
                .Value);
    }

    [Fact]
    public void Catalog_ProjectsIndexDefinitionsAcrossColumnAndTableRenames()
    {
        IReadOnlyList<MigrationOperation> operations =
        [
            Envelope(
                new EnsureTableIntent(
                    new ExpectedTableDefinition(
                        "legacy_items",
                        [new ExpectedColumnDefinition("legacy_code", typeof(string), true)],
                        schema: "legacy"),
                    SafeMigrationTableMode.StrictDefinition)),
            Envelope(
                new EnsureIndexIntent(
                    new ExpectedIndexDefinition(
                        "ux_items_code",
                        "legacy_items",
                        [new ExpectedIndexKeyDefinition(column: "legacy_code", prefixLength: 32)],
                        schema: "legacy",
                        unique: true))),
            Envelope(new RenameColumnIntent("legacy_code", "legacy_items", "code", "legacy")),
            Envelope(
                new RenameTableIntent(
                    "legacy_items",
                    newName: "items",
                    schema: "legacy",
                    newSchema: "app")),
        ];

        var inventory = Assert.Single(SafeMigrationExpectedCatalog.Create(operations));
        var index = Assert.Single(inventory.IndexDefinitions.Values);

        Assert.Equal("items", index.Table);
        Assert.Equal("app", index.Schema);
        Assert.Equal("code", Assert.Single(index.Keys).Column);
        Assert.Equal(32, index.Keys[0].PrefixLength);
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
