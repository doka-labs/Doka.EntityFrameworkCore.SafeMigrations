namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Exercises EF migration transactions, history recording, and safe-operation replay.
/// </summary>
[DbContext(typeof(SafeMigrationDbContext))]
[Migration(MigrationIdentifier)]
public sealed class SqlServerHistoryMigration : Migration
{
    /// <summary>Gets the stable migration identifier used in history assertions.</summary>
    public const string MigrationIdentifier = "20260930000100_SqlServerHistory";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            "ordinary_pipeline_probe",
            table => new { Id = table.Column<int>(type: "int", nullable: false) });
        migrationBuilder.CreateTableIfNotExists(
            "safe_history_probe",
            table => new { Id = table.Column<int>(type: "int", nullable: false) },
            constraints: table => table.PrimaryKey("PK_safe_history_probe", row => row.Id));
        migrationBuilder.AddColumnIfNotExists<string>(
            "Caption",
            "safe_history_probe",
            type: "nvarchar(80)",
            maxLength: 80,
            nullable: false,
            defaultValue: "ready");
        migrationBuilder.AddCheckConstraintIfNotExists(
            "CK_safe_history_probe_Id",
            "safe_history_probe",
            "[Id] >= 0");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTableIfExists("safe_history_probe");
        migrationBuilder.DropTable("ordinary_pipeline_probe");
    }
}
