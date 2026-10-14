namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Measures display-boundary allocation after an isolated process-wide collection.</summary>
// WHY: The forced collection must not perturb another test class's warmed allocation window.
[Collection(ProcessWideTestGroup.Name)]
public sealed class SafeMigrationDiagnosticTruncationAllocationTests
{
    /// <summary>Boundary selection performs no allocations, including after a garbage collection.</summary>
    [Fact]
    public void DisplayBoundarySelectionDoesNotAllocate()
    {
        // Arrange
        var builder = new StringBuilder(new string('x', 1020) + "\\u20AC");
        var checksum = SafeMigrationDiagnosticMessages.GetDisplayPrefixLength(builder, 0, 1024);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetAllocatedBytesForCurrentThread();

        // Act
        for (var index = 0; index < 10_000; index++)
        {
            checksum += SafeMigrationDiagnosticMessages.GetDisplayPrefixLength(builder, 0, 1024);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.Equal(10_001 * 1020, checksum);
        Assert.Equal(0, allocated);
    }
}
