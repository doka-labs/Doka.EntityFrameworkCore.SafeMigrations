namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies diagnostic explanations after an isolated process-wide collection.</summary>

// WHY: The explanation cache probe must not interrupt another test class's process-wide contracts.
[Collection(ProcessWideTestGroup.Name)]
public sealed class SafeMigrationDiagnosticMessagesAllocationTests
{
    /// <summary>Constant explanations do not depend on collectible enum metadata caches after GC.</summary>
    [Fact]
    public void ExplanationLookupPreservesReasonAfterGarbageCollection()
    {
        // Arrange
        var intent = RequiredColumnOperation()
            .Intent;

        var expected = SafeMigrationDiagnosticMessages.GetExplanation(
            intent,
            SafeMigrationObservedState.DataBlocked,
            "required_column_backfill_missing",
            "data_blocked");

        var checksum = expected.Reason.Length;
        var reasons = new string[1000];

        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Act
        for (var index = 0; index < 1000; index++)
        {
            reasons[index] = SafeMigrationDiagnosticMessages.GetExplanation(
                    intent,
                    SafeMigrationObservedState.DataBlocked,
                    "required_column_backfill_missing",
                    "data_blocked")
                .Reason;

            checksum += reasons[index].Length;
        }

        // Assert
        Assert.True(checksum > 0);
        Assert.All(reasons, reason => Assert.Equal(expected.Reason, reason));
    }

    private static SafeMigrationOperation RequiredColumnOperation() => new(
        new EnsureColumnIntent(
            "Items",
            new ExpectedColumnDefinition("IsActive", typeof(bool), isNullable: false, storeType: "tinyint(1)"),
            schema: "application"),
        SafeMigrationPolicy.ThrowIfDifferent);
}
