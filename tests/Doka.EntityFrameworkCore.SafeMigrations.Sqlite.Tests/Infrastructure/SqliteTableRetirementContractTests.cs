using System.Collections;

namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Constrains retirement certificates to exact references and unchanged atomic owner lifetimes.</summary>
public sealed class SqliteTableRetirementContractTests
{
    /// <summary>Only a later DROP of the same unmodeled and otherwise unchanged owner can subsume FK removal.</summary>
    /// <param name="variant">The boundary or owner-lifetime variation.</param>
    /// <param name="expected">Whether the structural certificate is valid.</param>
    [Theory]
    [InlineData("same", true)]
    [InlineData("ordinary-drop", true)]
    [InlineData("case-alias", true)]
    [InlineData("independent-column", true)]
    [InlineData("missing-drop", false)]
    [InlineData("earlier-drop", false)]
    [InlineData("different-owner", false)]
    [InlineData("mapped", false)]
    [InlineData("opaque", false)]
    [InlineData("insert", false)]
    [InlineData("rename-source", false)]
    [InlineData("rename-target", false)]
    [InlineData("recreated-before", false)]
    [InlineData("recreated-after", false)]
    [InlineData("owner-column", false)]
    [InlineData("annotated-fk", false)]
    [InlineData("annotated-drop", false)]
    [InlineData("qualified-fk", false)]
    [InlineData("qualified-drop", false)]
    public void CertificateRequiresBoundedRetiringLifetime(
        string variant,
        bool expected
    )
    {
        // Arrange
        var foreignKey = new SafeMigrationOperation(
            new DropForeignKeyIntent("fk_owner_principal",
                "owner",
                variant == "qualified-fk"
                    ? "attached"
                    : null),
            SafeMigrationPolicy.ThrowIfDifferent);

        var droppedOwner = variant switch
        {
            "different-owner" => "other",
            "case-alias" => "OWNER",
            _ => "owner",
        };

        var drop = new SafeMigrationOperation(
            new DropTableIntent(droppedOwner,
                variant == "qualified-drop"
                    ? "attached"
                    : null),
            SafeMigrationPolicy.ThrowIfDifferent);

        var operations = new List<MigrationOperation>
        {
            foreignKey,
            drop,
        };

        var modeled = new Dictionary<string, object>(SqliteIdentifierComparer.Instance);

        switch (variant)
        {
            case "ordinary-drop":
                operations[1] = new DropTableOperation { Name = "owner" };
                break;
            case "mapped":
                modeled.Add("owner", new object());
                break;
            case "missing-drop":
                operations.RemoveAt(1);
                break;
            case "earlier-drop":
                operations.Reverse();
                break;
            case "opaque":
                operations.Insert(1, new SqlOperation { Sql = "SELECT 1;" });
                break;
            case "insert":
                operations.Insert(1,
                    new InsertDataOperation
                    {
                        Table = "unrelated",
                        Columns = ["id"],
                        Values = new object[,] { { 1 } },
                    });

                break;
            case "rename-source":
                operations.Insert(1,
                    new RenameTableOperation
                    {
                        Name = "owner",
                        NewName = "renamed",
                    });

                break;
            case "rename-target":
                operations.Insert(1,
                    new RenameTableOperation
                    {
                        Name = "unrelated",
                        NewName = "owner",
                    });

                break;
            case "recreated-before":
                operations.Insert(0, new CreateTableOperation { Name = "owner" });
                break;
            case "recreated-after":
                operations.Add(new CreateTableOperation { Name = "owner" });
                break;
            case "owner-column":
                operations.Insert(1,
                    new DropColumnOperation
                    {
                        Table = "owner",
                        Name = "retained",
                    });

                break;
            case "independent-column":
                operations.Insert(1,
                    new AddColumnOperation
                    {
                        Table = "other",
                        Name = "added",
                    });

                break;
            case "annotated-fk":
                foreignKey.AddAnnotation("Unknown", true);
                break;
            case "annotated-drop":
                drop.AddAnnotation("Unknown", true);
                break;
        }

        // Act
        var certificate = SqliteTableRetirementContract.Capture(modeled, operations);

        // Assert
        Assert.Equal(expected, certificate.Contains(foreignKey));
    }

