namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Represents the initial, allowed transitional, and terminal constraint
/// contract of one table in an ordered migration direction.
/// </summary>
internal sealed partial class SafeMigrationExpectedTableConstraints
{
    private SafeMigrationExpectedTableConstraints(
        ExpectedPrimaryKeyDefinition? primaryKey,
        IReadOnlyList<ExpectedUniqueConstraintDefinition> uniqueConstraints,
        IReadOnlyList<ExpectedCheckConstraintDefinition> checkConstraints,
        IReadOnlyList<ExpectedForeignKeyDefinition> foreignKeys,
        IReadOnlyList<ExpectedPrimaryKeyDefinition> allowedPrimaryKeys,
        bool primaryKeyMayBeAbsent,
        IReadOnlyList<ExpectedUniqueConstraintDefinition> allowedUniqueConstraints,
        IReadOnlyList<ExpectedUniqueConstraintDefinition> requiredUniqueConstraints,
        IReadOnlyList<ExpectedCheckConstraintDefinition> allowedCheckConstraints,
        IReadOnlyList<ExpectedCheckConstraintDefinition> requiredCheckConstraints,
        IReadOnlyList<ExpectedForeignKeyDefinition> allowedForeignKeys,
        IReadOnlyList<ExpectedForeignKeyDefinition> requiredForeignKeys
    )
    {
        PrimaryKey = primaryKey;
        UniqueConstraints = uniqueConstraints;
        CheckConstraints = checkConstraints;
        ForeignKeys = foreignKeys;
        AllowedPrimaryKeys = allowedPrimaryKeys;
        PrimaryKeyMayBeAbsent = primaryKeyMayBeAbsent;
        AllowedUniqueConstraints = allowedUniqueConstraints;
        RequiredUniqueConstraints = requiredUniqueConstraints;
        AllowedCheckConstraints = allowedCheckConstraints;
        RequiredCheckConstraints = requiredCheckConstraints;
        AllowedForeignKeys = allowedForeignKeys;
        RequiredForeignKeys = requiredForeignKeys;
    }

    /// <summary>Gets the terminal primary-key definition, or null when the table has no terminal key.</summary>
    public ExpectedPrimaryKeyDefinition? PrimaryKey { get; }

    /// <summary>Gets the terminal unique-constraint definitions.</summary>
    public IReadOnlyList<ExpectedUniqueConstraintDefinition> UniqueConstraints { get; }

    /// <summary>Gets the terminal CHECK-constraint definitions.</summary>
    public IReadOnlyList<ExpectedCheckConstraintDefinition> CheckConstraints { get; }

    /// <summary>Gets the terminal foreign-key definitions.</summary>
    public IReadOnlyList<ExpectedForeignKeyDefinition> ForeignKeys { get; }

    /// <summary>Gets primary-key shapes allowed in initial, transitional or terminal table states.</summary>
    public IReadOnlyList<ExpectedPrimaryKeyDefinition> AllowedPrimaryKeys { get; }

    /// <summary>Gets whether an allowed table state may omit the primary key.</summary>
    public bool PrimaryKeyMayBeAbsent { get; }

    /// <summary>Gets unique-constraint shapes allowed in initial, transitional or terminal table states.</summary>
    public IReadOnlyList<ExpectedUniqueConstraintDefinition> AllowedUniqueConstraints { get; }

    /// <summary>Gets unique constraints whose absence is not an allowed transitional state.</summary>
    public IReadOnlyList<ExpectedUniqueConstraintDefinition> RequiredUniqueConstraints { get; }

    /// <summary>Gets CHECK-constraint shapes allowed in initial, transitional or terminal table states.</summary>
    public IReadOnlyList<ExpectedCheckConstraintDefinition> AllowedCheckConstraints { get; }

    /// <summary>Gets CHECK constraints whose absence is not an allowed transitional state.</summary>
    public IReadOnlyList<ExpectedCheckConstraintDefinition> RequiredCheckConstraints { get; }

    /// <summary>Gets foreign-key shapes allowed in initial, transitional or terminal table states.</summary>
    public IReadOnlyList<ExpectedForeignKeyDefinition> AllowedForeignKeys { get; }

    /// <summary>Gets foreign keys whose absence is not an allowed transitional state.</summary>
    public IReadOnlyList<ExpectedForeignKeyDefinition> RequiredForeignKeys { get; }

    /// <summary>
    /// Projects the table-constraint transition contract from one ordered operation stream.
    /// </summary>
    /// <param name="operations">The operations generated for one migration direction.</param>
    /// <returns>Constraint contracts keyed by exact schema and table identity.</returns>
    public static IReadOnlyDictionary<(string? Schema, string Table), SafeMigrationExpectedTableConstraints>
        FromOperations(
            IReadOnlyList<MigrationOperation> operations
        ) => FromOperations(operations, static schema => schema);

    /// <summary>
    /// Projects the table-constraint transition contract with provider-normalized table identity.
    /// </summary>
    /// <param name="operations">The operations generated for one migration direction.</param>
    /// <param name="normalizeSchema">The provider qualifier normalizer.</param>
    /// <returns>Constraint contracts keyed by normalized physical table identity.</returns>
    public static IReadOnlyDictionary<(string? Schema, string Table), SafeMigrationExpectedTableConstraints>
        FromOperations(
            IReadOnlyList<MigrationOperation> operations,
            Func<string?, string?> normalizeSchema
        ) => FromOperations(operations, normalizeSchema, StringComparer.Ordinal);

