namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Creates the canonical isolated context for the EF migration tooling contract.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SafeMigrationDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SafeMigrationDbContext>
{
    /// <inheritdoc />
    public SafeMigrationDbContext CreateDbContext(
        string[] args
    )
    {
        ArgumentNullException.ThrowIfNull(args);

        return new SafeMigrationDbContext(
            MySqlDesignTimeContextConfiguration.ConnectionString(),
            MySqlDesignTimeContextConfiguration.ServerVersion());
    }
}
