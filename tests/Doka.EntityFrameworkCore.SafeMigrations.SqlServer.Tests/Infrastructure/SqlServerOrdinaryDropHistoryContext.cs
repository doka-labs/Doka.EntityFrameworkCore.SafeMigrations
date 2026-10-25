namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Exercises ordinary guarded DROP through the actual EF migration and history pipeline.</summary>
public sealed class SqlServerOrdinaryDropHistoryContext : DbContext
{
    private readonly string _connectionString;

    /// <summary>Creates the context for one fixture-owned database.</summary>
    /// <param name="connectionString">The isolated database connection.</param>
    public SqlServerOrdinaryDropHistoryContext(string connectionString) => _connectionString = connectionString;

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlServer(_connectionString, provider => provider
            .MigrationsAssembly(typeof(SqlServerOrdinaryDropHistoryContext).Assembly.FullName)
            .CommandTimeout(SafeMigrationDbContext.CommandTimeoutSeconds));
        optionsBuilder.UseSqlServerSafeMigrations<SqlServerOrdinaryDropHistoryContext>();
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity("OrdinaryDropRow", entity =>
        {
            entity.ToTable("ordinary_drop_history");
            entity.Property<int>("Id").ValueGeneratedNever();
            entity.HasKey("Id").HasName("PK_ordinary_drop_history");
            entity.Property<long>("R");
        });
    }
}

/// <summary>Creates populated ordinary columns and an independent source-index prerequisite.</summary>
[DbContext(typeof(SqlServerOrdinaryDropHistoryContext))]
[Migration(MigrationIdentifier)]
public sealed class SqlServerOrdinaryDropInitialMigration : Migration
{
    /// <summary>Gets the isolated initial migration identity.</summary>
    public const string MigrationIdentifier = "20261020010100_OrdinaryDropInitial";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
        => SqlServerOrdinaryDropHistoryOperations.CreateInitial(migrationBuilder, check: false);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTableIfExists("ordinary_drop_history");
}

/// <summary>Retains a matching source confirmation before a DROP with a live independent index blocker.</summary>
[DbContext(typeof(SqlServerOrdinaryDropHistoryContext))]
[Migration(MigrationIdentifier)]
public sealed class SqlServerOrdinaryDropBlockedMigration : Migration
{
    /// <summary>Gets the isolated upgrade migration identity.</summary>
    public const string MigrationIdentifier = "20261020010200_OrdinaryDropBlocked";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
        => SqlServerOrdinaryDropHistoryOperations.AppendBlockedDrop(migrationBuilder);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.EnsureColumn("ordinary_drop_history",
            new ExpectedColumnDefinition("L", typeof(long), true, "bigint"), SafeMigrationPolicy.ThrowIfDifferent);
}

/// <summary>Discovers independent CHECK-only history without changing the index-test migrations.</summary>
public sealed class SqlServerOrdinaryDropCheckHistoryContext : DbContext
{
    private readonly string _connectionString;

    /// <summary>Creates the CHECK-only history fixture for an isolated database.</summary>
    /// <param name="connectionString">The isolated database connection.</param>
    public SqlServerOrdinaryDropCheckHistoryContext(string connectionString) => _connectionString = connectionString;

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlServer(_connectionString, provider => provider
            .MigrationsAssembly(typeof(SqlServerOrdinaryDropCheckHistoryContext).Assembly.FullName)
            .CommandTimeout(SafeMigrationDbContext.CommandTimeoutSeconds));
        optionsBuilder.UseSqlServerSafeMigrations<SqlServerOrdinaryDropCheckHistoryContext>();
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity("OrdinaryDropRow", entity =>
        {
            entity.ToTable("ordinary_drop_history");
            entity.Property<int>("Id").ValueGeneratedNever();
            entity.HasKey("Id").HasName("PK_ordinary_drop_history");
            entity.Property<long>("R");
        });
    }
}

/// <summary>Creates the same populated source with a CHECK-only native prerequisite.</summary>
[DbContext(typeof(SqlServerOrdinaryDropCheckHistoryContext))]
[Migration(Identifier)]
public sealed class SqlServerOrdinaryDropCheckInitialMigration : Migration
{
    /// <summary>Gets the independent CHECK-only initial migration identity.</summary>
    public const string Identifier = "20261020010300_OrdinaryDropCheckInitial";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
        => SqlServerOrdinaryDropHistoryOperations.CreateInitial(migrationBuilder, check: true);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTableIfExists("ordinary_drop_history");
}

/// <summary>Reuses the exact guarded source confirmation and DROP under independent CHECK-only history.</summary>
[DbContext(typeof(SqlServerOrdinaryDropCheckHistoryContext))]
[Migration(Identifier)]
public sealed class SqlServerOrdinaryDropCheckBlockedMigration : Migration
{
    /// <summary>Gets the independent CHECK-only upgrade migration identity.</summary>
    public const string Identifier = "20261020010400_OrdinaryDropCheckBlocked";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
        => SqlServerOrdinaryDropHistoryOperations.AppendBlockedDrop(migrationBuilder);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.EnsureColumn("ordinary_drop_history",
            new ExpectedColumnDefinition("L", typeof(long), true, "bigint"), SafeMigrationPolicy.ThrowIfDifferent);
}

/// <summary>Shares fixed historical operations without inheriting discoverable EF context attributes.</summary>
internal static class SqlServerOrdinaryDropHistoryOperations
{
    /// <summary>Creates identical data with exactly one native dependency family.</summary>
    internal static void CreateInitial(MigrationBuilder builder, bool check)
    {
        builder.CreateTableIfNotExists("ordinary_drop_history", table => new
        {
            Id = table.Column<int>(type: "int", nullable: false),
            L = table.Column<long>(type: "bigint", nullable: false),
            R = table.Column<long>(type: "bigint", nullable: false),
        }, constraints: table => table.PrimaryKey("PK_ordinary_drop_history", row => row.Id));
        // WHY: Explicit historical types avoid borrowing the final model, which deliberately no longer has L.
        builder.InsertData("ordinary_drop_history", ["Id", "L", "R"],
            ["int", "bigint", "bigint"], new object[,] { { 1, 1L, 4L }, { 2, 2L, 8L } });
        if (check)
        {
            builder.AddCheckConstraintIfNotExists("CK_ordinary_drop_history_L", "ordinary_drop_history", "L>0");
        }
        else
        {
            builder.CreateIndexIfNotExists("IX_ordinary_drop_history_L", "ordinary_drop_history", ["L"]);
        }
    }

    /// <summary>Produces identical guarded DROP operations in both independently discovered histories.</summary>
    internal static void AppendBlockedDrop(MigrationBuilder builder)
    {
        builder.EnsureColumn("ordinary_drop_history",
            new ExpectedColumnDefinition("L", typeof(long), false, "bigint"), SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropColumnIfExists("L", "ordinary_drop_history");
    }
}
