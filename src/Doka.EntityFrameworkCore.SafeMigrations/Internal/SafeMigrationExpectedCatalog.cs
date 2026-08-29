namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed class SafeMigrationExpectedTableInventory
{
    public SafeMigrationExpectedTableInventory(
        string table,
        string? schema,
        IEnumerable<string> columns,
        IEnumerable<string> indexes,
        IEnumerable<string> uniqueIndexes,
        IEnumerable<KeyValuePair<string, SafeMigrationDatabaseObjectKind>> constraints
    )
    {
        Table = table;
        Schema = schema;

        Columns = columns.ToHashSet(StringComparer.Ordinal);
        Indexes = indexes.ToHashSet(StringComparer.Ordinal);
        UniqueIndexes = uniqueIndexes.ToHashSet(StringComparer.Ordinal);
        Constraints = constraints.ToDictionary(
            static value => value.Key,
            static value => value.Value,
            StringComparer.Ordinal);
    }

    public string Table { get; }

    public string? Schema { get; }

    public IReadOnlySet<string> Columns { get; }

    public IReadOnlySet<string> Indexes { get; }

    public IReadOnlySet<string> UniqueIndexes { get; }

    public IReadOnlyDictionary<string, SafeMigrationDatabaseObjectKind> Constraints { get; }
}

internal static partial class SafeMigrationExpectedCatalog
{
    public static IReadOnlyList<SafeMigrationExpectedTableInventory> Create(
        IReadOnlyList<MigrationOperation> operations
    )
    {
        ArgumentNullException.ThrowIfNull(operations);

        var tables = new Dictionary<TableKey, MutableTable>();
        foreach (var envelope in operations.OfType<SafeMigrationOperation>())
        {
            Apply(tables, envelope.Intent);
        }

        return tables
            .Values
            .OrderBy(static value => value.Schema, StringComparer.Ordinal)
            .ThenBy(static value => value.Table, StringComparer.Ordinal)
            .Select(static value => value.Snapshot())
            .ToArray();
    }

    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        SafeMigrationIntent intent
    )
    {
        switch (intent)
        {
            case EnsureTableIntent value:
                Apply(tables, value);
                break;
            case DropTableIntent value:
                Apply(tables, value);
                break;
            case RenameTableIntent value:
                Apply(tables, value);
                break;
            case DropSchemaIntent value:
                Apply(tables, value);
                break;
            case EnsureColumnIntent value:
                Apply(tables, value);
                break;
            case DropColumnIntent value:
                Apply(tables, value);
                break;
            case RenameColumnIntent value:
                Apply(tables, value);
                break;
            case EnsureIndexIntent value:
                Apply(tables, value);
                break;
            case DropIndexIntent value:
                Apply(tables, value);
                break;
            case RenameIndexIntent value:
                Apply(tables, value);
                break;
            case EnsurePrimaryKeyIntent value:
                Apply(tables, value);
                break;
            case DropPrimaryKeyIntent value:
                Apply(tables, value);
                break;
            case EnsureUniqueConstraintIntent value:
                Apply(tables, value);
                break;
            case DropUniqueConstraintIntent value:
                Apply(tables, value);
                break;
            case EnsureCheckConstraintIntent value:
                Apply(tables, value);
                break;
            case DropCheckConstraintIntent value:
                Apply(tables, value);
                break;
            case EnsureForeignKeyIntent value:
                Apply(tables, value);
                break;
            case DropForeignKeyIntent value:
                Apply(tables, value);
                break;
        }
    }

    private static MutableTable? Find(
        Dictionary<TableKey, MutableTable> tables,
        string? schema,
        string table
    ) => tables.GetValueOrDefault(new TableKey(schema, table));

    private static void Rename(
        HashSet<string>? names,
        string oldName,
        string newName
    )
    {
        if (names?.Remove(oldName) == true)
        {
            names.Add(newName);
        }
    }

    private static void SetConstraint(
        MutableTable? table,
        string name,
        SafeMigrationDatabaseObjectKind kind
    ) => table?.Constraints[name] = kind;

    private readonly record struct TableKey(
        string? Schema,
        string Table
    );

    private sealed class MutableTable
    {
        private MutableTable(
            string table,
            string? schema
        )
        {
            Table = table;
            Schema = schema;
        }

        public string Table { get; set; }

        public string? Schema { get; set; }

        public HashSet<string> Columns { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Indexes { get; } = new(StringComparer.Ordinal);

        public HashSet<string> UniqueIndexes { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, SafeMigrationDatabaseObjectKind> Constraints { get; } = new(StringComparer.Ordinal);

        public static MutableTable From(
            ExpectedTableDefinition definition
        )
        {
            var table = new MutableTable(definition.Table, definition.Schema);
            table.Columns.UnionWith(definition.Columns.Select(static value => value.Name));
            if (definition.PrimaryKey is not null)
            {
                table.Constraints[definition.PrimaryKey.Name] = SafeMigrationDatabaseObjectKind.PrimaryKey;
            }

            AddConstraints(
                table,
                definition.UniqueConstraints.Select(static value => value.Name),
                SafeMigrationDatabaseObjectKind.UniqueConstraint);
            AddConstraints(
                table,
                definition.CheckConstraints.Select(static value => value.Name),
                SafeMigrationDatabaseObjectKind.CheckConstraint);
            AddConstraints(
                table,
                definition.ForeignKeys.Select(static value => value.Name),
                SafeMigrationDatabaseObjectKind.ForeignKey);

            return table;
        }

        public SafeMigrationExpectedTableInventory Snapshot() => new(
            Table,
            Schema,
            Columns,
            Indexes,
            UniqueIndexes,
            Constraints);

        private static void AddConstraints(
            MutableTable table,
            IEnumerable<string> names,
            SafeMigrationDatabaseObjectKind kind
        )
        {
            foreach (var name in names)
            {
                table.Constraints[name] = kind;
            }
        }
    }
}
