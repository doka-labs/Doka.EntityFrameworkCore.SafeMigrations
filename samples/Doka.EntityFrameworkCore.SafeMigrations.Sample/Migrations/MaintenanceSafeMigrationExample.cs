namespace Doka.EntityFrameworkCore.SafeMigrations.Sample.Migrations;

internal sealed class MaintenanceSafeMigrationExample : Migration
{
    protected override void Up(
        MigrationBuilder migrationBuilder
    ) => SampleMigrationUsage.BuildPostgreSqlMaintenanceOperations(migrationBuilder);

    protected override void Down(
        MigrationBuilder migrationBuilder
    ) => throw new NotSupportedException(
        "This sample requires an explicit forward-fix migration instead of automatic rollback.");
}
