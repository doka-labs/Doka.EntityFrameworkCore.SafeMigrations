using System;
using Doka.EntityFrameworkCore.MySql;
using Doka.EntityFrameworkCore.SafeMigrations.MySql.SplitTarget;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.SplitGenerator;

/// <summary>
/// Creates the split migration-owning context for EF Core design-time commands.
/// </summary>
public sealed class SplitPackageDbContextDesignFactory
    : IDesignTimeDbContextFactory<SplitPackageDbContext>
{
    /// <inheritdoc />
    public SplitPackageDbContext CreateDbContext(
        string[] args
    )
    {
        ArgumentNullException.ThrowIfNull(args);

        var optionsBuilder = new DbContextOptionsBuilder<SplitPackageDbContext>();

        optionsBuilder.UseMySql(
            "Server=127.0.0.1;User ID=package;Password=package;Database=package",
            MySqlServerVersion.MariaDb(new Version(11, 8, 0)));
        optionsBuilder.UseMySqlSafeMigrations(options => options
            .UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence)
            .UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe));

        return new SplitPackageDbContext(optionsBuilder.Options);
    }
}
