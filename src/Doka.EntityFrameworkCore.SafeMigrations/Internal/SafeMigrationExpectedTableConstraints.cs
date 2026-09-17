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
        )
    {
        ArgumentNullException.ThrowIfNull(operations);

        var tables = new Dictionary<(string? Schema, string Table), MutableConstraints>();

        // WHY: EF splits cyclic and provider-ordered constraints out of
        // CreateTable. Strict analysis must accept recoverable intermediate
        // states without treating future constraints as already mandatory.
        foreach (var operation in operations.OfType<SafeMigrationOperation>())
        {
            Apply(tables, operation.Intent);
        }

        return tables.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Snapshot());
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

        var relationalTable = model?.GetRelationalModel().FindTable(table, schema);
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

        var checkConstraints = ReadCheckConstraints(
            relationalTable,
            table,
            schema,
            runtimeCheckConstraints);

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
            .Select(constraint => new ExpectedCheckConstraintDefinition(
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
        SafeMigrationIntent intent
    )
    {
        switch (intent)
        {
            case EnsureTableIntent value:
                tables[(value.Definition.Schema, value.Definition.Table)] =
                    MutableConstraints.From(value.Definition);
                break;
            case DropTableIntent value:
                tables.Remove((value.Schema, value.Table));
                break;
            case RenameTableIntent value:
                RenameTable(tables, value);
                break;
            case DropColumnIntent value:
                DropColumn(tables, value);
                break;
            case RenameColumnIntent value:
                RenameColumn(tables, value);
                break;
            case EnsurePrimaryKeyIntent value when
                Find(tables, value.Definition.Schema, value.Definition.Table) is { } table:
                table.EnsurePrimaryKey(value.Definition);
                break;
            case DropPrimaryKeyIntent value when
                Find(tables, value.Schema, value.Table) is { PrimaryKey: { } primaryKey } table
                && StringComparer.Ordinal.Equals(primaryKey.Name, value.Name):
                table.DropPrimaryKey();
                break;
            case EnsureUniqueConstraintIntent value when
                Find(tables, value.Definition.Schema, value.Definition.Table) is { } table:
                table.EnsureUniqueConstraint(value.Definition);
                break;
            case DropUniqueConstraintIntent value when Find(tables, value.Schema, value.Table) is { } table:
                table.DropUniqueConstraint(value.Name);
                break;
            case EnsureCheckConstraintIntent value when
                Find(tables, value.Definition.Schema, value.Definition.Table) is { } table:
                table.EnsureCheckConstraint(value.Definition);
                break;
            case DropCheckConstraintIntent value when Find(tables, value.Schema, value.Table) is { } table:
                table.DropCheckConstraint(value.Name);
                break;
            case EnsureForeignKeyIntent value when
                Find(tables, value.Definition.Schema, value.Definition.Table) is { } table:
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
        RenameTableIntent intent
    )
    {
        if (!tables.Remove((intent.Schema, intent.Name), out var table))
        {
            return;
        }

        var newTable = intent.NewName ?? intent.Name;
        var newSchema = intent.NewSchema ?? intent.Schema;
        table.RenameOwner(newTable, newSchema);
        tables[(newSchema, newTable)] = table;

        foreach (var candidate in tables.Values)
        {
            candidate.RenamePrincipal(
                intent.Name,
                intent.Schema,
                newTable,
                newSchema);
        }
    }

    private static void RenameColumn(
        Dictionary<(string? Schema, string Table), MutableConstraints> tables,
        RenameColumnIntent intent
    )
    {
        Find(tables, intent.Schema, intent.Table)?.RenameLocalColumn(intent.Name, intent.NewName);

        foreach (var candidate in tables.Values)
        {
            candidate.RenamePrincipalColumn(
                intent.Table,
                intent.Schema,
                intent.Name,
                intent.NewName);
        }
    }

    private static void DropColumn(
        Dictionary<(string? Schema, string Table), MutableConstraints> tables,
        DropColumnIntent intent
    )
    {
        Find(tables, intent.Schema, intent.Table)?.ValidateLocalColumnDrop(intent.Name);

        foreach (var candidate in tables.Values)
        {
            candidate.ValidatePrincipalColumnDrop(
                intent.Table,
                intent.Schema,
                intent.Name);
        }
    }

    private sealed class MutableConstraints
    {
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
            IEnumerable<ExpectedForeignKeyDefinition> foreignKeys
        )
        {
            var uniqueConstraintSnapshot = uniqueConstraints.ToArray();
            var checkConstraintSnapshot = checkConstraints.ToArray();
            var foreignKeySnapshot = foreignKeys.ToArray();

            PrimaryKey = primaryKey;
            _primaryKeyMayBeAbsent = primaryKey is null;
            if (primaryKey is not null)
            {
                _allowedPrimaryKeys.Add(primaryKey);
            }

            UniqueConstraints = uniqueConstraintSnapshot.ToDictionary(
                static value => value.Name,
                StringComparer.Ordinal);
            CheckConstraints = checkConstraintSnapshot.ToDictionary(
                static value => value.Name,
                StringComparer.Ordinal);
            ForeignKeys = foreignKeySnapshot.ToDictionary(
                static value => value.Name,
                StringComparer.Ordinal);

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
            ExpectedTableDefinition definition
        ) => new(
            definition.PrimaryKey,
            definition.UniqueConstraints,
            definition.CheckConstraints,
            definition.ForeignKeys);

        public SafeMigrationExpectedTableConstraints Snapshot() => new(
            PrimaryKey,
            UniqueConstraints.Values.OrderBy(static value => value.Name, StringComparer.Ordinal).ToArray(),
            CheckConstraints.Values.OrderBy(static value => value.Name, StringComparer.Ordinal).ToArray(),
            ForeignKeys.Values.OrderBy(static value => value.Name, StringComparer.Ordinal).ToArray(),
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
            if (PrimaryKey is null
                || !SafeMigrationDefinitionEquivalence.PrimaryKey(PrimaryKey, definition))
            {
                _primaryKeyMayBeAbsent = true;
            }

            PrimaryKey = definition;
            AddAllowed(
                _allowedPrimaryKeys,
                definition,
                SafeMigrationDefinitionEquivalence.PrimaryKey);
        }

        public void DropPrimaryKey()
        {
            PrimaryKey = null;
            _primaryKeyMayBeAbsent = true;
        }

        public void EnsureUniqueConstraint(
            ExpectedUniqueConstraintDefinition definition
        ) => Ensure(
            UniqueConstraints,
            _allowedUniqueConstraints,
            _requiredUniqueConstraints,
            definition,
            SafeMigrationDefinitionEquivalence.UniqueConstraint);

        public void DropUniqueConstraint(
            string name
        ) => Drop(UniqueConstraints, _requiredUniqueConstraints, name);

        public void EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition definition
        ) => Ensure(
            CheckConstraints,
            _allowedCheckConstraints,
            _requiredCheckConstraints,
            definition,
            SafeMigrationDefinitionEquivalence.CheckConstraint);

        public void DropCheckConstraint(
            string name
        ) => Drop(CheckConstraints, _requiredCheckConstraints, name);

        public void EnsureForeignKey(
            ExpectedForeignKeyDefinition definition
        ) => Ensure(
            ForeignKeys,
            _allowedForeignKeys,
            _requiredForeignKeys,
            definition,
            SafeMigrationDefinitionEquivalence.ForeignKey);

        public void DropForeignKey(
            string name
        ) => Drop(ForeignKeys, _requiredForeignKeys, name);

        public void RenameOwner(
            string table,
            string? schema
        )
        {
            if (PrimaryKey is not null)
            {
                var renamed = new ExpectedPrimaryKeyDefinition(
                    PrimaryKey.Name,
                    table,
                    PrimaryKey.Columns,
                    schema);

                EnsurePrimaryKey(renamed);
            }

            ReplaceValues(
                UniqueConstraints,
                _allowedUniqueConstraints,
                _requiredUniqueConstraints,
                SafeMigrationDefinitionEquivalence.UniqueConstraint,
                value => new ExpectedUniqueConstraintDefinition(
                    value.Name,
                    table,
                    value.Columns,
                    schema));

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
        ) => ReplaceValues(
            ForeignKeys,
            _allowedForeignKeys,
            _requiredForeignKeys,
            SafeMigrationDefinitionEquivalence.ForeignKey,
            value => StringComparer.Ordinal.Equals(value.PrincipalTable, table)
                && StringComparer.Ordinal.Equals(value.PrincipalSchema, schema)
                    ? CopyForeignKey(value, principalTable: newTable, principalSchema: newSchema)
                    : value);

        public void RenameLocalColumn(
            string column,
            string newColumn
        )
        {
            if (CheckConstraints.Count > 0)
            {
                var table = CheckConstraints.Values.First().Table;

                throw new InvalidOperationException(
                    $"Cannot project column rename '{column}' to '{newColumn}' on table "
                    + $"'{table}'. Drop and recreate owned check constraints "
                    + "around the rename so their terminal expressions remain explicit.");
            }

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
                ForeignKeys,
                _allowedForeignKeys,
                _requiredForeignKeys,
                SafeMigrationDefinitionEquivalence.ForeignKey,
                value => CopyForeignKey(
                    value,
                    columns: RenameColumns(value.Columns, column, newColumn)));
        }

        public void RenamePrincipalColumn(
            string table,
            string? schema,
            string column,
            string newColumn
        ) => ReplaceValues(
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

        public void ValidateLocalColumnDrop(
            string column
        )
        {
            if (PrimaryKey is not null
                && ContainsColumn(PrimaryKey.Columns, column))
            {
                ThrowColumnDropDependency(
                    column,
                    PrimaryKey.Table,
                    "primary key",
                    PrimaryKey.Name);
            }

            var uniqueConstraint = UniqueConstraints.Values.FirstOrDefault(
                value => ContainsColumn(value.Columns, column));
            if (uniqueConstraint is not null)
            {
                ThrowColumnDropDependency(
                    column,
                    uniqueConstraint.Table,
                    "unique constraint",
                    uniqueConstraint.Name);
            }

            var checkConstraint = CheckConstraints.Values.FirstOrDefault(
                value => CheckConstraintMayReferenceColumn(value, column));
            if (checkConstraint is not null)
            {
                ThrowColumnDropDependency(
                    column,
                    checkConstraint.Table,
                    "check constraint",
                    checkConstraint.Name);
            }

            var foreignKey = ForeignKeys.Values.FirstOrDefault(
                value => ContainsColumn(value.Columns, column));
            if (foreignKey is not null)
            {
                ThrowColumnDropDependency(
                    column,
                    foreignKey.Table,
                    "foreign key",
                    foreignKey.Name);
            }
        }

        public void ValidatePrincipalColumnDrop(
            string table,
            string? schema,
            string column
        )
        {
            var foreignKey = ForeignKeys.Values.FirstOrDefault(value =>
                StringComparer.Ordinal.Equals(value.PrincipalTable, table)
                && StringComparer.Ordinal.Equals(value.PrincipalSchema, schema)
                && ContainsColumn(value.PrincipalColumns, column));
            if (foreignKey is null)
            {
                return;
            }

            ThrowColumnDropDependency(
                column,
                table,
                "referencing foreign key",
                foreignKey.Name,
                foreignKey.Table);
        }

        private static void ReplaceValues<T>(
            Dictionary<string, T> values,
            List<T> allowed,
            Dictionary<string, T> required,
            Func<T, T, bool> equivalent,
            Func<T, T> replace
        ) where T : class
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
        ) where T : class
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

        private static bool CheckConstraintMayReferenceColumn(
            ExpectedCheckConstraintDefinition definition,
            string column
        )
        {
            if (definition.Expression is null
                || !SafeMigrationSqlExpressionInspector.IsStructurallyComparable(definition.Expression))
            {
                return true;
            }

            return SafeMigrationSqlExpressionInspector.ReferencesIdentifier(
                definition.Expression,
                column);
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
        ) => definition.Expression is not null
            ? ExpectedCheckConstraintDefinition.FromExpression(
                definition.Name,
                table,
                definition.Expression,
                schema)
            : new ExpectedCheckConstraintDefinition(
                definition.Name,
                table,
                definition.Sql!,
                schema);

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
}
