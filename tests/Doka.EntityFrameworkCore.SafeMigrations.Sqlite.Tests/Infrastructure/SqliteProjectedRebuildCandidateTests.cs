namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Checks the reference-bound, single-pass index of potentially materialized DROP sources.</summary>
public sealed class SqliteProjectedRebuildCandidateTests
{
    /// <summary>
    /// Many interleaved owners retain exact candidates without accepting copied operation identities.
    /// </summary>
    [Fact]
    public void InterleavedOwnerCandidatesRemainReferenceBound()
    {
        // Arrange
        const int count = 256;
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        for (var index = 0; index < count; index++)
        {
            builder.AddColumnIfNotExists<int>("source", "owner_" + index.ToString(CultureInfo.InvariantCulture),
                type: "INTEGER", nullable: true);
        }

        var drops = new List<SafeMigrationOperation>(count);
        for (var index = count - 1; index >= 0; index--)
        {
            builder.DropColumnIfExists("source", "owner_" + index.ToString(CultureInfo.InvariantCulture));
            drops.Add((SafeMigrationOperation)builder.Operations[^1]);
        }

        var contract = SqliteRebuildArtifactContract.FromOperations(builder.Operations);
        var copied = new MigrationBuilder(builder.ActiveProvider);
        copied.DropColumnIfExists("source", "owner_0");

        // Act
        var recognized = drops.Select(contract.MayRebuildMissingColumn).ToArray();
        var copiedRecognized = contract.MayRebuildMissingColumn((SafeMigrationOperation)copied.Operations[0]);

        // Assert
        Assert.Equal(count, recognized.Length);
        Assert.All(recognized, Assert.True);
        Assert.False(copiedRecognized);
    }

    /// <summary>
    /// Candidate wishes retain conditional possibilities without replacing accepted owner-lifetime proofs.
    /// </summary>
    /// <param name="scenario">The authored source transition before the final DROP.</param>
    /// <param name="expected">Whether a native-missing DROP can require conditional row analysis.</param>
    [Theory]
    [InlineData("later_add", false)]
    [InlineData("other_owner", false)]
    [InlineData("new_owner", true)]
    [InlineData("opaque", false)]
    [InlineData("repeated_drop", true)]
    [InlineData("rename_owner", true)]
    [InlineData("rename_column", false)]
    [InlineData("ordinary_add", true)]
    [InlineData("typed_insert", true)]
    [InlineData("typed_update", true)]
    [InlineData("typed_delete", true)]
    [InlineData("safe_index_boundary", true)]
    [InlineData("safe_schema_boundary", true)]
    [InlineData("conditional_drop", true)]
    [InlineData("conditional_rename_source", true)]
    [InlineData("accepted_column_alias", true)]
    [InlineData("conditional_column_source", true)]
    [InlineData("ordinary_column_alias", true)]
    public void CandidateWishesNeverAuthorizeExecution(string scenario, bool expected)
    {
        // Arrange
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        var owner = "nodes";
        var column = "source";
        if (scenario == "later_add")
        {
            builder.DropColumnIfExists("source", owner);
            builder.AddColumnIfNotExists<int>("source", owner, type: "INTEGER", nullable: true);
        }
        else
        {
            if (scenario == "ordinary_add")
            {
                builder.AddColumn<int>("source", owner, type: "INTEGER", nullable: true);
            }
            else if (scenario == "rename_column")
            {
                builder.RenameColumnIfExists("old_source", owner, "source");
            }
            else
            {
                builder.AddColumnIfNotExists<int>("source", scenario == "other_owner" ? "other_nodes" : owner,
                    type: "INTEGER", nullable: true);
            }

            switch (scenario)
            {
                case "new_owner":
                    builder.DropTableIfExists(owner);
                    builder.CreateTableIfNotExists(owner, table => new
                    {
                        id = table.Column<int>(type: "INTEGER", nullable: false),
                    }, constraints: table => table.PrimaryKey("pk_nodes", value => value.id));

                    break;
                case "opaque":
                    _ = builder.Sql("SELECT 1;");
                    break;
                case "repeated_drop":
                    builder.DropColumnIfExists("source", owner);
                    break;
                case "rename_owner":
                    builder.RenameTableIfExists(owner, newName: "renamed_nodes");
                    owner = "renamed_nodes";
                    break;
                case "conditional_drop":
                    builder.DropTableIfExists(owner);
                    break;
                case "conditional_rename_source":
                    builder.RenameTableIfExists(owner, newName: "renamed_nodes");
                    break;
                case "accepted_column_alias":
                    builder.RenameColumnIfExists("source", owner, "renamed_source");
                    column = "renamed_source";
                    break;
                case "conditional_column_source":
                    builder.RenameColumnIfExists("source", owner, "renamed_source");
                    break;
                case "ordinary_column_alias":
                    builder.RenameColumn("source", owner, "renamed_source");
                    column = "renamed_source";
                    break;
                case "typed_insert":
                    builder.Operations.Add(new InsertDataOperation
                    {
                        Table = "writes",
                        Columns = ["id"],
                        Values = new object[,] { { 1 } },
                    });

                    break;
                case "typed_update":
                    builder.Operations.Add(new UpdateDataOperation
                    {
                        Table = "writes",
                        KeyColumns = ["id"],
                        KeyValues = new object[,] { { 1 } },
                        Columns = ["value"],
                        Values = new object[,] { { 2 } },
                    });

                    break;
                case "typed_delete":
                    builder.DeleteData("writes", "id", "INTEGER", 1);
                    break;
                case "safe_index_boundary":
                    builder.RenameIndexIfExists("absent_index", owner, "absent_target");
                    break;
                case "safe_schema_boundary":
                    builder.Operations.Add(new SafeMigrationOperation(
                        new EnsureSchemaIntent("main"), SafeMigrationPolicy.ThrowIfDifferent));

                    break;
            }

            builder.DropColumnIfExists(column, owner);
        }

        var drop = builder.Operations.OfType<SafeMigrationOperation>().Last(value => value.Intent is DropColumnIntent);
        var contract = SqliteRebuildArtifactContract.FromOperations(builder.Operations);

        // Act
        var candidate = contract.MayRebuildMissingColumn(drop);

        // Assert
        Assert.Equal(expected, candidate);
    }
}
