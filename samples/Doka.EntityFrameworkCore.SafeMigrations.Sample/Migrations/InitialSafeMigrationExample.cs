namespace Doka.EntityFrameworkCore.SafeMigrations.Sample.Migrations;

internal sealed class InitialSafeMigrationExample : Migration
{
    protected override void Up(
        MigrationBuilder migrationBuilder
    ) => SampleMigrationUsage.BuildUpOperations(migrationBuilder);

    protected override void Down(
        MigrationBuilder migrationBuilder
    ) => SampleMigrationUsage.BuildDownOperations(migrationBuilder);
}
