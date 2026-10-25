namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Compares prepared guards with independent interpolation-based rendering and allocation work.</summary>
public sealed partial class SqlServerGuardRenderingAllocationTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=guard_rendering;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    private static readonly SafeMigrationObservedState[] s_states = Enum.GetValues<SafeMigrationObservedState>();
}
