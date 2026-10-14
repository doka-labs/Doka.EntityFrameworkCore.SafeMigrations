namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Guards immutable singleton capture for ordinary mutable EF column operations.</summary>
public sealed class SafeMigrationRunnerColumnIdentityTests
{
    /// <summary>Each ordinary family keeps its authored owner and member after the source operation changes.</summary>
    /// <param name="kind">The equivalent ordinary column operation family.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureColumn)]
    [InlineData(SafeMigrationOperationKind.AlterColumn)]
    [InlineData(SafeMigrationOperationKind.DropColumn)]
    [InlineData(SafeMigrationOperationKind.RenameColumn)]
    public void OrdinaryColumnCaptureSnapshotsImmutableIdentifiers(SafeMigrationOperationKind kind)
    {
        // Arrange
        var operation = CreateOperation(kind);

        // Act
        var identity = SafeMigrationRunner.CaptureProviderIdentity(operation);
        ChangeIdentifiers(operation, "Changed", "Changed", "Changed");

        // Assert
        Assert.NotNull(identity);
        Assert.Equal(kind, identity.OperationKind);
        Assert.Equal("authored", identity.Schema);
        Assert.Equal("Items", identity.Table);
        Assert.Equal("Value", identity.ObjectName);
        Assert.Equal("Value", Assert.Single(identity.Columns));
        Assert.False(identity.Columns is IList<string>);
        Assert.False(identity.Columns is string[]);
        Assert.Equal(kind == SafeMigrationOperationKind.RenameColumn ? "NewValue" : null, identity.NewObjectName);
    }

    /// <summary>Malformed required column names and owners remain unknown rather than becoming placeholders.</summary>
    /// <param name="kind">The ordinary column family.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureColumn)]
    [InlineData(SafeMigrationOperationKind.AlterColumn)]
    [InlineData(SafeMigrationOperationKind.DropColumn)]
    [InlineData(SafeMigrationOperationKind.RenameColumn)]
    public void OrdinaryColumnCaptureRejectsMissingName(SafeMigrationOperationKind kind)
    {
        // Arrange
        var operation = CreateOperation(kind);
        ChangeIdentifiers(operation, name: null, "Items", "authored");

        // Act
        var identity = SafeMigrationRunner.CaptureProviderIdentity(operation);

        // Assert
        Assert.Null(identity);
    }

    /// <summary>Missing owner tables do not acquire a fabricated owner during singleton capture.</summary>
    /// <param name="kind">The ordinary column family.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureColumn)]
    [InlineData(SafeMigrationOperationKind.AlterColumn)]
    [InlineData(SafeMigrationOperationKind.DropColumn)]
    [InlineData(SafeMigrationOperationKind.RenameColumn)]
    public void OrdinaryColumnCaptureRejectsMissingTable(SafeMigrationOperationKind kind)
    {
        // Arrange
        var operation = CreateOperation(kind);
        ChangeIdentifiers(operation, "Value", table: null, "authored");

        // Act
        var identity = SafeMigrationRunner.CaptureProviderIdentity(operation);

        // Assert
        Assert.Null(identity);
    }

    /// <summary>Whitespace names or tables remain malformed under every ordinary column family.</summary>
    /// <param name="kind">The ordinary column family.</param>
    /// <param name="malformedName">Whether the member or owner is the malformed required identifier.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureColumn, true)]
    [InlineData(SafeMigrationOperationKind.AlterColumn, true)]
    [InlineData(SafeMigrationOperationKind.DropColumn, true)]
    [InlineData(SafeMigrationOperationKind.RenameColumn, true)]
    [InlineData(SafeMigrationOperationKind.EnsureColumn, false)]
    [InlineData(SafeMigrationOperationKind.AlterColumn, false)]
    [InlineData(SafeMigrationOperationKind.DropColumn, false)]
    [InlineData(SafeMigrationOperationKind.RenameColumn, false)]
    public void OrdinaryColumnCaptureRejectsWhitespaceIdentifiers(SafeMigrationOperationKind kind, bool malformedName)
    {
        // Arrange
        var operation = CreateOperation(kind);
        ChangeIdentifiers(operation, malformedName ? " " : "Value", malformedName ? "Items" : " ", "authored");

        // Act
        var identity = SafeMigrationRunner.CaptureProviderIdentity(operation);

        // Assert
        Assert.Null(identity);
    }

    /// <summary>An unqualified owner remains valid, while a whitespace qualifier remains malformed.</summary>
    /// <param name="kind">The ordinary column family.</param>
    /// <param name="schema">The authored qualifier.</param>
    /// <param name="isKnown">Whether the authored metadata remains usable.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureColumn, null, true)]
    [InlineData(SafeMigrationOperationKind.AlterColumn, null, true)]
    [InlineData(SafeMigrationOperationKind.DropColumn, null, true)]
    [InlineData(SafeMigrationOperationKind.RenameColumn, null, true)]
    [InlineData(SafeMigrationOperationKind.EnsureColumn, " ", false)]
    [InlineData(SafeMigrationOperationKind.AlterColumn, " ", false)]
    [InlineData(SafeMigrationOperationKind.DropColumn, " ", false)]
    [InlineData(SafeMigrationOperationKind.RenameColumn, " ", false)]
    public void OrdinaryColumnCapturePreservesQualifierMeaning(
        SafeMigrationOperationKind kind, string? schema, bool isKnown
    )
    {
        // Arrange
        var operation = CreateOperation(kind);
        ChangeIdentifiers(operation, "Value", "Items", schema);

        // Act
        var identity = SafeMigrationRunner.CaptureProviderIdentity(operation);

        // Assert
        Assert.Equal(isKnown, identity is not null);
        if (isKnown)
        {
            Assert.Null(identity!.Schema);
        }

    }

    /// <summary>Provides synthetic ordinary EF operations without rows, expressions or provider annotations.</summary>
    /// <param name="kind">The requested ordinary column family.</param>
    /// <returns>A mutable EF operation for the isolated capture tests.</returns>
    internal static MigrationOperation CreateOperation(SafeMigrationOperationKind kind) => kind switch
    {
        SafeMigrationOperationKind.EnsureColumn => new AddColumnOperation
            { Name = "Value", Table = "Items", Schema = "authored", ClrType = typeof(int) },
        SafeMigrationOperationKind.AlterColumn => new AlterColumnOperation
            { Name = "Value", Table = "Items", Schema = "authored", ClrType = typeof(int) },
        SafeMigrationOperationKind.DropColumn => new DropColumnOperation
            { Name = "Value", Table = "Items", Schema = "authored" },
        SafeMigrationOperationKind.RenameColumn => new RenameColumnOperation
            { Name = "Value", Table = "Items", Schema = "authored", NewName = "NewValue" },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static void ChangeIdentifiers(MigrationOperation operation, string? name, string? table, string? schema)
    {
        switch (operation)
        {
            case AddColumnOperation value:
                value.Name = name!;
                value.Table = table!;
                value.Schema = schema;
                break;
            case AlterColumnOperation value:
                value.Name = name!;
                value.Table = table!;
                value.Schema = schema;
                break;
            case DropColumnOperation value:
                value.Name = name!;
                value.Table = table!;
                value.Schema = schema;
                break;
            case RenameColumnOperation value:
                value.Name = name!;
                value.Table = table!;
                value.Schema = schema;
                value.NewName = "Changed";
                break;
            default:
                throw new ArgumentException("A column operation is required.", nameof(operation));
        }

    }
}
