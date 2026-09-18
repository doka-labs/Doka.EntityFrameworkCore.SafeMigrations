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

    [Fact]
    public void Catalog_ColumnDropWithDirectIndexDependencyFailsClosed()
    {
        // Arrange
        var index = new ExpectedIndexDefinition(
            "ix_items_legacy",
            "items",
            [new ExpectedIndexKeyDefinition(column: "legacy")]);
        var operations = CreateColumnDropOperations(index);

        // Act
        var exception = Record.Exception(() => SafeMigrationExpectedCatalog.Create(operations));

        // Assert
        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("index 'ix_items_legacy' may depend on it", invalidOperation.Message, StringComparison.Ordinal);
        Assert.Contains("Drop the index explicitly", invalidOperation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_ColumnDropWithIncludedColumnDependencyFailsClosed()
    {
        // Arrange
        var index = new ExpectedIndexDefinition(
            "ix_items_id",
            "items",
            [new ExpectedIndexKeyDefinition(column: "id")],
            includedColumns: ["legacy"]);
        var operations = CreateColumnDropOperations(index);

        // Act
        var exception = Record.Exception(() => SafeMigrationExpectedCatalog.Create(operations));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void Catalog_ColumnDropWithStructuredExpressionDependencyFailsClosed()
    {
        // Arrange
        var index = new ExpectedIndexDefinition(
            "ix_items_expression",
            "items",
            [new ExpectedIndexKeyDefinition(structuredExpression: SafeMigrationSql.Identifier("legacy"))]);
        var operations = CreateColumnDropOperations(index);

        // Act
        var exception = Record.Exception(() => SafeMigrationExpectedCatalog.Create(operations));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void Catalog_ColumnDropWithOpaqueExpressionFailsClosedWhenDependencyIsUnknown()
    {
        // Arrange
        var index = new ExpectedIndexDefinition(
            "ix_items_expression",
            "items",
            [new ExpectedIndexKeyDefinition(expression: "lower(id)")]);
        var operations = CreateColumnDropOperations(index);

        // Act
        var exception = Record.Exception(() => SafeMigrationExpectedCatalog.Create(operations));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void Catalog_ColumnDropWithStructuredFilterDependencyFailsClosed()
    {
        // Arrange
        var index = new ExpectedIndexDefinition(
            "ix_items_filtered",
            "items",
            [new ExpectedIndexKeyDefinition(column: "id")],
            structuredFilter: SafeMigrationSql.Binary(
                SafeMigrationSql.Identifier("legacy"),
                SafeMigrationSqlBinaryOperator.GreaterThan,
                SafeMigrationSql.Literal(0)));
        var operations = CreateColumnDropOperations(index);

        // Act
        var exception = Record.Exception(() => SafeMigrationExpectedCatalog.Create(operations));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void Catalog_ColumnDropWithOpaqueFilterFailsClosedWhenDependencyIsUnknown()
    {
        // Arrange
        var index = new ExpectedIndexDefinition(
            "ix_items_filtered",
            "items",
            [new ExpectedIndexKeyDefinition(column: "id")],
            filter: "id > 0");
        var operations = CreateColumnDropOperations(index);

        // Act
        var exception = Record.Exception(() => SafeMigrationExpectedCatalog.Create(operations));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void Catalog_ColumnDropAfterExplicitIndexDropRemovesOnlyTheColumn()
    {
        // Arrange
        var index = new ExpectedIndexDefinition(
            "ix_items_legacy",
            "items",
            [new ExpectedIndexKeyDefinition(column: "legacy")]);
        var operations = CreateColumnDropOperations(index, dropIndexFirst: true);

        // Act
        var inventory = Assert.Single(SafeMigrationExpectedCatalog.Create(operations));

        // Assert
        Assert.Equal(["id"], inventory.Columns);
        Assert.Empty(inventory.Indexes);
        Assert.Empty(inventory.IndexDefinitions);
    }

    [Fact]
    public void Catalog_ColumnDropPreservesUnrelatedStructuredIndex()
    {
        // Arrange
        var index = new ExpectedIndexDefinition(
            "ix_items_id",
            "items",
            [new ExpectedIndexKeyDefinition(column: "id")],
            structuredFilter: SafeMigrationSql.Binary(
                SafeMigrationSql.Identifier("id"),
                SafeMigrationSqlBinaryOperator.GreaterThan,
                SafeMigrationSql.Literal(0)));
        var operations = CreateColumnDropOperations(index);

        // Act
        var inventory = Assert.Single(SafeMigrationExpectedCatalog.Create(operations));

        // Assert
        Assert.Equal(["id"], inventory.Columns);
        Assert.Equal("ix_items_id", Assert.Single(inventory.Indexes));
        Assert.Equal("ix_items_id", Assert.Single(inventory.IndexDefinitions).Key);
    }

    [Fact]
    public void Catalog_ColumnDropWithoutOwnedTableStillRejectsKnownIndexDependency()
    {
        // Arrange
        IReadOnlyList<MigrationOperation> operations =
        [
            Envelope(
                new EnsureIndexIntent(
                    new ExpectedIndexDefinition(
                        "ix_items_legacy",
                        "items",
                        [new ExpectedIndexKeyDefinition(column: "legacy")]))),
            Envelope(new DropColumnIntent("legacy", "items")),
        ];

        // Act
        var exception = Record.Exception(() => SafeMigrationExpectedCatalog.Create(operations));

        // Assert
        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("index 'ix_items_legacy' may depend on it", invalidOperation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_ColumnDropWithoutOwnedTableAcceptsAnExplicitEarlierIndexDrop()
    {
        // Arrange
        IReadOnlyList<MigrationOperation> operations =
        [
            Envelope(
                new EnsureIndexIntent(
                    new ExpectedIndexDefinition(
                        "ix_items_legacy",
                        "items",
                        [new ExpectedIndexKeyDefinition(column: "legacy")]))),
            Envelope(new DropIndexIntent("ix_items_legacy", "items")),
            Envelope(new DropColumnIntent("legacy", "items")),
        ];

        // Act
        var exception = Record.Exception(() => SafeMigrationExpectedCatalog.Create(operations));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Catalog_ColumnAndTableRenamesPreserveKnownIndexDependencies()
    {
        // Arrange
        IReadOnlyList<MigrationOperation> operations =
        [
            Envelope(
                new EnsureIndexIntent(
                    new ExpectedIndexDefinition(
                        "ix_legacy_items_code",
                        "legacy_items",
                        [new ExpectedIndexKeyDefinition(column: "legacy_code")],
                        includedColumns: ["legacy_caption"]))),
            Envelope(new RenameColumnIntent("legacy_code", "legacy_items", "code")),
            Envelope(new RenameColumnIntent("legacy_caption", "legacy_items", "caption")),
            Envelope(new RenameTableIntent("legacy_items", newName: "items")),
            Envelope(new DropColumnIntent("caption", "items")),
        ];

        // Act
        var exception = Record.Exception(() => SafeMigrationExpectedCatalog.Create(operations));

        // Assert
        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("index 'ix_legacy_items_code' may depend on it", invalidOperation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_ColumnRenameWithOpaqueIndexExpressionFailsClosed()
    {
        // Arrange
        IReadOnlyList<MigrationOperation> operations =
        [
            Envelope(
                new EnsureIndexIntent(
                    new ExpectedIndexDefinition(
                        "ix_items_expression",
                        "items",
                        [new ExpectedIndexKeyDefinition(expression: "lower(legacy)")]))),
            Envelope(new RenameColumnIntent("legacy", "items", "caption")),
        ];

        // Act
        var exception = Record.Exception(() => SafeMigrationExpectedCatalog.Create(operations));

        // Assert
        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("may depend on it through an expression", invalidOperation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_ColumnRenamePreservesAnUnrelatedStructuredIndex()
    {
        // Arrange
        IReadOnlyList<MigrationOperation> operations =
        [
            Envelope(
                new EnsureIndexIntent(
                    new ExpectedIndexDefinition(
                        "ix_items_expression",
                        "items",
                        [new ExpectedIndexKeyDefinition(
                            structuredExpression: SafeMigrationSql.Function(
                                "lower",
                                SafeMigrationSql.Identifier("caption")))]))),
            Envelope(new RenameColumnIntent("legacy", "items", "name")),
        ];

        // Act
        var exception = Record.Exception(() => SafeMigrationExpectedCatalog.Create(operations));

        // Assert
        Assert.Null(exception);
    }

    private static List<MigrationOperation> CreateColumnDropOperations(
        ExpectedIndexDefinition index,
        bool dropIndexFirst = false
    )
    {
        var operations = new List<MigrationOperation>
        {
            Envelope(
                new EnsureTableIntent(
                    new ExpectedTableDefinition(
                        "items",
                        [
                            new ExpectedColumnDefinition("id", typeof(int), false),
                            new ExpectedColumnDefinition("legacy", typeof(int), true),
                        ]),
                    SafeMigrationTableMode.StrictDefinition)),
            Envelope(new EnsureIndexIntent(index)),
        };

        if (dropIndexFirst)
        {
            operations.Add(Envelope(new DropIndexIntent(index.Name, index.Table, index.Schema)));
        }

        operations.Add(Envelope(new DropColumnIntent("legacy", "items")));

        return operations;
    }
}
