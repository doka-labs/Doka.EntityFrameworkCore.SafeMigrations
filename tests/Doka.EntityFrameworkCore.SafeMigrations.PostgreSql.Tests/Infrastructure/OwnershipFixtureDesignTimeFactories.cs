namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

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
        options.UseNpgsql(
            PostgreSqlDesignTimeContextConfiguration.ConnectionString(),
            provider => provider
                .MigrationsAssembly(typeof(CoreOwnershipDbContext).Assembly.FullName!)
                .MigrationsHistoryTable("__SafeMigrationsCoreHistory"));
        options.UsePostgreSqlSafeMigrations<CoreOwnershipDbContext>();

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
        options.UseNpgsql(
            PostgreSqlDesignTimeContextConfiguration.ConnectionString(),
            provider => provider
                .MigrationsAssembly(typeof(CustomOwnershipDbContext).Assembly.FullName!)
                .MigrationsHistoryTable("__SafeMigrationsCustomHistory"));
        options.UsePostgreSqlSafeMigrations<CustomOwnershipDbContext>(safeMigrations =>
            safeMigrations.ExcludeModelManagedDataForExcludedTables());

        return new CustomOwnershipDbContext(options.Options);
    }
}
