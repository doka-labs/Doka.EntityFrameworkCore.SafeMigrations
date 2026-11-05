// WHY: Independent collections overlap over one assembly-owned server while each test owns its database.

[assembly: CollectionBehavior(CollectionBehavior.CollectionPerClass, MaxParallelThreads = 4)]
[assembly: TestFramework(
    "Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests.SqlServerTestFramework",
    "Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests")]

namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Shares one SQL Server fixture across selected executable live classes.</summary>
public sealed class SqlServerTestFramework : SharedContainerTestFramework<SqlServerContainerFixture>
{
    /// <summary>Creates the framework with xUnit's diagnostic message sink.</summary>
    /// <param name="messageSink">The diagnostic sink supplied by the xUnit runner.</param>
    public SqlServerTestFramework(
        IMessageSink messageSink
    ) : base(messageSink) { }
}
