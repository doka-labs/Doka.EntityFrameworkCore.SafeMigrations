namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Repeated alterations reuse normalized FK dependencies rather than rescanning every owner.</summary>
    [Fact]
    public void RepeatedAlterDependencyLookupDoesNotRescanForeignKeyOwners()
    {
        // Arrange
        var normalizer = new ColumnStateOwnershipNormalizer();
        var provider = new CapturingProjectedColumnAnalyzer();
        var projection = new SafeMigrationPreflightProjection(objectIdentityNormalizer: normalizer,
            projectedColumnAnalyzer: provider);
        ObserveAccepted(projection, new EnsureColumnIntent("items", VarcharColumn(200)),
            SafeMigrationObservedState.Matching);
        for (var number = 0; number < 500; number++)
        {
            Apply(projection, new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition($"fk_{number}",
                $"child_{number}", ["value"], $"parent_{number}", ["value"])));
        }

        var alter = new SafeMigrationOperation(new AlterColumnIntent("items", VarcharColumn(500), VarcharColumn(200)),
            SafeMigrationPolicy.RepairIfSafe);
        projection.Project(alter, AlterProof());
        var comparisonsBefore = normalizer.CountingComparer.EqualityChecks;

        // Act
        for (var iteration = 0; iteration < 100; iteration++)
        {
            projection.Project(alter, AlterProof());
        }

        var comparisons = normalizer.CountingComparer.EqualityChecks - comparisonsBefore;

        // Assert
        Assert.NotNull(provider.Context);
        Assert.False(provider.Context.Value.HasForeignKeyDependency);
        Assert.InRange(comparisons, 0, 10_000);
    }

    /// <summary>A no-FK stream does not allocate one dependency view for every unrelated table.</summary>
    [Fact]
    public void AlterDependencyLookupAvoidsNoForeignKeyTableScanAllocations()
    {
        // Arrange
        var provider = new CapturingProjectedColumnAnalyzer();
        var warmup = NoForeignKeyProjection(1, provider);
        var alter = new SafeMigrationOperation(new AlterColumnIntent("items", VarcharColumn(500), VarcharColumn(200)),
            SafeMigrationPolicy.RepairIfSafe);
        warmup.Project(alter, AlterProof());
        var projection = NoForeignKeyProjection(1_000, provider);
        var live = AlterProof();

        // Act
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        projection.Project(alter, live);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // Assert
        Assert.NotNull(provider.Context);
        Assert.False(provider.Context.Value.HasForeignKeyDependency);
        Assert.InRange(allocated, 0, 8_192);
    }

    /// <summary>Accepted rename aliases and FK drops invalidate only the cached dependency identities.</summary>
    [Fact]
    public void AlterDependencyCacheTracksRenamedAliasesAndAcceptedForeignKeyDrop()
    {
        // Arrange
        var provider = new CapturingProjectedColumnAnalyzer();
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new ColumnStateOwnershipNormalizer(normalizeAliases: true),
            projectedColumnAnalyzer: provider);
        Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", [VarcharColumn(200)],
            foreignKeys: [new ExpectedForeignKeyDefinition("fk_self", "items", ["value"], "items", ["value"])]),
            SafeMigrationTableMode.StrictDefinition));
        var initial = new SafeMigrationOperation(new AlterColumnIntent("items", VarcharColumn(500), VarcharColumn(200)),
            SafeMigrationPolicy.RepairIfSafe);
        projection.Project(initial, AlterProof());
        var initialDependency = provider.Context?.HasForeignKeyDependency;

        // Act
        ObserveAccepted(projection, new RenameTableIntent("items", "renamed"), SafeMigrationObservedState.Matching);
        var renamed = new SafeMigrationOperation(new AlterColumnIntent("RENAMED", VarcharColumn(500),
            VarcharColumn(200), "dbo"), SafeMigrationPolicy.RepairIfSafe);
        projection.Project(renamed, AlterProof());
        var renamedDependency = provider.Context?.HasForeignKeyDependency;
        ObserveAccepted(projection, new DropForeignKeyIntent(name: "fk_self", table: "renamed"),
            SafeMigrationObservedState.Matching);
        projection.Project(renamed, AlterProof());

        // Assert
        Assert.True(initialDependency);
        Assert.True(renamedDependency);
        Assert.NotNull(provider.Context);
        Assert.False(provider.Context.Value.HasForeignKeyDependency);
    }

    private static SafeMigrationPreflightProjection NoForeignKeyProjection(
        int unrelatedTables,
        CapturingProjectedColumnAnalyzer provider
    )
    {
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider);
        Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", [VarcharColumn(200)]),
            SafeMigrationTableMode.StrictDefinition));
        for (var number = 0; number < unrelatedTables; number++)
        {
            Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition($"unrelated_{number}", [Column("id")]),
                SafeMigrationTableMode.StrictDefinition));
        }

        return projection;
    }
}
