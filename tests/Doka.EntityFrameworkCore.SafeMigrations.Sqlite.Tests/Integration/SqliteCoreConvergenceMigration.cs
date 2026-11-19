namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>
/// Provides the SQLite core convergence migration fixture contract.
/// </summary>
[DbContext(typeof(SqliteSafeMigrationTestContext))]
[Migration(MigrationIdentifier)]
// WHY: EF migration discovery constructs this fixture from its attributed migration identity.
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SqliteCoreConvergenceMigration : Migration
{
    /// <summary>
    /// Identifies this fixture migration for EF discovery and migration-history assertions.
    /// </summary>
    public const string MigrationIdentifier = "20260918000100_SqliteCoreConvergence";

    /// <inheritdoc />
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

    /// <inheritdoc />
    protected override void Down(
        MigrationBuilder migrationBuilder
    ) => throw new NotSupportedException(
        "The SQLite convergence baseline is forward-only and has no destructive Down path.");
}
