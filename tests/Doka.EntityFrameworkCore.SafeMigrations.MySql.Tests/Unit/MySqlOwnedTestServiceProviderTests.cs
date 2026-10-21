namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Guards ownership of test-only canonical/native profiles without opening a database.</summary>
public sealed class MySqlOwnedTestServiceProviderTests
{
    /// <summary>
    /// More than EF's global-cache threshold of legitimate profiles must remain independently usable.
    /// </summary>
    [Fact]
    public async Task GeneratedProfilesRemainIsolatedAcrossParallelContexts()
    {
        // Arrange
        const int profileCount = 25;
        var versions = Enumerable.Range(1, profileCount)
            .Select(static patch => MySqlServerVersion.MariaDb(new Version(11, 8, patch)))
            .ToArray();

        // Act
        var roots = await Task.WhenAll(versions.Select(static version => Task.Run(() =>
        {
            using var context = new GeneratedFacetPendingDropDbContext(
                "Server=127.0.0.1;Port=1;Database=owned_profile;User ID=test", version);

            _ = context.Model;
            var root = context.GetService<IDbContextOptions>()
                .FindExtension<CoreOptionsExtension>()!.InternalServiceProvider;

            return root;
        })));

        // Assert
        Assert.All(roots, static root => Assert.NotNull(root));
        Assert.Equal(profileCount, roots.Distinct(ReferenceEqualityComparer.Instance).Count());
    }

    /// <summary>The context retains one explicit root across operations and releases it after its EF scope.</summary>
    /// <param name="asynchronous">Whether the owner uses asynchronous disposal.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedProfileDisposalReleasesOnlyItsOwnedRoot(bool asynchronous)
    {
        // Arrange
        var version = MySqlServerVersion.MariaDb(new Version(11, 8, 8));
        var owned = new GeneratedFacetPendingDropDbContext(
            "Server=127.0.0.1;Port=1;Database=owned_profile;User ID=test", version);

        using var sibling = new GeneratedFacetPendingDropDbContext(
            "Server=127.0.0.1;Port=1;Database=sibling_profile;User ID=test", version);

        var root = owned.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()!.InternalServiceProvider;

        var repeatedRoot = owned.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()!.InternalServiceProvider;

        var siblingRoot = sibling.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()!.InternalServiceProvider;

        // Act
        if (asynchronous)
        {
            await owned.DisposeAsync();
        }
        else
        {
            owned.Dispose();
        }

        var failure = Record.Exception(() => root!.GetService(typeof(IMigrationsSqlGenerator)));
        var siblingModel = sibling.Model;

        // Assert
        Assert.NotNull(root);
        Assert.Same(root, repeatedRoot);
        Assert.NotSame(root, siblingRoot);
        Assert.IsType<ObjectDisposedException>(failure);
        Assert.NotNull(siblingModel.FindEntityType(typeof(GeneratedFacetPendingDropRow)));
    }
}
