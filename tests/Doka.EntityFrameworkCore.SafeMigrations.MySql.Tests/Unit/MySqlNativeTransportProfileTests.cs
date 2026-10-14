namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>
/// Guards source-bound native table identity without guessing from an engine family or authored spelling.
/// </summary>
public sealed class MySqlNativeTransportProfileTests
{
    /// <summary>The same required transport probe preserves all three native table-name modes exactly.</summary>
    /// <param name="mode">The independently returned initialization-time native owner mode.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NativeTransportProfilePreservesExactQualifiedMode(int mode)
    {
        // Arrange
        using var facts = Facts();
        facts.Rows.Add(64L * 1024 * 1024, mode);
        await using var reader = facts.CreateDataReader();

        // Act
        var profile = await MySqlSafeMigrationProviderAnalyzer.ReadNativeTransportProfileAsync(
            reader, CancellationToken.None);

        // Assert
        Assert.Equal(64L * 1024 * 1024, profile.MaximumPacket);
        Assert.Equal(mode, profile.OwnerMode);
    }

    /// <summary>Truncation, aliases, nonintegral values or unsupported modes cannot grant table equality.</summary>
    /// <param name="shape">The malformed native result being rejected.</param>
    [Theory]
    [InlineData("empty")]
    [InlineData("truncated")]
    [InlineData("duplicate")]
    [InlineData("mode_negative")]
    [InlineData("mode_large")]
    [InlineData("mode_text")]
    [InlineData("mode_fractional")]
    [InlineData("mode_null")]
    [InlineData("packet_zero")]
    [InlineData("packet_wide")]
    [InlineData("packet_text")]
    public async Task NativeTransportProfileRejectsMalformedFacts(string shape)
    {
        // Arrange
        using var facts = Facts();
        switch (shape)
        {
            case "empty":
                break;
            case "truncated":
                facts.Columns.RemoveAt(1);
                break;
            case "duplicate":
                facts.Rows.Add(64L * 1024 * 1024, 0);
                facts.Rows.Add(64L * 1024 * 1024, 1);
                break;
            case "mode_negative":
                facts.Rows.Add(64L * 1024 * 1024, -1);
                break;
            case "mode_large":
                facts.Rows.Add(64L * 1024 * 1024, 3);
                break;
            case "mode_text":
                facts.Rows.Add(64L * 1024 * 1024, "1");
                break;
            case "mode_fractional":
                facts.Rows.Add(64L * 1024 * 1024, 0.5m);
                break;
            case "mode_null":
                facts.Rows.Add(64L * 1024 * 1024, DBNull.Value);
                break;
            case "packet_zero":
                facts.Rows.Add(0L, 0);
                break;
            case "packet_wide":
                facts.Rows.Add(ulong.MaxValue, 0);
                break;
            case "packet_text":
                facts.Rows.Add("67108864", 0);
                break;
            default:
                throw new UnreachableException();
        }

        await using var reader = facts.CreateDataReader();

        // Act
        var failure = await Record.ExceptionAsync(() =>
            MySqlSafeMigrationProviderAnalyzer.ReadNativeTransportProfileAsync(reader, CancellationToken.None));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
    }

    /// <summary>Creates the exact result shape while permitting adversarial raw scalar types.</summary>
    /// <returns>An owned two-field native profile fixture.</returns>
    private static DataTable Facts()
    {
        var table = new DataTable { Locale = CultureInfo.InvariantCulture };
        table.Columns.Add("packet", typeof(object));
        table.Columns.Add("owner_mode", typeof(object));

        return table;
    }
}
