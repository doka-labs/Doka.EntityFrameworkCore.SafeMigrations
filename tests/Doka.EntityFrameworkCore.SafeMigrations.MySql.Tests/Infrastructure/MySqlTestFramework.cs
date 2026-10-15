namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Shares one lazily activated MySQL engine across independent live test-class collections.</summary>
public sealed class MySqlTestFramework : SharedContainerTestFramework<MySqlEngineContainerFixture>
{
    /// <summary>Creates the native fixture adapter for the current test assembly execution.</summary>
    /// <param name="messageSink">The xUnit diagnostic message sink.</param>
    public MySqlTestFramework(Xunit.Abstractions.IMessageSink messageSink) : base(messageSink) { }
}
