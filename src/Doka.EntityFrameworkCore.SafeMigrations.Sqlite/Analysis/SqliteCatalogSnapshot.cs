namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Represents one immutable read of the active SQLite catalog.</summary>
internal sealed class SqliteCatalogSnapshot
{
    /// <summary>Initializes a SQLite catalog snapshot.</summary>
    public SqliteCatalogSnapshot(
        string version,
        bool foreignKeysEnabled,
        bool legacyAlterTableEnabled,
        IReadOnlyDictionary<string, SqliteTableSnapshot> tables,
        IReadOnlyList<SqliteSchemaObjectSnapshot> otherObjects
    )
    {
        Version = version;
        ForeignKeysEnabled = foreignKeysEnabled;
        LegacyAlterTableEnabled = legacyAlterTableEnabled;
        Tables = tables;
        OtherObjects = otherObjects;
    }

    /// <summary>Gets the SQLite engine version.</summary>
    public string Version { get; }

    /// <summary>Gets whether foreign-key enforcement was enabled.</summary>
    public bool ForeignKeysEnabled { get; }

    /// <summary>Gets whether legacy alter-table behavior was enabled.</summary>
    public bool LegacyAlterTableEnabled { get; }

    /// <summary>Gets tables keyed by SQLite physical identifier semantics.</summary>
    public IReadOnlyDictionary<string, SqliteTableSnapshot> Tables { get; }

    /// <summary>Gets non-table schema objects visible to rebuild safety checks.</summary>
    public IReadOnlyList<SqliteSchemaObjectSnapshot> OtherObjects { get; }
}

/// <summary>Represents the catalog state of one SQLite table.</summary>
internal sealed class SqliteTableSnapshot
{
    /// <summary>Initializes a SQLite table snapshot.</summary>
    public SqliteTableSnapshot(
        string name,
        string sql,
        IReadOnlyDictionary<string, SqliteColumnSnapshot> columns,
        IReadOnlyList<SqliteIndexSnapshot> indexes,
        IReadOnlyList<SqliteForeignKeySnapshot> foreignKeys,
        IReadOnlyList<SqliteCheckSnapshot> checks,
        IReadOnlyList<string> primaryKeyColumns,
        IReadOnlyList<SqliteIndexKeySnapshot> primaryKeyKeys,
        string? primaryKeyName,
        IReadOnlyList<SqliteUniqueSnapshot> uniqueConstraints,
        bool hasUnmodeledConstraintOptions,
        bool isStrict,
        bool withoutRowId
    )
    {
        Name = name;
        Sql = sql;
        Columns = columns;
        Indexes = indexes;
        ForeignKeys = foreignKeys;
        Checks = checks;
        PrimaryKeyColumns = primaryKeyColumns;
        PrimaryKeyKeys = primaryKeyKeys;
        PrimaryKeyName = primaryKeyName;
        UniqueConstraints = uniqueConstraints;
        HasUnmodeledConstraintOptions = hasUnmodeledConstraintOptions;
        IsStrict = isStrict;
        WithoutRowId = withoutRowId;
    }

    /// <summary>Gets the physical table name.</summary>
    public string Name { get; }

    /// <summary>Gets the persisted create-table SQL.</summary>
    public string Sql { get; }

    /// <summary>Gets physical columns by identifier.</summary>
    public IReadOnlyDictionary<string, SqliteColumnSnapshot> Columns { get; }

    /// <summary>Gets physical indexes.</summary>
    public IReadOnlyList<SqliteIndexSnapshot> Indexes { get; }

    /// <summary>Gets physical foreign keys.</summary>
    public IReadOnlyList<SqliteForeignKeySnapshot> ForeignKeys { get; }

    /// <summary>Gets parsed check constraints.</summary>
    public IReadOnlyList<SqliteCheckSnapshot> Checks { get; }

    /// <summary>Gets ordered primary-key columns.</summary>
    public IReadOnlyList<string> PrimaryKeyColumns { get; }

    /// <summary>Gets ordered physical primary-key facets.</summary>
    public IReadOnlyList<SqliteIndexKeySnapshot> PrimaryKeyKeys { get; }

    /// <summary>Gets the declared primary-key name, when one exists.</summary>
    public string? PrimaryKeyName { get; }

    /// <summary>Gets unique constraints backed by automatic SQLite indexes.</summary>
    public IReadOnlyList<SqliteUniqueSnapshot> UniqueConstraints { get; }

    /// <summary>Gets whether constraint syntax includes unsupported options.</summary>
    public bool HasUnmodeledConstraintOptions { get; }

    /// <summary>Gets whether the table uses SQLite strict typing.</summary>
    public bool IsStrict { get; }

    /// <summary>Gets whether the table omits the implicit row identifier.</summary>
    public bool WithoutRowId { get; }
}

/// <summary>Represents physical SQLite column facets.</summary>
internal sealed record SqliteColumnSnapshot(
    int Ordinal,
    string Name,
    string StoreType,
    bool IsNullable,
    string? DefaultSql,
    int PrimaryKeyOrdinal,
    int HiddenKind,
    string? Collation,
    string? GeneratedSql,
    bool IsStored,
    bool AutoIncrement
);

/// <summary>Represents a physical SQLite index.</summary>
internal sealed record SqliteIndexSnapshot(
    string Name,
    bool Unique,
    string Origin,
    bool Partial,
    IReadOnlyList<SqliteIndexKeySnapshot> Keys,
    string? Filter,
    string Sql
);

/// <summary>Represents one ordered physical SQLite index key.</summary>
internal sealed record SqliteIndexKeySnapshot(
    int Ordinal,
    int ColumnId,
    string? Column,
    string? Expression,
    bool Descending,
    string Collation,
    bool IsKey
);

/// <summary>Represents a physical SQLite foreign key.</summary>
internal sealed record SqliteForeignKeySnapshot(
    int Id,
    string? Name,
    string PrincipalTable,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> PrincipalColumns,
    ReferentialAction OnUpdate,
    ReferentialAction OnDelete,
    string Match
);

/// <summary>Represents a parsed SQLite check constraint.</summary>
internal sealed record SqliteCheckSnapshot(
    string? Name,
    string Expression
);

/// <summary>Represents a SQLite unique constraint and its physical index.</summary>
internal sealed record SqliteUniqueSnapshot(
    string? Name,
    string PhysicalName,
    IReadOnlyList<string> Columns,
    IReadOnlyList<SqliteIndexKeySnapshot> Keys
);

/// <summary>Represents a non-table object from <c>sqlite_schema</c>.</summary>
internal sealed record SqliteSchemaObjectSnapshot(
    string Type,
    string Name,
    string Table,
    string Sql
);
