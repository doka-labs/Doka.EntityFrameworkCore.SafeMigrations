namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed class SqliteSafeMigrationLimitsTests
{
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
        var supported = SqliteSafeMigrationLimits.IsSupportedVersion(value);

        Assert.Equal(expected, supported);
    }
}
