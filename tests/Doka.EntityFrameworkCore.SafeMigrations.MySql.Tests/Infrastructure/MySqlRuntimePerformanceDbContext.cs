namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Hosts the generic runtime workload independently of application models and explicit preflight.</summary>
public sealed class MySqlRuntimePerformanceDbContext : DbContext
{
    /// <summary>Initializes a context with the observed workload connection.</summary>
    /// <param name="options">The provider configuration and command observer.</param>
    public MySqlRuntimePerformanceDbContext(
        DbContextOptions<MySqlRuntimePerformanceDbContext> options
    ) : base(options) { }
}

/// <summary>Keeps the explicitly authored workload model free from unrelated model-change checks.</summary>
[DbContext(typeof(MySqlRuntimePerformanceDbContext))]
public sealed class MySqlRuntimePerformanceSnapshot : ModelSnapshot
{
    /// <inheritdoc />
    protected override void BuildModel(
        ModelBuilder modelBuilder
    ) => ArgumentNullException.ThrowIfNull(modelBuilder);
}

/// <summary>Defines a generic initial convergence workload with columns and ordered key dependencies.</summary>
[DbContext(typeof(MySqlRuntimePerformanceDbContext))]
[Migration("20261001000100_RuntimeConvergence")]
public sealed class MySqlRuntimeConvergenceMigration : Migration
{
    /// <summary>Gets the workload table count.</summary>
    public const int TableCount = 30;

    /// <summary>Returns a deterministic, non-consumer-specific workload table name.</summary>
    /// <param name="ordinal">The table position in the chain.</param>
    /// <returns>The identifier used by workload setup and migration operations.</returns>
    public static string TableName(
        int ordinal
    ) => "runtime_records_" + ordinal.ToString("D2", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    protected override void Up(
        MigrationBuilder migrationBuilder
    )
    {
        for (var ordinal = 0; ordinal < TableCount; ordinal++)
        {
            var tableName = TableName(ordinal);

            migrationBuilder.ConvergeTableFromModel(
                tableName,
                table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: false),
                    ParentId = table.Column<int>(type: "int", nullable: true),
                    Value01 = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true),
                    Value02 = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true),
                    Value03 = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true),
                    Value04 = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true),
                    Value05 = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true),
                    Value06 = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true),
                    Value07 = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true),
                    Value08 = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true),
                    Value09 = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true),
                },
                constraints: table => table.PrimaryKey("PK_" + tableName, row => row.Id),
                policy: SafeMigrationPolicy.RepairIfSafe);

            migrationBuilder.AddUniqueConstraintIfNotExists("UQ_" + tableName + "_Code", tableName, ["Code"]);
            migrationBuilder.CreateIndexIfNotExists("IX_" + tableName + "_ParentId", tableName, ["ParentId"]);
            if (ordinal > 0)
            {
                // WHY: The chain proves a later operation observes keys and tables created by preceding operations.
                migrationBuilder.AddForeignKeyIfNotExists(
                    "FK_" + tableName + "_Parent",
                    tableName,
                    ["ParentId"],
                    TableName(ordinal - 1),
                    ["Id"],
                    onDelete: ReferentialAction.Restrict);
            }
        }
    }

    /// <inheritdoc />
    protected override void Down(
        MigrationBuilder migrationBuilder
    ) => throw new NotSupportedException("The runtime workload has a forward-only convergence baseline.");
}

/// <summary>Adds strict operations after the legacy convergence migration in the same pending stream.</summary>
[DbContext(typeof(MySqlRuntimePerformanceDbContext))]
[Migration("20261001000200_RuntimeStrictColumns")]
public sealed class MySqlRuntimeStrictMigration : Migration
{
    /// <inheritdoc />
    protected override void Up(
        MigrationBuilder migrationBuilder
    )
    {
        for (var ordinal = 0; ordinal < MySqlRuntimeConvergenceMigration.TableCount; ordinal++)
        {
            migrationBuilder.AddColumnIfNotExists<string>(
                "Marker",
                MySqlRuntimeConvergenceMigration.TableName(ordinal),
                type: "varchar(32)",
                maxLength: 32,
                nullable: true);
        }
    }

    /// <inheritdoc />
    protected override void Down(
        MigrationBuilder migrationBuilder
    ) => throw new NotSupportedException("The runtime workload is forward-only.");
}

/// <summary>Hosts a small ordered raw-DML regression independently of the timed convergence workload.</summary>
public sealed class MySqlRuntimeFreshStateDbContext : DbContext
{
    /// <summary>Initializes the dedicated live-state context.</summary>
    /// <param name="options">The actual provider options and observer.</param>
    public MySqlRuntimeFreshStateDbContext(
        DbContextOptions<MySqlRuntimeFreshStateDbContext> options
    ) : base(options) { }
}

/// <summary>Describes the deliberately model-independent fresh-state fixture.</summary>
[DbContext(typeof(MySqlRuntimeFreshStateDbContext))]
public sealed class MySqlRuntimeFreshStateSnapshot : ModelSnapshot
{
    /// <inheritdoc />
    protected override void BuildModel(
        ModelBuilder modelBuilder
    ) => ArgumentNullException.ThrowIfNull(modelBuilder);
}

