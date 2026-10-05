namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies bounded proof command counts and shared immutable prefix storage.</summary>
public sealed class SqlServerProjectedSeedProofCacheTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=seed_proofs;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>
    /// Repeated ordered keys reuse one provider query and one immutable seed buffer, including negative results.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RepeatedCandidate_ExecutesOnceAndSharesResultAndRows(int result)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = Catalog(context);
        var seed = Seed(1);
        var rows = new[] { seed };
        var lineage = new SqlServerProjectedSeedLineage(Table());
        lineage.Seeds.Add(seed);
        var cache = new SqlServerProjectedSeedProofCache(rows);
        var executions = 0;
        var proofs = new List<SqlServerProjectedSeedKeyProof>();
        Task<int> Execute(
            string sql,
            CancellationToken token
        )
        {
            executions++;

            return Task.FromResult(result);
        }

        // Act
        for (var ordinal = 0; ordinal < 100; ordinal++)
        {
            proofs.Add(await cache.GetAsync(lineage, ["Code"], true, null, builder, Execute, CancellationToken.None));
        }

        // Assert
        Assert.Equal(1, executions);
        Assert.All(proofs, proof =>
        {
            Assert.Same(proofs[0], proof);
            Assert.Same(rows, proof.Seeds);
            Assert.Equal(result, proof.Result);
        });
    }

    /// <summary>Appended seed versions retain a shared complete buffer instead of quadratic copied prefixes.</summary>
    [Fact]
    public async Task AppendedSeedVersions_ShareLinearImmutableReferenceStorage()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = Catalog(context);
        var rows = Enumerable.Range(1, 100).Select(Seed).ToArray();
        var lineage = new SqlServerProjectedSeedLineage(Table());
        var cache = new SqlServerProjectedSeedProofCache(rows);
        var proofs = new List<SqlServerProjectedSeedKeyProof>();
        var executions = 0;
        Task<int> Execute(
            string sql,
            CancellationToken token
        )
        {
            executions++;

            return Task.FromResult(1);
        }

        // Act
        foreach (var seed in rows)
        {
            lineage.Seeds.Add(seed);
            proofs.Add(await cache.GetAsync(lineage, ["Code"], true, null, builder, Execute, CancellationToken.None));
        }

        // Assert
        Assert.Equal(rows.Length, executions);
        Assert.All(proofs, proof => Assert.Same(rows, proof.Seeds));
        Assert.Equal(Enumerable.Range(1, rows.Length), proofs.Select(static proof => proof.SeedCount));
        Assert.Equal(100, proofs[0].Seeds.Length);
        Assert.Equal(1, proofs[0].SeedCount);
    }

    /// <summary>Candidate tuple order and filter semantics are separate provider cache identities.</summary>
    [Fact]
    public async Task ChangedCandidateOrFilter_DoesNotBorrowTheEarlierProof()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = Catalog(context);
        var rows = new[] { Seed(1) };
        var lineage = new SqlServerProjectedSeedLineage(Table());
        lineage.Seeds.Add(rows[0]);
        var cache = new SqlServerProjectedSeedProofCache(rows);
        var executions = 0;
        Task<int> Execute(
            string sql,
            CancellationToken token
        )
        {
            executions++;

            return Task.FromResult(1);
        }

        // Act
        var first = await cache.GetAsync(lineage, ["Id", "Code"], true, null, builder, Execute, CancellationToken.None);
        var reordered = await cache.GetAsync(
            lineage, ["Code", "Id"], true, null, builder, Execute, CancellationToken.None);

        var filtered = await cache.GetAsync(lineage, ["Code", "Id"], true,
            SafeMigrationSql.IsNotNull(SafeMigrationSql.Identifier("Code")), builder, Execute, CancellationToken.None);

        // Assert
        Assert.Equal(3, executions);
        Assert.NotSame(first, reordered);
        Assert.NotSame(reordered, filtered);
        Assert.Same(first.Seeds, reordered.Seeds);
        Assert.Same(first.Seeds, filtered.Seeds);
    }

    /// <summary>
    /// The real capture path executes one candidate proof despite many subsequent equivalent key operations.
    /// </summary>
    [Fact]
    public async Task OrderedCapture_RepeatedKeysDoNotRepeatTheCandidateCommand()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = new SqlServerSafeMigrationProviderAnalyzer(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var operations = new List<SafeMigrationOperation>
        {
            new(new EnsureTableIntent(Table(), SafeMigrationTableMode.StrictDefinition),
                SafeMigrationPolicy.ThrowIfDifferent),
            new(Seed(1), SafeMigrationPolicy.ThrowIfDifferent),
        };

        for (var ordinal = 0; ordinal < 100; ordinal++)
        {
            operations.Add(new SafeMigrationOperation(new EnsureIndexIntent(
                new ExpectedIndexDefinition("IX_seed_" + ordinal, "seeds",
                    [new ExpectedIndexKeyDefinition("Code")], unique: true)), SafeMigrationPolicy.ThrowIfDifferent));
        }

        var commands = new List<string>();
        Task<int> Execute(
            string sql,
            CancellationToken token
        )
        {
            commands.Add(sql);

            return Task.FromResult(1);
        }

        // Act
        await analyzer.CaptureProjectedSeedProofsAsync(operations, Execute, CancellationToken.None);

        // Assert
        Assert.Equal(2, commands.Count);
        Assert.Single(commands, static sql => sql.Contains(
            "AS nvarchar(20)) COLLATE DATABASE_DEFAULT", StringComparison.Ordinal));
    }

    private static ExpectedTableDefinition Table()
        => new("seeds", [new ExpectedColumnDefinition("Id", typeof(int), false, "int"),
            new ExpectedColumnDefinition("Code", typeof(string), true, "nvarchar(20)")],
            primaryKey: new ExpectedPrimaryKeyDefinition("PK_seeds", "seeds", ["Id"]));

    private static EnsureModelManagedDataIntent Seed(int id)
        => new("seeds", ["Id"], ["int"], ["Id", "Code"], ["int", "nvarchar(20)"],
            new object?[,] { { id, "code_" + id } }, null,
            uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["Code"])]);

    private static SqlServerSafeMigrationCatalogSqlBuilder Catalog(DbContext context)
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());
}
