namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies the closed SQL-literal boundary for operation-local required-add catalog facts.</summary>
public sealed class MySqlRequiredColumnAddFactTests
{
    /// <summary>Every supported status binds to a fixed quoted constant rather than server-provided SQL.</summary>
    /// <param name="status">The exact catalog status.</param>
    /// <param name="expected">The provider-owned SQL literal.</param>
    [Theory]
    [InlineData("complete", "'complete'")]
    [InlineData("fresh", "'fresh'")]
    [InlineData("staged", "'staged'")]
    [InlineData("conflict", "'conflict'")]
    [InlineData("unavailable", "'unavailable'")]
    [InlineData("set_null", "'set_null'")]
    [InlineData("ordinary", "'ordinary'")]
    public void KnownStatusBindsFixedLiteral(string status, string expected)
    {
        // Arrange
        var catalogValue = new string(status.AsSpan());

        // Act
        var binding = MySqlSafeMigrationProviderAnalyzer.RequiredColumnAddStatusBinding(catalogValue);

        // Assert
        Assert.Equal(expected, binding);
        Assert.NotSame(catalogValue, binding);
    }

    /// <summary>Unknown, NULL, case-different and SQL-shaped results cannot enter generated expressions.</summary>
    /// <param name="status">The untrusted catalog result.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("COMPLETE")]
    [InlineData(" complete")]
    [InlineData("staged ")]
    [InlineData("unknown")]
    [InlineData("'complete' OR TRUE")]
    public void InvalidStatusIsRejected(string? status)
    {
        // Arrange
        var catalogValue = status;

        // Act
        var exception = Record.Exception(
            () => MySqlSafeMigrationProviderAnalyzer.RequiredColumnAddStatusBinding(catalogValue));

        // Assert
        var invalid = Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("The MySQL required-add facts query returned an unknown status.", invalid.Message);
    }
}
