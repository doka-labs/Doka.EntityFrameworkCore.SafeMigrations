namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>Binds a provider SQL result to the exact authored and accepted insert lineage.</summary>
/// <param name="Table">The unchanged destination table definition.</param>
/// <param name="Seeds">The exact immutable insert references evaluated by SQL Server.</param>
/// <param name="Result">One for proven unique, zero for collision, minus one for unproven.</param>
/// <param name="SeedCount">
/// The evaluated prefix of the immutable lineage buffer, or minus one for its whole length.
/// </param>
internal sealed record SqlServerProjectedSeedKeyProof(
    ExpectedTableDefinition Table,
    EnsureModelManagedDataIntent[] Seeds,
    int Result,
    int SeedCount = -1
)
{
    /// <summary>Checks that no refused operation or structural mutation contributed to this proof.</summary>
    /// <param name="accepted">The provider's ordered accepted insert lineage.</param>
    /// <param name="source">The current ordered projected column definitions.</param>
    /// <param name="prospective">The pending insert, when validating rows before acceptance.</param>
    /// <returns>True only for the exact unchanged lineage evaluated by the provider.</returns>
    internal bool Matches(
        SqlServerProjectedSeedLineage accepted,
        ISafeMigrationProjectedColumnSource source,
        EnsureModelManagedDataIntent? prospective = null
    )
    {
        var expectedCount = accepted.Seeds.Count + (prospective is null ? 0 : 1);
        var count = SeedCount < 0 ? Seeds.Length : SeedCount;
        if (!ReferenceEquals(Table, accepted.Table) || count != expectedCount || count > Seeds.Length)
        {
            return false;
        }

        for (var ordinal = 0; ordinal < accepted.Seeds.Count; ordinal++)
        {
            if (!ReferenceEquals(Seeds[ordinal], accepted.Seeds[ordinal]))
            {
                return false;
            }
        }

        if (prospective is not null && !ReferenceEquals(Seeds[count - 1], prospective))
        {
            return false;
        }

        foreach (var definition in Table.Columns)
        {
            if (!source.TryGetProjectedColumn(Table.Table, Table.Schema, definition.Name, out var projected)
                || !ReferenceEquals(definition, projected))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Contains only inserts within one unchanged, newly-created table lineage.</summary>
internal sealed class SqlServerProjectedSeedLineage(ExpectedTableDefinition table)
{
    /// <summary>Gets the exact destination definition that opened this lineage.</summary>
    internal ExpectedTableDefinition Table { get; } = table;

    /// <summary>Gets the immutable insert intents observed in operation order.</summary>
    internal List<EnsureModelManagedDataIntent> Seeds { get; } = [];

    /// <summary>Gets the ordered physical candidate contracts that must constrain prospective inserts.</summary>
    internal Dictionary<string, SqlServerProjectedSeedContract> UniqueContracts { get; } = InitialContracts(table);

    /// <summary>Gets whether an unsupported filtered contract prevents an exact prospective row proof.</summary>
    internal bool HasUnprovenContract { get; set; }

    private static Dictionary<string, SqlServerProjectedSeedContract> InitialContracts(
        ExpectedTableDefinition definition
    )
    {
        var result = new Dictionary<string, SqlServerProjectedSeedContract>(StringComparer.Ordinal);
        if (definition.PrimaryKey is { } primaryKey)
        {
            result.Add(primaryKey.Name, new SqlServerProjectedSeedContract(primaryKey.Columns));
        }

        foreach (var uniqueKey in definition.UniqueConstraints)
        {
            result.Add(uniqueKey.Name, new SqlServerProjectedSeedContract(uniqueKey.Columns));
        }

        return result;
    }
}

/// <summary>Captures the immutable ordered candidate and structured filter of an authored physical key.</summary>
/// <param name="Columns">The ordered candidate key columns.</param>
/// <param name="Filter">The supported structured filter, or null for an ordinary key.</param>
internal sealed record SqlServerProjectedSeedContract(
    IReadOnlyList<string> Columns,
    SafeMigrationSqlExpression? Filter = null
);

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
    private readonly Dictionary<(string Schema, string Table), SqlServerProjectedSeedLineage>
        _acceptedSeedLineages = [];
    private Dictionary<SafeMigrationIntent, List<SqlServerProjectedSeedKeyProof>> _projectedSeedKeys = [];
    private Dictionary<EnsureModelManagedDataIntent, List<SqlServerProjectedSeedKeyProof>> _projectedSeedRows = [];

    private void ResetProjectedSeedProofs()
    {
        _acceptedSeedLineages.Clear();
        _projectedSeedKeys = [];
        _projectedSeedRows = [];
        _projectedIndexFilters = [];
    }

    private void InvalidateProjectedSeedProofs() => _acceptedSeedLineages.Clear();

    private void ObserveProjectedSeedOperation(
        SafeMigrationOperation operation,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action is not (SafeMigrationAction.Apply or SafeMigrationAction.NoOp
            or SafeMigrationAction.Repair))
        {
            return;
        }

        ObserveSeedLineage(_acceptedSeedLineages, operation.Intent, decision.Action == SafeMigrationAction.NoOp);
    }

    private Task ReadProjectedSeedKeyProofsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyList<SafeMigrationOperation> operations,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {

        return CaptureProjectedSeedProofsAsync(operations, ExecuteProofAsync, cancellationToken);

        async Task<int> ExecuteProofAsync(
            string sql,
            CancellationToken token
        )
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            ApplyCommandTimeout(command, commandTimeout);
            command.CommandText = sql;

            return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Captures bounded row proofs with invocation-local memoization of immutable seed snapshots.</summary>
    /// <param name="operations">The ordered authored stream.</param>
    /// <param name="execute">The invocation's provider scalar execution boundary.</param>
    /// <param name="cancellationToken">The invocation cancellation token.</param>
    internal async Task CaptureProjectedSeedProofsAsync(
        IReadOnlyList<SafeMigrationOperation> operations,
        Func<string, CancellationToken, Task<int>> execute,
        CancellationToken cancellationToken
    )
    {
        var filters = await CaptureProjectedFilterProofsAsync(operations, execute, cancellationToken);
        var authored = new Dictionary<(string Schema, string Table), SqlServerProjectedSeedLineage>();
        var result = new Dictionary<SafeMigrationIntent, List<SqlServerProjectedSeedKeyProof>>();
        var rows = new Dictionary<EnsureModelManagedDataIntent, List<SqlServerProjectedSeedKeyProof>>();
        var caches = new Dictionary<SqlServerProjectedSeedLineage, SqlServerProjectedSeedProofCache>();
        var buffers = PrepareSeedBuffers(operations, cancellationToken);
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = operations[ordinal];

            var identity = UniqueKeyIdentity(operation.Intent);
            if (identity is not null && authored.TryGetValue(identity.Value, out var lineage)
                && lineage.Seeds.Count != 0)
            {
                var columns = UniqueKeyColumns(operation.Intent);
                var capturedMetadataMatches = lineage.Seeds.All(seed =>
                    seed.KeyColumns.SequenceEqual(columns, StringComparer.Ordinal)
                    || seed.UniqueKeys.Any(key => key.Columns.SequenceEqual(columns, StringComparer.Ordinal)));

                var filter = operation.Intent is EnsureIndexIntent index
                    ? SqlServerSafeMigrationCatalogSqlBuilder.GetStructuredIndexFilter(index.Definition) : null;

                var proof = await CaptureAsync(lineage, columns, capturedMetadataMatches || filter is not null, filter);
                AddProof(result, operation.Intent, proof);
            }

            ObserveSeedLineage(authored, operation.Intent, noOp: false);
            if (operation.Intent is EnsureTableIntent table)
            {
                caches.Add(authored[(table.Definition.Schema ?? "dbo", table.Definition.Table)],
                    new SqlServerProjectedSeedProofCache(buffers[ordinal]));
            }

            if (operation.Intent is EnsureModelManagedDataIntent seed
                && authored.TryGetValue((seed.Schema ?? "dbo", seed.Table), out var prospective))
            {
                var candidates = prospective.UniqueContracts.Values
                    .Concat(prospective.Seeds.SelectMany(static value => value.UniqueKeys)
                        .Select(static key => new SqlServerProjectedSeedContract(key.Columns)))
                    .Append(new SqlServerProjectedSeedContract(seed.KeyColumns));

                var cache = GetCache(prospective);
                var outcome = prospective.HasUnprovenContract ? -1 : 1;
                foreach (var candidate in candidates)
                {
                    var proof = await CaptureAsync(
                        prospective, candidate.Columns, metadataMatches: true, candidate.Filter);

                    outcome = proof.Result == 0 || outcome == 0 ? 0
                        : proof.Result == -1 || outcome == -1 ? -1 : 1;
                }

                AddProof(rows, seed, new SqlServerProjectedSeedKeyProof(
                    prospective.Table, cache.Seeds, outcome, prospective.Seeds.Count));
            }
        }

        // WHY: The provider observer must see exactly the accepted immutable
        // references, not merely matching Core key metadata or column hashes.
        // Publishing only a completed capture also prevents cancellation reuse.
        cancellationToken.ThrowIfCancellationRequested();
        _projectedSeedKeys = result;
        _projectedSeedRows = rows;
        _projectedIndexFilters = filters;

        async Task<SqlServerProjectedSeedKeyProof> CaptureAsync(
            SqlServerProjectedSeedLineage lineage,
            IReadOnlyList<string> columns,
            bool metadataMatches,
            SafeMigrationSqlExpression? filter = null
        )
        {
            var cache = GetCache(lineage);

            return await cache.GetAsync(lineage, columns, metadataMatches, filter, _catalogSqlBuilder,
                execute, cancellationToken);
        }

        SqlServerProjectedSeedProofCache GetCache(SqlServerProjectedSeedLineage lineage)
        {
            return caches[lineage];
        }
    }

    private static Dictionary<int, EnsureModelManagedDataIntent[]> PrepareSeedBuffers(
        IReadOnlyList<SafeMigrationOperation> operations,
        CancellationToken cancellationToken
    )
    {
        var lineages = new Dictionary<(string Schema, string Table), SqlServerProjectedSeedLineage>();
        var tables = new Dictionary<int, SqlServerProjectedSeedLineage>();
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var intent = operations[ordinal].Intent;
            ObserveSeedLineage(lineages, intent, noOp: false);
            if (intent is EnsureTableIntent table)
            {
                tables.Add(ordinal, lineages[(table.Definition.Schema ?? "dbo", table.Definition.Table)]);
            }
        }

        // WHY: Every proof retains only a prefix count over this shared immutable
        // buffer. S seed batches therefore retain S references, not S squared.

        return tables.ToDictionary(static table => table.Key, static table => table.Value.Seeds.ToArray());
    }

    private SafeMigrationProviderAnalysis QualifyProjectedSeedOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (operation.Intent is not EnsureModelManagedDataIntent seed
            || analysis.IsInvariantUnsupported || analysis.IsOpaqueProjectionUnknown
            || analysis.ObservedState is not (SafeMigrationObservedState.Missing or SafeMigrationObservedState.Matching)
            || columns is not ISafeMigrationProjectedTableSource tableSource
            || !tableSource.TryGetProjectedTableState(seed.Table, seed.Schema, out var state)
            || !state.IsNewlyCreated || state.HasUnknownStructure)
        {
            return analysis;
        }

        if (!_acceptedSeedLineages.TryGetValue((seed.Schema ?? "dbo", seed.Table), out var accepted)
            || !_projectedSeedRows.TryGetValue(seed, out var proofs))
        {
            return UnprovenProjectedSeed();
        }

        var proof = proofs.FirstOrDefault(candidate => candidate.Matches(accepted, columns, seed));

        return proof?.Result switch
        {
            1 => analysis,
            0 => new SafeMigrationProviderAnalysis(SafeMigrationObservedState.DataBlocked,
                SafeMigrationRepairCapability.None, false, "projected_seed_row_collision"),
            _ => UnprovenProjectedSeed(),
        };
    }

    private static SafeMigrationProviderAnalysis UnprovenProjectedSeed()
        => new(SafeMigrationObservedState.PrerequisiteMissing, SafeMigrationRepairCapability.None,
            false, "projected_seed_row_data_unproven");

    private static void AddProof<TIntent>(
        Dictionary<TIntent, List<SqlServerProjectedSeedKeyProof>> target,
        TIntent intent,
        SqlServerProjectedSeedKeyProof proof
    ) where TIntent : SafeMigrationIntent
    {
        if (!target.TryGetValue(intent, out var proofs))
        {
            proofs = [];
            target.Add(intent, proofs);
        }

        proofs.Add(proof);
    }

    private SafeMigrationProviderAnalysis? ProjectedSeedKeyAnalysis(
        SafeMigrationIntent intent,
        ISafeMigrationProjectedColumnSource source
    )
    {
        var identity = UniqueKeyIdentity(intent);
        if (identity is null || !_projectedSeedKeys.TryGetValue(intent, out var proofs)
            || !_acceptedSeedLineages.TryGetValue(identity.Value, out var accepted))
        {
            return null;
        }

        var proof = proofs.FirstOrDefault(candidate => candidate.Matches(accepted, source));
        if (proof is null)
        {
            return null;
        }

        return proof.Result switch
        {
            1 => new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
                SafeMigrationRepairCapability.None, false, "projected_seed_key_data_safe"),
            0 => new SafeMigrationProviderAnalysis(SafeMigrationObservedState.DataBlocked,
                SafeMigrationRepairCapability.None, false, "projected_seed_key_collision"),
            _ => new SafeMigrationProviderAnalysis(SafeMigrationObservedState.PrerequisiteMissing,
                SafeMigrationRepairCapability.None, false, "projected_seed_key_data_unproven"),
        };
    }

    private static void ObserveSeedLineage(
        Dictionary<(string Schema, string Table), SqlServerProjectedSeedLineage> lineages,
        SafeMigrationIntent intent,
        bool noOp
    )
    {
        switch (intent)
        {
            case EnsureTableIntent ensure when !noOp:
                lineages[(ensure.Definition.Schema ?? "dbo", ensure.Definition.Table)]
                    = new SqlServerProjectedSeedLineage(ensure.Definition);
                break;
            case EnsureModelManagedDataIntent seed:
                if (lineages.TryGetValue((seed.Schema ?? "dbo", seed.Table), out var lineage))
                {
                    lineage.Seeds.Add(seed);
                }

                break;
            case EnsurePrimaryKeyIntent key:
                AddUniqueContract(key.Definition.Table, key.Definition.Schema,
                    key.Definition.Name, key.Definition.Columns);
                break;
            case EnsureUniqueConstraintIntent key:
                AddUniqueContract(key.Definition.Table, key.Definition.Schema,
                    key.Definition.Name, key.Definition.Columns);
                break;
            case EnsureIndexIntent { Definition.Unique: true } key:
                if (lineages.TryGetValue((key.Definition.Schema ?? "dbo", key.Definition.Table), out var indexLineage))
                {
                    var filter = SqlServerSafeMigrationCatalogSqlBuilder.GetStructuredIndexFilter(key.Definition);
                    indexLineage.HasUnprovenContract |= key.Definition.Filter is not null && filter is null
                        || key.Definition.Keys.Any(static column => column.Column is null);
                    if (!indexLineage.HasUnprovenContract)
                    {
                        indexLineage.UniqueContracts[key.Definition.Name]
                            = new SqlServerProjectedSeedContract(
                                key.Definition.Keys.Select(static column => column.Column!).ToArray(),
                                filter);
                    }
                }

                break;
            case DropPrimaryKeyIntent key:
                lineages.Remove((key.Schema ?? "dbo", key.Table));
                break;
            case DropUniqueConstraintIntent key:
                lineages.Remove((key.Schema ?? "dbo", key.Table));
                break;
            case DropIndexIntent key:
                lineages.Remove((key.Schema ?? "dbo", key.Table));
                break;
            case RenameIndexIntent key:
                lineages.Remove((key.Schema ?? "dbo", key.Table));
                break;
            case ModelManagedDataIntent mutation:
                lineages.Remove((mutation.Schema ?? "dbo", mutation.Table));
                break;
            case DropTableIntent drop:
                lineages.Remove((drop.Schema ?? "dbo", drop.Table));
                break;
            case RenameTableIntent rename:
                lineages.Remove((rename.Schema ?? "dbo", rename.Name));
                lineages.Remove((rename.NewSchema ?? rename.Schema ?? "dbo", rename.NewName ?? rename.Name));
                break;
            case EnsureColumnIntent column:
                lineages.Remove((column.Schema ?? "dbo", column.Table));
                break;
            case AlterColumnIntent column:
                lineages.Remove((column.Schema ?? "dbo", column.Table));
                break;
            case DropColumnIntent column:
                lineages.Remove((column.Schema ?? "dbo", column.Table));
                break;
            case RenameColumnIntent column:
                lineages.Remove((column.Schema ?? "dbo", column.Table));
                break;
        }

        void AddUniqueContract(
            string table,
            string? schema,
            string name,
            IReadOnlyList<string> columns
        )
        {
            if (lineages.TryGetValue((schema ?? "dbo", table), out var lineage))
            {
                lineage.UniqueContracts[name] = new SqlServerProjectedSeedContract(columns);
            }
        }
    }

    private static (string Schema, string Table)? UniqueKeyIdentity(SafeMigrationIntent intent)
        => intent switch
        {
            EnsurePrimaryKeyIntent key => (key.Definition.Schema ?? "dbo", key.Definition.Table),
            EnsureUniqueConstraintIntent key => (key.Definition.Schema ?? "dbo", key.Definition.Table),
            EnsureIndexIntent { Definition.Unique: true, } key
                when key.Definition.Keys.All(static column => column.Column is not null)
                => (key.Definition.Schema ?? "dbo", key.Definition.Table),
            _ => null,
        };

    private static IReadOnlyList<string> UniqueKeyColumns(SafeMigrationIntent intent)
        => intent switch
        {
            EnsurePrimaryKeyIntent key => key.Definition.Columns,
            EnsureUniqueConstraintIntent key => key.Definition.Columns,
            EnsureIndexIntent key => key.Definition.Keys.Select(static column => column.Column!).ToArray(),
            _ => throw new UnreachableException(),
        };
}
