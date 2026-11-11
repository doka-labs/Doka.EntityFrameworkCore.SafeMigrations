namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Guards ownership of test-only canonical/native profiles without opening a database.</summary>
public sealed class MySqlOwnedTestServiceProviderTests
{
    /// <summary>The shared budget factory must not depend on EF's process-wide profile cache.</summary>
    [Fact]
    public async Task BudgetProfilesRemainIsolatedAcrossParallelContexts()
    {
        // Arrange
        const int profileCount = 25;
        var versions = Enumerable
            .Range(1, profileCount)
            .Select(static patch => MySqlServerVersion.MySql(new Version(8, 4, patch)))
            .ToArray();

        // Act
        var roots = await Task.WhenAll(
            versions.Select(static version => Task.Run(() =>
            {
                using var context = MySqlProvider106ContractTests.CreateContext(version: version);

                _ = context.Model;
                var root = context
                    .GetService<IDbContextOptions>()
                    .FindExtension<CoreOptionsExtension>()!.InternalServiceProvider;

                return root;
            })));

        // Assert
        Assert.All(roots, static root => Assert.NotNull(root));
        Assert.Equal(profileCount,
            roots
                .Distinct(ReferenceEqualityComparer.Instance)
                .Count());
    }

    /// <summary>Both unit and native consumers release the budget root only after their EF scope.</summary>
    /// <param name="asynchronous">Whether the consumer selects asynchronous disposal.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BudgetProfileDisposalReleasesOnlyItsOwnedRoot(
        bool asynchronous
    )
    {
        // Arrange
        // WHY: The fallback scope also releases ownership if sibling setup or an assertion fails.
        // ReSharper disable once UseAwaitUsing
        using var owned = MySqlProvider106ContractTests.CreateContext();

        // WHY: This sibling deliberately exercises synchronous cleanup alongside either owner path.
        // ReSharper disable once UseAwaitUsing
        using var sibling = MySqlProvider106ContractTests.CreateContext();
        var root = owned
            .GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()!.InternalServiceProvider;

        var repeatedRoot = owned
            .GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()!.InternalServiceProvider;

        var siblingRoot = sibling
            .GetService<IDbContextOptions>()
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
        Assert.NotNull(siblingModel.FindEntityType("strings"));
    }

    /// <summary>
    /// More than EF's global-cache threshold of legitimate profiles must remain independently usable.
    /// </summary>
    [Fact]
    public async Task GeneratedProfilesRemainIsolatedAcrossParallelContexts()
    {
        // Arrange
        const int profileCount = 25;
        var versions = Enumerable
            .Range(1, profileCount)
            .Select(static patch => MySqlServerVersion.MariaDb(new Version(11, 8, patch)))
            .ToArray();

        // Act
        var roots = await Task.WhenAll(
            versions.Select(static version => Task.Run(() =>
            {
                using var context = new GeneratedFacetPendingDropDbContext(
                    "Server=127.0.0.1;Port=1;Database=owned_profile;User ID=test",
                    version);

                _ = context.Model;
                var root = context
                    .GetService<IDbContextOptions>()
                    .FindExtension<CoreOptionsExtension>()!.InternalServiceProvider;

                return root;
            })));

        // Assert
        Assert.All(roots, static root => Assert.NotNull(root));
        Assert.Equal(profileCount,
            roots
                .Distinct(ReferenceEqualityComparer.Instance)
                .Count());
    }

    /// <summary>The context retains one explicit root across operations and releases it after its EF scope.</summary>
    /// <param name="asynchronous">Whether the owner uses asynchronous disposal.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedProfileDisposalReleasesOnlyItsOwnedRoot(
        bool asynchronous
    )
    {
        // Arrange
        var version = MySqlServerVersion.MariaDb(new Version(11, 8, 8));

        // WHY: Setup can fail before the explicit disposal Act; the fallback
        // remains idempotent and owns only this context's private service root.
        // ReSharper disable once UseAwaitUsing
        using var owned = new GeneratedFacetPendingDropDbContext(
            "Server=127.0.0.1;Port=1;Database=owned_profile;User ID=test",
            version);

        using var sibling = new GeneratedFacetPendingDropDbContext(
            "Server=127.0.0.1;Port=1;Database=sibling_profile;User ID=test",
            version);

        var root = owned
            .GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()!.InternalServiceProvider;

        var repeatedRoot = owned
            .GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()!.InternalServiceProvider;

        var siblingRoot = sibling
            .GetService<IDbContextOptions>()
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

    /// <summary>A setup exception still releases the generated context's privately owned root.</summary>
    [Fact]
    public void GeneratedProfileFallbackReleasesItsRootAfterSetupFailure()
    {
        // Arrange
        var version = MySqlServerVersion.MariaDb(new Version(11, 8, 8));
        var expected = new InvalidOperationException("Controlled generated-profile setup failure.");
        IServiceProvider? root = null;

        // WHY: A throw-only lambda can bind to xUnit's async overload. This
        // explicitly synchronous setup exercises the owner's synchronous fallback.
        var failDuringSetup = () =>
        {
            using var owned = new GeneratedFacetPendingDropDbContext(
                "Server=127.0.0.1;Port=1;Database=owned_profile;User ID=test",
                version);

            root = owned
                .GetService<IDbContextOptions>()
                .FindExtension<CoreOptionsExtension>()!.InternalServiceProvider;

            throw expected;
        };

        // Act
        var failure = Record.Exception(failDuringSetup);
        var disposalFailure = Record.Exception(() => root!.GetService(typeof(IMigrationsSqlGenerator)));

        // Assert
        Assert.Same(expected, failure);
        Assert.NotNull(root);
        Assert.IsType<ObjectDisposedException>(disposalFailure);
    }

    /// <summary>Specialized profiles remain outside EF's process-wide provider cache.</summary>
    /// <param name="pendingKey">Whether to exercise the separate canonical pending-key context.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpecializedProfilesRemainIsolatedAcrossParallelContexts(
        bool pendingKey
    )
    {
        // Arrange
        const int profileCount = 25;
        var versions = Enumerable
            .Range(1, profileCount)
            .Select(static patch => MySqlServerVersion.MariaDb(new Version(11, 8, patch)))
            .ToArray();

        // Act
        var roots = await Task.WhenAll(versions.Select(version => Task.Run(() =>
        {
            using var context = CreateSpecializedContext(pendingKey, version);
            _ = context.Model;

            return context
                .GetService<IDbContextOptions>()
                .FindExtension<CoreOptionsExtension>()!.InternalServiceProvider;
        })));

        // Assert
        Assert.All(roots, static root => Assert.NotNull(root));
        Assert.Equal(profileCount,
            roots
                .Distinct(ReferenceEqualityComparer.Instance)
                .Count());
    }

    /// <summary>Specialized roots remain usable through EF cleanup and never dispose a sibling's services.</summary>
    /// <param name="pendingKey">Whether to exercise the separate canonical pending-key context.</param>
    /// <param name="asynchronous">Whether cleanup uses asynchronous disposal.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SpecializedProfileDisposalReleasesOnlyItsOwnedRoot(
        bool pendingKey,
        bool asynchronous
    )
    {
        // Arrange
        var version = MySqlServerVersion.MariaDb(new Version(11, 8, 8));

        // WHY: Fallback cleanup also owns setup failures and repeated disposal after the explicit Act.
        // ReSharper disable once UseAwaitUsing
        using var owned = CreateSpecializedContext(pendingKey, version);

        // WHY: The sibling deliberately remains alive across either disposal path of the first context.
        // ReSharper disable once UseAwaitUsing
        using var sibling = CreateSpecializedContext(pendingKey, version);
        var root = owned
            .GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()!.InternalServiceProvider;

        var siblingRoot = sibling
            .GetService<IDbContextOptions>()
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
        Assert.NotSame(root, siblingRoot);
        Assert.IsType<ObjectDisposedException>(failure);
        Assert.NotNull(siblingModel);
    }

    /// <summary>A private native root must preserve the intentionally missing SafeMigrations handler.</summary>
    [Fact]
    public void NoHandlerProfilePreservesTheExpectedGenerationFailure()
    {
        // Arrange
        using var context = CreateSpecializedContext(pendingKey: false,
            MySqlServerVersion.MariaDb(new Version(11, 8, 8)));

        var operations = new MigrationBuilder(context.Database.ProviderName!);
        operations.CreateTableIfNotExists("must_not_exist",
            table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
            });

        var generator = context.GetService<IMigrationsSqlGenerator>();

        // Act
        var failure = Record.Exception(() => generator.Generate(operations.Operations, context.Model));

        // Assert
        Assert.Equal(MySqlMigrationHandlerFailureCode.UnknownOperationType,
            Assert.IsType<MySqlMigrationOperationHandlerException>(failure)
                .FailureCode);
    }

    /// <summary>Ordinary runtime contexts retain shared caching instead of receiving per-context roots.</summary>
    [Fact]
    public void OrdinaryProfileRetainsItsSharedProviderCache()
    {
        // Arrange
        var version = MySqlServerVersion.MariaDb(new Version(11, 8, 8));
        using var first = new SafeMigrationDbContext(
            "Server=127.0.0.1;Port=1;Database=first_profile;User ID=test",
            version);

        using var second = new SafeMigrationDbContext(
            "Server=127.0.0.1;Port=1;Database=second_profile;User ID=test",
            version);

        // Act
        var firstRoot = first
            .GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()!.InternalServiceProvider;

        var secondRoot = second
            .GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()!.InternalServiceProvider;

        var firstModelSource = first.GetService<IModelSource>();
        var secondModelSource = second.GetService<IModelSource>();

        // Assert
        Assert.Null(firstRoot);
        Assert.Null(secondRoot);
        Assert.Same(firstModelSource, secondModelSource);
    }

    /// <summary>Creates only the two intentionally isolated profiles without opening a native connection.</summary>
    /// <param name="pendingKey">Whether the context owns a dedicated canonical migration assembly.</param>
    /// <param name="version">The explicit native profile used in the cache boundary probe.</param>
    /// <returns>The context responsible for releasing its own provider root.</returns>
    private static DbContext CreateSpecializedContext(
        bool pendingKey,
        MySqlServerVersion version
    ) => pendingKey
        ? new ModelManagedPendingKeyDbContext(
            "Server=127.0.0.1;Port=1;Database=owned_profile;User ID=test",
            version)
        : new SafeMigrationDbContext(
            "Server=127.0.0.1;Port=1;Database=owned_profile;User ID=test",
            version,
            registerSafeMigrations: false);
}
