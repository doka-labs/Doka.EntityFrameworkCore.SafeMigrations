namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Bounds physical ordinal preservation to proven native column and table lifetimes.</summary>
public sealed class SqliteRebuildColumnOrderTests
{
    /// <summary>Renames keep identity, whereas dropped/re-added columns and owners start new lifetimes.</summary>
    /// <param name="change">The accepted structural change before the final native reconstruction.</param>
    /// <param name="expected">The exact resulting column order.</param>
    [Theory]
    [InlineData("unchanged", "id,first,last")]
    [InlineData("rename-column", "id,renamed,last")]
    [InlineData("rename-table", "id,first,last")]
    [InlineData("replace-column", "id,last,first")]
    [InlineData("add-column", "id,first,last,new")]
    [InlineData("recreate", "id,last,first")]
    [InlineData("ordinary", "id,last,first")]
    public void AcceptedLifetimesDoNotBorrowDiscardedNativeOrdinals(
        string change,
        string expected
    )
    {
        // Arrange
        var columns = new Dictionary<string, SqliteColumnSnapshot>(SqliteIdentifierComparer.Instance);
        var names = new[] { "id", "first", "last" };
        for (var index = 0; index < names.Length; index++)
        {
            columns.Add(names[index], new SqliteColumnSnapshot(index, names[index], "INTEGER", false,
                null, index == 0 ? 1 : 0, 0, null, null, false, false));
        }

        var table = new SqliteTableSnapshot("host", string.Empty, columns, [], [], [], ["id"], [], "pk", [],
            false, false, false);
        var snapshot = new SqliteCatalogSnapshot("3.46.1", true, false,
            new Dictionary<string, SqliteTableSnapshot>(SqliteIdentifierComparer.Instance) { ["host"] = table }, []);

        var operations = new List<MigrationOperation>();
        var target = change == "rename-table" ? "renamed_host" : "host";
        switch (change)
        {
            case "rename-column":
                operations.Add(new RenameColumnOperation { Table = "host", Name = "first", NewName = "renamed" });
                break;
            case "rename-table":
                operations.Add(new RenameTableOperation { Name = "host", NewName = target });
                break;
            case "replace-column":
                operations.Add(new DropColumnOperation { Table = "host", Name = "first" });
                operations.Add(new AddColumnOperation { Table = "host", Name = "first" });
                break;
            case "add-column":
                operations.Add(new AddColumnOperation { Table = "host", Name = "new" });
                break;
            case "recreate":
                operations.Add(new DropTableOperation { Name = "host" });
                operations.Add(new CreateTableOperation { Name = "host" });
                break;
        }

        var drop = new DropUniqueConstraintOperation { Table = target, Name = "independent" };
        operations.Add(drop);
        var ownership = new SqliteGeneratedColumnOwnership(operations, change == "ordinary" ? [drop] : []);
        var creator = new CreateTableOperation { Name = "actual_native_name" };
        var destinationNames = change == "add-column"
            ? new[] { "id", "new", "last", "first" }
            : new[] { "id", "last", change == "rename-column" ? "renamed" : "first" };

        foreach (var name in destinationNames)
        {
            creator.Columns.Add(new AddColumnOperation { Table = creator.Name, Name = name });
        }

        var originalColumns = creator.Columns.ToArray();
        MigrationOperation[] rewritten = [creator, new RenameTableOperation { Name = creator.Name, NewName = target }];

        // Act
        var contract = new SqliteRebuildColumnOrder(snapshot, operations, ownership);
        contract.Apply(ownership.ResolveRebuiltCreators(rewritten), null);

        // Assert
        Assert.Equal(expected, string.Join(',', creator.Columns.Select(static column => column.Name)));
        Assert.All(creator.Columns, column => Assert.Contains(column, originalColumns));
        Assert.Equal(0, columns["id"].Ordinal);
        Assert.Equal(1, columns["first"].Ordinal);
        Assert.Equal(2, columns["last"].Ordinal);
    }
}
