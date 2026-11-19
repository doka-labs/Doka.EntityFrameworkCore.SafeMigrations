namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Captures authored constraint transitions without crossing table lifetimes or SQL barriers.</summary>
internal sealed class SqliteTableConstraintTransitions
{
    private readonly Dictionary<SafeMigrationOperation, SafeMigrationExpectedTableConstraints>? _tables;

    /// <summary>Captures each strict creator's bounded initial and allowed constraint shapes.</summary>
    /// <param name="operations">The current structural segment, not the cumulative rebuild history.</param>
    public SqliteTableConstraintTransitions(
        IReadOnlyList<MigrationOperation> operations
    )
    {
        if (operations.All(static operation => operation is not SafeMigrationOperation { Intent: EnsureTableIntent }))
        {
            return;
        }

        _tables = new Dictionary<SafeMigrationOperation, SafeMigrationExpectedTableConstraints>(
            ReferenceEqualityComparer.Instance);

        List<MigrationOperation> pending = [];
        var creators = new HashSet<string>(SqliteIdentifierComparer.Instance);

        // WHY: Traverse the stable ordered source by ordinal without materializing another collection.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var operationIndex = 0; operationIndex < operations.Count; operationIndex++)
        {
            var operation = operations[operationIndex];
            var table = operation switch
            {
                SafeMigrationOperation { Intent: EnsureTableIntent ensure } => ensure.Definition.Table,
                CreateTableOperation create => create.Name,
                _ => null,
            };

            if (!SqliteSafeMigrationOperationClassifier.CanParticipateInStructuralBatch(operation)
                || operation is DropTableOperation or RenameTableOperation
                || operation is SafeMigrationOperation { Intent: DropTableIntent or RenameTableIntent })
            {
                Flush();
                continue;
            }

            if (table is not null
                && creators.Contains(table))
            {
                Flush();
            }

            pending.Add(operation);
            if (table is not null)
            {
                _ = creators.Add(table);
            }
        }

        Flush();

        return;

        void Flush()
        {
            if (pending.Count == 0)
            {
                return;
            }

            if (creators.Count == 0)
            {
                pending.Clear();

                return;
            }

            // WHY: Allowed forms describe only the authored structural window.
            // A later lifetime or opaque SQL cannot lend a constraint to an
            // earlier creator; ordered prerequisite proof remains separate.
            var catalog = SafeMigrationExpectedTableConstraints.FromOperations(
                pending,
                static schema => schema is null || SqliteIdentifierComparer.Instance.Equals(schema, "main")
                    ? null
                    : schema,
                SqliteIdentifierComparer.Instance,
                capturedSqlIdentifierDelimiter: '"',
                foldUnquotedCapturedSqlIdentifiers: false,
                columnComparer: SqliteIdentifierComparer.Instance);

            // WHY: Traverse the stable ordered source by ordinal without materializing another collection.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var operationIndex = 0; operationIndex < pending.Count; operationIndex++)
            {
                var operation = pending[operationIndex];
                if (operation is SafeMigrationOperation { Intent: EnsureTableIntent intent } safe
                    && catalog.TryGetValue((intent.Definition.Schema, intent.Definition.Table), out var contract))
                {
                    _tables[safe] = contract;
                }
            }

            pending.Clear();
            creators.Clear();
        }
    }

    /// <summary>Gets the transition contract of this exact authored table creator.</summary>
    /// <param name="operation">The creator whose original base definition remains authoritative.</param>
    /// <returns>The bounded constraint contract, or null when no table creator was captured.</returns>
    public SafeMigrationExpectedTableConstraints? Find(
        SafeMigrationOperation operation
    ) => _tables?.GetValueOrDefault(operation);
}
