namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationDefinitionTests
{
    /// <summary>A key definition snapshots caller-owned member order before that collection is mutated.</summary>
    [Fact]
    public void DefinitionsSnapshotMutableInputs()
    {
        // Arrange
        var columns = new List<string>
        {
            "tenant_id",
            "id",
        };

        // Act
        var key = new ExpectedPrimaryKeyDefinition("pk_items", "items", columns);
        columns[0] = "changed";

        // Assert
        Assert.Equal(["tenant_id", "id"], key.Columns);
    }

    /// <summary>Binary defaults isolate both their original input and every returned buffer.</summary>
    [Fact]
    public void BinaryDefaultSnapshotsInputAndEveryRead()
    {
        // Arrange
        var bytes = new byte[] { 1, 2, 3 };

        // Act
        var defaultValue = SafeMigrationDefaultValue.Literal(bytes);
        bytes[0] = 9;
        var firstRead = defaultValue.LiteralValue;
        if (firstRead is byte[] buffer)
        {
            buffer[1] = 9;
        }

        var secondRead = defaultValue.LiteralValue;

        // Assert
        Assert.IsType<byte[]>(firstRead);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(secondRead));
    }

    /// <summary>Each invalid definition is independently rejected at its own constructor boundary.</summary>
    /// <param name="scenario">The single invalid definition contract exercised by this case.</param>
    /// <param name="exceptionType">The exact documented rejection type.</param>
    [Theory]
    [InlineData("index-key-role", typeof(ArgumentException))]
    [InlineData("generated-default", typeof(ArgumentException))]
    [InlineData("foreign-key-arity", typeof(ArgumentException))]
    [InlineData("duplicate-column", typeof(ArgumentException))]
    [InlineData("default-type", typeof(ArgumentException))]
    [InlineData("required-null-default", typeof(ArgumentException))]
    [InlineData("nonunique-null-policy", typeof(ArgumentException))]
    [InlineData("included-key-member", typeof(ArgumentException))]
    [InlineData("delete-action", typeof(ArgumentOutOfRangeException))]
    [InlineData("missing-key-member", typeof(ArgumentException))]
    [InlineData("alter-column-identity", typeof(ArgumentException))]
    public void InvalidOrAmbiguousDefinitionsFailAtConstruction(
        string scenario,
        Type exceptionType
    )
    {
        // Arrange
        var invalidDefinition = scenario;

        // Act
        var exception = Record.Exception(() => CreateInvalidDefinition(invalidDefinition));

        // Assert
        Assert.IsType(exceptionType, exception);
    }

    /// <summary>Constructs one invalid shape without sharing a previously constructed definition.</summary>
    private static object CreateInvalidDefinition(
        string scenario
    ) => scenario switch
    {
        "index-key-role" => new ExpectedIndexKeyDefinition(column: "id", expression: "lower(id)"),
        "generated-default" => new ExpectedColumnDefinition("computed",
            typeof(int),
            false,
            computedColumnSql: "1 + 1",
            defaultValue: SafeMigrationDefaultValue.Literal(2)),
        "foreign-key-arity" => new ExpectedForeignKeyDefinition("fk", "items", ["a", "b"], "parents", ["id"]),
        "duplicate-column" => new ExpectedTableDefinition("items",
        [
            new ExpectedColumnDefinition("id", typeof(int), false),
            new ExpectedColumnDefinition("id", typeof(int), false),
        ]),
        "default-type" => new ExpectedColumnDefinition("value",
            typeof(int),
            false,
            defaultValue: SafeMigrationDefaultValue.Literal("wrong")),
        "required-null-default" => new ExpectedColumnDefinition("value",
            typeof(int),
            false,
            defaultValue: SafeMigrationDefaultValue.Literal(null)),
        "nonunique-null-policy" => new ExpectedIndexDefinition("ix",
            "items",
            [new ExpectedIndexKeyDefinition(column: "id")],
            unique: false,
            nullsDistinct: true),
        "included-key-member" => new ExpectedIndexDefinition("ix",
            "items",
            [new ExpectedIndexKeyDefinition(column: "id")],
            includedColumns: ["id"]),
        "delete-action" => new ExpectedForeignKeyDefinition("fk",
            "items",
            ["parent_id"],
            "parents",
            ["id"],
            onDelete: (ReferentialAction)999),
        "missing-key-member" => new ExpectedTableDefinition("items",
            [new ExpectedColumnDefinition("id", typeof(int), false)],
            primaryKey: new ExpectedPrimaryKeyDefinition("pk", "items", ["missing"])),
        "alter-column-identity" => new AlterColumnIntent("items",
            new ExpectedColumnDefinition("new_name", typeof(int), false),
            new ExpectedColumnDefinition("old_name", typeof(int), false)),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };
}
