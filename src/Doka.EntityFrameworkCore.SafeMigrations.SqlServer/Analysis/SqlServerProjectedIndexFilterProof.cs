namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>Binds one constant-only filter proof to its immutable authored destination column references.</summary>
/// <param name="Columns">Only the authored filter columns qualified by the provider.</param>
/// <param name="Result">One for proven conversion, zero for rejected conversion, minus one for unproven.</param>
/// <param name="AllColumnsMutated">Whether every predicate column originated in an authored column mutation.</param>
internal sealed record SqlServerProjectedIndexFilterProof(
    ExpectedColumnDefinition[] Columns,
    int Result,
    bool AllColumnsMutated = false
)
{
    /// <summary>
    /// Identifies an exact replacement proof that may supersede only the old filter-specific failure.
    /// </summary>
    internal const string SupportedReplacementCode = "projected_index_filter_replacement_supported";

    /// <summary>Rejects a cached proof after any represented column changes.</summary>
    /// <param name="columns">The currently authored column references.</param>
    /// <returns>True only for the same ordered immutable definition references.</returns>
    internal bool Matches(IReadOnlyList<ExpectedColumnDefinition> columns)
        => Columns.Length == columns.Count
            && Columns.Select((column, ordinal) => ReferenceEquals(column, columns[ordinal]))
                .All(static equal => equal);
}

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
    private Dictionary<EnsureIndexIntent, List<SqlServerProjectedIndexFilterProof>> _projectedIndexFilters = [];

    private async Task<Dictionary<EnsureIndexIntent, List<SqlServerProjectedIndexFilterProof>>>
        CaptureProjectedFilterProofsAsync(
            IReadOnlyList<SafeMigrationOperation> operations,
            Func<string, CancellationToken, Task<int>> execute,
            CancellationToken cancellationToken
        )
    {
        var authored = new Dictionary<(string Schema, string Table), Dictionary<string, ExpectedColumnDefinition>>();
        var mutations = new Dictionary<(string Schema, string Table), HashSet<string>>();
        var result = new Dictionary<EnsureIndexIntent, List<SqlServerProjectedIndexFilterProof>>();
        var cache = new Dictionary<string, List<SqlServerProjectedIndexFilterProof>>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operation.Intent is EnsureIndexIntent index
                && SqlServerSafeMigrationCatalogSqlBuilder.GetStructuredIndexFilter(index.Definition) is { } filter)
            {
                var identity = _catalogSqlBuilder.ProjectedSeedFilterIdentity(filter);
                if (identity is not null
                    && authored.TryGetValue((index.Definition.Schema ?? "dbo", index.Definition.Table),
                        out var definitions))
                {
                    var captured = SqlServerSafeMigrationCatalogSqlBuilder.GetIndexFilterComparisons(filter)
                        .Select(static comparison => comparison.Column).Distinct(StringComparer.Ordinal)
                        .Where(definitions.ContainsKey).Select(name => definitions[name]).ToArray();

                    var allMutated = captured.Length != 0
                        && mutations.TryGetValue((index.Definition.Schema ?? "dbo", index.Definition.Table),
                            out var changed)
                        && captured.All(column => changed.Contains(column.Name));

                    if (!cache.TryGetValue(identity, out var proofs))
                    {
                        proofs = [];
                        cache.Add(identity, proofs);
                    }

                    var proof = proofs.FirstOrDefault(candidate => candidate.Matches(captured));
                    if (proof is null)
                    {
                        var sql = _catalogSqlBuilder.BuildProjectedIndexFilterProofSql(filter, captured);
                        var value = sql is null ? -1 : await execute(sql, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (value is < -1 or > 1)
                        {
                            throw new InvalidOperationException(
                                "SQL Server returned an invalid projected filter proof.");
                        }

                        proof = new SqlServerProjectedIndexFilterProof(captured, value);
                        proofs.Add(proof);
                    }

                    if (proof.AllColumnsMutated != allMutated)
                    {
                        // WHY: Constant SQL results can be reused across equal
                        // definitions, but only a column-mutation origin plus
                        // exact accepted references replaces old live rejection.
                        proof = proof with { AllColumnsMutated = allMutated };
                    }

                    if (!result.TryGetValue(index, out var operationProofs))
                    {
                        operationProofs = [];
                        result.Add(index, operationProofs);
                    }

                    operationProofs.Add(proof);
                }
            }

            switch (operation.Intent)
            {
                case EnsureTableIntent table:
                    authored[(table.Definition.Schema ?? "dbo", table.Definition.Table)]
                        = table.Definition.Columns.ToDictionary(static column => column.Name, StringComparer.Ordinal);
                    mutations[(table.Definition.Schema ?? "dbo", table.Definition.Table)]
                        = new HashSet<string>(StringComparer.Ordinal);
                    break;
                case EnsureColumnIntent column:
                    SetColumn(column.Table, column.Schema, column.Definition);
                    break;
                case AlterColumnIntent column:
                    SetColumn(column.Table, column.Schema, column.Definition);
                    break;
                case DropColumnIntent column:
                    if (authored.TryGetValue((column.Schema ?? "dbo", column.Table), out var tableColumns))
                    {
                        tableColumns.Remove(column.Name);
                    }

                    if (mutations.TryGetValue((column.Schema ?? "dbo", column.Table), out var changed))
                    {
                        changed.Remove(column.Name);
                    }

                    break;
                case DropTableIntent table:
                    authored.Remove((table.Schema ?? "dbo", table.Table));
                    mutations.Remove((table.Schema ?? "dbo", table.Table));
                    break;
                case RenameTableIntent table:
                    authored.Remove((table.Schema ?? "dbo", table.Name));
                    authored.Remove((table.NewSchema ?? table.Schema ?? "dbo", table.NewName ?? table.Name));
                    mutations.Remove((table.Schema ?? "dbo", table.Name));
                    mutations.Remove((table.NewSchema ?? table.Schema ?? "dbo", table.NewName ?? table.Name));
                    break;
                case RenameColumnIntent column:
                    authored.Remove((column.Schema ?? "dbo", column.Table));
                    mutations.Remove((column.Schema ?? "dbo", column.Table));
                    break;
            }
        }

        return result;

        void SetColumn(
            string table,
            string? schema,
            ExpectedColumnDefinition definition
        )
        {
            if (!authored.TryGetValue((schema ?? "dbo", table), out var columns))
            {
                columns = new Dictionary<string, ExpectedColumnDefinition>(StringComparer.Ordinal);
                authored.Add((schema ?? "dbo", table), columns);
            }

            columns[definition.Name] = definition;
            if (!mutations.TryGetValue((schema ?? "dbo", table), out var changed))
            {
                changed = new HashSet<string>(StringComparer.Ordinal);
                mutations.Add((schema ?? "dbo", table), changed);
            }

            changed.Add(definition.Name);
        }
    }

    private SafeMigrationProviderAnalysis? ProjectedIndexFilterAnalysis(
        SafeMigrationIntent intent,
        ISafeMigrationProjectedColumnSource source,
        SqlServerProjectedKeyTable? snapshot,
        SafeMigrationProviderAnalysis live
    )
    {
        if (intent is not EnsureIndexIntent index
            || SqlServerSafeMigrationCatalogSqlBuilder.GetStructuredIndexFilter(index.Definition) is not { } filter)
        {
            return null;
        }

        if (_catalogSqlBuilder.ProjectedSeedFilterIdentity(filter) is null)
        {
            return FilterUnsupported();
        }

        var captured = new List<ExpectedColumnDefinition>();
        var hasUntouchedColumn = false;
        foreach (var name in SqlServerSafeMigrationCatalogSqlBuilder.GetIndexFilterComparisons(filter)
            .Select(static comparison => comparison.Column).Distinct(StringComparer.Ordinal))
        {
            if (source.TryGetProjectedColumn(index.Definition.Table, index.Definition.Schema, name, out var column))
            {
                if (column.ComputedExpression is not null || column.ComputedColumnSql is not null)
                {
                    return FilterUnsupported();
                }

                captured.Add(column);
            }
            else if (snapshot?.Columns.ContainsKey(name) != true)
            {
                return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.PrerequisiteMissing,
                    SafeMigrationRepairCapability.None, false, "projected_index_filter_column_unknown");
            }
            else
            {
                hasUntouchedColumn = true;
            }
        }

        if (live.ObservedState == SafeMigrationObservedState.Unsupported
            && live.Code == SqlServerSafeMigrationRuntimePlan.IndexFilterUnsupportedCode
            && (captured.Count == 0 || hasUntouchedColumn))
        {
            // WHY: An unchanged predicate column retains its original failed
            // physical conversion proof. Another newly added column or an
            // empty-table row proof cannot repair that filter contract.
            return live;
        }

        if (captured.Count == 0)
        {
            return null;
        }

        var proof = _projectedIndexFilters.TryGetValue(index, out var proofs)
            ? proofs.FirstOrDefault(candidate => candidate.Matches(captured)) : null;

        // WHY: The original catalog can report a missing table, not the target
        // column's filter conversion. Exact provider-qualified constants must
        // precede Core's otherwise valid empty-table or added-NULL promotion.

        if (proof?.Result != 1)
        {
            return FilterUnsupported();
        }

        if (live.ObservedState == SafeMigrationObservedState.Unsupported
            && live.Code == SqlServerSafeMigrationRuntimePlan.IndexFilterUnsupportedCode)
        {
            var recreated = source is ISafeMigrationProjectedTableSource tables
                && tables.TryGetProjectedTableState(index.Definition.Table, index.Definition.Schema, out var state)
                && state.IsNewlyCreated && !state.HasUnknownStructure;

            return proof.AllColumnsMutated || recreated
                ? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
                    SafeMigrationRepairCapability.None, false,
                    SqlServerProjectedIndexFilterProof.SupportedReplacementCode)
                : live;
        }

        return null;
    }

    private static SafeMigrationProviderAnalysis FilterUnsupported()
        => new(SafeMigrationObservedState.Unsupported, SafeMigrationRepairCapability.None,
            false, "index_filter_value_unproven");
}
