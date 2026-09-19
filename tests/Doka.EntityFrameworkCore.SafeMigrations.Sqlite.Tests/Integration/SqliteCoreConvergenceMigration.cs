namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

[DbContext(typeof(SqliteSafeMigrationTestContext))]
[Migration(MigrationIdentifier)]
public sealed class SqliteCoreConvergenceMigration : Migration
{
    public const string MigrationIdentifier = "202609180001_SqliteCoreConvergence";

    protected override void Up(
        MigrationBuilder migrationBuilder
    )
    {
        migrationBuilder.CreateTable(
            "pipeline_probe",
            table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_pipeline_probe", value => value.Id));
        migrationBuilder.CreateTableIfNotExists(
            "pipeline_state",
            table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_pipeline_state", value => value.Id),
            policy: SafeMigrationPolicy.ExistenceOnly,
            mode: SafeMigrationTableMode.ConvergenceContainer);
        migrationBuilder.AddColumnIfNotExists<string>(
            "payload",
            "pipeline_state",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(
        MigrationBuilder migrationBuilder
    ) => throw new NotSupportedException(
        "The SQLite convergence baseline is forward-only and has no destructive Down path.");
}
