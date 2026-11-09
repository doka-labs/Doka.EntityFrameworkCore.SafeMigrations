namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Owns a fixed migration family whose reverse target intentionally omits the retiring FK cycle.</summary>
internal sealed class SqliteRetirementMigrationContext : DbContext
{
    private readonly DbConnection _connection;
    private readonly IServiceProvider _internalServices;
    private readonly bool _safeFirst;

    internal SqliteRetirementMigrationContext(
        DbConnection connection,
        bool safeFirst,
        IServiceProvider internalServices
    )
    {
        _connection = connection;
        _safeFirst = safeFirst;
        _internalServices = internalServices;
    }

    /// <inheritdoc />
    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        if (_safeFirst)
        {
            optionsBuilder.UseSqliteSafeMigrations();
        }

        optionsBuilder.UseSqlite(_connection,
            options => options.MigrationsAssembly(typeof(SqliteRetirementMigrationContext).Assembly.FullName));

        if (!_safeFirst)
        {
            optionsBuilder.UseSqliteSafeMigrations();
        }

        // WHY: This fixed model family shares class-owned services without extending EF's global cache.
        optionsBuilder.UseInternalServiceProvider(_internalServices);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    ) => ConfigureModel(modelBuilder, includeRetiring: true);

    /// <summary>Defines both real migration targets without substituting the final runtime model during Down.</summary>
    internal static void ConfigureModel(
        ModelBuilder modelBuilder,
        bool includeRetiring
    )
    {
        modelBuilder.Entity("RetainedHost", entity =>
        {
            entity.ToTable("retained_host");
            entity.Property<int>("id").HasColumnType("INTEGER").ValueGeneratedNever();
            entity.Property<string>("marker").HasColumnType("TEXT").IsRequired();
            entity.HasKey("id").HasName("pk_retained_host");
            entity.HasIndex("marker").HasDatabaseName("ix_retained_host_marker");
        });

        if (!includeRetiring)
        {
            return;
        }

        modelBuilder.Entity("RetiringParent", entity =>
        {
            entity.ToTable("retiring_parent");
            entity.Property<int>("id").HasColumnType("INTEGER").ValueGeneratedNever();
            entity.Property<int?>("child_id").HasColumnType("INTEGER");
            entity.HasKey("id").HasName("pk_retiring_parent");
        });

        modelBuilder.Entity("RetiringChild", entity =>
        {
            entity.ToTable("retiring_child");
            entity.Property<int>("id").HasColumnType("INTEGER").ValueGeneratedNever();
            entity.Property<int?>("parent_id").HasColumnType("INTEGER");
            entity.HasKey("id").HasName("pk_retiring_child");
        });

        modelBuilder.Entity("RetiringParent")
            .HasOne("RetiringChild", null)
            .WithMany()
            .HasForeignKey("child_id")
            .HasConstraintName("fk_retiring_parent_child")
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity("RetiringChild")
            .HasOne("RetiringParent", null)
            .WithMany()
            .HasForeignKey("parent_id")
            .HasConstraintName("fk_retiring_child_parent")
            .OnDelete(DeleteBehavior.NoAction);
    }
}

/// <summary>Creates retained application schema before the independent feature upgrade.</summary>
[DbContext(typeof(SqliteRetirementMigrationContext))]
[Migration(MigrationIdentifier)]
public sealed class SqliteRetirementBaselineMigration : Migration
{
    /// <summary>Gets the baseline identity used to verify exact reverse history.</summary>
    public const string MigrationIdentifier = "20261109000100_SqliteRetirementBaseline";

    /// <inheritdoc />
    protected override void Up(
        MigrationBuilder migrationBuilder
    )
    {
        migrationBuilder.CreateTableIfNotExists("retained_host", table => new
        {
            id = table.Column<int>(type: "INTEGER", nullable: false),
            marker = table.Column<string>(type: "TEXT", nullable: false),
        }, constraints: table => table.PrimaryKey("pk_retained_host", value => value.id));

        migrationBuilder.CreateIndexIfNotExists("ix_retained_host_marker", "retained_host", ["marker"]);
    }

    /// <inheritdoc />
    protected override void Down(
        MigrationBuilder migrationBuilder
    ) => migrationBuilder.DropTableIfExists("retained_host");

    /// <inheritdoc />
    protected override void BuildTargetModel(
        ModelBuilder modelBuilder
    ) => SqliteRetirementMigrationContext.ConfigureModel(modelBuilder, includeRetiring: false);
}

/// <summary>Creates and retires the real cycle through safe migration operations and ordinary EF history.</summary>
[DbContext(typeof(SqliteRetirementMigrationContext))]
[Migration(MigrationIdentifier)]
public sealed class SqliteRetirementCycleMigration : Migration
{
    /// <summary>Gets the upgrade identity used for reapplication and replay.</summary>
    public const string MigrationIdentifier = "20261109000200_SqliteRetirementCycle";

    /// <inheritdoc />
    protected override void Up(
        MigrationBuilder migrationBuilder
    )
    {
        migrationBuilder.CreateTableIfNotExists("retiring_parent", table => new
        {
            id = table.Column<int>(type: "INTEGER", nullable: false),
            child_id = table.Column<int>(type: "INTEGER", nullable: true),
        }, constraints: table => table.PrimaryKey("pk_retiring_parent", value => value.id));

        migrationBuilder.CreateTableIfNotExists("retiring_child", table => new
        {
            id = table.Column<int>(type: "INTEGER", nullable: false),
            parent_id = table.Column<int>(type: "INTEGER", nullable: true),
        }, constraints: table => table.PrimaryKey("pk_retiring_child", value => value.id));

        migrationBuilder.AddForeignKeyIfNotExists("fk_retiring_parent_child", "retiring_parent", ["child_id"],
            "retiring_child", ["id"]);

        migrationBuilder.AddForeignKeyIfNotExists("fk_retiring_child_parent", "retiring_child", ["parent_id"],
            "retiring_parent", ["id"]);
    }

    /// <inheritdoc />
    protected override void Down(
        MigrationBuilder migrationBuilder
    )
    {
        migrationBuilder.DropForeignKeyIfExists("fk_retiring_parent_child", "retiring_parent");
        migrationBuilder.DropTableIfExists("retiring_child");
        migrationBuilder.DropTableIfExists("retiring_parent");
    }

    /// <inheritdoc />
    protected override void BuildTargetModel(
        ModelBuilder modelBuilder
    ) => SqliteRetirementMigrationContext.ConfigureModel(modelBuilder, includeRetiring: true);
}
