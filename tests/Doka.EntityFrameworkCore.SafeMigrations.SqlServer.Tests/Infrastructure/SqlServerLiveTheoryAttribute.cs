namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Runs parameterized SQL Server container tests only on a supported x86-64 host.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SqlServerLiveTheoryAttribute : TheoryAttribute
{
    /// <summary>
    /// Skips unsupported local architectures while leaving x86-64 CI as a required live gate.
    /// </summary>
    public SqlServerLiveTheoryAttribute()
    {
        // WHY: Microsoft does not support SQL Server Linux containers through ARM emulation.
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            Skip = "SQL Server Linux containers require a supported x86-64 host.";
        }
    }
}
