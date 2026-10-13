namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Checks opt-in physical owner equality without changing schemas or member identifiers.</summary>
public sealed class SafeMigrationExpectedCatalogNativeOwnerTests
{
    /// <summary>A qualified case-insensitive owner retains its standalone index in the terminal inventory.</summary>
    /// <param name="foldTables">Whether the provider has explicitly qualified case-insensitive table identity.</param>
    /// <param name="foreignSchema">Whether the standalone declaration belongs to another physical schema.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void InventoryRetainsOnlyQualifiedPhysicalOwnerDeclarations(bool foldTables, bool foreignSchema)
    {
        // Arrange
        var index = Index("ROWS", foreignSchema ? "foreign" : "selected");
        MigrationOperation[] operations = [Table(), Table("ROWS", foreignSchema ? "foreign" : "selected"), index];
        var comparer = foldTables ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        // Act
        var inventory = SafeMigrationExpectedCatalog.Create(operations,
            static schema => schema == "selected" ? null : schema, comparer);

        // Assert
        var sameOwner = foldTables && !foreignSchema;
        Assert.Equal(sameOwner ? 1 : 2, inventory.Count);
        var authored = Assert.Single(inventory, table => comparer.Equals(table.Table, "rows")
            && table.Schema is null or "selected");
        Assert.Equal(sameOwner, authored.UniqueIndexes.Contains("ux_value"));
        Assert.Equal("ROWS", Assert.IsType<EnsureIndexIntent>(index.Intent).Definition.Table);
    }

    /// <summary>Ordered drops use the same physical owner contract as inventory creation.</summary>
    /// <param name="foldTables">Whether table names identify one native case-insensitive owner.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InventoryRetiresIndexAcrossOnlyQualifiedOwnerAliases(bool foldTables)
    {
        // Arrange
        MigrationOperation[] operations =
        [
            Table(),
            Table("ROWS"),
            Index("ROWS"),
            new SafeMigrationOperation(new DropIndexIntent("ux_value", "rows"),
                SafeMigrationPolicy.ThrowIfDifferent),
        ];

        var comparer = foldTables ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        // Act
        var inventory = SafeMigrationExpectedCatalog.Create(operations, static schema => schema, comparer);

        // Assert
        Assert.Equal(foldTables ? 0 : 1, inventory.Sum(static table => table.UniqueIndexes.Count));
    }

    /// <summary>Dependency validation consumes owner equality but never changes column or schema equality.</summary>
    /// <param name="foldTables">Whether table aliases are physically equivalent.</param>
    /// <param name="sameColumn">Whether the exact declared index member is being dropped.</param>
    /// <param name="sameSchema">Whether both operations belong to the same physical schema.</param>
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void DependencyValidationKeepsMemberAndSchemaBoundaries(bool foldTables, bool sameColumn, bool sameSchema)
    {
        // Arrange
        MigrationOperation[] operations =
        [
            Index("ROWS", "selected"),
            new SafeMigrationOperation(new DropColumnIntent(sameColumn ? "value" : "VALUE", "rows",
                sameSchema ? "selected" : "foreign"), SafeMigrationPolicy.ThrowIfDifferent),
        ];

        var comparer = foldTables ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        // Act
        var failure = Record.Exception(() => SafeMigrationExpectedIndexTransitions.Validate(operations,
            static schema => schema, comparer));

        // Assert
        if (foldTables && sameColumn && sameSchema)
        {
            Assert.IsType<InvalidOperationException>(failure);
        }
        else
        {
            Assert.Null(failure);
        }

    }

    /// <summary>Existing callers retain exact owner identity without requesting a native comparer.</summary>
    [Fact]
    public void ExistingInventoryOverloadRetainsOrdinalOwnerIdentity()
    {
        // Arrange
        MigrationOperation[] operations = [Table(), Table("ROWS"), Index("ROWS")];

        // Act
        var inventory = SafeMigrationExpectedCatalog.Create(operations);

        // Assert
        Assert.Equal(2, inventory.Count);
        Assert.Empty(Assert.Single(inventory, static table => table.Table == "rows").UniqueIndexes);
    }

    /// <summary>Builds the explicit owner without any inferred target-model declarations.</summary>
    /// <param name="table">The explicitly declared table name.</param>
    /// <param name="schema">The explicitly declared qualifier.</param>
    private static SafeMigrationOperation Table(
        string table = "rows",
        string? schema = null
    ) => new(new EnsureTableIntent(new ExpectedTableDefinition(table,
            [new ExpectedColumnDefinition("value", typeof(int), isNullable: true, storeType: "int")], schema: schema),
        SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Builds one independently authored unique index for the requested physical owner.</summary>
    /// <param name="table">The declared physical owner.</param>
    /// <param name="schema">The declared physical qualifier.</param>
    private static SafeMigrationOperation Index(
        string table,
        string? schema = null
    ) => new(new EnsureIndexIntent(
        new ExpectedIndexDefinition("ux_value", table, [new ExpectedIndexKeyDefinition(column: "value")],
            unique: true, schema: schema)), SafeMigrationPolicy.ThrowIfDifferent);
}
