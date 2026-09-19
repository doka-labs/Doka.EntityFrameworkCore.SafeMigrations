namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed class SqliteRebuildArtifactContractTests
{
    [Theory]
    [MemberData(nameof(RebuildArtifactDriftCases))]
    public void GetUnsupportedRebuildFeature_ClassifiesEveryModelOwnershipDrift(
        string drift,
        string expectedCode
    )
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection);
        var model = context.GetService<IDesignTimeModel>().Model;
        var contract = SqliteRebuildArtifactContract.FromModel(model);
        var table = CreateDriftedTable(drift);
        var snapshot = Snapshot(table);

        var code = contract.GetUnsupportedRebuildFeature(
            snapshot,
            table.Name,
            (_, _) => true,
            (_, _) => true);

        Assert.Equal(expectedCode, code);
    }

    public static TheoryData<string, string> RebuildArtifactDriftCases()
        => new()
        {
            { "unmanaged_index", "table_rebuild_unmanaged_index" },
            { "missing_column", "table_rebuild_unmodeled_target_column" },
            { "missing_index", "table_rebuild_unmodeled_target_index" },
            { "different_primary_key", "table_rebuild_unmanaged_primary_key" },
            { "different_primary_key_name", "table_rebuild_primary_key_drift" },
            { "different_primary_key_collation", "table_rebuild_primary_key_drift" },
            { "unmanaged_unique", "table_rebuild_unmanaged_unique_constraint" },
            { "different_unique_name", "table_rebuild_unique_constraint_drift" },
            { "different_unique_collation", "table_rebuild_unique_constraint_drift" },
            { "unmanaged_check", "table_rebuild_unmanaged_check_constraint" },
            { "different_check_name", "table_rebuild_check_constraint_drift" },
            { "different_foreign_key_name", "table_rebuild_foreign_key_drift" },
            { "provider_constraint_option", "table_rebuild_provider_constraint_option" },
            { "missing_primary_key", "table_rebuild_unmodeled_target_primary_key" },
            { "missing_unique", "table_rebuild_unmodeled_target_unique_constraint" },
            { "missing_foreign_key", "table_rebuild_unmodeled_target_foreign_key" },
            { "missing_check", "table_rebuild_unmodeled_target_check_constraint" },
        };

    [Fact]
    public void AddedIndexNameCannotAuthorizeMissingColumnWithSameName()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection);
        var operation = new CreateIndexOperation
        {
            Name = "Code",
            Table = "rebuild_entities",
            Columns = ["Id"],
        };

        var contract = SqliteRebuildArtifactContract.FromModel(
            context.GetService<IDesignTimeModel>().Model,
            [operation]);

        var table = RebuildTable(includeCode: false);

        var code = contract.GetUnsupportedRebuildFeature(
            Snapshot(table),
            table.Name,
            (_, _) => true,
            (_, _) => true);

        Assert.Equal("table_rebuild_unmodeled_target_column", code);
    }

    private static SqliteTableSnapshot CreateDriftedTable(
        string drift
    ) => drift switch
    {
        "unmanaged_index" => RebuildTable(indexes: [Index("ix_rebuild_entities_legacy", "Code")]),
        "missing_column" => RebuildTable(includeCode: false),
        "missing_index" => ConstraintChildTable(),
        "different_primary_key" => RebuildTable(primaryKeyColumns: ["Code"]),
        "different_primary_key_name" => RebuildTable(primaryKeyName: "pk_legacy"),
        "different_primary_key_collation" => RebuildTable(
            primaryKeyKeys: [Key("Id", collation: "NOCASE")]),
        "unmanaged_unique" => RebuildTable(uniqueConstraints:
            [Unique("uq_rebuild_entities_code", "Code"), UniqueComposite("uq_rebuild_entities_legacy", "Id", "Code")]),
        "different_unique_name" => RebuildTable(uniqueConstraints: [Unique("uq_legacy", "Code")]),
        "different_unique_collation" => RebuildTable(uniqueConstraints:
            [Unique("uq_rebuild_entities_code", "Code", collation: "NOCASE")]),
        "unmanaged_check" => RebuildTable(checks:
            [Check("ck_rebuild_entities_code", "length(\"Code\") > 0"), Check("ck_legacy", "Id > 10")]),
        "different_check_name" => RebuildTable(checks:
            [Check("ck_legacy", "length(\"Code\") > 0")]),
        "different_foreign_key_name" => ConstraintChildTable(
            indexes: [Index("ix_constraint_children_parent_id", "ParentId")],
            foreignKeys: [ForeignKey("fk_legacy")]),
        "provider_constraint_option" => RebuildTable(hasUnmodeledConstraintOptions: true),
        "missing_primary_key" => RebuildTable(primaryKeyColumns: []),
        "missing_unique" => RebuildTable(uniqueConstraints: []),
        "missing_foreign_key" => ConstraintChildTable(
            indexes: [Index("ix_constraint_children_parent_id", "ParentId")]),
        "missing_check" => RebuildTable(checks: []),
        _ => throw new ArgumentOutOfRangeException(nameof(drift), drift, "Unknown rebuild drift fixture."),
    };

    private static SqliteCatalogSnapshot Snapshot(
        SqliteTableSnapshot table
    ) => new(
        "3.46.1",
        foreignKeysEnabled: true,
        legacyAlterTableEnabled: false,
        tables: new Dictionary<string, SqliteTableSnapshot>(SqliteIdentifierComparer.Instance)
        {
            [table.Name] = table,
        },
        otherObjects: []);

    private static SqliteTableSnapshot RebuildTable(
        bool includeCode = true,
        IReadOnlyList<SqliteIndexSnapshot>? indexes = null,
        IReadOnlyList<string>? primaryKeyColumns = null,
        IReadOnlyList<SqliteIndexKeySnapshot>? primaryKeyKeys = null,
        string primaryKeyName = "pk_rebuild_entities",
        IReadOnlyList<SqliteUniqueSnapshot>? uniqueConstraints = null,
        IReadOnlyList<SqliteCheckSnapshot>? checks = null,
        bool hasUnmodeledConstraintOptions = false
    )
    {
        var columns = new Dictionary<string, SqliteColumnSnapshot>(SqliteIdentifierComparer.Instance)
        {
            ["Id"] = Column(0, "Id", primaryKeyOrdinal: 1),
        };

        if (includeCode)
        {
            columns.Add("Code", Column(1, "Code"));
        }

        return new SqliteTableSnapshot(
            "rebuild_entities",
            "CREATE TABLE rebuild_entities (Id INTEGER NOT NULL, Code TEXT NOT NULL)",
            columns,
            indexes ?? [],
            [],
            checks ?? [Check("ck_rebuild_entities_code", "length(\"Code\") > 0")],
            primaryKeyColumns ?? ["Id"],
            primaryKeyKeys ?? [Key("Id")],
            primaryKeyName,
            uniqueConstraints ?? [Unique("uq_rebuild_entities_code", "Code")],
            hasUnmodeledConstraintOptions,
            isStrict: false,
            withoutRowId: false);
    }

    private static SqliteTableSnapshot ConstraintChildTable(
        IReadOnlyList<SqliteIndexSnapshot>? indexes = null,
        IReadOnlyList<SqliteForeignKeySnapshot>? foreignKeys = null
    ) => new(
        "constraint_children",
        "CREATE TABLE constraint_children (Id INTEGER NOT NULL, ParentId INTEGER NULL)",
        new Dictionary<string, SqliteColumnSnapshot>(SqliteIdentifierComparer.Instance)
        {
            ["Id"] = Column(0, "Id", primaryKeyOrdinal: 1),
            ["ParentId"] = Column(1, "ParentId", isNullable: true),
        },
        indexes ?? [],
        foreignKeys ?? [],
        [],
        ["Id"],
        [Key("Id")],
        "pk_constraint_children",
        [],
        hasUnmodeledConstraintOptions: false,
        isStrict: false,
        withoutRowId: false);

    private static SqliteColumnSnapshot Column(
        int ordinal,
        string name,
        bool isNullable = false,
        int primaryKeyOrdinal = 0
    ) => new(
        ordinal,
        name,
        name == "Id" || name == "ParentId" ? "INTEGER" : "TEXT",
        isNullable,
        DefaultSql: null,
        primaryKeyOrdinal,
        HiddenKind: 0,
        Collation: "BINARY",
        GeneratedSql: null,
        IsStored: false,
        AutoIncrement: false);

    private static SqliteIndexSnapshot Index(
        string name,
        string column
    ) => new(
        name,
        Unique: false,
        Origin: "c",
        Partial: false,
        [new SqliteIndexKeySnapshot(0, 0, column, Expression: null, Descending: false, "BINARY", IsKey: true)],
        Filter: null,
        $"CREATE INDEX \"{name}\" ON \"unused\" (\"{column}\")");

    private static SqliteUniqueSnapshot Unique(
        string name,
        string column,
        string collation = "BINARY"
    ) => new(name, "sqlite_autoindex_" + name, [column], [Key(column, collation)]);

    private static SqliteUniqueSnapshot UniqueComposite(
        string name,
        string firstColumn,
        string secondColumn
    ) => new(
        name,
        "sqlite_autoindex_" + name,
        [firstColumn, secondColumn],
        [Key(firstColumn), Key(secondColumn, ordinal: 1)]);

    private static SqliteIndexKeySnapshot Key(
        string column,
        string collation = "BINARY",
        int ordinal = 0
    ) => new(
        ordinal,
        ordinal,
        column,
        Expression: null,
        Descending: false,
        collation,
        IsKey: true);

    private static SqliteForeignKeySnapshot ForeignKey(
        string name
    ) => new(
        Id: 0,
        name,
        "constraint_parents",
        ["ParentId"],
        ["Id"],
        ReferentialAction.NoAction,
        ReferentialAction.Cascade,
        Match: "NONE");

    private static SqliteCheckSnapshot Check(
        string name,
        string expression
    ) => new(name, expression);
}
