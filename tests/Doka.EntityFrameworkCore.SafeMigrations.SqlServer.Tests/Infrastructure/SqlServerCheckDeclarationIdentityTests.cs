namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Preserves native owner and constraint identity guards around mixed CHECK prefixes.</summary>
public sealed class SqlServerCheckDeclarationIdentityTests
{
    /// <summary>Ordinary CHECK declarations expose parent aliases to the existing catalog collision guard.</summary>
    [Fact]
    public void TypedCheckDeclarationsRetainOwnerAndConstraintSpellings()
    {
        // Arrange
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        builder.AddCheckConstraint("CK_source", "Nodes", "[Upper]>0");
        builder.RenameColumnIfExists("Upper", "nodes", "NewValue");
        var references = SqlServerIdentifierContract.Collect(builder.Operations.OfType<SafeMigrationOperation>());

        // Act
        var captured = SqlServerIdentifierContract.IncludeCheckDeclarationReferences(builder.Operations, references);

        // Assert
        Assert.Contains(captured, static reference => reference.Scope == SqlServerIdentifierScope.SchemaObject
            && reference.Name == "Nodes");
        Assert.Contains(captured, static reference => reference.Scope == SqlServerIdentifierScope.SchemaObject
            && reference.Name == "nodes");
        Assert.Contains(captured, static reference => reference.Name == "CK_source");
        Assert.Contains(SqlServerIdentifierContract.BuildCollisionGuardCommands(captured, false),
            static sql => sql.Contains("COLLATE CATALOG_DEFAULT", StringComparison.Ordinal));
    }

    /// <summary>Ordinary operations without CHECK declarations do not allocate another identity inventory.</summary>
    [Fact]
    public void UnrelatedProviderOperationsPreserveExistingIdentityInventory()
    {
        // Arrange
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        builder.AddColumn<int>("Value", "Nodes", nullable: true);
        var references = SqlServerIdentifierContract.Collect(Array.Empty<SafeMigrationOperation>());

        // Act
        var captured = SqlServerIdentifierContract.IncludeCheckDeclarationReferences(builder.Operations, references);

        // Assert
        Assert.Same(references, captured);
    }
}
