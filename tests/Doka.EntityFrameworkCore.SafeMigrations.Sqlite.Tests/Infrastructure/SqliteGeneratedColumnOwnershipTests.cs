namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Bounds generated-facet ownership to accepted table identities and physical lifetimes.</summary>
public sealed class SqliteGeneratedColumnOwnershipTests
{
    /// <summary>Native rebuild metadata can specialize only its exact accepted safe target table.</summary>
    [Fact]
    public void UnrelatedNativeRebuildDoesNotInheritSafeOwnership()
    {
        // Arrange
        var safeDrop = new DropColumnOperation { Name = "obsolete", Table = "safe_nodes" };
        var ordinaryDrop = new DropColumnOperation { Name = "obsolete", Table = "ordinary_nodes" };
        var safeCreator = new CreateTableOperation { Name = "native_safe" };
        var ordinaryCreator = new CreateTableOperation { Name = "native_ordinary" };
        var ownership = new SqliteGeneratedColumnOwnership([safeDrop, ordinaryDrop], [ordinaryDrop]);
        MigrationOperation[] rewritten =
        [
            safeCreator,
            new RenameTableOperation { Name = safeCreator.Name, NewName = safeDrop.Table },
            ordinaryCreator,
            new RenameTableOperation { Name = ordinaryCreator.Name, NewName = ordinaryDrop.Table },
        ];

        // Act
        var creators = ownership.ResolveCreators(rewritten);

        // Assert
        Assert.Same(safeCreator, Assert.Single(creators));
        Assert.DoesNotContain(ordinaryCreator, creators);
    }

    /// <summary>A compiler target follows accepted table renames but not a discarded physical lifetime.</summary>
    /// <param name="recreate">Whether ordinary DROP and CREATE discard the safe lifetime after its rename.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenamedSafeLifetimeDoesNotLeakThroughOrdinaryRecreation(bool recreate)
    {
        // Arrange
        var operations = new List<MigrationOperation>
        {
            new DropColumnOperation { Name = "obsolete", Table = "old_nodes" },
            new RenameTableOperation { Name = "old_nodes", NewName = "new_nodes" },
        };

        var ordinary = new List<MigrationOperation>();
        if (recreate)
        {
            ordinary.Add(new DropTableOperation { Name = "new_nodes" });
            ordinary.Add(new CreateTableOperation { Name = "new_nodes" });
            operations.AddRange(ordinary);
        }

        var ownership = new SqliteGeneratedColumnOwnership(operations, ordinary);
        var creator = new CreateTableOperation { Name = "compiler_owned_name" };
        MigrationOperation[] rewritten =
        [
            creator,
            new RenameTableOperation { Name = creator.Name, NewName = "new_nodes" },
        ];

        // Act
        var creators = ownership.ResolveCreators(rewritten);

        // Assert
        Assert.Equal(!recreate, creators.Contains(creator));
    }

    /// <summary>User-authored rename objects cannot impersonate compiler-produced rebuild provenance.</summary>
    [Fact]
    public void AuthoredRenameDoesNotCertifyUnrelatedCreator()
    {
        // Arrange
        var drop = new DropColumnOperation { Name = "obsolete", Table = "authored_name" };
        var rename = new RenameTableOperation { Name = "authored_name", NewName = "safe_nodes" };
        var creator = new CreateTableOperation { Name = rename.Name };
        var ownership = new SqliteGeneratedColumnOwnership([drop, rename], [rename]);

        // Act
        var creators = ownership.ResolveCreators([creator, rename]);

        // Assert
        Assert.Empty(creators);
    }
}
