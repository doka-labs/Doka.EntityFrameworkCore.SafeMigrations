namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>
/// Memoizes provider results and immutable row snapshots within one analysis and row-lineage version.
/// </summary>
internal sealed class SqlServerProjectedSeedProofCache(EnsureModelManagedDataIntent[] seeds)
{
    private int _seedCount;
    private readonly Dictionary<string, SqlServerProjectedSeedKeyProof> _proofs = new(StringComparer.Ordinal);

    /// <summary>Gets the immutable complete lineage buffer shared by all evaluated prefixes.</summary>
    internal EnsureModelManagedDataIntent[] Seeds { get; } = seeds;

    /// <summary>
    /// Evaluates each ordered candidate tuple once at the current lineage version, including negative proofs.
    /// </summary>
    /// <param name="lineage">The analysis-owned destination and authored rows.</param>
    /// <param name="columns">The ordered candidate columns.</param>
    /// <param name="metadataMatches">Whether the caller's captured key metadata authorizes this candidate.</param>
    /// <param name="filter">The immutable structured filter, or null for an ordinary candidate.</param>
    /// <param name="builder">The provider's shared typed SQL proof builder.</param>
    /// <param name="execute">The invocation's scalar execution boundary.</param>
    /// <param name="cancellationToken">The caller cancellation token.</param>
    /// <returns>A result bound to the shared immutable seed snapshot.</returns>
    internal async Task<SqlServerProjectedSeedKeyProof> GetAsync(
        SqlServerProjectedSeedLineage lineage,
        IReadOnlyList<string> columns,
        bool metadataMatches,
        SafeMigrationSqlExpression? filter,
        SqlServerSafeMigrationCatalogSqlBuilder builder,
        Func<string, CancellationToken, Task<int>> execute,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = lineage.Seeds.Count;
        if (count > Seeds.Length)
        {
            throw new InvalidOperationException("SQL Server authored seed prefix exceeds its immutable buffer.");
        }

        if (_seedCount != count)
        {
            _seedCount = count;
            _proofs.Clear();
        }

        if (!metadataMatches)
        {
            return new SqlServerProjectedSeedKeyProof(lineage.Table, Seeds, -1, count);
        }

        // WHY: Length-prefixed names retain tuple order without collisions
        // between legal identifiers containing delimiters or punctuation.
        var tuple = new StringBuilder();
        foreach (var column in columns)
        {
            tuple.Append(column.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(column);
        }

        var predicate = filter is null ? string.Empty : builder.ProjectedSeedFilterIdentity(filter);
        if (predicate is null)
        {
            return new SqlServerProjectedSeedKeyProof(lineage.Table, Seeds, -1, count);
        }

        tuple.Append('/').Append(predicate.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(predicate);

        var identity = tuple.ToString();
        if (_proofs.TryGetValue(identity, out var previous))
        {
            return previous;
        }

        var prefix = new ArraySegment<EnsureModelManagedDataIntent>(Seeds, 0, count);
        var sql = builder.BuildProjectedSeedKeyProofSql(prefix, lineage.Table.Columns, columns,
            requireCapturedUniqueKey: false, filter);

        var result = sql is null ? -1 : await execute(sql, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is < -1 or > 1)
        {
            throw new InvalidOperationException("SQL Server returned an invalid authored key proof.");
        }

        var proof = new SqlServerProjectedSeedKeyProof(lineage.Table, Seeds, result, count);
        _proofs.Add(identity, proof);

        return proof;
    }
}
