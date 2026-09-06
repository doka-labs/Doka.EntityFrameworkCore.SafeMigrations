namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Creates the Core ownership fixture for real EF tooling qualification.</summary>
public sealed class CoreOwnershipDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<CoreOwnershipDbContext>
{
    /// <inheritdoc />
    public CoreOwnershipDbContext CreateDbContext(
        string[] args
    )
    {
        var options = new DbContextOptionsBuilder<CoreOwnershipDbContext>();
        options.UseMySql(
            MySqlDesignTimeContextConfiguration.ConnectionString(),
            MySqlDesignTimeContextConfiguration.ServerVersion(),
            provider => provider
                .MigrationsAssembly(typeof(CoreOwnershipDbContext).Assembly.FullName!)
                .MigrationsHistoryTable("__SafeMigrationsCoreHistory"));
        options.UseMySqlSafeMigrations<CoreOwnershipDbContext>();

        return new CoreOwnershipDbContext(options.Options);
    }
}

/// <summary>Creates the custom ownership fixture for real EF tooling qualification.</summary>
public sealed class CustomOwnershipDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<CustomOwnershipDbContext>
{
    /// <inheritdoc />
    public CustomOwnershipDbContext CreateDbContext(
        string[] args
    )
    {
        var options = new DbContextOptionsBuilder<CustomOwnershipDbContext>();
        options.UseMySql(
            MySqlDesignTimeContextConfiguration.ConnectionString(),
            MySqlDesignTimeContextConfiguration.ServerVersion(),
            provider => provider
                .MigrationsAssembly(typeof(CustomOwnershipDbContext).Assembly.FullName!)
                .MigrationsHistoryTable("__SafeMigrationsCustomHistory"));
        options.UseMySqlSafeMigrations<CustomOwnershipDbContext>(safeMigrations =>
            safeMigrations.ExcludeModelManagedDataForExcludedTables());

        return new CustomOwnershipDbContext(options.Options);
    }
}
