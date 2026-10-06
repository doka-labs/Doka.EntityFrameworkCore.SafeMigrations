namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Checks bounded declaration provenance without inferring complete table ownership.</summary>
public sealed class MySqlBackfillConstraintCatalogTests
{
    /// <summary>Shares the immutable empty catalog when no relevant backfill exists.</summary>
    [Fact]
    public void Create_ReusesEmptyCatalogWithoutRelevantBackfill()
    {
        // Arrange
        var unrelated = new SafeMigrationOperation(new EnsureUniqueConstraintIntent(
            new ExpectedUniqueConstraintDefinition("uq_value", "rows", ["value"])),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var empty = MySqlBackfillConstraintCatalog.Create([]);
        var declarationsOnly = MySqlBackfillConstraintCatalog.Create([unrelated]);

        // Assert
        Assert.Same(empty, declarationsOnly);
        Assert.False(declarationsOnly.RequiresNullFreeRows(unrelated));
    }

    /// <summary>Retains aliases and transient declarations while excluding unrelated tables and columns.</summary>
    [Fact]
    public void Create_RetainsAliasesAndLaterDropWithoutUnrelatedHazards()
    {
        // Arrange
        var altered = Alter("renamed_rows", "renamed_value");
        var otherColumn = Alter("renamed_rows", "other_value");
        var otherTable = Alter("other_rows", "renamed_value");
        SafeMigrationOperation[] operations =
        [
            new(new EnsureUniqueConstraintIntent(new ExpectedUniqueConstraintDefinition(
                "uq_value", "rows", ["value"], schema: "selected")), SafeMigrationPolicy.ThrowIfDifferent),
            new(new RenameColumnIntent("value", "rows", "renamed_value"), SafeMigrationPolicy.ThrowIfDifferent),
            new(new RenameTableIntent("rows", "renamed_rows"), SafeMigrationPolicy.ThrowIfDifferent),
            altered,
            otherColumn,
            otherTable,
            new(new DropUniqueConstraintIntent("uq_value", "renamed_rows"), SafeMigrationPolicy.ThrowIfDifferent),
        ];

        // Act
        var catalog = MySqlBackfillConstraintCatalog.Create(operations, "selected");

        // Assert
        Assert.True(catalog.RequiresNullFreeRows(altered));
        Assert.False(catalog.RequiresNullFreeRows(otherColumn));
        Assert.False(catalog.RequiresNullFreeRows(otherTable));
    }

    /// <summary>Matches column spelling case-insensitively without changing table identity.</summary>
    [Fact]
    public void Create_MatchesColumnCaseButPreservesTableCase()
    {
        // Arrange
        var altered = Alter("rows", "value");
        var otherTable = Alter("ROWS", "value");
        SafeMigrationOperation[] operations =
        [
            new(new EnsureUniqueConstraintIntent(new ExpectedUniqueConstraintDefinition(
                "uq_value", "rows", ["VALUE"])), SafeMigrationPolicy.ThrowIfDifferent),
            altered,
            otherTable,
        ];

        // Act
        var catalog = MySqlBackfillConstraintCatalog.Create(operations);

        // Assert
        Assert.True(catalog.RequiresNullFreeRows(altered));
        Assert.False(catalog.RequiresNullFreeRows(otherTable));
    }

    /// <summary>Rebinds only exact operation identities and rejects a mismatched correspondence.</summary>
    [Fact]
    public void Rebind_RetainsOnlyOriginalHazardAndChecksOrdinalCount()
    {
        // Arrange
        var original = Alter("rows", "value");
        var rebound = Alter("physical_rows", "value");
        var different = Alter("rows", "value");
        var catalog = MySqlBackfillConstraintCatalog.Create(
        [
            new(new EnsureUniqueConstraintIntent(new ExpectedUniqueConstraintDefinition(
                "uq_value", "rows", ["value"])), SafeMigrationPolicy.ThrowIfDifferent),
            original,
        ]);

        // Act
        var transferred = catalog.Rebind([original], [rebound]);
        var omitted = catalog.Rebind([different], [rebound]);
        var failure = Record.Exception(() => catalog.Rebind([original], []));

        // Assert
        Assert.True(transferred.RequiresNullFreeRows(rebound));
        Assert.False(transferred.RequiresNullFreeRows(original));
        Assert.False(omitted.RequiresNullFreeRows(rebound));
        Assert.IsType<ArgumentException>(failure);
    }

    /// <summary>Handles a large repeated stream with reference-identity deduplication.</summary>
    [Fact]
    public void Create_DeduplicatesRepeatedBackfillReferences()
    {
        // Arrange
        var operation = Alter("rows", "value");
        var operations = Enumerable.Repeat(operation, 100_000).ToArray();
        operations[0] = new SafeMigrationOperation(new EnsureUniqueConstraintIntent(
            new ExpectedUniqueConstraintDefinition("uq_value", "rows", ["value"])),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var catalog = MySqlBackfillConstraintCatalog.Create(operations);

        // Assert
        Assert.True(catalog.RequiresNullFreeRows(operation));
        Assert.False(catalog.RequiresNullFreeRows(operations[0]));
    }

    private static SafeMigrationOperation Alter(
        string table,
        string column
    ) => new(new AlterColumnIntent(table,
        new ExpectedColumnDefinition(column, typeof(string), false, "varchar(20)",
            defaultValue: SafeMigrationDefaultValue.Literal("x")),
        new ExpectedColumnDefinition(column, typeof(string), true, "varchar(10)")),
        SafeMigrationPolicy.RepairIfSafe);
}
