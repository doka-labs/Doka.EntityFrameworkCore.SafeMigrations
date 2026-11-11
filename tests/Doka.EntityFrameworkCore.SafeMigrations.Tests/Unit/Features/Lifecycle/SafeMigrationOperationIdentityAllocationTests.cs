namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Compares retained one-column identity metadata with the original capture shape.</summary>

// WHY: Forced collections isolate metadata capture after runtime caches can be discarded.
[Collection(ProcessWideTestGroup.Name)]
public sealed class SafeMigrationOperationIdentityAllocationTests
{
    /// <summary>Production one-column identities retain the original captured column.</summary>
    [Fact]
    public void SingleColumnProductionPreservesRetainedMetadata()
    {
        // Arrange
        const int count = 10_000;
        var intent = new EnsureColumnIntent(
            "Items",
            new ExpectedColumnDefinition("IsActive", typeof(bool), isNullable: false));

        var baseline = new SafeMigrationOperationIdentity[count];
        var candidate = new SafeMigrationOperationIdentity[count];
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Act
        for (var index = 0; index < count; index++)
        {
            baseline[index] = FrozenArrayIdentity(intent);
        }

        for (var index = 0; index < count; index++)
        {
            candidate[index] = SafeMigrationOperationIdentity.Create(intent);
        }

        // Assert
        Assert.All(baseline, identity => Assert.Equal("IsActive", Assert.Single(identity.Columns)));
        Assert.All(candidate, identity => Assert.Equal("IsActive", Assert.Single(identity.Columns)));
    }

    // WHY: Preserve the original captured shape as an independent metadata compatibility reference.
    private static SafeMigrationOperationIdentity FrozenArrayIdentity(
        EnsureColumnIntent intent
    ) => SafeMigrationOperationIdentity.CreateKnown(
        intent.Kind,
        intent.Schema,
        intent.Table,
        intent.ObjectName,
        Array.AsReadOnly<string>([intent.ObjectName]));

    /// <summary>Ordinary column families preserve the original validated captured metadata.</summary>
    /// <param name="kind">The ordinary operation family to capture.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureColumn)]
    [InlineData(SafeMigrationOperationKind.AlterColumn)]
    [InlineData(SafeMigrationOperationKind.DropColumn)]
    [InlineData(SafeMigrationOperationKind.RenameColumn)]
    public void OrdinaryColumnProductionPreservesRetainedMetadata(
        SafeMigrationOperationKind kind
    )
    {
        // Arrange
        const int count = 10_000;
        var operation = SafeMigrationRunnerColumnIdentityTests.CreateOperation(kind);
        var baseline = new SafeMigrationOperationIdentity?[count];
        var candidate = new SafeMigrationOperationIdentity?[count];
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Act
        for (var index = 0; index < count; index++)
        {
            baseline[index] = FrozenOrdinaryColumnIdentity(operation);
        }

        for (var index = 0; index < count; index++)
        {
            candidate[index] = SafeMigrationRunner.CaptureProviderIdentity(operation);
        }

        // Assert
        Assert.All(baseline, identity => Assert.Equal("Value", Assert.Single(identity!.Columns)));
        Assert.All(candidate, identity => Assert.Equal("Value", Assert.Single(identity!.Columns)));
        Assert.Equal(baseline[0]!.NewObjectName, candidate[0]!.NewObjectName);
    }

    // WHY: Retain original member validation in the independent metadata compatibility reference.
    private static SafeMigrationOperationIdentity? FrozenOrdinaryColumnIdentity(
        MigrationOperation operation
    )
    {
        var (kind, schema, table, name, newName) = operation switch
        {
            AddColumnOperation value =>
                (SafeMigrationOperationKind.EnsureColumn, value.Schema, value.Table, value.Name, null),
            AlterColumnOperation value =>
                (SafeMigrationOperationKind.AlterColumn, value.Schema, value.Table, value.Name, null),
            DropColumnOperation value =>
                (SafeMigrationOperationKind.DropColumn, value.Schema, value.Table, value.Name, (string?)null),
            RenameColumnOperation value =>
                (SafeMigrationOperationKind.RenameColumn, value.Schema, value.Table, value.Name, value.NewName),
            _ => throw new ArgumentException("An ordinary column operation is required.", nameof(operation)),
        };

        var columns = Array.AsReadOnly<string>([name]);
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
