namespace Doka.EntityFrameworkCore.SafeMigrations.Sample;

/// <summary>Builds the actual source-frozen operations shared by the executable and in-process tests.</summary>
internal static class SampleMigrationUsage
{
    /// <summary>Constructs both independent example streams and writes their observed operation counts.</summary>
    /// <param name="output">The destination for the sample's source-construction summary.</param>
    /// <returns>The exact streams used to produce the summary, without executing database commands.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="output" /> is null.</exception>
    internal static Scenario Run(
        TextWriter output
    )
    {
        ArgumentNullException.ThrowIfNull(output);

        var initial = new MigrationBuilder("Doka.Sample.Provider");
        BuildUpOperations(initial);
        var maintenance = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        BuildPostgreSqlMaintenanceOperations(maintenance);

        output.WriteLine("Doka.EntityFrameworkCore.SafeMigrations sample");
        output.WriteLine("Generated sample migration operations without executing SQL.");
        output.WriteLine($"Initial-migration example operation count: {initial.Operations.Count}");
        output.WriteLine($"Maintenance example operation count: {maintenance.Operations.Count}");
        output.WriteLine(
            "See SampleMigrationUsage, InitialSafeMigrationExample, and MaintenanceSafeMigrationExample "
            + "for the recommended migration patterns.");

        // WHY: Only these local builders own the lists; callers receive ordered read-only views of the actual output.
        return new Scenario(initial.Operations.AsReadOnly(), maintenance.Operations.AsReadOnly());
    }

    /// <summary>
    /// Builds an initial legacy-convergence migration that can repair incomplete
    /// table shapes across existing application instances.
    /// </summary>
    /// <param name="migrationBuilder">The migration builder that receives the operations.</param>
    /// <exception cref="ArgumentNullException"><paramref name="migrationBuilder" /> is null.</exception>
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
    /// <exception cref="ArgumentNullException"><paramref name="migrationBuilder" /> is null.</exception>
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

    /// <summary>Retains the two independent operation streams actually constructed by the sample.</summary>
    /// <param name="InitialOperations">The forward-only granular convergence baseline.</param>
    /// <param name="MaintenanceOperations">The separate PostgreSQL maintenance example.</param>
    internal sealed record Scenario(
        IReadOnlyList<MigrationOperation> InitialOperations,
        IReadOnlyList<MigrationOperation> MaintenanceOperations
    );
}
