namespace Doka.EntityFrameworkCore.SafeMigrations.Sample;

/// <summary>Runs the source-construction sample without opening a database connection.</summary>
internal static class Program
{
    /// <summary>Checks adapter registration touchpoints and prints the constructed migration scenario.</summary>
    private static void Main()
    {
        _ = new DbContextOptionsBuilder().UseMySqlSafeMigrations(options =>
            options.UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence));

        _ = new DbContextOptionsBuilder().UsePostgreSqlSafeMigrations(options =>
            options.UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence));

        _ = new DbContextOptionsBuilder().UseSqliteSafeMigrations(options =>
            options.UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence));

        _ = new DbContextOptionsBuilder().UseSqlServerSafeMigrations(options =>
            options.UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence));

        _ = SampleMigrationUsage.Run(Console.Out);
    }
}