    /// <summary>Projects transitions with the provider's physical table-name equality.</summary>
    /// <param name="operations">The operations generated for one bounded migration direction.</param>
    /// <param name="normalizeSchema">The provider qualifier normalizer.</param>
    /// <param name="tableComparer">The provider's physical table-name comparer.</param>
    /// <returns>Constraint contracts keyed by normalized physical table identity.</returns>
    public static IReadOnlyDictionary<(string? Schema, string Table), SafeMigrationExpectedTableConstraints>
        FromOperations(
            IReadOnlyList<MigrationOperation> operations,
            Func<string?, string?> normalizeSchema,
            StringComparer tableComparer
        ) => FromOperations(operations, normalizeSchema, tableComparer, null, false);

    /// <summary>Projects captured CHECK transitions with the provider's verified native identifier roles.</summary>
    /// <param name="operations">The complete ordered migration direction.</param>
    /// <param name="normalizeSchema">The provider qualifier normalizer.</param>
    /// <param name="tableComparer">The physical table-name comparer.</param>
    /// <param name="capturedSqlIdentifierDelimiter">The native quote for an originally unquoted renamed member.</param>
    /// <param name="foldUnquotedCapturedSqlIdentifiers">
    /// Whether native unquoted ASCII identifiers fold to lowercase.
    /// </param>
    /// <returns>The initial, allowed transitional, and terminal physical constraint contracts.</returns>
    public static IReadOnlyDictionary<(string? Schema, string Table), SafeMigrationExpectedTableConstraints>
        FromOperations(
            IReadOnlyList<MigrationOperation> operations,
            Func<string?, string?> normalizeSchema,
            StringComparer tableComparer,
            char? capturedSqlIdentifierDelimiter,
            bool foldUnquotedCapturedSqlIdentifiers
        ) => FromOperations(
        operations,
        normalizeSchema,
        tableComparer,
        capturedSqlIdentifierDelimiter,
        foldUnquotedCapturedSqlIdentifiers,
        StringComparer.Ordinal);

    /// <summary>Separates certified column-member identity from physical table-name identity.</summary>
    /// <param name="operations">The complete ordered migration direction.</param>
    /// <param name="normalizeSchema">The provider qualifier normalizer.</param>
    /// <param name="tableComparer">The physical table-name comparer.</param>
    /// <param name="capturedSqlIdentifierDelimiter">The native quote for an originally unquoted renamed member.</param>
    /// <param name="foldUnquotedCapturedSqlIdentifiers">Whether unquoted ASCII column roles fold to lowercase.</param>
    /// <param name="columnComparer">The independently certified physical column-member comparer.</param>
    /// <returns>The initial, allowed transitional, and terminal constraint contracts.</returns>
    public static IReadOnlyDictionary<(string? Schema, string Table), SafeMigrationExpectedTableConstraints>
        FromOperations(
            IReadOnlyList<MigrationOperation> operations,
            Func<string?, string?> normalizeSchema,
            StringComparer tableComparer,
            char? capturedSqlIdentifierDelimiter,
            bool foldUnquotedCapturedSqlIdentifiers,
            IEqualityComparer<string> columnComparer
        )
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(normalizeSchema);
        ArgumentNullException.ThrowIfNull(tableComparer);
        ArgumentNullException.ThrowIfNull(columnComparer);

        var comparer = new TableIdentityComparer(normalizeSchema, tableComparer);
        var tables = new Dictionary<(string? Schema, string Table), MutableConstraints>(comparer);

        // WHY: EF splits cyclic and provider-ordered constraints out of
        // CreateTable. Strict analysis must accept recoverable intermediate
        // states without treating future constraints as already mandatory.
        foreach (var operation in operations.OfType<SafeMigrationOperation>())
        {
            Apply(
                tables,
                operation.Intent,
                normalizeSchema,
                capturedSqlIdentifierDelimiter,
                foldUnquotedCapturedSqlIdentifiers,
                columnComparer);
        }

        var result =
            new Dictionary<(string? Schema, string Table), SafeMigrationExpectedTableConstraints>(
                tables.Count,
                comparer);

        foreach (var pair in tables)
        {
            result.Add(pair.Key, pair.Value.Snapshot());
        }

        return result;
    }

    private sealed class TableIdentityComparer(
        Func<string?, string?> normalizeSchema,
        StringComparer tableComparer
    ) : IEqualityComparer<(string? Schema, string Table)>
    {
        public bool Equals(
            (string? Schema, string Table) left,
            (string? Schema, string Table) right
        ) => tableComparer.Equals(left.Table, right.Table)
            && StringComparer.Ordinal.Equals(normalizeSchema(left.Schema), normalizeSchema(right.Schema));

        public int GetHashCode(
            (string? Schema, string Table) value
        ) => HashCode.Combine(
            tableComparer.GetHashCode(value.Table),
            normalizeSchema(value.Schema) is { } schema ? StringComparer.Ordinal.GetHashCode(schema) : 0);
    }
}
