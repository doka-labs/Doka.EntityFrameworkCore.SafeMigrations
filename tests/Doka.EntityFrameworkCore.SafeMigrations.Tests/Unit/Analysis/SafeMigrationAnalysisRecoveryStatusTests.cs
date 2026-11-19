namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Protects best-effort scalar recovery evidence independently from operational cleanup.</summary>
public sealed class SafeMigrationAnalysisRecoveryStatusTests
{
    private const string StatusKey = "test.recovery-status";

    /// <summary>Ordinary diagnostic storage keeps unrelated evidence and the actual final recovery state.</summary>
    [Fact]
    public void RecoveryStatusRetainsExistingEvidence()
    {
        // Arrange
        var original = new InvalidOperationException("Original failure.")
        {
            Data =
            {
                ["existing"] = "retained",
                [StatusKey] = "session_quarantined",
            },
        };

        // Act
        SafeMigrationAnalysisCleanup.RetainRecoveryStatus(original, StatusKey, "session_closed");

        // Assert
        Assert.Equal("retained", original.Data["existing"]);
        Assert.Equal("session_closed", original.Data[StatusKey]);
    }

    /// <summary>Successful attachment does not change the identity or token of an original cancellation.</summary>
    [Fact]
    public void RecoveryStatusPreservesOriginalCancellation()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var original = new OperationCanceledException(cancellationSource.Token);

        // Act
        var observed = CapturePrimaryWithStatus(original);

        // Assert
        Assert.Same(original, observed);
        Assert.Equal(cancellationSource.Token,
            Assert.IsType<OperationCanceledException>(observed)
                .CancellationToken);

        Assert.Equal("session_closed", original.Data[StatusKey]);
    }

    /// <summary>Neither a hostile getter nor a read-only dictionary may replace the primary work failure.</summary>
    /// <param name="readOnly">Whether Data returns a read-only dictionary instead of throwing on access.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableStatusStoragePreservesOriginalFailure(
        bool readOnly
    )
    {
        // Arrange
        var original = new HostileDataException(readOnly);

        // Act
        var observed = CapturePrimaryWithStatus(original);

        // Assert
        Assert.Same(original, observed);
    }

    /// <summary>Exercises status attachment in the same exception-unwinding boundary as operational recovery.</summary>
    private static Exception? CapturePrimaryWithStatus(
        Exception original
    ) => Record.Exception((Action)(() =>
    {
        try
        {
            throw original;
        }
        finally
        {
            SafeMigrationAnalysisCleanup.RetainRecoveryStatus(original, StatusKey, "session_closed");
        }
    }));

    /// <summary>Rejects diagnostic storage without changing the separately retained primary identity.</summary>
    private sealed class HostileDataException : Exception
    {
        private readonly IDictionary _data = new Dictionary<string, object?>().AsReadOnly();
        private readonly bool _readOnly;

        /// <summary>Selects a hostile getter or read-only dictionary for the exact exception instance.</summary>
        internal HostileDataException(
            bool readOnly
        )
        {
            _readOnly = readOnly;
        }

        /// <inheritdoc />
        public override IDictionary Data => _readOnly
            ? _data
            : throw new InvalidOperationException("Diagnostic storage unavailable.");
    }
}
