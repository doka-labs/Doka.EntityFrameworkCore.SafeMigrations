namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies canonical key identity with isolated process-wide collections.</summary>

// WHY: Keep forced collections from interrupting other process-wide contracts.
[Collection(ProcessWideTestGroup.Name)]
public sealed class SafeMigrationModelManagedKeyIdentityAllocationTests
{
    /// <summary>Canonical keys retain the authored list without a permutation after garbage collection.</summary>
    /// <param name="singleMember">Whether the input contains only one key member.</param>
    /// <param name="collect">Whether to collect after initially canonicalizing the key.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CompositeKeyAlreadyOrderedPathPreservesReference(
        bool singleMember,
        bool collect
    )
    {
        // Arrange
        string[] columns = singleMember
            ? ["Id"]
            : ["Code", "Id"];

        var initial = SafeMigrationModelManagedKeyIdentity.CanonicalizeColumns(
            columns,
            normalizer: null,
            out var initialOrdinals);

        if (collect)
        {
            // WHY: Reference preservation must not depend on surviving weak runtime caches.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        IReadOnlyList<string> canonical = columns;
        int[]? ordinals = null;

        // Act
        for (var iteration = 0; iteration < 512; iteration++)
        {
            canonical = SafeMigrationModelManagedKeyIdentity.CanonicalizeColumns(
                columns,
                normalizer: null,
                out ordinals);
        }

        // Assert
        Assert.Same(columns, initial);
        Assert.Null(initialOrdinals);
        Assert.Same(columns, canonical);
        Assert.Null(ordinals);
    }
}
