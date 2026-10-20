namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Captures authored constraint transitions without crossing table lifetimes or SQL barriers.</summary>
internal sealed class SqliteTableConstraintTransitions
{
    private readonly Dictionary<SafeMigrationOperation, SafeMigrationExpectedTableConstraints>? _tables;

    /// <summary>Captures each strict creator's bounded initial and allowed constraint shapes.</summary>
    /// <param name="operations">The current structural segment, not the cumulative rebuild history.</param>
    public SqliteTableConstraintTransitions(IReadOnlyList<MigrationOperation> operations)
    {
        if (!operations.Any(static operation => operation is SafeMigrationOperation { Intent: EnsureTableIntent }))
        {
            return;
        }

        _tables = new Dictionary<SafeMigrationOperation, SafeMigrationExpectedTableConstraints>(
            ReferenceEqualityComparer.Instance);

        var pending = new List<MigrationOperation>();
        var creators = new HashSet<string>(SqliteIdentifierComparer.Instance);

        foreach (var operation in operations)
        {
            var table = operation is SafeMigrationOperation { Intent: EnsureTableIntent ensure }
                ? ensure.Definition.Table
                : operation is CreateTableOperation create ? create.Name : null;

            if (!SqliteSafeMigrationOperationClassifier.CanParticipateInStructuralBatch(operation)
                || operation is DropTableOperation or RenameTableOperation
                || operation is SafeMigrationOperation { Intent: DropTableIntent or RenameTableIntent })
            {
                Flush();
                continue;
            }

            if (table is not null && creators.Contains(table))
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
                    ? null : schema,
                SqliteIdentifierComparer.Instance,
                capturedSqlIdentifierDelimiter: '"',
                foldUnquotedCapturedSqlIdentifiers: false,
                columnComparer: SqliteIdentifierComparer.Instance);

            foreach (var operation in pending)
            {
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
    public SafeMigrationExpectedTableConstraints? Find(SafeMigrationOperation operation)
        => _tables?.GetValueOrDefault(operation);
}
