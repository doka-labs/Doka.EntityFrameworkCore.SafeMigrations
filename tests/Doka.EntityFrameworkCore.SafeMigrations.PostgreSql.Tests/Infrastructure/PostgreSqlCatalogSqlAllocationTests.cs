namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>
/// Bounds what composing one table-definition classifier allocates per expected column.
/// </summary>
/// <remarks>
/// This test exists because the classifier's cost per column was never bounded by anything. A
/// measured run over the hundred-thousand-operation contract attributed 36.4 GB of allocation to
/// one analysis while the dispatched SQL text accounted for 6 percent of it. The bound below is
/// deliberately loose: it is a regression fence against reintroducing per-column composition, not
/// a performance claim, and the absolute figure moves with the runtime and the type mappings.
///
/// The measured figure at the time of writing is about 2 500 bytes per column. The bound sits
/// well above it so ordinary drift does not fail the suite, while a return to composing the
/// table match twice per build, or to one string fragment per column, exceeds it.
/// </remarks>
public sealed class PostgreSqlCatalogSqlAllocationTests
{
    private const string OfflineConnectionString =
        "Host=127.0.0.1;Port=1;Database=allocation_bound;Username=test;Password=test";

    /// <summary>Composition allocates a bounded amount for each additional expected column.</summary>
    [Fact]
    public void TableDefinitionComposition_AllocatesABoundedAmountPerColumn()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(OfflineConnectionString, registerSafeMigrations: false);
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        var helper = context.GetService<ISqlGenerationHelper>();
        var narrow = BuildOperation(context, 4);
        var wide = BuildOperation(context, 64);

        // WHY: The first builds populate the type-mapping and renderer caches, which would
        // otherwise be charged to whichever size runs first.
        for (var warmup = 0; warmup < 20; warmup++)
        {
            _ = new PostgreSqlSafeMigrationCatalogSqlBuilder(mappings, helper).Build(narrow);
            _ = new PostgreSqlSafeMigrationCatalogSqlBuilder(mappings, helper).Build(wide);
        }

        // Act
        var narrowBytes = MeasureAllocation(mappings, helper, narrow);
        var wideBytes = MeasureAllocation(mappings, helper, wide);

        // Assert
        var bytesPerColumn = (wideBytes - narrowBytes) / 60;

        Assert.InRange(bytesPerColumn, 1, 4_000);
    }

    private static long MeasureAllocation(
        IRelationalTypeMappingSource mappings,
        ISqlGenerationHelper helper,
        SafeMigrationOperation operation
    )
    {
        const int iterations = 200;
        // WHY: GetTotalAllocatedBytes counts the whole process, so a live test allocating on
        // another thread lands inside this window and fails the bound at random. The loop below
        // never awaits, so it stays on one thread and the thread-local counter measures it alone.
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            _ = new PostgreSqlSafeMigrationCatalogSqlBuilder(mappings, helper).Build(operation).StateExpression;
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
    }

    private static SafeMigrationOperation BuildOperation(
        SafeMigrationDbContext context,
        int columnCount
    )
    {
        var columns = Enumerable.Range(0, columnCount)
            .Select(static index => new ExpectedColumnDefinition(
                "column_" + index.ToString(CultureInfo.InvariantCulture), typeof(int), false, "integer"))
            .ToArray();

        var migration = new MigrationBuilder(context.Database.ProviderName!);
        migration.EnsureTable(
            new ExpectedTableDefinition("allocation_table", columns),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        return (SafeMigrationOperation)migration.Operations[0];
    }
}
