namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Reuses immutable native SQLite metadata for sparse ordered constraint projection.</summary>
internal sealed class SqliteLivePrerequisiteSource : ISafeMigrationLivePrerequisiteSource
{
    private static readonly StringComparer s_identifierComparer = SqliteIdentifierComparer.Instance;
    private readonly SqliteCatalogSnapshot _snapshot;

    /// <summary>Captures the already-read catalog without querying or consulting the target model.</summary>
    /// <param name="snapshot">The caller-owned immutable native catalog capture.</param>
    public SqliteLivePrerequisiteSource(SqliteCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _snapshot = snapshot;
    }

    /// <inheritdoc />
    public bool HasLiveColumn(string table, string? schema, string column) => IsMain(schema)
        && _snapshot.Tables.TryGetValue(table, out var owner)
        && owner.Columns.ContainsKey(column);

    /// <inheritdoc />
    public bool RequiresLiveForeignKeyQualification(EnsureForeignKeyIntent intent) =>
        IsMain(intent.Definition.Schema) && IsMain(intent.Definition.PrincipalSchema)
        && _snapshot.Tables.ContainsKey(intent.Definition.Table)
        && _snapshot.Tables.ContainsKey(intent.Definition.PrincipalTable);

    /// <inheritdoc />
    public IReadOnlyList<SafeMigrationLiveCandidateKey>? GetLiveForeignKeyCandidateKeys(
        EnsureForeignKeyIntent intent,
        ISafeMigrationProjectedColumnSource columns)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(columns);

        var definition = intent.Definition;
        if (!IsMain(definition.Schema) || !IsMain(definition.PrincipalSchema)
            || !_snapshot.Tables.TryGetValue(definition.PrincipalTable, out var principal)
            || !HasDependentColumns(definition, columns)
            || !HasColumns(principal, definition.PrincipalSchema, definition.PrincipalColumns, columns)
            || !RetainsPrincipalKeyCollations(principal, definition, columns))
        {
            return null;
        }

        List<SafeMigrationLiveCandidateKey>? candidates = null;
        if (SqliteCatalogEquivalence.IdentifiersEqual(principal.PrimaryKeyColumns, definition.PrincipalColumns)
            && SqliteCatalogEquivalence.KeyMatchesDeclaredColumns(
                principal, principal.PrimaryKeyKeys, definition.PrincipalColumns))
        {
            (candidates ??= []).Add(new SafeMigrationLiveCandidateKey(
                SafeMigrationDatabaseObjectKind.PrimaryKey,
                principal.PrimaryKeyName ?? "sqlite_primary_key"));
        }

        foreach (var unique in principal.UniqueConstraints)
        {
            if (SqliteCatalogEquivalence.KeyMatchesDeclaredColumns(principal, unique.Keys, definition.PrincipalColumns))
            {
                (candidates ??= []).Add(new SafeMigrationLiveCandidateKey(
                    SafeMigrationDatabaseObjectKind.UniqueConstraint,
                    unique.Name ?? unique.PhysicalName,
                    unique.PhysicalName));
            }
        }

        foreach (var index in principal.Indexes)
        {
            if (index is { Origin: "c", Unique: true, Partial: false }
                && SqliteCatalogEquivalence.KeyMatchesDeclaredColumns(
                    principal, index.Keys, definition.PrincipalColumns))
            {
                (candidates ??= []).Add(new SafeMigrationLiveCandidateKey(
                    SafeMigrationDatabaseObjectKind.Index, index.Name));
            }
        }

        // WHY: SQLite applies the parent's affinity and collation to child values;
        // child storage need not match. Parent unique-key collations must exactly
        // match its declared columns, even when every projected child value is NULL.
        return candidates is null ? [] : candidates.AsReadOnly();
    }

    /// <summary>Accepts a missing native child only through a preceding known newly created owner.</summary>
    private bool HasDependentColumns(
        ExpectedForeignKeyDefinition foreignKey,
        ISafeMigrationProjectedColumnSource columns)
    {
        if (_snapshot.Tables.TryGetValue(foreignKey.Table, out var dependent))
        {
            return HasColumns(dependent, foreignKey.Schema, foreignKey.Columns, columns);
        }

        if (columns is not ISafeMigrationProjectedTableSource tables
            || !tables.TryGetProjectedTableState(foreignKey.Table, foreignKey.Schema, out var state)
            || !state.IsNewlyCreated || state.HasUnknownStructure)
        {
            return false;
        }

        foreach (var name in foreignKey.Columns)
        {
            if (!columns.TryGetProjectedColumn(foreignKey.Table, foreignKey.Schema, name, out _))
            {
                return false;
            }
        }

        // WHY: Core owns ordered creation and separate current row epochs.
        // The snapshot still independently proves the principal and its key;
        // SQLite does not require child/parent affinity or collation equality.
        return true;
    }

    /// <summary>Accepts only requested live columns or exact preceding accepted definitions.</summary>
    private static bool HasColumns(
        SqliteTableSnapshot table,
        string? schema,
        IReadOnlyList<string> names,
        ISafeMigrationProjectedColumnSource columns)
    {
        foreach (var name in names)
        {
            if (!table.Columns.ContainsKey(name)
                && !columns.TryGetProjectedColumn(table.Name, schema, name, out _))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Never reuses an old key's collation certificate after a principal-column collation change.</summary>
    private static bool RetainsPrincipalKeyCollations(
        SqliteTableSnapshot principal,
        ExpectedForeignKeyDefinition foreignKey,
        ISafeMigrationProjectedColumnSource columns)
    {
        foreach (var name in foreignKey.PrincipalColumns)
        {
            if (principal.Columns.TryGetValue(name, out var actual)
                && columns.TryGetProjectedColumn(principal.Name, foreignKey.PrincipalSchema, name, out var projected)
                && (projected.Collation?.Schema is not null
                    || !s_identifierComparer.Equals(
                        projected.Collation?.Name ?? "BINARY", actual.Collation ?? "BINARY")))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Rejects attached databases instead of borrowing unrelated main-catalog facts.</summary>
    private static bool IsMain(string? schema) => schema is null || s_identifierComparer.Equals(schema, "main");
}
