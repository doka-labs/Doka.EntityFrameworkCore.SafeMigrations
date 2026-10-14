namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Verifies strict table transition capture remains bounded to its authored physical lifetime.</summary>
public sealed class SqliteTableConstraintTransitionsTests
{
    /// <summary>Later constraints cannot authorize an earlier table across a structural or opaque boundary.</summary>
    /// <param name="boundary">The lifetime, repeated-definition, or SQL boundary in the authored stream.</param>
    [Theory]
    [InlineData("sql")]
    [InlineData("drop_recreate")]
    [InlineData("rename")]
    [InlineData("repeated")]
    public void FutureConstraintCannotCrossCreatorBoundary(string boundary)
    {
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        AddCreator(builder, "children");
        var initial = Assert.IsType<SafeMigrationOperation>(builder.Operations[0]);
        switch (boundary)
        {
            case "sql":
                builder.Sql("SELECT 1;");
                break;
            case "drop_recreate":
                builder.DropTableIfExists("children");
                AddCreator(builder, "children");
                break;
            case "rename":
                builder.RenameTableIfExists("children", newName: "renamed_children");
                break;
            case "repeated":
                AddCreator(builder, "children");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(boundary));
        }

        var table = boundary == "rename" ? "renamed_children" : "children";
        builder.AddForeignKeyIfNotExists("fk_children_parent", table, ["ParentId"], "parents", ["Id"]);

        var transitions = new SqliteTableConstraintTransitions(builder.Operations);
        var initialContract = transitions.Find(initial);

        Assert.NotNull(initialContract);
        Assert.Empty(initialContract.AllowedForeignKeys);
        Assert.Empty(initialContract.RequiredForeignKeys);
        if (boundary is "repeated" or "drop_recreate")
        {
            var later = Assert.IsType<SafeMigrationOperation>(builder.Operations[^2]);
            Assert.Single(Assert.IsType<SafeMigrationExpectedTableConstraints>(
                transitions.Find(later)).AllowedForeignKeys);
        }
    }

    /// <summary>Inline constraints remain required while only appended standalone constraints become allowed.</summary>
    [Fact]
    public void UntouchedInlineForeignKeyRemainsRequired()
    {
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        builder.CreateTableIfNotExists("children", table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
                ParentId = table.Column<int>(type: "INTEGER", nullable: true),
            }, constraints: table =>
            {
                table.PrimaryKey("pk_children", item => item.Id);
                table.ForeignKey("fk_children_parent", item => item.ParentId, "parents", "Id");
            });
        builder.AddForeignKeyIfNotExists("fk_children_other", "children", ["Id"], "other_parents", ["Id"]);
        var creator = Assert.IsType<SafeMigrationOperation>(builder.Operations[0]);

        var contract = new SqliteTableConstraintTransitions(builder.Operations).Find(creator);

        Assert.NotNull(contract);
        Assert.Equal(2, contract.AllowedForeignKeys.Count);
        Assert.Equal("fk_children_parent", Assert.Single(contract.RequiredForeignKeys).Name);
    }

    /// <summary>Authors one valid strict baseline used by the boundary probes.</summary>
    private static void AddCreator(MigrationBuilder builder, string tableName)
        => builder.CreateTableIfNotExists(tableName, table => new
        {
            Id = table.Column<int>(type: "INTEGER", nullable: false),
            ParentId = table.Column<int>(type: "INTEGER", nullable: true),
        }, constraints: table => table.PrimaryKey("pk_children", item => item.Id));
}
