namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Defines the isolated forward-only core convergence and migration-history fixture.</summary>
[DbContext(typeof(SafeMigrationDbContext))]
[Migration(MigrationIdentifier)]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class CoreConvergenceMigration : Migration
{
    /// <summary>Identifies the exact forward-only convergence migration in native history assertions.</summary>
    public const string MigrationIdentifier = "20260817000100_CoreConvergence";

    /// <inheritdoc />
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

    /// <inheritdoc />
    protected override void Down(
        MigrationBuilder migrationBuilder
    ) => throw new NotSupportedException("The convergence baseline is forward-only and has no destructive Down path.");
}
