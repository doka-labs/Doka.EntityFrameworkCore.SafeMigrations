namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

internal sealed partial class SqliteSafeMigrationProviderAnalyzer
{
    /// <inheritdoc />
    public Task<IReadOnlyList<SafeMigrationUnexpectedObject>> FindUnexpectedObjectsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operations);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = SqliteSafeMigrationCatalog.Read(
            context.Database.GetDbConnection(),
            context.Database.CurrentTransaction?.GetDbTransaction());

        var expectedTables = SafeMigrationExpectedCatalog
            .Create(operations, NormalizeSchema)
            .Where(static table => table.Schema is null)
            .ToDictionary(static table => table.Table, s_identifierComparer);

        var result = new List<SafeMigrationUnexpectedObject>();

        foreach (var table in snapshot.Tables.Values.OrderBy(static value => value.Name, s_identifierComparer))
        {
            if (!expectedTables.TryGetValue(table.Name, out var expectedTable))
            {
                result.Add(
                    new SafeMigrationUnexpectedObject(
                        SafeMigrationDatabaseObjectKind.Table,
                        schema: null,
                        table: null,
                        table.Name,
                        "unexpected_table"));
                continue;
            }

            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var column in table.Columns.Values.Where(column => !Contains(expectedTable.Columns, column.Name)))
            {
                result.Add(
                    new SafeMigrationUnexpectedObject(
                        SafeMigrationDatabaseObjectKind.Column,
                        schema: null,
                        table.Name,
                        column.Name,
                        "unexpected_column"));
            }

            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var index in table.Indexes.Where(index => index.Origin == "c"
                         && !Contains(expectedTable.Indexes, index.Name)))
            {
                result.Add(
                    new SafeMigrationUnexpectedObject(
                        SafeMigrationDatabaseObjectKind.Index,
                        schema: null,
                        table.Name,
                        index.Name,
                        "unexpected_index"));
            }

            if (table.PrimaryKeyColumns.Count > 0)
            {
                AddUnexpectedConstraint(
                    result,
                    expectedTable,
                    table.Name,
                    table.PrimaryKeyName ?? "sqlite_primary_key",
                    SafeMigrationDatabaseObjectKind.PrimaryKey);
            }

            foreach (var unique in table.UniqueConstraints)
            {
                AddUnexpectedConstraint(
                    result,
                    expectedTable,
                    table.Name,
                    unique.Name ?? unique.PhysicalName,
                    SafeMigrationDatabaseObjectKind.UniqueConstraint);
            }

            // WHY: Use the CHECK ordinal to produce a stable fallback identity for unnamed constraints.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < table.Checks.Count; index++)
            {
                AddUnexpectedConstraint(
                    result,
                    expectedTable,
                    table.Name,
                    table.Checks[index].Name ?? $"sqlite_check_{index.ToString(CultureInfo.InvariantCulture)}",
                    SafeMigrationDatabaseObjectKind.CheckConstraint);
            }

            foreach (var foreignKey in table.ForeignKeys)
            {
                AddUnexpectedConstraint(
                    result,
                    expectedTable,
                    table.Name,
                    foreignKey.Name ?? $"sqlite_fk_{foreignKey.Id.ToString(CultureInfo.InvariantCulture)}",
                    SafeMigrationDatabaseObjectKind.ForeignKey);
            }
        }

        return Task.FromResult(RemoveSemanticAliases(context, operations, result, snapshot, cancellationToken));
    }

    /// <summary>Removes only aliases proven against the same snapshot used to construct this inventory.</summary>
    private IReadOnlyList<SafeMigrationUnexpectedObject> RemoveSemanticAliases(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        List<SafeMigrationUnexpectedObject> findings,
        SqliteCatalogSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        if (findings.Count == 0)
        {
            return findings.AsReadOnly();
        }

        var semanticAliases = new HashSet<int>();
        foreach (var candidates in SafeMigrationSemanticCandidateFactory
                     .Create(operations, findings, projectUniqueIndexesAsUniqueConstraints: true)
                     .Chunk(SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture))
        {
            // WHY: These windows belong to one read-only inventory invocation.
            // Re-reading the full catalog for every 512 candidates repeats all
            // schema and per-object queries without adding fresher evidence.
            // The snapshot is never retained across invocations or row proofs.
            var analyses = Analyze(
                context,
                candidates
                    .Select(static candidate => (MigrationOperation)candidate.Operation)
                    .ToArray(),
                targetModels: null,
                snapshot,
                cancellationToken);

            // WHY: Pair each alias candidate with the analysis returned at the same ordinal.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < candidates.Length; index++)
            {
                if (analyses[index].ObservedState == SafeMigrationObservedState.Matching)
                {
                    _ = semanticAliases.Add(candidates[index].UnexpectedObjectIndex);
                }
            }
        }

        if (semanticAliases.Count == 0)
        {
            return findings.AsReadOnly();
        }

        return findings
            .Where((_, index) => !semanticAliases.Contains(index))
            .ToArray();
    }

    /// <summary>Adds a physical constraint not covered by the expected table inventory.</summary>
    private static void AddUnexpectedConstraint(
        List<SafeMigrationUnexpectedObject> result,
        SafeMigrationExpectedTableInventory expectedTable,
        string table,
        string name,
        SafeMigrationDatabaseObjectKind kind
    )
    {
        if (expectedTable.Constraints.Any(value => value.Value == kind && s_identifierComparer.Equals(value.Key, name)))
        {
            return;
        }

        result.Add(
            new SafeMigrationUnexpectedObject(
                kind,
                schema: null,
                table,
                name,
                kind switch
                {
                    SafeMigrationDatabaseObjectKind.PrimaryKey => "unexpected_primary_key",
                    SafeMigrationDatabaseObjectKind.UniqueConstraint => "unexpected_unique_constraint",
                    SafeMigrationDatabaseObjectKind.CheckConstraint => "unexpected_check_constraint",
                    SafeMigrationDatabaseObjectKind.ForeignKey => "unexpected_foreign_key",
                    SafeMigrationDatabaseObjectKind.Table
                        or SafeMigrationDatabaseObjectKind.Column
                        or SafeMigrationDatabaseObjectKind.Index => throw new UnreachableException(
                            $"Object kind '{kind}' is not a constraint."),
                    _ => throw new UnreachableException($"Object kind value '{(int)kind}' is not defined."),
                }));
    }

    /// <summary>Tests inventory membership using SQLite's verified identifier semantics.</summary>
    private static bool Contains(
        IReadOnlySet<string> values,
        string value
    ) => values.Any(candidate => s_identifierComparer.Equals(candidate, value));
}