/// <summary>Commits an earlier successful migration before a later raw-DML change.</summary>
[DbContext(typeof(MySqlRuntimeFreshStateDbContext))]
[Migration("20261001000300_RuntimeFreshStateInit")]
public sealed class MySqlRuntimeFreshStateInitialMigration : Migration
{
    /// <inheritdoc />
    protected override void Up(
        MigrationBuilder migrationBuilder
    )
    {
        migrationBuilder.CreateTableIfNotExists(
            "runtime_fresh_rows",
            table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                Code = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_runtime_fresh_rows", row => row.Id));

        migrationBuilder.Sql("INSERT INTO `runtime_fresh_rows` VALUES (1, 'duplicate'), (2, 'second');");
    }

    /// <inheritdoc />
    protected override void Down(
        MigrationBuilder migrationBuilder
    ) => throw new NotSupportedException("The fresh-state regression is forward-only.");
}

/// <summary>Introduces duplicates after earlier successful work so a later guard must read fresh data.</summary>
[DbContext(typeof(MySqlRuntimeFreshStateDbContext))]
[Migration("20261001000400_RuntimeFreshStateUnique")]
public sealed class MySqlRuntimeFreshStateUniqueMigration : Migration
{
    /// <inheritdoc />
    protected override void Up(
        MigrationBuilder migrationBuilder
    )
    {
        migrationBuilder.Sql("UPDATE `runtime_fresh_rows` SET `Code` = 'duplicate' WHERE `Code` = 'second';");
        migrationBuilder.AddUniqueConstraintIfNotExists(
            "UQ_runtime_fresh_rows_Code",
            "runtime_fresh_rows",
            ["Code"]);
    }

    /// <inheritdoc />
    protected override void Down(
        MigrationBuilder migrationBuilder
    ) => throw new NotSupportedException("The fresh-state regression is forward-only.");
}

/// <summary>Hosts one real guarded operation so failure recovery does not repeat the large timing workload.</summary>
public sealed class MySqlRuntimeScopeProbeDbContext : DbContext
{
    /// <summary>Initializes the small migration-execution probe.</summary>
    /// <param name="options">The actual provider options and observer.</param>
    public MySqlRuntimeScopeProbeDbContext(
        DbContextOptions<MySqlRuntimeScopeProbeDbContext> options
    ) : base(options) { }
}

/// <summary>Describes the model-independent one-operation scope probe.</summary>
[DbContext(typeof(MySqlRuntimeScopeProbeDbContext))]
public sealed class MySqlRuntimeScopeProbeSnapshot : ModelSnapshot
{
    /// <inheritdoc />
    protected override void BuildModel(
        ModelBuilder modelBuilder
    ) => ArgumentNullException.ThrowIfNull(modelBuilder);
}

/// <summary>Applies one guarded table operation through EF's actual migration execution pipeline.</summary>
[DbContext(typeof(MySqlRuntimeScopeProbeDbContext))]
[Migration("20261001000500_RuntimeScopeProbe")]
public sealed class MySqlRuntimeScopeProbeMigration : Migration
{
    /// <inheritdoc />
    protected override void Up(
        MigrationBuilder migrationBuilder
    ) => migrationBuilder.CreateTableIfNotExists(
        "runtime_records_probe",
        table => new
        {
            Id = table.Column<int>(type: "int", nullable: false),
            Code = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: true),
        },
        constraints: table => table.PrimaryKey("PK_runtime_records_probe", row => row.Id));

    /// <inheritdoc />
    protected override void Down(
        MigrationBuilder migrationBuilder
    ) => throw new NotSupportedException("The scope probe is forward-only.");
}

/// <summary>Hosts one lazy guarded operation whose short prepared setup statements can be compacted.</summary>
public sealed class MySqlRuntimeCompactedSetupProbeDbContext : DbContext
{
    /// <summary>Initializes the compacted setup execution probe.</summary>
    /// <param name="options">The actual provider options and acquired-resource observer.</param>
    public MySqlRuntimeCompactedSetupProbeDbContext(
        DbContextOptions<MySqlRuntimeCompactedSetupProbeDbContext> options
    ) : base(options) { }
}

/// <summary>Describes the model-independent compacted setup probe.</summary>
[DbContext(typeof(MySqlRuntimeCompactedSetupProbeDbContext))]
public sealed class MySqlRuntimeCompactedSetupProbeSnapshot : ModelSnapshot
{
    /// <inheritdoc />
    protected override void BuildModel(
        ModelBuilder modelBuilder
    ) => ArgumentNullException.ThrowIfNull(modelBuilder);
}

/// <summary>Applies one column operation with lazy state evaluation through real migration execution.</summary>
[DbContext(typeof(MySqlRuntimeCompactedSetupProbeDbContext))]
[Migration("20261001000600_RuntimeCompactedSetupProbe")]
public sealed class MySqlRuntimeCompactedSetupProbeMigration : Migration
{
    /// <inheritdoc />
    protected override void Up(
        MigrationBuilder migrationBuilder
    )
    {
        // WHY: Required-column repair evaluates state lazily, yielding real compacted prepared setup groups.
        migrationBuilder.AddColumnIfNotExists<string>(
            "Code",
            "runtime_records_probe",
            type: "varchar(80)",
            maxLength: 80,
            nullable: false,
            defaultValue: string.Empty,
            policy: SafeMigrationPolicy.RepairIfSafe);
    }

    /// <inheritdoc />
    protected override void Down(
        MigrationBuilder migrationBuilder
    ) => throw new NotSupportedException("The compacted setup probe is forward-only.");
}
