namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Shares one SQL Server container across every live contract test class.
/// </summary>
/// <remarks>
/// WHY: A class fixture starts one container per test class, and xUnit runs classes as
/// separate collections in parallel. Several SQL Server instances then compete for the
/// same runner memory and CPU, which dominates engine job time. One collection fixture
/// keeps a single instance for the assembly and serializes the live tests that share it.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class SqlServerSharedContainer : ICollectionFixture<SqlServerContainerFixture>
{
    /// <summary>The xUnit collection name that owns the shared SQL Server container.</summary>
    public const string Name = "SqlServer live contracts";
}
