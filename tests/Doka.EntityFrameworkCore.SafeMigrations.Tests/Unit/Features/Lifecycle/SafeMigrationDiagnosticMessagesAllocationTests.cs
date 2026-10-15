namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Measures diagnostic explanations after an isolated process-wide collection.</summary>
// WHY: The explanation cache probe forces collection outside all other warmed allocation windows.
[Collection(ProcessWideTestGroup.Name)]
public sealed class SafeMigrationDiagnosticMessagesAllocationTests
{
    /// <summary>Constant explanations do not depend on collectible enum metadata caches after GC.</summary>
    [Fact]
    public void ExplanationLookupDoesNotAllocateAfterGarbageCollection()
    {
        // Arrange
        var intent = RequiredColumnOperation().Intent;
        var checksum = SafeMigrationDiagnosticMessages.GetExplanation(intent,
            SafeMigrationObservedState.DataBlocked, "required_column_backfill_missing", "data_blocked").Reason.Length;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetAllocatedBytesForCurrentThread();

        // Act
        for (var index = 0; index < 1000; index++)
        {
            checksum += SafeMigrationDiagnosticMessages.GetExplanation(intent,
                SafeMigrationObservedState.DataBlocked,
                "required_column_backfill_missing", "data_blocked").Reason.Length;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.True(checksum > 0);
        Assert.Equal(0, allocated);
    }

    private static SafeMigrationOperation RequiredColumnOperation() => new(
        new EnsureColumnIntent("Items",
            new ExpectedColumnDefinition("IsActive", typeof(bool), isNullable: false, storeType: "tinyint(1)"),
            schema: "application"),
        SafeMigrationPolicy.ThrowIfDifferent);
}
