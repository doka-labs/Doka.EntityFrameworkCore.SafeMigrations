namespace Doka.EntityFrameworkCore.SafeMigrations.Sample;

internal static class Program
{
    private static void Main()
    {
        _ = new DbContextOptionsBuilder().UseMariaDbSafeMigrations();
        _ = new DbContextOptionsBuilder().UsePostgreSqlSafeMigrations();

        var initialMigrationBuilder = new MigrationBuilder("Doka.Sample.Provider");
        SampleMigrationUsage.BuildUpOperations(initialMigrationBuilder);

        var maintenanceMigrationBuilder = new MigrationBuilder("Doka.Sample.Provider");
        SampleMigrationUsage.BuildMaintenanceOperations(maintenanceMigrationBuilder);

        var legacyStrictModeBuilder = new MigrationBuilder("Doka.Sample.Provider");
        SampleMigrationUsage.BuildLegacyStrictModeExamples(legacyStrictModeBuilder);

        Console.WriteLine("Doka.EntityFrameworkCore.SafeMigrations sample");
        Console.WriteLine("Generated sample migration operations without executing SQL.");
        Console.WriteLine($"Initial-migration example operation count: {initialMigrationBuilder.Operations.Count}");
        Console.WriteLine($"Maintenance example operation count: {maintenanceMigrationBuilder.Operations.Count}");
        Console.WriteLine($"Legacy strict-mode example operation count: {legacyStrictModeBuilder.Operations.Count}");
        Console.WriteLine(
            "See SampleMigrationUsage, InitialSafeMigrationExample, and MaintenanceSafeMigrationExample " +
            "for the recommended migration patterns.");
    }
}
