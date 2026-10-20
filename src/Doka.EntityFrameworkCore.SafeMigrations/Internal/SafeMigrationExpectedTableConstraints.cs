namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Represents the initial, allowed transitional, and terminal constraint
/// contract of one table in an ordered migration direction.
/// </summary>
internal sealed class SafeMigrationExpectedTableConstraints
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

    public ExpectedPrimaryKeyDefinition? PrimaryKey { get; }

    public IReadOnlyList<ExpectedUniqueConstraintDefinition> UniqueConstraints { get; }

    public IReadOnlyList<ExpectedCheckConstraintDefinition> CheckConstraints { get; }

    public IReadOnlyList<ExpectedForeignKeyDefinition> ForeignKeys { get; }

    public IReadOnlyList<ExpectedPrimaryKeyDefinition> AllowedPrimaryKeys { get; }

    public bool PrimaryKeyMayBeAbsent { get; }

    public IReadOnlyList<ExpectedUniqueConstraintDefinition> AllowedUniqueConstraints { get; }

    public IReadOnlyList<ExpectedUniqueConstraintDefinition> RequiredUniqueConstraints { get; }

    public IReadOnlyList<ExpectedCheckConstraintDefinition> AllowedCheckConstraints { get; }

    public IReadOnlyList<ExpectedCheckConstraintDefinition> RequiredCheckConstraints { get; }

    public IReadOnlyList<ExpectedForeignKeyDefinition> AllowedForeignKeys { get; }

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

    /// <summary>
    /// Resolves the target constraint contract from EF's relational model.
    /// </summary>
    /// <param name="model">The migration target model.</param>
    /// <param name="table">The table name.</param>
    /// <param name="schema">The table schema, or null for the provider default.</param>
    /// <param name="runtimeCheckConstraints">
    /// The operation-owned check constraints used when EF supplies a
    /// read-optimized runtime model.
    /// </param>
    /// <returns>The target constraints, or null when the model does not own the table.</returns>
    public static SafeMigrationExpectedTableConstraints? FromModel(
        IModel? model,
        string table,
        string? schema,
        IReadOnlyList<ExpectedCheckConstraintDefinition>? runtimeCheckConstraints = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        var relationalTable = model
            ?.GetRelationalModel()
            .FindTable(table, schema);
        if (relationalTable is null)
        {
            return null;
        }

        var primaryKey = relationalTable.PrimaryKey is not null
            ? new ExpectedPrimaryKeyDefinition(
                relationalTable.PrimaryKey.Name,
                table,
                relationalTable.PrimaryKey.Columns.Select(static column => column.Name),
                schema)
            : null;

        var uniqueConstraints = relationalTable
            .UniqueConstraints
            .Where(constraint => !ReferenceEquals(constraint, relationalTable.PrimaryKey))
            .OrderBy(static constraint => constraint.Name, StringComparer.Ordinal)
            .Select(constraint => new ExpectedUniqueConstraintDefinition(
                constraint.Name,
                table,
                constraint.Columns.Select(static column => column.Name),
                schema))
            .ToArray();

        var checkConstraints = ReadCheckConstraints(relationalTable, table, schema, runtimeCheckConstraints);

        var foreignKeys = relationalTable
            .ForeignKeyConstraints
            .OrderBy(static constraint => constraint.Name, StringComparer.Ordinal)
            .Select(constraint => new ExpectedForeignKeyDefinition(
                constraint.Name,
                table,
                constraint.Columns.Select(static column => column.Name),
                constraint.PrincipalTable.Name,
                constraint.PrincipalColumns.Select(static column => column.Name),
                schema,
                constraint.PrincipalTable.Schema,
                onDelete: constraint.OnDeleteAction))
            .ToArray();

        return new SafeMigrationExpectedTableConstraints(
            primaryKey,
            uniqueConstraints,
            checkConstraints,
            foreignKeys,
            primaryKey is null ? [] : [primaryKey],
            primaryKey is null,
            uniqueConstraints,
            uniqueConstraints,
            checkConstraints,
            checkConstraints,
            foreignKeys,
            foreignKeys);
    }

    private static IReadOnlyList<ExpectedCheckConstraintDefinition> ReadCheckConstraints(
        ITable table,
        string tableName,
        string? schema,
        IReadOnlyList<ExpectedCheckConstraintDefinition>? runtimeCheckConstraints
    )
    {
        IEnumerable<ICheckConstraint> checkConstraints;

        try
        {
            checkConstraints = table.CheckConstraints;
        }
        catch (InvalidOperationException) when (runtimeCheckConstraints is not null)
        {
            // WHY: EF deliberately omits check-constraint configuration from
            // runtime models. The exact EnsureTable definition remains the
            // authoritative baseline for those constraints.
            return runtimeCheckConstraints;
        }

        return checkConstraints
            .OrderBy(static constraint => constraint.Name, StringComparer.Ordinal)
            .Select(constraint => SafeMigrationExpectedDefinitionFactory.CheckConstraint(
                constraint.Name
                ?? throw new InvalidOperationException(
                    $"The relational check constraint on '{tableName}' has no database name."),
                tableName,
                constraint.Sql,
                schema))
            .ToArray();
    }

    private static void Apply(
        Dictionary<(string? Schema, string Table), MutableConstraints> tables,
        SafeMigrationIntent intent,
        Func<string?, string?> normalizeSchema,
        char? capturedSqlIdentifierDelimiter,
        bool foldUnquotedCapturedSqlIdentifiers,
        IEqualityComparer<string> columnComparer
    )
    {
        switch (intent)
        {
            case EnsureTableIntent value:
                tables[(value.Definition.Schema, value.Definition.Table)] = MutableConstraints.From(
                    value.Definition,
                    normalizeSchema,
                    capturedSqlIdentifierDelimiter,
                    foldUnquotedCapturedSqlIdentifiers,
                    columnComparer);
                break;
            case DropTableIntent value:
                tables.Remove((value.Schema, value.Table));
                break;
            case RenameTableIntent value:
                RenameTable(tables, value, normalizeSchema);
                break;
            case DropColumnIntent value:
                DropColumn(tables, value, normalizeSchema);
                break;
            case RenameColumnIntent value:
                RenameColumn(tables, value, normalizeSchema);
                break;
            case EnsurePrimaryKeyIntent value
                when Find(tables, value.Definition.Schema, value.Definition.Table) is { } table:
                table.EnsurePrimaryKey(value.Definition);
                break;
            case DropPrimaryKeyIntent value
                when Find(tables, value.Schema, value.Table) is { PrimaryKey: { } primaryKey } table
                && StringComparer.Ordinal.Equals(primaryKey.Name, value.Name):
                table.DropPrimaryKey();
                break;
            case EnsureUniqueConstraintIntent value
                when Find(tables, value.Definition.Schema, value.Definition.Table) is { } table:
                table.EnsureUniqueConstraint(value.Definition);
                break;
            case DropUniqueConstraintIntent value when Find(tables, value.Schema, value.Table) is { } table:
                table.DropUniqueConstraint(value.Name);
                break;
            case EnsureCheckConstraintIntent value
                when Find(tables, value.Definition.Schema, value.Definition.Table) is { } table:
                table.EnsureCheckConstraint(value.Definition);
                break;
            case DropCheckConstraintIntent value when Find(tables, value.Schema, value.Table) is { } table:
                table.DropCheckConstraint(value.Name);
                break;
            case EnsureForeignKeyIntent value
                when Find(tables, value.Definition.Schema, value.Definition.Table) is { } table:
                table.EnsureForeignKey(value.Definition);
                break;
            case DropForeignKeyIntent value when Find(tables, value.Schema, value.Table) is { } table:
                table.DropForeignKey(value.Name);
                break;
        }
    }

    private static MutableConstraints? Find(
        Dictionary<(string? Schema, string Table), MutableConstraints> tables,
        string? schema,
        string table
    ) => tables.GetValueOrDefault((schema, table));

    private static void RenameTable(
        Dictionary<(string? Schema, string Table), MutableConstraints> tables,
        RenameTableIntent intent,
        Func<string?, string?> normalizeSchema
    )
    {
        if (!tables.Remove((intent.Schema, intent.Name), out var table))
        {
            return;
        }

        var newTable = intent.NewName ?? intent.Name;
        var newSchema = normalizeSchema(intent.NewSchema ?? intent.Schema);
        table.RenameOwner(newTable, newSchema);
        tables[(newSchema, newTable)] = table;

        foreach (var candidate in tables.Values)
        {
            candidate.RenamePrincipal(intent.Name, normalizeSchema(intent.Schema), newTable, newSchema);
        }
    }

    private static void RenameColumn(
        Dictionary<(string? Schema, string Table), MutableConstraints> tables,
        RenameColumnIntent intent,
        Func<string?, string?> normalizeSchema
    )
    {
        Find(tables, intent.Schema, intent.Table)
            ?.RenameLocalColumn(intent.Name, intent.NewName);

        foreach (var candidate in tables.Values)
        {
            candidate.RenamePrincipalColumn(intent.Table, normalizeSchema(intent.Schema), intent.Name, intent.NewName);
        }
    }

    private static void DropColumn(
        Dictionary<(string? Schema, string Table), MutableConstraints> tables,
        DropColumnIntent intent,
        Func<string?, string?> normalizeSchema
    )
    {
        Find(tables, intent.Schema, intent.Table)
            ?.ValidateLocalColumnDrop(intent.Name);

        foreach (var candidate in tables.Values)
        {
            candidate.ValidatePrincipalColumnDrop(intent.Table, normalizeSchema(intent.Schema), intent.Name);
        }
    }

    private sealed class MutableConstraints
    {
        private readonly Func<string?, string?> _normalizeSchema;
        private readonly char? _capturedSqlIdentifierDelimiter;
        private readonly bool _foldUnquotedCapturedSqlIdentifiers;
        private readonly IEqualityComparer<string> _columnComparer;
        private readonly List<ExpectedPrimaryKeyDefinition> _allowedPrimaryKeys = [];
        private readonly List<ExpectedUniqueConstraintDefinition> _allowedUniqueConstraints = [];
        private readonly List<ExpectedCheckConstraintDefinition> _allowedCheckConstraints = [];
        private readonly List<ExpectedForeignKeyDefinition> _allowedForeignKeys = [];
        private readonly Dictionary<string, ExpectedUniqueConstraintDefinition> _requiredUniqueConstraints;
        private readonly Dictionary<string, ExpectedCheckConstraintDefinition> _requiredCheckConstraints;
        private readonly Dictionary<string, ExpectedForeignKeyDefinition> _requiredForeignKeys;
        private bool _primaryKeyMayBeAbsent;

        private MutableConstraints(
            ExpectedPrimaryKeyDefinition? primaryKey,
            IEnumerable<ExpectedUniqueConstraintDefinition> uniqueConstraints,
            IEnumerable<ExpectedCheckConstraintDefinition> checkConstraints,
            IEnumerable<ExpectedForeignKeyDefinition> foreignKeys,
            Func<string?, string?> normalizeSchema,
            char? capturedSqlIdentifierDelimiter,
            bool foldUnquotedCapturedSqlIdentifiers,
            IEqualityComparer<string> columnComparer
        )
        {
            _normalizeSchema = normalizeSchema;
            _capturedSqlIdentifierDelimiter = capturedSqlIdentifierDelimiter;
            _foldUnquotedCapturedSqlIdentifiers = foldUnquotedCapturedSqlIdentifiers;
            _columnComparer = columnComparer;

            var uniqueConstraintSnapshot = uniqueConstraints
                .Select(Normalize)
                .ToArray();
            var checkConstraintSnapshot = checkConstraints
                .Select(Normalize)
                .ToArray();
            var foreignKeySnapshot = foreignKeys
                .Select(Normalize)
                .ToArray();

            PrimaryKey = primaryKey is null ? null : Normalize(primaryKey);
            _primaryKeyMayBeAbsent = primaryKey is null;
            if (PrimaryKey is not null)
            {
                _allowedPrimaryKeys.Add(PrimaryKey);
            }

            UniqueConstraints = uniqueConstraintSnapshot.ToDictionary(
                static value => value.Name,
                StringComparer.Ordinal);
            CheckConstraints = checkConstraintSnapshot.ToDictionary(static value => value.Name, StringComparer.Ordinal);
            ForeignKeys = foreignKeySnapshot.ToDictionary(static value => value.Name, StringComparer.Ordinal);

            _allowedUniqueConstraints.AddRange(uniqueConstraintSnapshot);
            _allowedCheckConstraints.AddRange(checkConstraintSnapshot);
            _allowedForeignKeys.AddRange(foreignKeySnapshot);
            _requiredUniqueConstraints = new Dictionary<string, ExpectedUniqueConstraintDefinition>(
                UniqueConstraints,
                StringComparer.Ordinal);
            _requiredCheckConstraints = new Dictionary<string, ExpectedCheckConstraintDefinition>(
                CheckConstraints,
                StringComparer.Ordinal);
            _requiredForeignKeys = new Dictionary<string, ExpectedForeignKeyDefinition>(
                ForeignKeys,
                StringComparer.Ordinal);
        }

        public ExpectedPrimaryKeyDefinition? PrimaryKey { get; private set; }

        public Dictionary<string, ExpectedUniqueConstraintDefinition> UniqueConstraints { get; }

        public Dictionary<string, ExpectedCheckConstraintDefinition> CheckConstraints { get; }

        public Dictionary<string, ExpectedForeignKeyDefinition> ForeignKeys { get; }

        public static MutableConstraints From(
            ExpectedTableDefinition definition,
            Func<string?, string?> normalizeSchema,
            char? capturedSqlIdentifierDelimiter,
            bool foldUnquotedCapturedSqlIdentifiers,
            IEqualityComparer<string> columnComparer
        ) => new(
            definition.PrimaryKey,
            definition.UniqueConstraints,
            definition.CheckConstraints,
            definition.ForeignKeys,
            normalizeSchema,
            capturedSqlIdentifierDelimiter,
            foldUnquotedCapturedSqlIdentifiers,
            columnComparer);

        public SafeMigrationExpectedTableConstraints Snapshot() => new(
            PrimaryKey,
            UniqueConstraints
                .Values
                .OrderBy(static value => value.Name, StringComparer.Ordinal)
                .ToArray(),
            CheckConstraints
                .Values
                .OrderBy(static value => value.Name, StringComparer.Ordinal)
                .ToArray(),
            ForeignKeys
                .Values
                .OrderBy(static value => value.Name, StringComparer.Ordinal)
                .ToArray(),
            _allowedPrimaryKeys.ToArray(),
            _primaryKeyMayBeAbsent,
            Order(_allowedUniqueConstraints),
            Order(_requiredUniqueConstraints.Values),
            Order(_allowedCheckConstraints),
            Order(_requiredCheckConstraints.Values),
            Order(_allowedForeignKeys),
            Order(_requiredForeignKeys.Values));

        public void EnsurePrimaryKey(
            ExpectedPrimaryKeyDefinition definition
        )
        {
            definition = Normalize(definition);

            if (PrimaryKey is null
                || !SafeMigrationDefinitionEquivalence.PrimaryKey(PrimaryKey, definition))
            {
                _primaryKeyMayBeAbsent = true;
            }

            PrimaryKey = definition;
            AddAllowed(_allowedPrimaryKeys, definition, SafeMigrationDefinitionEquivalence.PrimaryKey);
        }

        public void DropPrimaryKey()
        {
            PrimaryKey = null;
            _primaryKeyMayBeAbsent = true;
        }

        public void EnsureUniqueConstraint(
            ExpectedUniqueConstraintDefinition definition
        )
        {
            definition = Normalize(definition);

            Ensure(
                UniqueConstraints,
                _allowedUniqueConstraints,
                _requiredUniqueConstraints,
                definition,
                SafeMigrationDefinitionEquivalence.UniqueConstraint);
        }

        public void DropUniqueConstraint(
            string name
        ) => Drop(UniqueConstraints, _requiredUniqueConstraints, name);

        public void EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition definition
        )
        {
            definition = Normalize(definition);

            Ensure(
                CheckConstraints,
                _allowedCheckConstraints,
                _requiredCheckConstraints,
                definition,
                SafeMigrationDefinitionEquivalence.CheckConstraint);
        }

        public void DropCheckConstraint(
            string name
        ) => Drop(CheckConstraints, _requiredCheckConstraints, name);

        public void EnsureForeignKey(
            ExpectedForeignKeyDefinition definition
        )
        {
            definition = Normalize(definition);

            Ensure(
                ForeignKeys,
                _allowedForeignKeys,
                _requiredForeignKeys,
                definition,
                SafeMigrationDefinitionEquivalence.ForeignKey);
        }

        public void DropForeignKey(
            string name
        ) => Drop(ForeignKeys, _requiredForeignKeys, name);

        public void RenameOwner(
            string table,
            string? schema
        )
        {
            schema = _normalizeSchema(schema);

            if (PrimaryKey is not null)
            {
                var renamed = new ExpectedPrimaryKeyDefinition(PrimaryKey.Name, table, PrimaryKey.Columns, schema);

                EnsurePrimaryKey(renamed);
            }

            ReplaceValues(
                UniqueConstraints,
                _allowedUniqueConstraints,
                _requiredUniqueConstraints,
                SafeMigrationDefinitionEquivalence.UniqueConstraint,
                value => new ExpectedUniqueConstraintDefinition(value.Name, table, value.Columns, schema));

            ReplaceValues(
                CheckConstraints,
                _allowedCheckConstraints,
                _requiredCheckConstraints,
                SafeMigrationDefinitionEquivalence.CheckConstraint,
                value => CopyCheck(value, table, schema));

            ReplaceValues(
                ForeignKeys,
                _allowedForeignKeys,
                _requiredForeignKeys,
                SafeMigrationDefinitionEquivalence.ForeignKey,
                value => CopyForeignKey(value, table: table, schema: schema));
        }

        public void RenamePrincipal(
            string table,
            string? schema,
            string newTable,
            string? newSchema
        )
        {
            schema = _normalizeSchema(schema);
            newSchema = _normalizeSchema(newSchema);

            ReplaceValues(
                ForeignKeys,
                _allowedForeignKeys,
                _requiredForeignKeys,
                SafeMigrationDefinitionEquivalence.ForeignKey,
                value => StringComparer.Ordinal.Equals(value.PrincipalTable, table)
                    && StringComparer.Ordinal.Equals(value.PrincipalSchema, schema)
                        ? CopyForeignKey(value, principalTable: newTable, principalSchema: newSchema)
                        : value);
        }

        public void RenameLocalColumn(
            string column,
            string newColumn
        )
        {
            if (PrimaryKey is not null)
            {
                var renamed = new ExpectedPrimaryKeyDefinition(
                    PrimaryKey.Name,
                    PrimaryKey.Table,
                    RenameColumns(PrimaryKey.Columns, column, newColumn),
                    PrimaryKey.Schema);

                EnsurePrimaryKey(renamed);
            }

            ReplaceValues(
                UniqueConstraints,
                _allowedUniqueConstraints,
                _requiredUniqueConstraints,
                SafeMigrationDefinitionEquivalence.UniqueConstraint,
                value => new ExpectedUniqueConstraintDefinition(
                    value.Name,
                    value.Table,
                    RenameColumns(value.Columns, column, newColumn),
                    value.Schema));

            ReplaceValues(
                CheckConstraints,
                _allowedCheckConstraints,
                _requiredCheckConstraints,
                SafeMigrationDefinitionEquivalence.CheckConstraint,
                value => RenameCheckColumn(value, column, newColumn));

            ReplaceValues(
                ForeignKeys,
                _allowedForeignKeys,
                _requiredForeignKeys,
                SafeMigrationDefinitionEquivalence.ForeignKey,
                value => CopyForeignKey(value, columns: RenameColumns(value.Columns, column, newColumn)));
        }

        public void RenamePrincipalColumn(
            string table,
            string? schema,
            string column,
            string newColumn
        )
        {
            schema = _normalizeSchema(schema);

            ReplaceValues(
                ForeignKeys,
                _allowedForeignKeys,
                _requiredForeignKeys,
                SafeMigrationDefinitionEquivalence.ForeignKey,
                value => StringComparer.Ordinal.Equals(value.PrincipalTable, table)
                    && StringComparer.Ordinal.Equals(value.PrincipalSchema, schema)
                        ? CopyForeignKey(
                            value,
                            principalColumns: RenameColumns(value.PrincipalColumns, column, newColumn))
                        : value);
        }

        public void ValidateLocalColumnDrop(
            string column
        )
        {
            if (PrimaryKey is not null
                && ContainsColumn(PrimaryKey.Columns, column))
            {
                ThrowColumnDropDependency(column, PrimaryKey.Table, "primary key", PrimaryKey.Name);
            }

            var uniqueConstraint =
                UniqueConstraints.Values.FirstOrDefault(value => ContainsColumn(value.Columns, column));
            if (uniqueConstraint is not null)
            {
                ThrowColumnDropDependency(column, uniqueConstraint.Table, "unique constraint", uniqueConstraint.Name);
            }

            var checkConstraint =
                CheckConstraints.Values.FirstOrDefault(value => CheckConstraintMayReferenceColumn(value, column));
            if (checkConstraint is not null)
            {
                ThrowColumnDropDependency(column, checkConstraint.Table, "check constraint", checkConstraint.Name);
            }

            var foreignKey = ForeignKeys.Values.FirstOrDefault(value => ContainsColumn(value.Columns, column));
            if (foreignKey is not null)
            {
                ThrowColumnDropDependency(column, foreignKey.Table, "foreign key", foreignKey.Name);
            }
        }

        public void ValidatePrincipalColumnDrop(
            string table,
            string? schema,
            string column
        )
        {
            schema = _normalizeSchema(schema);

            var foreignKey = ForeignKeys.Values.FirstOrDefault(value =>
                StringComparer.Ordinal.Equals(value.PrincipalTable, table)
                && StringComparer.Ordinal.Equals(value.PrincipalSchema, schema)
                && ContainsColumn(value.PrincipalColumns, column));
            if (foreignKey is null)
            {
                return;
            }

            ThrowColumnDropDependency(column, table, "referencing foreign key", foreignKey.Name, foreignKey.Table);
        }

        private ExpectedPrimaryKeyDefinition Normalize(
            ExpectedPrimaryKeyDefinition definition
        ) => new(definition.Name, definition.Table, definition.Columns, _normalizeSchema(definition.Schema));

        private ExpectedUniqueConstraintDefinition Normalize(
            ExpectedUniqueConstraintDefinition definition
        ) => new(definition.Name, definition.Table, definition.Columns, _normalizeSchema(definition.Schema));

        private ExpectedCheckConstraintDefinition Normalize(
            ExpectedCheckConstraintDefinition definition
        ) => SafeMigrationCapturedExpressionNormalization.NormalizeCheck(
            CopyCheck(definition, definition.Table, _normalizeSchema(definition.Schema)),
            _foldUnquotedCapturedSqlIdentifiers);

        /// <summary>Renames only certified column roles without re-rendering captured native SQL.</summary>
        private ExpectedCheckConstraintDefinition RenameCheckColumn(
            ExpectedCheckConstraintDefinition definition,
            string column,
            string newColumn
        )
        {
            if (definition.Expression is not { } expression
                || !SafeMigrationSqlExpressionInspector.IsStructurallyComparable(expression))
            {
                throw CheckColumnRenameUnproven(definition, column, newColumn);
            }

            if (!SafeMigrationSqlExpressionInspector.ReferencesColumnIdentifier(expression, column, _columnComparer))
            {
                return definition;
            }

            if (definition.CapturedSql is { } source)
            {
                // WHY: Native numeric domains and function/type spellings
                // must survive a rename byte-for-byte. The bounded parser
                // rewrites only terminal member tokens; absent native quote
                // authority or opaque source retains the fail-closed boundary.
                if (!SafeMigrationSqlExpressionParser.TryRenameColumnIdentifier(
                        source,
                        column,
                        newColumn,
                        _columnComparer,
                        out var renamedSql,
                        out var renamedExpression,
                        _capturedSqlIdentifierDelimiter,
                        _foldUnquotedCapturedSqlIdentifiers))
                {
                    throw CheckColumnRenameUnproven(definition, column, newColumn);
                }

                return ExpectedCheckConstraintDefinition.FromCapturedExpression(
                    definition.Name,
                    definition.Table,
                    renamedExpression,
                    renamedSql,
                    definition.Schema);
            }

            return ExpectedCheckConstraintDefinition.FromExpression(
                definition.Name,
                definition.Table,
                SafeMigrationSqlExpressionInspector.RenameColumnIdentifier(
                    expression,
                    column,
                    newColumn,
                    _columnComparer),
                definition.Schema);
        }

        /// <summary>Retains the explicit drop/recreate requirement when a CHECK rename cannot be proved.</summary>
        private static InvalidOperationException CheckColumnRenameUnproven(
            ExpectedCheckConstraintDefinition definition,
            string column,
            string newColumn
        ) => new(
            $"Cannot project column rename '{column}' to '{newColumn}' on table "
            + $"'{definition.Table}'. Drop and recreate owned check constraints "
            + "around the rename so their terminal expressions remain explicit.");

        private ExpectedForeignKeyDefinition Normalize(
            ExpectedForeignKeyDefinition definition
        ) => new(
            definition.Name,
            definition.Table,
            definition.Columns,
            definition.PrincipalTable,
            definition.PrincipalColumns,
            _normalizeSchema(definition.Schema),
            _normalizeSchema(definition.PrincipalSchema),
            definition.OnUpdate,
            definition.OnDelete);

        private static void ReplaceValues<T>(
            Dictionary<string, T> values,
            List<T> allowed,
            Dictionary<string, T> required,
            Func<T, T, bool> equivalent,
            Func<T, T> replace
        )
            where T : class
        {
            foreach (var pair in values.ToArray())
            {
                var replacement = replace(pair.Value);
                if (equivalent(pair.Value, replacement))
                {
                    continue;
                }

                required.Remove(pair.Key);
                values[pair.Key] = replacement;
                AddAllowed(allowed, replacement, equivalent);
            }
        }

        private static void Ensure<T>(
            Dictionary<string, T> values,
            List<T> allowed,
            Dictionary<string, T> required,
            T definition,
            Func<T, T, bool> equivalent
        )
            where T : class
        {
            var name = GetName(definition);
            if (!values.TryGetValue(name, out var current)
                || !equivalent(current, definition))
            {
                required.Remove(name);
            }

            values[name] = definition;
            AddAllowed(allowed, definition, equivalent);
        }

        private static void Drop<T>(
            Dictionary<string, T> values,
            Dictionary<string, T> required,
            string name
        )
        {
            values.Remove(name);
            required.Remove(name);
        }

        private static void AddAllowed<T>(
            List<T> allowed,
            T definition,
            Func<T, T, bool> equivalent
        )
        {
            if (!allowed.Any(value => equivalent(value, definition)))
            {
                allowed.Add(definition);
            }
        }

        private static string GetName<T>(
            T definition
        ) => definition switch
        {
            ExpectedUniqueConstraintDefinition value => value.Name,
            ExpectedCheckConstraintDefinition value => value.Name,
            ExpectedForeignKeyDefinition value => value.Name,
            _ => throw new InvalidOperationException(
                $"Unsupported table constraint definition '{typeof(T).FullName}'."),
        };

        private static T[] Order<T>(
            IEnumerable<T> values
        ) => values
            .OrderBy(GetName, StringComparer.Ordinal)
            .ToArray();

        private static string[] RenameColumns(
            IReadOnlyList<string> columns,
            string column,
            string newColumn
        ) => columns
            .Select(value => StringComparer.Ordinal.Equals(value, column) ? newColumn : value)
            .ToArray();

        private static bool ContainsColumn(
            IReadOnlyList<string> columns,
            string column
        ) => columns.Contains(column, StringComparer.Ordinal);

        private bool CheckConstraintMayReferenceColumn(
            ExpectedCheckConstraintDefinition definition,
            string column
        )
        {
            if (definition.Expression is null
                || !SafeMigrationSqlExpressionInspector.IsStructurallyComparable(definition.Expression))
            {
                return true;
            }

            return SafeMigrationSqlExpressionInspector.ReferencesColumnIdentifier(
                definition.Expression,
                column,
                _columnComparer);
        }

        private static void ThrowColumnDropDependency(
            string column,
            string table,
            string dependencyKind,
            string dependencyName,
            string? dependencyTable = null
        ) => throw new InvalidOperationException(
            $"Cannot project column drop '{column}' on table '{table}' while {dependencyKind} "
            + $"'{dependencyName}' on table '{dependencyTable ?? table}' depends on it. "
            + "Drop the constraint explicitly before the column "
            + "drop and recreate it afterward when required.");

        private static ExpectedCheckConstraintDefinition CopyCheck(
            ExpectedCheckConstraintDefinition definition,
            string table,
            string? schema
        ) => definition.CapturedSql is { } source
            ? ExpectedCheckConstraintDefinition.FromCapturedExpression(
                definition.Name,
                table,
                definition.Expression!,
                source,
                schema)
            : definition.Expression is not null
                ? ExpectedCheckConstraintDefinition.FromExpression(
                    definition.Name,
                    table,
                    definition.Expression,
                    schema)
                : new ExpectedCheckConstraintDefinition(definition.Name, table, definition.Sql!, schema);

        private static ExpectedForeignKeyDefinition CopyForeignKey(
            ExpectedForeignKeyDefinition definition,
            string? table = null,
            IReadOnlyList<string>? columns = null,
            string? principalTable = null,
            IReadOnlyList<string>? principalColumns = null,
            string? schema = null,
            string? principalSchema = null
        ) => new(
            definition.Name,
            table ?? definition.Table,
            columns ?? definition.Columns,
            principalTable ?? definition.PrincipalTable,
            principalColumns ?? definition.PrincipalColumns,
            schema ?? definition.Schema,
            principalSchema ?? definition.PrincipalSchema,
            definition.OnUpdate,
            definition.OnDelete);
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
