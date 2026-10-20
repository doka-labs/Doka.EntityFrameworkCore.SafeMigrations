namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Owns an isolated generated-column model for real migration and rebuild execution.</summary>
internal sealed class SqliteGeneratedTableContractContext(
    DbConnection connection,
    IServiceProvider internalServices
) : DbContext
{
    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite(connection,
            options => options.MigrationsAssembly(typeof(SqliteGeneratedTableContractContext).Assembly.FullName));
        optionsBuilder.UseSqliteSafeMigrations();

        // WHY: These fixed test models share class-owned services instead of
        // growing EF's process-wide provider cache as the matrix runs in parallel.
        optionsBuilder.UseInternalServiceProvider(internalServices);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Node>(entity =>
        {
            entity.ToTable("generated_nodes", table =>
                table.HasCheckConstraint("ck_generated_nodes_bounds", "right_bound >= left_bound"));
            entity.HasKey(value => value.Id).HasName("pk_generated_nodes");
            entity.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(value => value.Left).HasColumnName("left_bound");
            entity.Property(value => value.Right).HasColumnName("right_bound");
            entity.Property(value => value.Label).HasColumnName("label").IsRequired();
            entity.Property(value => value.HasDescendants).HasColumnName("has_descendants")
                .HasComputedColumnSql("right_bound > left_bound", stored: false);
        });
    }

    private sealed class Node
    {
        public int Id { get; set; }

        public long Left { get; set; }

        public long Right { get; set; }

        public string Label { get; set; } = string.Empty;

        public bool HasDescendants { get; set; }
    }
}

/// <summary>Executes the same typed helper shape emitted for a fresh safe generated table.</summary>
[DbContext(typeof(SqliteGeneratedTableContractContext))]
[Migration(MigrationIdentifier)]
public sealed class SqliteGeneratedTableContractMigration : Migration
{
    /// <summary>Gets the isolated fixture's EF-valid migration identity.</summary>
    public const string MigrationIdentifier = "20261020000100_SqliteGeneratedTableContract";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTableIfNotExists("generated_nodes", table => new
        {
            id = table.Column<int>(type: "INTEGER", nullable: false),
            left_bound = table.Column<long>(type: "INTEGER", nullable: false),
            right_bound = table.Column<long>(type: "INTEGER", nullable: false),
            label = table.Column<string>(type: "TEXT", nullable: false),
            has_descendants = table.Column<bool>(type: "INTEGER", nullable: false,
                computedColumnSql: "right_bound > left_bound", stored: false),
        }, constraints: table =>
        {
            table.PrimaryKey("pk_generated_nodes", value => value.id);
            table.CheckConstraint("ck_generated_nodes_bounds", "right_bound >= left_bound");
        });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTableIfExists("generated_nodes");
    }
}

/// <summary>Proves that generated NULL rejection rolls back DDL and does not advance migration history.</summary>
[DbContext(typeof(SqliteGeneratedTableContractContext))]
[Migration(MigrationIdentifier)]
public sealed class SqliteGeneratedNullRejectionMigration : Migration
{
    /// <summary>Gets the isolated fixture's EF-valid migration identity.</summary>
    public const string MigrationIdentifier = "20261020000200_SqliteGeneratedNullRejection";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTableIfNotExists("generated_nulls", table => new
        {
            id = table.Column<int>(type: "INTEGER", nullable: false),
            source = table.Column<long>(type: "INTEGER", nullable: true),
            derived = table.Column<long>(type: "INTEGER", nullable: false,
                computedColumnSql: "source", stored: false),
        });
        migrationBuilder.Sql("INSERT INTO generated_nulls (id, source) VALUES (1, NULL);");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTableIfExists("generated_nulls");
    }
}
