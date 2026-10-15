namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs selected live PostgreSQL classes with one assembly-owned container.</summary>
public sealed class PostgreSqlTestFramework : SharedContainerTestFramework<PostgreSqlContainerFixture>
{
    /// <summary>Creates the test framework used by xUnit for this assembly.</summary>
    /// <param name="messageSink">The xUnit diagnostic message sink.</param>
    public PostgreSqlTestFramework(
        Xunit.Abstractions.IMessageSink messageSink
    ) : base(messageSink) { }
}
