namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Discovers only the ordered integer upgrade fixture's real EF migrations.</summary>
public sealed class SqlServerIntegerUpgradeContext : DbContext
{
    private readonly string _connectionString;

    /// <summary>Creates the migration context for one isolated database.</summary>
    /// <param name="connectionString">The fixture-owned isolated database connection.</param>
    public SqlServerIntegerUpgradeContext(
        string connectionString
    ) => _connectionString = connectionString;

    /// <inheritdoc />
    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlServer(_connectionString, provider => provider
            .MigrationsAssembly(typeof(SqlServerIntegerUpgradeContext).Assembly.FullName)
            .CommandTimeout(SafeMigrationDbContext.CommandTimeoutSeconds));
        optionsBuilder.UseSqlServerSafeMigrations<SqlServerIntegerUpgradeContext>();
    }

    /// <inheritdoc />
    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity("IntegerUpgradeRow", entity =>
        {
            entity.ToTable("ordered_values");
            entity.Property<int>("Id");
            entity.HasKey("Id").HasName("PK_ordered_values");
            entity.Property<long>("Lower");
            entity.Property<long>("Upper");
            entity.Property<long>("Depth").HasDefaultValue(0L);
            entity.Property<long?>("Position");
            entity.HasIndex("Lower", "Upper").HasDatabaseName("IX_new_range");
        });
    }
}

/// <summary>Creates a populated, source-stamped integer table with removable physical dependencies.</summary>
[DbContext(typeof(SqlServerIntegerUpgradeContext))]
[Migration(MigrationIdentifier)]
public sealed class SqlServerIntegerInitialMigration : Migration
{
    /// <summary>Gets the fixture's initial migration identity.</summary>
    public const string MigrationIdentifier = "20261007000100_IntegerInitial";

    /// <inheritdoc />
    protected override void Up(
        MigrationBuilder migrationBuilder
    )
    {
        migrationBuilder.CreateTableIfNotExists("ordered_values", table => new
        {
            Id = table.Column<int>(type: "int", nullable: false),
            Lower = table.Column<int>(type: "int", nullable: false),
            Upper = table.Column<int>(type: "int", nullable: false),
            Depth = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
            Position = table.Column<int>(type: "int", nullable: true),
        }, constraints: table => table.PrimaryKey("PK_ordered_values", row => row.Id));
        migrationBuilder.AddCheckConstraintIfNotExists("CK_lower_old", "ordered_values", "[Lower]>=0");
        migrationBuilder.CreateIndexIfNotExists("IX_old_range", "ordered_values", ["Lower", "Upper"]);
        migrationBuilder.InsertData(table: "ordered_values", columns: ["Id", "Lower", "Upper", "Depth", "Position"],
            columnTypes: ["int", "int", "int", "int", "int"],
            values: new object?[,] { { 1, 1, 2, 0, null }, { 2, int.MaxValue - 1, int.MaxValue, 0, 0 } });
    }

    /// <inheritdoc />
    protected override void Down(
        MigrationBuilder migrationBuilder
    ) => migrationBuilder.DropTableIfExists("ordered_values");
}

/// <summary>Runs a general ordered drop, lossless widening, populated CHECK, and index replacement upgrade.</summary>
[DbContext(typeof(SqlServerIntegerUpgradeContext))]
[Migration(MigrationIdentifier)]
public sealed class SqlServerIntegerUpgradeMigration : Migration
{
    /// <summary>Gets the fixture's upgrade migration identity.</summary>
    public const string MigrationIdentifier = "20261007000200_IntegerUpgrade";

    /// <inheritdoc />
    protected override void Up(
        MigrationBuilder migrationBuilder
    )
    {
        migrationBuilder.CreateTableIfNotExists("upgrade_marker", table => new
        {
            Id = table.Column<int>(type: "int", nullable: false),
        });
        migrationBuilder.DropIndexIfExists("IX_old_range", "ordered_values");
        migrationBuilder.DropCheckConstraintIfExists("CK_lower_old", "ordered_values");
        foreach (var name in new[] { "Lower", "Upper", "Depth", "Position" })
        {
            migrationBuilder.AlterColumnIfDifferentFromModel(builder => builder.AlterColumn<long>(
                name, "ordered_values", type: "bigint", nullable: name == "Position",
                defaultValue: name == "Depth" ? 0L : null, oldClrType: typeof(int), oldType: "int",
                oldNullable: name == "Position", oldDefaultValue: name == "Depth" ? 0 : null),
                SafeMigrationPolicy.RepairIfSafe);
        }

        migrationBuilder.AddCheckConstraintIfNotExists("CK_lower", "ordered_values", "[Lower]>=1");
        migrationBuilder.AddCheckConstraintIfNotExists("CK_range", "ordered_values", "[Upper]>[Lower]");
        migrationBuilder.AddCheckConstraintIfNotExists("CK_depth", "ordered_values", "[Depth]>=0");
        migrationBuilder.AddCheckConstraintIfNotExists("CK_position", "ordered_values", "[Position]>=0");
        migrationBuilder.CreateIndexIfNotExists("IX_new_range", "ordered_values", ["Lower", "Upper"]);
    }

    /// <inheritdoc />
    protected override void Down(
        MigrationBuilder migrationBuilder
    )
        => throw new NotSupportedException("A lossless integer upgrade does not authorize automatic narrowing.");
}
