namespace Doka.EntityFrameworkCore.SafeMigrations.Sample;

internal static class SampleMigrationUsage
{
    /// <summary>
    /// Builds an initial legacy-convergence migration that can repair incomplete
    /// table shapes across existing application instances.
    /// </summary>
    /// <param name="migrationBuilder">The migration builder that receives the operations.</param>
    /// <exception cref="ArgumentNullException"><paramref name="migrationBuilder"/> is null.</exception>
    public static void BuildUpOperations(
        MigrationBuilder migrationBuilder
    )
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        migrationBuilder.ConvergeTableFromModel(
            "users",
            table => new
            {
                id = table.Column<Guid>(nullable: false),
                email = table.Column<string>(maxLength: 320, nullable: false),
                display_name = table.Column<string>(maxLength: 200, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_users", value => value.id);
                table.UniqueConstraint("ux_users_email", value => value.email);
            });

        migrationBuilder.ConvergeTableFromModel(
            "orders",
            table => new
            {
                id = table.Column<Guid>(nullable: false),
                user_id = table.Column<Guid>(nullable: false),
                total = table.Column<decimal>(precision: 18, scale: 2, nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_orders", value => value.id);
                table.ForeignKey(
                    "fk_orders_users_user_id",
                    value => value.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_orders_total_non_negative",
                "orders",
                SafeMigrationSql.Binary(
                    SafeMigrationSql.Identifier("total"),
                    SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
                    SafeMigrationSql.Literal(0))),
            SafeMigrationPolicy.ThrowIfDifferent);

        migrationBuilder.CreateIndexIfNotExists(
            "ix_orders_user_id",
            "orders",
            ["user_id"]);
    }

    /// <summary>Builds representative PostgreSQL maintenance operations.</summary>
    /// <param name="migrationBuilder">The migration builder that receives the operations.</param>
    /// <exception cref="ArgumentNullException"><paramref name="migrationBuilder"/> is null.</exception>
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
