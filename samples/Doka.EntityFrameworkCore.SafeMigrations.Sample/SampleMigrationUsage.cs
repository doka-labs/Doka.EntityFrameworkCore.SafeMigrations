namespace Doka.EntityFrameworkCore.SafeMigrations.Sample;

internal static class SampleMigrationUsage
{
    public static void BuildUpOperations(
        MigrationBuilder migrationBuilder
    )
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        // This path models a consolidated initial migration that must be safe to rerun
        // against an existing database with partial schema already present.
        migrationBuilder.CreateTableIfNotExists(
            "users",
            table => new
            {
                id = table.Column<Guid>(nullable: false),
                email = table.Column<string>(type: "varchar(320)", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_users", x => x.id));

        migrationBuilder.CreateTableIfNotExists(
            "orders",
            table => new
            {
                id = table.Column<Guid>(nullable: false),
                user_id = table.Column<Guid>(nullable: false),
                total = table.Column<decimal>(type: "decimal(18,2)", nullable: false)
            },
            constraints: table => table.PrimaryKey("pk_orders", x => x.id));

        migrationBuilder.AddColumnIfNotExists<string>(
            name: "display_name",
            table: "users",
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            maxLength: 200,
            nullable: true);

        migrationBuilder.CreateIndexIfNotExists(
            name: "ix_orders_user_id",
            table: "orders",
            columns: ["user_id"],
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        migrationBuilder.AddUniqueConstraintIfNotExists(
            name: "ux_users_email",
            table: "users",
            columns: ["email"],
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        migrationBuilder.AddCheckConstraintIfNotExists(
            name: "ck_orders_total_non_negative",
            table: "orders",
            sql: "total >= 0",
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.ThrowIfDifferent,
                PreflightOnly: true));

        migrationBuilder.AddForeignKeyIfNotExists(
            name: "fk_orders_users_user_id",
            table: "orders",
            columns: ["user_id"],
            principalTable: "users",
            principalColumns: ["id"],
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));
    }

    public static void BuildMaintenanceOperations(
        MigrationBuilder migrationBuilder
    )
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        // Follow-up maintenance migrations can mix rename and alter operations with the
        // same safe/idempotent semantics as the initial synchronization path.
        migrationBuilder.EnsureSchemaExists("reporting");

        migrationBuilder.RenameTableIfExists(name: "legacy_users", newName: "users");

        migrationBuilder.RenameColumnIfExists(name: "FullName", table: "users", newName: "DisplayName");

        migrationBuilder.RenameIndexIfExists(
            name: "ix_legacy_orders_customer_id",
            newName: "ix_orders_user_id",
            table: "orders");

        migrationBuilder.AlterColumnIfDifferent<string>(
            name: "DisplayName",
            table: "users",
            type: "varchar(200)",
            nullable: true,
            oldClrType: typeof(string),
            oldType: "varchar(100)",
            oldNullable: true);

        migrationBuilder.AddPrimaryKeyIfNotExists(
            name: "pk_daily_snapshots",
            table: "daily_snapshots",
            columns: ["id"],
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            schema: "reporting");

        migrationBuilder.DropPrimaryKeyIfExists(table: "legacy_import_jobs", name: "pk_legacy_import_jobs");

        migrationBuilder.DropSchemaIfExists("legacy_reporting");
    }

    public static void BuildMaintenanceRollbackOperations(
        MigrationBuilder migrationBuilder
    )
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        // A follow-up migration can also keep its rollback path explicit, rather than
        // falling back to the initial-migration teardown semantics.
        migrationBuilder.EnsureSchemaExists("legacy_reporting");

        migrationBuilder.AddPrimaryKeyIfNotExists(
            name: "pk_legacy_import_jobs",
            table: "legacy_import_jobs",
            columns: ["id"],
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        migrationBuilder.DropPrimaryKeyIfExists(
            table: "daily_snapshots",
            name: "pk_daily_snapshots",
            schema: "reporting");

        migrationBuilder.AlterColumnIfDifferent<string>(
            name: "DisplayName",
            table: "users",
            type: "varchar(100)",
            nullable: true,
            oldClrType: typeof(string),
            oldType: "varchar(200)",
            oldNullable: true);

        migrationBuilder.RenameIndexIfExists(
            name: "ix_orders_user_id",
            newName: "ix_legacy_orders_customer_id",
            table: "orders");

        migrationBuilder.RenameColumnIfExists(name: "DisplayName", table: "users", newName: "FullName");

        migrationBuilder.RenameTableIfExists(name: "users", newName: "legacy_users");

        // Intentionally not dropping the 'reporting' schema here — dropping a schema in a rollback is destructive if it contains other objects.
    }

    public static void BuildLegacyStrictModeExamples(
        MigrationBuilder migrationBuilder
    )
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        // The legacy strict-mode overloads remain supported for consumers who only need
        // classic idempotent execution plus "throw if different" validation.
        migrationBuilder.AddColumnIfNotExists<string>(
            name: "ExternalReference",
            table: "users",
            type: "varchar(64)",
            nullable: true,
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        migrationBuilder.CreateIndexIfNotExists(
            name: "ix_users_external_reference",
            table: "users",
            columns: ["ExternalReference"],
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);
    }

    public static void BuildDownOperations(
        MigrationBuilder migrationBuilder
    )
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        migrationBuilder.DropForeignKeyIfExists(name: "fk_orders_users_user_id", table: "orders");

        migrationBuilder.DropCheckConstraintIfExists(name: "ck_orders_total_non_negative", table: "orders");

        migrationBuilder.DropUniqueConstraintIfExists(name: "ux_users_email", table: "users");

        migrationBuilder.DropIndexIfExists(name: "ix_orders_user_id", table: "orders");

        migrationBuilder.DropColumnIfExists(name: "display_name", table: "users");

        migrationBuilder.DropTableIfExists("orders");

        migrationBuilder.DropTableIfExists("users");
    }
}
