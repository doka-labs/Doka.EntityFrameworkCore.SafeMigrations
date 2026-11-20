namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>
/// Provides isolated SQLite coverage for safe migration limits.
/// </summary>
public sealed class SqliteSafeMigrationLimitsTests
{
    /// <summary>
    /// SQLite version validation enforces the official EF Core engine floor.
    /// </summary>
    /// <param name="value">The value defining this isolated fixture case.</param>
    /// <param name="expected">The exact expected result for this independent case.</param>
    [Theory]
    [InlineData("3.46.0", false)]
    [InlineData("3.46.1", true)]
    [InlineData("3.47.0", true)]
    [InlineData("4.0.0", true)]
    [InlineData("invalid", false)]
    public void IsSupportedVersion_EnforcesOfficialEfCoreEngineFloor(
        string value,
        bool expected
    )
    {
        // Arrange: The fixed or theory-supplied SQL/value is the complete input.

        // Act
        var supported = SqliteSafeMigrationLimits.IsSupportedVersion(value);

        // Assert
        Assert.Equal(expected, supported);
    }
}
