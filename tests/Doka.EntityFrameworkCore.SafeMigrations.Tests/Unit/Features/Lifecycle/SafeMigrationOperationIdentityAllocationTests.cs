namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Compares one-column identity metadata using deterministic retained-object allocation evidence.</summary>
public sealed class SafeMigrationOperationIdentityAllocationTests
{
    /// <summary>Production one-column identities retain less metadata than an array plus read-only wrapper.</summary>
    [Fact]
    public void SingleColumnProductionReducesRetainedMetadataAllocation()
    {
        // Arrange
        const int count = 10_000;
        var intent = new EnsureColumnIntent("Items",
            new ExpectedColumnDefinition("IsActive", typeof(bool), isNullable: false));

        var baseline = new SafeMigrationOperationIdentity[count];
        var candidate = new SafeMigrationOperationIdentity[count];
        _ = FrozenArrayIdentity(intent);
        _ = SafeMigrationOperationIdentity.Create(intent);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Act
        var beforeBaseline = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < count; index++)
        {
            baseline[index] = FrozenArrayIdentity(intent);
        }

        var baselineBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBaseline;
        var beforeCandidate = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < count; index++)
        {
            candidate[index] = SafeMigrationOperationIdentity.Create(intent);
        }

        var candidateBytes = GC.GetAllocatedBytesForCurrentThread() - beforeCandidate;

        // Assert
        Assert.All(baseline, identity => Assert.Equal("IsActive", Assert.Single(identity.Columns)));
        Assert.All(candidate, identity => Assert.Equal("IsActive", Assert.Single(identity.Columns)));
        Assert.True(candidateBytes < baselineBytes, "The candidate must prove lower retained metadata allocation.");
        Console.WriteLine($"Identity metadata: count={count}; baseline-bytes={baselineBytes}; "
            + $"candidate-bytes={candidateBytes}; saved-bytes={baselineBytes - candidateBytes}.");
    }

    // WHY: Retaining every identity in a preallocated array prevents ephemeral-object
    // elimination from changing the comparison. No timestamps, hardware budgets or database work are involved.
    private static SafeMigrationOperationIdentity FrozenArrayIdentity(EnsureColumnIntent intent) =>
        SafeMigrationOperationIdentity.CreateKnown(intent.Kind, intent.Schema, intent.Table, intent.ObjectName,
            Array.AsReadOnly(new[] { intent.ObjectName }));

    /// <summary>Ordinary column families reduce allocation against their original validated captures.</summary>
    /// <param name="kind">The ordinary operation family to measure.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureColumn)]
    [InlineData(SafeMigrationOperationKind.AlterColumn)]
    [InlineData(SafeMigrationOperationKind.DropColumn)]
    [InlineData(SafeMigrationOperationKind.RenameColumn)]
    public void OrdinaryColumnProductionReducesRetainedMetadataAllocation(SafeMigrationOperationKind kind)
    {
        // Arrange
        const int count = 10_000;
        var operation = SafeMigrationRunnerColumnIdentityTests.CreateOperation(kind);
        var baseline = new SafeMigrationOperationIdentity?[count];
        var candidate = new SafeMigrationOperationIdentity?[count];
        _ = FrozenOrdinaryColumnIdentity(operation);
        _ = SafeMigrationRunner.CaptureProviderIdentity(operation);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Act
        var beforeBaseline = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < count; index++)
        {
            baseline[index] = FrozenOrdinaryColumnIdentity(operation);
        }

        var baselineBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBaseline;
        var beforeCandidate = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < count; index++)
        {
            candidate[index] = SafeMigrationRunner.CaptureProviderIdentity(operation);
        }

        var candidateBytes = GC.GetAllocatedBytesForCurrentThread() - beforeCandidate;

        // Assert
        Assert.All(baseline, identity => Assert.Equal("Value", Assert.Single(identity!.Columns)));
        Assert.All(candidate, identity => Assert.Equal("Value", Assert.Single(identity!.Columns)));
        Assert.Equal(baseline[0]!.NewObjectName, candidate[0]!.NewObjectName);
        Assert.True(candidateBytes < baselineBytes, "Production must reduce retained ordinary-column allocation.");
        Console.WriteLine($"Ordinary column metadata: kind={kind}; count={count}; baseline-bytes={baselineBytes}; "
            + $"candidate-bytes={candidateBytes}; saved-bytes={baselineBytes - candidateBytes}.");
    }

    // WHY: This frozen baseline includes the original member-validation enumeration.
    // Comparing against bare CreateKnown would omit allocation already paid by ordinary EF captures.
    private static SafeMigrationOperationIdentity? FrozenOrdinaryColumnIdentity(MigrationOperation operation)
    {
        var (kind, schema, table, name, newName) = operation switch
        {
            AddColumnOperation value =>
                (SafeMigrationOperationKind.EnsureColumn, value.Schema, value.Table, value.Name, (string?)null),
            AlterColumnOperation value =>
                (SafeMigrationOperationKind.AlterColumn, value.Schema, value.Table, value.Name, (string?)null),
            DropColumnOperation value =>
                (SafeMigrationOperationKind.DropColumn, value.Schema, value.Table, value.Name, (string?)null),
            RenameColumnOperation value =>
                (SafeMigrationOperationKind.RenameColumn, value.Schema, value.Table, value.Name, value.NewName),
            _ => throw new ArgumentException("An ordinary column operation is required.", nameof(operation)),
        };

        var columns = Array.AsReadOnly(new[] { name });
        if (string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(table)
            || (schema is not null && string.IsNullOrWhiteSpace(schema))
            || (newName is not null && string.IsNullOrWhiteSpace(newName)))
        {
            return null;
        }

        foreach (var column in columns)
        {
            if (string.IsNullOrWhiteSpace(column))
            {
                return null;
            }

        }

        return SafeMigrationOperationIdentity.CreateKnown(kind, schema, table, name, columns, newObjectName: newName);
    }

}
