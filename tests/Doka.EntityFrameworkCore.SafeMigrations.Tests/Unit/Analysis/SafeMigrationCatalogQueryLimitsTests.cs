namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>
/// Pins independent statement, batch, capture and native-packet bounds, including exact boundary rejection.
/// </summary>
public sealed class SafeMigrationCatalogQueryLimitsTests
{
    /// <summary>
    /// Pins separate operation, statement and capture bounds so one transport limit cannot silently replace
    /// another.
    /// </summary>
    [Fact]
    public void StatementBatchAndCaptureBounds_RemainIndependentAndNested()
    {
        // Arrange
        const int expectedStatementBound = 32;
        const int expectedBatchBound = 8;
        const int expectedCaptureBound = 512;

        // Act
        var statementBound = SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement;
        var batchBound = SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch;
        var captureBound = SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture;

        // Assert
        Assert.Equal(expectedStatementBound, statementBound);
        Assert.Equal(expectedBatchBound, batchBound);
        Assert.Equal(expectedCaptureBound, captureBound);
        Assert.InRange(statementBound * batchBound, 0, captureBound);
    }

    /// <summary>Accepts exact bounds and rejects each independently exceeded limit.</summary>
    /// <param name="extraParameters">The increment above the exact parameter bound.</param>
    /// <param name="extraPayload">The increment above the exact payload bound.</param>
    /// <param name="expected">Whether the supplied counts exceed their limits.</param>
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, true)]
    [InlineData(0, 1, true)]
    public void Exceeded_AcceptsExactBoundariesAndRejectsTheFirstExcessValue(
        int extraParameters,
        int extraPayload,
        bool expected
    )
    {
        // Arrange
        var parameters = SafeMigrationCatalogQueryLimits.MaximumParameters + extraParameters;
        var payload = SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes + extraPayload;
        var maximumPayload = SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes;

        // Act
        var exceeded = SafeMigrationCatalogQueryLimits.Exceeded(parameters, payload, maximumPayload);

        // Assert
        Assert.Equal(expected, exceeded);
    }

    /// <summary>Rejects invalid counters independently of the other valid limits.</summary>
    /// <param name="parameters">The supplied parameter count.</param>
    /// <param name="payload">The supplied payload size.</param>
    /// <param name="maximumPayload">The configured payload bound.</param>
    [Theory]
    [InlineData(-1, 0, 1)]
    [InlineData(0, -1, 1)]
    [InlineData(0, 0, 0)]
    public void Exceeded_RejectsInvalidCounters(
        int parameters,
        int payload,
        int maximumPayload
    )
    {
        // Arrange: The theory supplies the counter tuple.

        // Act
        var exception = Record.Exception(() =>
            SafeMigrationCatalogQueryLimits.Exceeded(parameters, payload, maximumPayload));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    /// <summary>Uses half the native packet limit without exceeding the global payload cap.</summary>
    /// <param name="maximumPacket">The native packet bound.</param>
    /// <param name="expected">The independently declared expected payload bound.</param>
    [Theory]
    [InlineData(2_097_152, 1_048_576)]
    [InlineData(8_388_608, 4_194_304)]
    [InlineData(long.MaxValue, 4_194_304)]
    public void MySqlMaximumUtf8PayloadBytes_UsesHalfPacketWithTheGlobalCap(
        long maximumPacket,
        int expected
    )
    {
        // Arrange: The theory supplies the native packet size and expected bound.

        // Act
        var actual = SafeMigrationCatalogQueryLimits.MySqlMaximumUtf8PayloadBytes(maximumPacket);

        // Assert
        Assert.Equal(expected, actual);
    }

    /// <summary>Rejects packet limits that cannot reserve a positive half-packet payload.</summary>
    /// <param name="maximumPacket">The invalid native packet bound.</param>
    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void MySqlMaximumUtf8PayloadBytes_RejectsInvalidPacketLimits(
        long maximumPacket
    )
    {
        // Arrange: The theory supplies the invalid packet size.

        // Act
        var exception = Record.Exception(() =>
            SafeMigrationCatalogQueryLimits.MySqlMaximumUtf8PayloadBytes(maximumPacket));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }
}
