namespace Doka.EntityFrameworkCore.SafeMigrations.Sample.Migrations;

internal sealed class MaintenanceSafeMigrationExample : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => SampleMigrationUsage.BuildMaintenanceOperations(migrationBuilder);

    protected override void Down(MigrationBuilder migrationBuilder)
        => SampleMigrationUsage.BuildMaintenanceRollbackOperations(migrationBuilder);
}
