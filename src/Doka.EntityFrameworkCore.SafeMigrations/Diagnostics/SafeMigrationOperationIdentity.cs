namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Identifies an authored operation without retaining SQL, defaults or row values.</summary>
/// <remarks>
/// Qualifiers remain exactly as authored. A null schema means unqualified when a table is known;
/// it is not a resolved live database identity. Unknown or inapplicable owners remain null.
/// </remarks>
public sealed class SafeMigrationOperationIdentity
{
    private static readonly System.Collections.ObjectModel.ReadOnlyCollection<string> s_emptyColumns
        = Array.AsReadOnly(Array.Empty<string>());

    private SafeMigrationOperationIdentity(
        SafeMigrationOperationKind? operationKind,
        string? schema,
        string? table,
        string objectName,
        IReadOnlyList<string>? columns,
        string? principalSchema,
        string? principalTable,
        IReadOnlyList<string>? principalColumns,
        string? newSchema,
        string? newTable,
        string? newObjectName
    )
    {
        OperationKind = operationKind;
        Schema = schema;
        Table = table;
        ObjectName = objectName;
        Columns = columns ?? [];
        PrincipalSchema = principalSchema;
        PrincipalTable = principalTable;
        PrincipalColumns = principalColumns ?? [];
        NewSchema = newSchema;
        NewTable = newTable;
        NewObjectName = newObjectName;
    }

    /// <summary>Gets the known operation family, or null when no safe-operation family applies.</summary>
    /// <remarks>A known family does not certify that an ordinary EF operation is owned or analyzed as safe.</remarks>
    public SafeMigrationOperationKind? OperationKind { get; }

    /// <summary>Gets the authored owner qualifier, or null when unqualified or inapplicable.</summary>
    public string? Schema { get; }

    /// <summary>Gets the owning table, or null when the operation has no known table owner.</summary>
    public string? Table { get; }

    /// <summary>Gets the primary object name with its original, unqualified meaning.</summary>
    public string ObjectName { get; }

    /// <summary>Gets the ordered named columns; functional expressions are never included.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>Gets the authored foreign-key principal qualifier, or null when unqualified or inapplicable.</summary>
    public string? PrincipalSchema { get; }

    /// <summary>Gets the referenced foreign-key table, or null when inapplicable.</summary>
    public string? PrincipalTable { get; }

    /// <summary>Gets ordered referenced columns, or an empty collection when inapplicable or not authored.</summary>
    public IReadOnlyList<string> PrincipalColumns { get; }

    /// <summary>Gets an explicitly authored rename qualifier, or null when unchanged or inapplicable.</summary>
    public string? NewSchema { get; }

    /// <summary>Gets the table-rename target name, including an unchanged name for a qualifier-only rename.</summary>
    public string? NewTable { get; }

    /// <summary>Gets the column or index rename target, or null when inapplicable.</summary>
    public string? NewObjectName { get; }

    /// <summary>Captures identifier-only metadata from every supported immutable safe intent.</summary>
    /// <param name="intent">The immutable intent whose structural identifiers are required.</param>
    /// <returns>An immutable identifier-only view with no resolved-schema guesses.</returns>
    /// <exception cref="ArgumentNullException">The intent is null.</exception>
    public static SafeMigrationOperationIdentity Create(SafeMigrationIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        // WHY: Definitions already own immutable snapshots. Retaining their identifier lists
        // avoids cloning seed matrices, expressions, annotations or entire operation contracts.
        return intent switch
        {
            EnsureSchemaIntent item => CreateKnown(item.Kind, item.Name, null, item.Name),
            DropSchemaIntent item => CreateKnown(item.Kind, item.Name, null, item.Name),
            EnsureTableIntent item => CreateKnown(
                item.Kind, item.Definition.Schema, item.Definition.Table, item.ObjectName,
                CaptureTableColumns(item.Definition.Columns)),
            DropTableIntent item => CreateKnown(item.Kind, item.Schema, item.Table, item.ObjectName),
            RenameTableIntent item => CreateKnown(
                item.Kind, item.Schema, item.Name, item.ObjectName,
                newSchema: item.NewSchema, newTable: item.NewName ?? item.Name),
            EnsureColumnIntent item => CreateKnown(
                item.Kind, item.Schema, item.Table, item.ObjectName, SingleColumn(item.ObjectName)),
            DropColumnIntent item => CreateKnown(
                item.Kind, item.Schema, item.Table, item.ObjectName, SingleColumn(item.Name)),
            RenameColumnIntent item => CreateKnown(
                item.Kind, item.Schema, item.Table, item.ObjectName, SingleColumn(item.Name),
                newObjectName: item.NewName),
            AlterColumnIntent item => CreateKnown(
                item.Kind, item.Schema, item.Table, item.ObjectName, SingleColumn(item.ObjectName)),
            EnsureIndexIntent item => CreateKnown(
                item.Kind, item.Definition.Schema, item.Definition.Table, item.ObjectName,
                CaptureIndexColumns(item.Definition.Keys)),
            DropIndexIntent item => CreateKnown(item.Kind, item.Schema, item.Table, item.ObjectName),
            RenameIndexIntent item => CreateKnown(
                item.Kind, item.Schema, item.Table, item.ObjectName, newObjectName: item.NewName),
            EnsurePrimaryKeyIntent item => CreateKnown(
                item.Kind, item.Definition.Schema, item.Definition.Table, item.ObjectName, item.Definition.Columns),
            DropPrimaryKeyIntent item => CreateKnown(item.Kind, item.Schema, item.Table, item.ObjectName),
            EnsureUniqueConstraintIntent item => CreateKnown(
                item.Kind, item.Definition.Schema, item.Definition.Table, item.ObjectName, item.Definition.Columns),
            DropUniqueConstraintIntent item => CreateKnown(item.Kind, item.Schema, item.Table, item.ObjectName),
            EnsureCheckConstraintIntent item => CreateKnown(
                item.Kind, item.Definition.Schema, item.Definition.Table, item.ObjectName),
            DropCheckConstraintIntent item => CreateKnown(item.Kind, item.Schema, item.Table, item.ObjectName),
            EnsureForeignKeyIntent item => CreateKnown(
                item.Kind, item.Definition.Schema, item.Definition.Table, item.ObjectName, item.Definition.Columns,
                item.Definition.PrincipalSchema, item.Definition.PrincipalTable, item.Definition.PrincipalColumns),
            DropForeignKeyIntent item => CreateKnown(item.Kind, item.Schema, item.Table, item.ObjectName),
            ModelManagedDataIntent item => CreateKnown(
                item.Kind, item.Schema, item.Table, item.ObjectName, item.Columns),
            _ => throw new UnreachableException(),
        };
    }

