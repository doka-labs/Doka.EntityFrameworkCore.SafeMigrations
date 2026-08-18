namespace Doka.EntityFrameworkCore.SafeMigrations.Sample;

internal static class SampleMigrationUsage
{
    public static void BuildUpOperations(
        MigrationBuilder migrationBuilder
    )
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        var users = new ExpectedTableDefinition(
            "users",
            [
                new ExpectedColumnDefinition("id", typeof(Guid), isNullable: false),
                new ExpectedColumnDefinition("email", typeof(string), isNullable: false, maxLength: 320),
                new ExpectedColumnDefinition("display_name", typeof(string), isNullable: true, maxLength: 200),
            ],
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_users", "users", ["id"]),
            uniqueConstraints:
            [
                new ExpectedUniqueConstraintDefinition("ux_users_email", "users", ["email"]),
            ]);

        migrationBuilder.ConvergeTable(users);

        var orders = new ExpectedTableDefinition(
            "orders",
            [
                new ExpectedColumnDefinition("id", typeof(Guid), isNullable: false),
                new ExpectedColumnDefinition("user_id", typeof(Guid), isNullable: false),
                new ExpectedColumnDefinition("total", typeof(decimal), isNullable: false, precision: 18, scale: 2),
            ],
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_orders", "orders", ["id"]),
            checkConstraints:
            [
                new ExpectedCheckConstraintDefinition("ck_orders_total_non_negative", "orders", "total >= 0"),
            ],
            foreignKeys:
            [
                new ExpectedForeignKeyDefinition(
                    "fk_orders_users_user_id",
                    "orders",
                    ["user_id"],
                    "users",
                    ["id"],
                    onDelete: ReferentialAction.Cascade),
            ]);

        migrationBuilder.ConvergeTable(
            orders,
            [
                new ExpectedIndexDefinition(
                    "ix_orders_user_id",
                    "orders",
                    [new ExpectedIndexKeyDefinition(column: "user_id")]),
            ]);
    }

    public static void BuildPostgreSqlMaintenanceOperations(
        MigrationBuilder migrationBuilder
    )
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.EnsureSchemaExists("reporting");
        migrationBuilder.RenameTableIfExists("legacy_users", "users");
        migrationBuilder.RenameColumnIfExists("full_name", "users", "display_name");
        migrationBuilder.RenameIndexIfExists("ix_legacy_orders_customer_id", "orders", "ix_orders_user_id");

        var oldColumn = new ExpectedColumnDefinition(
            "display_name",
            typeof(string),
            isNullable: true,
            storeType: "character varying(200)",
            maxLength: 200);

        var targetColumn = new ExpectedColumnDefinition(
            "display_name",
            typeof(string),
            isNullable: true,
            storeType: "character varying(200)",
            maxLength: 200,
            comment: "Canonical display name");

        migrationBuilder.AlterColumnIfDifferent("users", targetColumn, oldColumn, SafeMigrationPolicy.RepairIfSafe);
    }
}
