namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies display boundaries after an isolated process-wide collection.</summary>

// WHY: The forced collection must not interrupt another test class's process-wide contracts.
[Collection(ProcessWideTestGroup.Name)]
public sealed class SafeMigrationDiagnosticTruncationAllocationTests
{
    /// <summary>Boundary selection preserves a complete escape sequence after garbage collection.</summary>
    [Fact]
    public void DisplayBoundarySelectionPreservesEscapeAfterGarbageCollection()
    {
        // Arrange
        var builder = new StringBuilder(new string('x', 1020) + "\\u20AC");
        var checksum = SafeMigrationDiagnosticMessages.GetDisplayPrefixLength(builder, 0, 1024);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Act
        for (var index = 0; index < 10_000; index++)
        {
            checksum += SafeMigrationDiagnosticMessages.GetDisplayPrefixLength(builder, 0, 1024);
        }

        // Assert
        Assert.Equal(10_001 * 1020, checksum);
    }
}