    /// <summary>Creates an identifier-only view from known authored metadata.</summary>
    /// <remarks>Callers must supply immutable column snapshots, never mutable EF operation arrays.</remarks>
    /// <param name="operationKind">The known family, or null when no safe-operation family applies.</param>
    /// <param name="schema">The authored owner qualifier.</param>
    /// <param name="table">The known owner table.</param>
    /// <param name="objectName">The known primary object name.</param>
    /// <param name="columns">Immutable ordered member identifiers.</param>
    /// <param name="principalSchema">The authored principal qualifier.</param>
    /// <param name="principalTable">The known principal table.</param>
    /// <param name="principalColumns">Immutable ordered principal identifiers.</param>
    /// <param name="newSchema">The explicitly authored target qualifier.</param>
    /// <param name="newTable">The known target table.</param>
    /// <param name="newObjectName">The known target member.</param>
    /// <returns>The immutable structural identity.</returns>
    internal static SafeMigrationOperationIdentity CreateKnown(
        SafeMigrationOperationKind? operationKind,
        string? schema,
        string? table,
        string objectName,
        IReadOnlyList<string>? columns = null,
        string? principalSchema = null,
        string? principalTable = null,
        IReadOnlyList<string>? principalColumns = null,
        string? newSchema = null,
        string? newTable = null,
        string? newObjectName = null
    ) => new(
        operationKind, schema, table, objectName, columns,
        principalSchema, principalTable, principalColumns, newSchema, newTable, newObjectName);

    // WHY: A single-column identity does not need an array plus a collection wrapper.
    // This immutable representation saves one object and 32 measured bytes per retained
    // identity on the qualified local runtime. Formatting consumes it through indexed reads.
    private static SingleIdentifierList SingleColumn(string name) => new(name);

    /// <summary>Captures one known ordinary-operation member without an array and collection wrapper.</summary>
    /// <param name="name">The mutable operation's current member name, or null when unavailable.</param>
    /// <returns>An immutable identifier snapshot, or null when the member name is unavailable.</returns>
    internal static IReadOnlyList<string>? CaptureSingleColumn(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return SingleColumn(name);
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<string> CaptureTableColumns(
        IReadOnlyList<ExpectedColumnDefinition> columns
    )
    {
        var names = new string[columns.Count];
        // WHY: Copy table-member names into matching ordinals of the owned identifier snapshot.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < names.Length; index++)
        {
            names[index] = columns[index].Name;
        }

        return Array.AsReadOnly(names);
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<string> CaptureIndexColumns(
        IReadOnlyList<ExpectedIndexKeyDefinition> keys
    )
    {
        var count = 0;
        // WHY: Indexed copying saved enumerators but showed inconsistent whole-method CPU results.
        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var key in keys)
        {
            count += key.Column is null ? 0 : 1;
        }

        if (count == 0)
        {
            return s_emptyColumns;
        }

        var names = new string[count];
        var index = 0;
        // WHY: Keep the same measured snapshot traversal while filling only captured column names.
        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var key in keys)
        {
            if (key.Column is { } column)
            {
                names[index++] = column;
            }

        }

        return Array.AsReadOnly(names);
    }

    /// <summary>Retains one immutable identifier without an array or mutable collection interface.</summary>
    private sealed class SingleIdentifierList : IReadOnlyList<string>
    {
        private readonly string _value;

        /// <summary>Initializes the one-element identifier view.</summary>
        /// <param name="value">The already validated immutable identifier.</param>
        public SingleIdentifierList(string value)
        {
            _value = value;
        }

        /// <summary>Gets the fixed element count.</summary>
        public int Count => 1;

        /// <summary>Gets the identifier at its only valid ordinal.</summary>
        /// <param name="index">The zero-based ordinal, which must be zero.</param>
        /// <returns>The original immutable identifier.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The ordinal is not zero.</exception>
        public string this[int index] => index == 0 ? _value : throw new ArgumentOutOfRangeException(nameof(index));

        /// <inheritdoc />
        public IEnumerator<string> GetEnumerator()
        {
            yield return _value;
        }

        /// <inheritdoc />
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
