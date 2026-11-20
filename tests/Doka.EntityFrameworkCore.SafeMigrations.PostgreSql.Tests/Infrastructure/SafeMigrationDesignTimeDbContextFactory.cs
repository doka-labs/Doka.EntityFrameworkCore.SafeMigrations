namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>
/// Verifies SafeMigrations Design Time Db Context Factory contracts in independently owned test state.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SafeMigrationDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SafeMigrationDbContext>
{
    /// <summary>Create Db Context.</summary>
    public SafeMigrationDbContext CreateDbContext(
        string[] args
    )
    {
        ArgumentNullException.ThrowIfNull(args);

        return new SafeMigrationDbContext(PostgreSqlDesignTimeContextConfiguration.ConnectionString());
    }
}
