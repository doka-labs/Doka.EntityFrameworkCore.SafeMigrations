namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Discovers the isolated generated-column upgrade's real EF migration history.</summary>
public sealed class SqlServerGeneratedUpgradeContext : DbContext
{
    private readonly string _connectionString;

    /// <summary>Creates the real migration context for one fixture-owned database.</summary>
    /// <param name="connectionString">The isolated database connection.</param>
    public SqlServerGeneratedUpgradeContext(string connectionString) => _connectionString = connectionString;

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlServer(_connectionString, provider => provider
            .MigrationsAssembly(typeof(SqlServerGeneratedUpgradeContext).Assembly.FullName)
            .CommandTimeout(SafeMigrationDbContext.CommandTimeoutSeconds));
        optionsBuilder.UseSqlServerSafeMigrations<SqlServerGeneratedUpgradeContext>();
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity("GeneratedUpgradeRow", entity =>
        {
            entity.ToTable("generated_upgrade_nodes");
            entity.Property<int>("Id").ValueGeneratedNever();
            entity.HasKey("Id").HasName("PK_generated_upgrade_nodes");
            entity.Property<long>("L");
            entity.Property<long>("R");
            entity.Property<long>("Depth");
            entity.Property<long>("Position");
            entity.Property<bool>("HasChildren").HasComputedColumnSql(
                SqlServerGeneratedUpgradeMigration.ComputedSql, stored: false);
            entity.HasIndex("L", "HasChildren", "R").HasDatabaseName("IX_generated_upgrade_ancestor");
        });
    }
}

/// <summary>Creates and populates an ordinary source table before generated-column support is added.</summary>
[DbContext(typeof(SqlServerGeneratedUpgradeContext))]
[Migration(MigrationIdentifier)]
public sealed class SqlServerGeneratedInitialMigration : Migration
{
    /// <summary>Gets the isolated initial migration identity.</summary>
    public const string MigrationIdentifier = "20261020000100_GeneratedInitial";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTableIfNotExists("generated_upgrade_nodes", table => new
        {
            Id = table.Column<int>(type: "int", nullable: false),
            L = table.Column<long>(type: "bigint", nullable: false),
            R = table.Column<long>(type: "bigint", nullable: false),
            Depth = table.Column<long>(type: "bigint", nullable: false),
            Position = table.Column<long>(type: "bigint", nullable: false),
        }, constraints: table => table.PrimaryKey("PK_generated_upgrade_nodes", row => row.Id));
        // WHY: This isolated historical migration intentionally has no target
        // snapshot. Explicit store types keep EF's data command independent of
        // the later runtime model and exercise the real historical operation.
        migrationBuilder.InsertData(table: "generated_upgrade_nodes", columns: ["Id", "L", "R", "Depth", "Position"],
            columnTypes: ["int", "bigint", "bigint", "bigint", "bigint"],
            values: new object[,] { { 1, 1L, 4L, 0L, 0L }, { 2, long.MaxValue - 1, long.MaxValue, 0L, 0L } });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTableIfExists("generated_upgrade_nodes");
}

/// <summary>Adds a generated ancestor flag, four independently parsed CHECKs, and its composite index.</summary>
[DbContext(typeof(SqlServerGeneratedUpgradeContext))]
[Migration(MigrationIdentifier)]
public sealed class SqlServerGeneratedUpgradeMigration : Migration
{
    /// <summary>Gets the isolated upgrade migration identity.</summary>
    public const string MigrationIdentifier = "20261020000200_GeneratedUpgrade";

    /// <summary>Gets the overflow-guarded native flag expression retained by the EF operation round trip.</summary>
    public const string ComputedSql = "CONVERT(bit,CASE WHEN [R]>CASE WHEN [L]=9223372036854775807 "
        + "THEN [L] ELSE [L]+1 END THEN 1 ELSE 0 END)";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumnIfNotExistsFromModel(static builder => builder.AddColumn<bool>(
            "HasChildren", "generated_upgrade_nodes", type: "bit", nullable: false,
            computedColumnSql: ComputedSql, stored: false), SafeMigrationPolicy.ThrowIfDifferent);
        migrationBuilder.AddCheckConstraintIfNotExists("CK_generated_lower", "generated_upgrade_nodes", "[L]>=1");
        migrationBuilder.AddCheckConstraintIfNotExists("CK_generated_range", "generated_upgrade_nodes", "[R]>[L]");
        migrationBuilder.AddCheckConstraintIfNotExists("CK_generated_depth", "generated_upgrade_nodes", "[Depth]>=0");
        migrationBuilder.AddCheckConstraintIfNotExists(
            "CK_generated_position", "generated_upgrade_nodes", "[Position]>=0");
        migrationBuilder.CreateIndexIfNotExists("IX_generated_upgrade_ancestor", "generated_upgrade_nodes",
            ["L", "HasChildren", "R"]);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndexIfExists("IX_generated_upgrade_ancestor", "generated_upgrade_nodes");
        migrationBuilder.DropCheckConstraintIfExists("CK_generated_lower", "generated_upgrade_nodes");
        migrationBuilder.DropCheckConstraintIfExists("CK_generated_range", "generated_upgrade_nodes");
        migrationBuilder.DropCheckConstraintIfExists("CK_generated_depth", "generated_upgrade_nodes");
        migrationBuilder.DropCheckConstraintIfExists("CK_generated_position", "generated_upgrade_nodes");
        migrationBuilder.DropColumnIfExists("HasChildren", "generated_upgrade_nodes");
    }
}
