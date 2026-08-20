namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationDefinitionTests
{
    [Fact]
    public void DefinitionsSnapshotMutableInputs()
    {
        var columns = new List<string>
        {
            "tenant_id",
            "id"
        };

        var bytes = new byte[]
        {
            1,
            2,
            3,
        };

        var key = new ExpectedPrimaryKeyDefinition("pk_items", "items", columns);
        var defaultValue = SafeMigrationDefaultValue.Literal(bytes);

        columns[0] = "changed";
        bytes[0] = 9;

        var firstRead = Assert.IsType<byte[]>(defaultValue.LiteralValue);
        firstRead[1] = 9;

        var secondRead = Assert.IsType<byte[]>(defaultValue.LiteralValue);

        Assert.Equal(
            [
                "tenant_id",
                "id",
            ],
            key.Columns);
        Assert.Equal(
            new byte[]
            {
                1,
                2,
                3,
            },
            secondRead);
    }

    [Fact]
    public void InvalidOrAmbiguousDefinitionsFailAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => new ExpectedIndexKeyDefinition(column: "id", expression: "lower(id)"));
        Assert.Throws<ArgumentException>(() => new ExpectedColumnDefinition(
            "computed",
            typeof(int),
            false,
            computedColumnSql: "1 + 1",
            defaultValue: SafeMigrationDefaultValue.Literal(2)));
        Assert.Throws<ArgumentException>(() => new ExpectedForeignKeyDefinition(
            "fk",
            "items",
            [
                "a",
                "b",
            ],
            "parents",
            ["id"]));
        Assert.Throws<ArgumentException>(() => new ExpectedTableDefinition(
            "items",
            [
                new ExpectedColumnDefinition("id", typeof(int), false),
                new ExpectedColumnDefinition("id", typeof(int), false),
            ]));
        Assert.Throws<ArgumentException>(() => new ExpectedColumnDefinition(
            "value",
            typeof(int),
            false,
            defaultValue: SafeMigrationDefaultValue.Literal("wrong")));
        Assert.Throws<ArgumentException>(() => new ExpectedColumnDefinition(
            "value",
            typeof(int),
            false,
            defaultValue: SafeMigrationDefaultValue.Literal(null)));
        Assert.Throws<ArgumentException>(() => new ExpectedIndexDefinition(
            "ix",
            "items",
            [new ExpectedIndexKeyDefinition(column: "id")],
            unique: false,
            nullsDistinct: true));
        Assert.Throws<ArgumentException>(() => new ExpectedIndexDefinition(
            "ix",
            "items",
            [new ExpectedIndexKeyDefinition(column: "id")],
            includedColumns: ["id"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExpectedForeignKeyDefinition(
            "fk",
            "items",
            ["parent_id"],
            "parents",
            ["id"],
            onDelete: (ReferentialAction)999));
        Assert.Throws<ArgumentException>(() => new ExpectedTableDefinition(
            "items",
            [new ExpectedColumnDefinition("id", typeof(int), false)],
            primaryKey: new ExpectedPrimaryKeyDefinition("pk", "items", ["missing"])));
        Assert.Throws<ArgumentException>(() => new AlterColumnIntent(
            "items",
            new ExpectedColumnDefinition("new_name", typeof(int), false),
            new ExpectedColumnDefinition("old_name", typeof(int), false)));
    }
}