    /// <summary>Equal-looking operations and caller-list mutation cannot replace certificate ownership.</summary>
    [Fact]
    public void CertificateSnapshotsExactOperationOwnership()
    {
        // Arrange
        var foreignKey = new SafeMigrationOperation(
            new DropForeignKeyIntent("fk_owner_principal", "owner"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var copy = new SafeMigrationOperation(foreignKey.Intent, foreignKey.Policy);
        var operations = new List<MigrationOperation>
        {
            foreignKey,
            new SafeMigrationOperation(new DropTableIntent("owner"), SafeMigrationPolicy.ThrowIfDifferent),
        };

        // Act
        var certificate = SqliteTableRetirementContract.Capture(
            new Dictionary<string, object>(SqliteIdentifierComparer.Instance),
            operations);

        operations.Clear();
        operations.Add(copy);

        // Assert
        Assert.True(certificate.Contains(foreignKey));
        Assert.False(certificate.Contains(copy));
    }

    /// <summary>Cumulative future DROPs do not authorize an FK rebuild in an earlier structural window.</summary>
    [Fact]
    public void RebuildContractUsesOnlyItsCurrentWindowForRetirement()
    {
        // Arrange
        var foreignKey = new SafeMigrationOperation(
            new DropForeignKeyIntent("fk_owner_principal", "owner"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var drop = new SafeMigrationOperation(new DropTableIntent("owner"), SafeMigrationPolicy.ThrowIfDifferent);
        MigrationOperation[] history = [foreignKey, new SqlOperation { Sql = "SELECT 1;" }, drop];

        // Act
        var firstWindow = SqliteRebuildArtifactContract.FromModel(null, history, [foreignKey]);
        var completeWindow = SqliteRebuildArtifactContract.FromModel(null, [foreignKey, drop]);

        // Assert
        Assert.False(firstWindow.ElidesRetiringForeignKey(foreignKey));
        Assert.True(completeWindow.ElidesRetiringForeignKey(foreignKey));
    }

    /// <summary>Connected-owner validation indexes once and does not reuse verdicts across snapshots.</summary>
    [Fact]
    public void NativeComponentEvidenceIsLinearAndBoundToOneSnapshotAndWindow()
    {
        // Arrange
        var parentKey = new SafeMigrationOperation(
            new DropForeignKeyIntent("fk_parent_child", "parent"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var childKey = new SafeMigrationOperation(
            new DropForeignKeyIntent("fk_child_parent", "child"),
            SafeMigrationPolicy.ThrowIfDifferent);

        MigrationOperation[] operations =
        [
            parentKey,
            childKey,
            new SafeMigrationOperation(new DropTableIntent("child"), SafeMigrationPolicy.ThrowIfDifferent),
            new SafeMigrationOperation(new DropTableIntent("parent"), SafeMigrationPolicy.ThrowIfDifferent),
        ];

        var tables = new CountingTables(new Dictionary<string, SqliteTableSnapshot>(SqliteIdentifierComparer.Instance)
        {
            ["parent"] = NativeTable("parent", "child"),
            ["child"] = NativeTable("child", "parent"),
        });

        var firstSnapshot = new SqliteCatalogSnapshot("3.46.1", true, false, tables, []);
        var changedSnapshot = new SqliteCatalogSnapshot("3.46.1",
            true,
            false,
            tables,
            [new SqliteSchemaObjectSnapshot("view", "unmanaged_view", "unmanaged_view", "SELECT id FROM child")]);

        var contract = SqliteRebuildArtifactContract.FromModel(null, operations);

        // Act
        var firstParent = Classify(contract, firstSnapshot, parentKey);
        var firstChild = Classify(contract, firstSnapshot, childKey);
        var firstReads = tables.ValuesReadCount;
        var changedParent = Classify(contract, changedSnapshot, parentKey);
        var changedChild = Classify(contract, changedSnapshot, childKey);
        var changedReads = tables.ValuesReadCount;
        var nextWindow = contract.WithOperationHistory(operations);
        var nextParent = Classify(nextWindow, firstSnapshot, parentKey);

        // Assert
        Assert.Null(firstParent);
        Assert.Null(firstChild);
        Assert.Equal(1, firstReads);
        Assert.Equal("table_rebuild_view", changedParent);
        Assert.Equal("table_rebuild_view", changedChild);
        Assert.Equal(2, changedReads);
        Assert.Null(nextParent);
        Assert.Equal(3, tables.ValuesReadCount);
    }

    /// <summary>A failed graph capture cannot publish partial metadata that a retry treats as valid.</summary>
    [Fact]
    public void FailedNativeIndexCaptureDoesNotPoisonRetryEvidence()
    {
        // Arrange
        var key = new SafeMigrationOperation(new DropForeignKeyIntent("fk_parent_child", "parent"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var contract = SqliteRebuildArtifactContract.FromModel(null,
        [
            key,
            new SafeMigrationOperation(new DropTableIntent("child"), SafeMigrationPolicy.ThrowIfDifferent),
            new SafeMigrationOperation(new DropTableIntent("parent"), SafeMigrationPolicy.ThrowIfDifferent),
        ]);

        var tables = new CountingTables(new Dictionary<string, SqliteTableSnapshot>(SqliteIdentifierComparer.Instance)
        {
            ["parent"] = NativeTable("parent", "child"),
            ["child"] = NativeTable("child", "parent"),
        });

        var original = new SqliteCatalogSnapshot("3.46.1", true, false, tables, []);
        var changed = new SqliteCatalogSnapshot("3.46.1",
            true,
            false,
            tables,
            [new SqliteSchemaObjectSnapshot("view", "new_view", "new_view", "SELECT id FROM child")]);

        // Act
        var originalCode = Classify(contract, original, key);
        tables.FailNextValuesRead = true;
        var failure = Record.Exception(() => Classify(contract, changed, key));
        var retryCode = Classify(contract, changed, key);

        // Assert
        Assert.Null(originalCode);
        Assert.Equal("synthetic-catalog-read-failure",
            Assert.IsType<InvalidOperationException>(failure)
                .Message);

        Assert.Equal("table_rebuild_view", retryCode);
        Assert.Equal(3, tables.ValuesReadCount);
    }

    /// <summary>Consumes the real rebuild authorization path without a native database or test-only shortcut.</summary>
    private static string? Classify(
        SqliteRebuildArtifactContract contract,
        SqliteCatalogSnapshot snapshot,
        SafeMigrationOperation operation
    ) => contract.GetUnsupportedRebuildFeature(snapshot,
        ((DropForeignKeyIntent)operation.Intent).Table,
        static (_, _) => true,
        static (_, _) => true,
        operation: operation);

    /// <summary>Builds one ordinary native table in a two-owner FK component.</summary>
    private static SqliteTableSnapshot NativeTable(
        string owner,
        string principal
    ) => new(owner,
        $"CREATE TABLE {owner} (id INTEGER PRIMARY KEY, principal_id INTEGER REFERENCES {principal}(id))",
        new Dictionary<string, SqliteColumnSnapshot>(SqliteIdentifierComparer.Instance)
        {
            ["id"] = new(0,
                "id",
                "INTEGER",
                false,
                null,
                1,
                0,
                null,
                null,
                false,
                false),
        },
        [],
        [
            new SqliteForeignKeySnapshot(0,
                $"fk_{owner}_{principal}",
                principal,
                ["principal_id"],
                ["id"],
                ReferentialAction.NoAction,
                ReferentialAction.NoAction,
                "NONE"),
        ],
        [],
        ["id"],
        [],
        null,
        [],
        false,
        false,
        false);

    /// <summary>Counts full-table catalog access independently of machine speed or allocation.</summary>
    private sealed class CountingTables : IReadOnlyDictionary<string, SqliteTableSnapshot>
    {
        private readonly Dictionary<string, SqliteTableSnapshot> _tables;

        public CountingTables(
            Dictionary<string, SqliteTableSnapshot> tables
        )
        {
            _tables = tables;
        }

        public int ValuesReadCount { get; private set; }

        public bool FailNextValuesRead { get; set; }

        public IEnumerable<SqliteTableSnapshot> Values
        {
            get
            {
                ValuesReadCount++;
                if (FailNextValuesRead)
                {
                    FailNextValuesRead = false;

                    throw new InvalidOperationException("synthetic-catalog-read-failure");
                }

                return _tables.Values;
            }
        }

        public IEnumerable<string> Keys => _tables.Keys;

        public int Count => _tables.Count;

        public SqliteTableSnapshot this[
            string key
        ] => _tables[key];

        public bool ContainsKey(
            string key
        ) => _tables.ContainsKey(key);

        public bool TryGetValue(
            string key,
            out SqliteTableSnapshot value
        ) => _tables.TryGetValue(key, out value!);

        public IEnumerator<KeyValuePair<string, SqliteTableSnapshot>> GetEnumerator() => _tables.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
