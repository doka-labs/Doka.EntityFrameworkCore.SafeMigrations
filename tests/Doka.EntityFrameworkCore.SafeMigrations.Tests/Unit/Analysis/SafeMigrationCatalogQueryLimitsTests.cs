namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationCatalogQueryLimitsTests
{
    [Fact]
    public void StatementBatchAndCaptureBounds_RemainIndependentAndNested()
    {
        Assert.Equal(32, SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement);
        Assert.Equal(8, SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch);
        Assert.Equal(512, SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture);
        Assert.True(
            SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement
            * SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch
            <= SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture);
    }

    [Fact]
    public void Exceeded_AcceptsExactBoundariesAndRejectsTheFirstExcessValue()
    {
        Assert.False(
            SafeMigrationCatalogQueryLimits.Exceeded(
                SafeMigrationCatalogQueryLimits.MaximumParameters,
                SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes,
                SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes));
        Assert.True(
            SafeMigrationCatalogQueryLimits.Exceeded(
                SafeMigrationCatalogQueryLimits.MaximumParameters + 1,
                SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes,
                SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes));
        Assert.True(
            SafeMigrationCatalogQueryLimits.Exceeded(
                SafeMigrationCatalogQueryLimits.MaximumParameters,
                SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes + 1,
                SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes));
    }

    [Theory]
    [InlineData(-1, 0, 1)]
    [InlineData(0, -1, 1)]
    [InlineData(0, 0, 0)]
    public void Exceeded_RejectsInvalidCounters(
        int parameters,
        int payload,
        int maximumPayload
    ) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        SafeMigrationCatalogQueryLimits.Exceeded(parameters, payload, maximumPayload));

    [Theory]
    [InlineData(2_097_152, 1_048_576)]
    [InlineData(8_388_608, 4_194_304)]
    [InlineData(long.MaxValue, 4_194_304)]
    public void MySqlMaximumUtf8PayloadBytes_UsesHalfPacketWithTheGlobalCap(
        long maximumPacket,
        int expected
    ) => Assert.Equal(expected, SafeMigrationCatalogQueryLimits.MySqlMaximumUtf8PayloadBytes(maximumPacket));

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void MySqlMaximumUtf8PayloadBytes_RejectsInvalidPacketLimits(
        long maximumPacket
    ) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        SafeMigrationCatalogQueryLimits.MySqlMaximumUtf8PayloadBytes(maximumPacket));
}
