namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Proves ownership of provider handles whose configuration fails before registration.</summary>
public sealed class SafeMigrationCatalogAcquisitionOwnershipTests
{
    /// <summary>
    /// Constructor failure releases the unpublished native batch and preserves configuration failure.
    /// </summary>
    /// <param name="setter">The failing virtual provider setter.</param>
    /// <param name="failDisposal">Whether synchronous cleanup also throws.</param>
    [Theory]
    [InlineData("transaction", false)]
    [InlineData("timeout", false)]
    [InlineData("transaction", true)]
    [InlineData("timeout", true)]
    public void NativeConfigurationFailureReleasesUnpublishedBatch(
        string setter,
        bool failDisposal
    )
    {
        // Arrange
        using var connection = new CatalogAcquisitionTestConnection(native: true)
        {
            FailingSetter = setter,
            FailDisposal = failDisposal,
        };

        using var transaction = connection.BeginTransaction();

        // Act
        var failure = Record.Exception(() => new SafeMigrationCatalogBatch(connection, 17, transaction));

        // Assert
        var configurationFailure = Assert.IsType<InvalidOperationException>(failure);
        Assert.Same(connection.ConfigurationFailure, configurationFailure);
        Assert.Equal(1, connection.HandleDisposals);
        Assert.Same(failDisposal
                ? connection.DisposalFailure
                : null,
            configurationFailure.Data["SafeMigrations.CatalogBatchDisposeException"]);
    }

    /// <summary>A rejected command never enters the sequential collection and is disposed exactly once.</summary>
    /// <param name="setter">The failing virtual provider setter.</param>
    /// <param name="failDisposal">Whether synchronous cleanup also throws.</param>
    [Theory]
    [InlineData("transaction", false)]
    [InlineData("timeout", false)]
    [InlineData("transaction", true)]
    [InlineData("timeout", true)]
    public async Task SequentialConfigurationFailureDoesNotRetainCommand(
        string setter,
        bool failDisposal
    )
    {
        // Arrange
        await using var connection = new CatalogAcquisitionTestConnection(native: false)
        {
            FailingSetter = setter,
            FailDisposal = failDisposal,
        };

        // WHY: This in-memory transaction probe tests synchronous acquisition, not database I/O.
        // ReSharper disable once MethodHasAsyncOverload
        await using var transaction = connection.BeginTransaction();
        var batch = new SafeMigrationCatalogBatch(connection, 17, transaction);

        // Act
        var failure = Record.Exception(() => batch.CreateCommand());
        await batch.DisposeAsync();

        // Assert
        var configurationFailure = Assert.IsType<InvalidOperationException>(failure);
        Assert.Same(connection.ConfigurationFailure, configurationFailure);
        Assert.Equal(0, batch.Count);
        Assert.Equal(1, connection.HandleDisposals);
        Assert.Same(failDisposal
                ? connection.DisposalFailure
                : null,
            configurationFailure.Data["SafeMigrations.CatalogCommandDisposeException"]);
    }

    /// <summary>Successful configuration transfers cleanup to the normal owning transport.</summary>
    /// <param name="native">Whether configuration acquires a native batch or sequential command.</param>
    /// <param name="failDisposal">Whether the later owned cleanup fails.</param>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task SuccessfulAcquisitionRetainsNormalCleanup(
        bool native,
        bool failDisposal
    )
    {
        // Arrange
        await using var connection = new CatalogAcquisitionTestConnection(native) { FailDisposal = failDisposal };

        // WHY: This in-memory transaction probe tests synchronous acquisition, not database I/O.
        // ReSharper disable once MethodHasAsyncOverload
        await using var transaction = connection.BeginTransaction();

        // Act
        var batch = new SafeMigrationCatalogBatch(connection, 17, transaction);
        if (!native)
        {
            batch.CreateCommand();
        }

        var disposalsBeforeCleanup = connection.HandleDisposals;
        var failure = await Record.ExceptionAsync(() => batch
            .DisposeAsync()
            .AsTask());

        await batch.DisposeAsync();

        // Assert
        Assert.Equal(0, disposalsBeforeCleanup);
        Assert.Equal(1, connection.HandleDisposals);
        Assert.Same(failDisposal
                ? connection.DisposalFailure
                : null,
            failure);
    }
}
