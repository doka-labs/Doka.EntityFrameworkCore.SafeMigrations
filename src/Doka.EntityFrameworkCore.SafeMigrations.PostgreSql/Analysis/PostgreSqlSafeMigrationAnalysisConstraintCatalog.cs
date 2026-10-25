namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

/// <summary>Shares live-normalized contracts only within one identity-stable operation segment.</summary>
internal sealed class PostgreSqlSafeMigrationAnalysisConstraintCatalog
{
    private readonly Entry[] _entries;

    private PostgreSqlSafeMigrationAnalysisConstraintCatalog(Entry[] entries) => _entries = entries;

    /// <summary>Indexes barrier-delimited catalogs by safe-subset ordinal without duplicating dictionaries.</summary>
    /// <param name="operations">The complete stream, or the safe-only stream for a direct provider call.</param>
    /// <param name="rejectedOrdinals">Safe-subset ordinals already rejected by native owner qualification.</param>
    /// <param name="currentSchema">The live verified creation-schema identity.</param>
    /// <returns>Exact segment-owned transition catalogs.</returns>
    public static PostgreSqlSafeMigrationAnalysisConstraintCatalog Create(
        IReadOnlyList<MigrationOperation> operations,
        HashSet<int> rejectedOrdinals,
        string? currentSchema
    )
    {
        var entries = new List<Entry>();
        var pending = new List<MigrationOperation>();
        var start = 0;
        var safeOrdinal = 0;
        foreach (var operation in operations)
        {
            if (operation is SafeMigrationOperation safeOperation)
            {
                if (safeOperation.Intent is EnsureSchemaIntent or DropSchemaIntent)
                {
                    Flush();
                    safeOrdinal++;
                    start = safeOrdinal;

                    continue;
                }

                if (!rejectedOrdinals.Contains(safeOrdinal))
                {
                    pending.Add(safeOperation);
                }

                safeOrdinal++;

                continue;
            }

            if (operation is not SafeMigrationDesignTimeServicesRequiredOperation
                && !PostgreSqlSafeMigrationProviderAnalyzer.ProviderOperationPreservesTableState(operation))
            {
                Flush();
            }
        }

        Flush();

        return new PostgreSqlSafeMigrationAnalysisConstraintCatalog(entries.ToArray());

        void Flush()
        {
            if (start == safeOrdinal)
            {
                return;
            }

            string? Normalize(string? schema) => PostgreSqlSafeMigrationSchemaIdentity.Normalize(schema, currentSchema);

            // WHY: Rejected identities cannot lend transition contracts to
            // earlier tables, and opaque operations cannot carry index/drop
            // obligations across an unobserved change of physical owners.
            SafeMigrationExpectedIndexTransitions.Validate(pending, Normalize);
            var catalog = SafeMigrationExpectedTableConstraints.FromOperations(pending, Normalize,
                StringComparer.Ordinal, '"', foldUnquotedCapturedSqlIdentifiers: true);

            entries.Add(new Entry(start, safeOrdinal, catalog));
            pending.Clear();
            start = safeOrdinal;
        }
    }

    /// <summary>Finds only the table contract in the operation's own physical-identity segment.</summary>
    /// <param name="safeOrdinal">The safe-subset ordinal used by classification transport.</param>
    /// <param name="operation">The immutable safe operation being classified.</param>
    /// <returns>The segment-owned table contract, or null for other operation kinds.</returns>
    public SafeMigrationExpectedTableConstraints? Find(int safeOrdinal, SafeMigrationOperation operation)
    {
        if (operation.Intent is not EnsureTableIntent table)
        {
            return null;
        }

        var left = 0;
        var right = _entries.Length - 1;
        while (left <= right)
        {
            var middle = left + ((right - left) >> 1);
            var entry = _entries[middle];
            if (safeOrdinal < entry.Start)
            {
                right = middle - 1;
            }
            else if (safeOrdinal >= entry.End)
            {
                left = middle + 1;
            }
            else
            {
                return entry.Catalog.GetValueOrDefault((table.Definition.Schema, table.Definition.Table));
            }
        }

        throw new InvalidOperationException("The PostgreSQL analysis table contract has no owning segment.");
    }

    /// <summary>Stores one dictionary per stable segment instead of one reference per operation.</summary>
    private sealed record Entry(int Start, int End,
        IReadOnlyDictionary<(string? Schema, string Table), SafeMigrationExpectedTableConstraints> Catalog);
}
