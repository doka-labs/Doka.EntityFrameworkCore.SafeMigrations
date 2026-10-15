namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Measures canonical key identity allocation without concurrent process-wide collections.</summary>
// WHY: Keep both collection and warmed-cache cases in one exclusive theory with unchanged case data.
[Collection(ProcessWideTestGroup.Name)]
public sealed class SafeMigrationModelManagedKeyIdentityAllocationTests
{
    /// <summary>Canonical key orders allocate no permutation or comparer, including after garbage collection.</summary>
    /// <param name="singleMember">Whether the input contains only one key member.</param>
    /// <param name="collect">Whether to collect after warming the canonicalization path.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CompositeKeyAlreadyOrderedPathAvoidsAllocations(bool singleMember, bool collect)
    {
        // Arrange
        string[] columns = singleMember ? ["Id"] : ["Code", "Id"];
        for (var iteration = 0; iteration < 256; iteration++)
        {
            _ = SafeMigrationModelManagedKeyIdentity.CanonicalizeColumns(columns, normalizer: null, out _);
        }

        if (collect)
        {
            // WHY: The ordered path must not depend on a weak runtime cache
            // retaining an earlier comparer or permutation allocation.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        IReadOnlyList<string> canonical = columns;
        int[]? ordinals = null;

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 512; iteration++)
        {
            canonical = SafeMigrationModelManagedKeyIdentity.CanonicalizeColumns(columns, normalizer: null,
                out ordinals);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.Equal(0L, allocated);
        Assert.Same(columns, canonical);
        Assert.Null(ordinals);
    }
}
