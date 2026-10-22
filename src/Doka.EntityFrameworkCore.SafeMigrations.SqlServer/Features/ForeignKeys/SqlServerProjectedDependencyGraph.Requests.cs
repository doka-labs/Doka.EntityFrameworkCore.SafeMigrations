namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerProjectedDependencyGraph
{
    /// <summary>Determines whether an operation stream needs dependency metadata.</summary>
    /// <param name="operations">The safe operation stream.</param>
    /// <returns>Whether structural dependency or schema validation is necessary.</returns>
    public static bool IsRequired(
        IReadOnlyList<SafeMigrationOperation> operations
    ) => operations.Any(static operation => operation.Intent is EnsureTableIntent
        or DropTableIntent
        or RenameTableIntent
        or EnsureForeignKeyIntent
        or DropForeignKeyIntent
        or EnsureSchemaIntent
        or DropSchemaIntent);

    private static HashSet<BindingRequest> CollectRequests(
        IReadOnlyList<SafeMigrationOperation> operations
    )
    {
        var result = new HashSet<BindingRequest>();
        foreach (var operation in operations)
        {
            switch (operation.Intent)
            {
                case EnsureSchemaIntent schema:
                    result.Add(new BindingRequest(schema.Name, string.Empty, null, null));
                    break;
                case DropSchemaIntent schema:
                    result.Add(new BindingRequest(schema.Name, string.Empty, null, null));
                    break;
                case EnsureTableIntent table:
                    result.Add(
                        new BindingRequest(table.Definition.Schema ?? "dbo", table.Definition.Table, null, null));
                    foreach (var foreignKey in table.Definition.ForeignKeys)
                    {
                        AddForeignKey(foreignKey);
                    }

                    break;
                case DropTableIntent table:
                    result.Add(new BindingRequest(table.Schema ?? "dbo", table.Table, null, null));
                    break;
                case RenameTableIntent table:
                    result.Add(new BindingRequest(table.Schema ?? "dbo", table.Name, null, null));
                    result.Add(
                        new BindingRequest(
                            table.NewSchema ?? table.Schema ?? "dbo",
                            table.NewName ?? table.Name,
                            null,
                            null));
                    if ((table.NewName ?? table.Name) != table.Name
                        && (table.NewSchema ?? table.Schema ?? "dbo") != (table.Schema ?? "dbo"))
                    {
                        result.Add(new BindingRequest(table.Schema ?? "dbo", table.NewName ?? table.Name, null, null));
                    }

                    break;
                case EnsureForeignKeyIntent foreignKey:
                    AddForeignKey(foreignKey.Definition);
                    break;
                case DropForeignKeyIntent foreignKey:
                    result.Add(new BindingRequest(foreignKey.Schema ?? "dbo", foreignKey.Table, null, foreignKey.Name));
                    break;
                case DropPrimaryKeyIntent key:
                    result.Add(new BindingRequest(key.Schema ?? "dbo", key.Table, null, key.Name));
                    break;
                case DropUniqueConstraintIntent key:
                    result.Add(new BindingRequest(key.Schema ?? "dbo", key.Table, null, key.Name));
                    break;
                case DropCheckConstraintIntent check:
                    result.Add(new BindingRequest(check.Schema ?? "dbo", check.Table, null, check.Name));
                    break;
                case DropIndexIntent index:
                    result.Add(new BindingRequest(index.Schema ?? "dbo", index.Table, null, index.Name));
                    break;
                case RenameIndexIntent index:
                    result.Add(new BindingRequest(index.Schema ?? "dbo", index.Table, null, index.Name));
                    break;
                case RenameColumnIntent column:
                    result.Add(new BindingRequest(column.Schema ?? "dbo", column.Table, column.Name, null));
                    break;
                case DropColumnIntent column:
                    result.Add(new BindingRequest(column.Schema ?? "dbo", column.Table, column.Name, null));
                    break;
            }
        }

        return result;

        void AddForeignKey(
            ExpectedForeignKeyDefinition definition
        )
        {
            // WHY: Capture corresponding dependent and principal members in key order.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var ordinal = 0; ordinal < definition.Columns.Count; ordinal++)
            {
                result.Add(
                    new BindingRequest(
                        definition.Schema ?? "dbo",
                        definition.Table,
                        definition.Columns[ordinal],
                        definition.Name));
                result.Add(
                    new BindingRequest(
                        definition.PrincipalSchema ?? "dbo",
                        definition.PrincipalTable,
                        definition.PrincipalColumns[ordinal],
                        null));
            }
        }
    }

    private static InlineRequest[] CollectInlineRequests(
        IReadOnlyList<SafeMigrationOperation> operations
    )
    {
        var result = new List<InlineRequest>();
        var renames = new List<SafeMigrationIntent>();
        var authoredTables = new Dictionary<TableKey, ExpectedTableDefinition>();
        var authoredColumns = new Dictionary<TableKey, Dictionary<string, List<ExpectedColumnDefinition>>>();
        var requestedColumns = new Dictionary<TableKey, HashSet<string>>();

        foreach (var operation in operations)
        {
            if (operation.Intent is not EnsureForeignKeyIntent foreign)
            {
                continue;
            }

            var owner = new TableKey(foreign.Definition.Schema ?? "dbo", foreign.Definition.Table);
            if (!requestedColumns.TryGetValue(owner, out var requested))
            {
                requested = new HashSet<string>(StringComparer.Ordinal);
                requestedColumns.Add(owner, requested);
            }

            requested.UnionWith(foreign.Definition.Columns);
        }

        foreach (var operation in operations)
        {
            if (operation.Intent is RenameTableIntent or RenameColumnIntent)
            {
                renames.Add(operation.Intent);
            }

            ExpectedTableDefinition? table;
            IReadOnlyList<ExpectedForeignKeyDefinition> foreignKeys;
            var standalone = false;
            var sparse = false;
            Dictionary<string, List<ExpectedColumnDefinition>>? alternatives = null;
            switch (operation.Intent)
            {
                case EnsureTableIntent ensure:
                    table = ensure.Definition;
                    authoredTables[new TableKey(table.Schema ?? "dbo", table.Table)] = table;
                    foreach (var definition in table.Columns)
                    {
                        CaptureColumn(new TableKey(table.Schema ?? "dbo", table.Table), definition);
                    }

                    foreignKeys = table.ForeignKeys;
                    break;
                case EnsureForeignKeyIntent ensure when authoredTables.TryGetValue(
                        new TableKey(ensure.Definition.Schema ?? "dbo", ensure.Definition.Table),
                        out var source)
                    && ensure.Definition.Columns.All(name => source.Columns.Any(column => column.Name == name)):
                    table = source;
                    foreignKeys = [ensure.Definition];
                    standalone = true;
                    break;
                case EnsureForeignKeyIntent ensure when authoredColumns.TryGetValue(
                    new TableKey(ensure.Definition.Schema ?? "dbo", ensure.Definition.Table),
                    out var additions):
                    table = null;
                    foreignKeys = [ensure.Definition];
                    standalone = true;
                    sparse = true;
                    alternatives = additions;
                    break;
                case EnsureColumnIntent column:
                    CaptureColumn(new TableKey(column.Schema ?? "dbo", column.Table), column.Definition);
                    continue;
                case DropColumnIntent column:
                    if (authoredColumns.TryGetValue(new TableKey(column.Schema ?? "dbo", column.Table), out var old))
                    {
                        old.Remove(column.Name);
                    }

                    continue;
                case DropTableIntent drop:
                    authoredTables.Remove(new TableKey(drop.Schema ?? "dbo", drop.Table));
                    authoredColumns.Remove(new TableKey(drop.Schema ?? "dbo", drop.Table));
                    continue;
                case RenameTableIntent rename:
                    // WHY: A source rename is not proof that a captured authored
                    // table reached its destination. Inline principal identity
                    // rebinding remains supported; standalone children fail closed.
                    authoredTables.Remove(new TableKey(rename.Schema ?? "dbo", rename.Name));
                    authoredTables.Remove(
                        new TableKey(rename.NewSchema ?? rename.Schema ?? "dbo", rename.NewName ?? rename.Name));
                    authoredColumns.Remove(new TableKey(rename.Schema ?? "dbo", rename.Name));
                    authoredColumns.Remove(
                        new TableKey(rename.NewSchema ?? rename.Schema ?? "dbo", rename.NewName ?? rename.Name));
                    continue;
                default:
                    continue;
            }

            foreach (var foreignKey in foreignKeys)
            {
                var principalTable = foreignKey.PrincipalTable;
                var principalSchema = foreignKey.PrincipalSchema ?? "dbo";
                var principalColumns = foreignKey.PrincipalColumns.ToArray();

                // WHY: A later reference to an accepted rename target needs
                // metadata from its initial source. This is only a hypothesis
                // until projection proves the same physical ids reached that
                // target; a rejected rename can never activate this proof.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var previous = renames.Count - 1; previous >= 0; previous--)
                {
                    switch (renames[previous])
                    {
                        case RenameTableIntent rename when (rename.NewName ?? rename.Name) == principalTable
                            && (rename.NewSchema ?? rename.Schema ?? "dbo") == principalSchema:
                            principalTable = rename.Name;
                            principalSchema = rename.Schema ?? "dbo";
                            break;
                        case RenameColumnIntent rename when rename.Table == principalTable
                            && (rename.Schema ?? "dbo") == principalSchema:
                            // WHY: Rewrite renamed principal members in their existing composite-key slots.
                            // ReSharper disable once ForCanBeConvertedToForeach
                            // ReSharper disable once LoopCanBeConvertedToQuery
                            for (var column = 0; column < principalColumns.Length; column++)
                            {
                                if (principalColumns[column] == rename.NewName)
                                {
                                    principalColumns[column] = rename.Name;
                                }
                            }

                            break;
                    }
                }

                var initial = new ExpectedForeignKeyDefinition(
                    foreignKey.Name,
                    foreignKey.Table,
                    foreignKey.Columns,
                    principalTable,
                    principalColumns,
                    foreignKey.Schema,
                    principalSchema,
                    onUpdate: foreignKey.OnUpdate,
                    onDelete: foreignKey.OnDelete);

                if (sparse)
                {
                    // WHY: Retain each independent FK operand's ordinal in its capture request.
                    // ReSharper disable once ForCanBeConvertedToForeach
                    // ReSharper disable once LoopCanBeConvertedToQuery
                    for (var ordinal = 0; ordinal < foreignKey.Columns.Count; ordinal++)
                    {
                        // WHY: Every authored proposal remains a hypothesis
                        // until accepted. Capture independent alternatives,
                        // never all combinations of a composite FK shape.
                        result.Add(new InlineRequest(null, foreignKey, initial, standalone, ordinal));
                        if (!alternatives!.TryGetValue(foreignKey.Columns[ordinal], out var definitions))
                        {
                            continue;
                        }

                        // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator
                        foreach (var definition in definitions)
                        {
                            var operand = new ExpectedTableDefinition(
                                foreignKey.Table,
                                [definition],
                                foreignKey.Schema);

                            result.Add(new InlineRequest(operand, foreignKey, initial, standalone, ordinal));
                        }
                    }

                    continue;
                }

                result.Add(new InlineRequest(table, foreignKey, initial, standalone));
            }
        }

        return result.ToArray();

        void CaptureColumn(
            TableKey owner,
            ExpectedColumnDefinition definition
        )
        {
            if (!requestedColumns.TryGetValue(owner, out var wanted)
                || !wanted.Contains(definition.Name))
            {
                // WHY: A later ADD can complete an earlier CREATE's composite
                // operands. Retain only requested columns from both sources;
                // unrelated convergence columns never inflate this capture.
                return;
            }

            if (!authoredColumns.TryGetValue(owner, out var added))
            {
                added = [];
                authoredColumns.Add(owner, added);
            }

            if (!added.TryGetValue(definition.Name, out var definitions))
            {
                definitions = [];
                added.Add(definition.Name, definitions);
            }

            if (!definitions.Any(candidate => SafeMigrationDefinitionEquivalence.Column(candidate, definition)))
            {
                definitions.Add(definition);
            }
        }
    }

    private readonly record struct BindingRequest(
        string Schema,
        string Table,
        string? Column,
        string? ForeignKey
    );

    private readonly record struct InlineRequest(
        ExpectedTableDefinition? Table,
        ExpectedForeignKeyDefinition ForeignKey,
        ExpectedForeignKeyDefinition InitialForeignKey,
        bool IsStandalone,
        int OperandOrdinal = -1
    )
    {
        /// <summary>Distinguishes independent sparse operand facts from complete inline child proofs.</summary>
        public bool IsSparse => OperandOrdinal >= 0;
    }
}
