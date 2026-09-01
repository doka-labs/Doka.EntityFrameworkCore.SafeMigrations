namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

[DbContext(typeof(SafeMigrationDbContext))]
[Migration(MigrationIdentifier)]
public sealed class CoreConvergenceMigration : Migration
{
    public const string MigrationIdentifier = "202608170001_CoreConvergence";

    protected override void Up(
        MigrationBuilder migrationBuilder
    )
    {
        migrationBuilder.CreateTable(
            "pipeline_probe",
            table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_pipeline_probe", value => value.Id));

        migrationBuilder.CreateTableIfNotExists(
            "pipeline_state",
            table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_pipeline_state", value => value.Id),
            policy: SafeMigrationPolicy.ExistenceOnly,
            mode: SafeMigrationTableMode.ConvergenceContainer);

        migrationBuilder.AddColumnIfNotExists<string>(
            "payload",
            "pipeline_state",
            type: "varchar(80)",
            maxLength: 80,
            nullable: false,
            policy: SafeMigrationPolicy.RepairIfSafe);
    }

    protected override void Down(
        MigrationBuilder migrationBuilder
    ) => throw new NotSupportedException("The convergence baseline is forward-only and has no destructive Down path.");
}
