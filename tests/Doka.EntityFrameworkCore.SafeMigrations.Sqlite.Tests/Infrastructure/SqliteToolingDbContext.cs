namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>
/// Provides the SQLite tooling db context fixture contract.
/// </summary>
public sealed class SqliteToolingDbContext : SqliteToolingDbContextBase<SqliteToolingDbContext>
{
    /// <summary>
    /// Initializes the fixture with explicitly supplied dependencies and ownership.
    /// </summary>
    /// <param name="connectionString">The connection string defining this isolated fixture case.</param>
    public SqliteToolingDbContext(
        string connectionString
    ) : base(connectionString, SafeMigrationScaffoldingMode.Strict) { }
}

/// <summary>
/// Provides the SQLite legacy tooling db context fixture contract.
/// </summary>
public sealed class SqliteLegacyToolingDbContext : SqliteToolingDbContextBase<SqliteLegacyToolingDbContext>
{
    /// <summary>
    /// Initializes the fixture with explicitly supplied dependencies and ownership.
    /// </summary>
    /// <param name="connectionString">The connection string defining this isolated fixture case.</param>
    public SqliteLegacyToolingDbContext(
        string connectionString
    ) : base(connectionString, SafeMigrationScaffoldingMode.LegacyConvergence) { }
}

/// <summary>
/// Provides the SQLite tooling db context base fixture contract.
/// </summary>
/// <typeparam name="TContext">The concrete tooling context that owns this fixture's EF options.</typeparam>
public abstract class SqliteToolingDbContextBase<TContext> : DbContext
    where TContext : DbContext
{
    private readonly string _connectionString;
    private readonly SafeMigrationScaffoldingMode _mode;

    /// <summary>
    /// Initializes the fixture with explicitly supplied dependencies and ownership.
    /// </summary>
    /// <param name="connectionString">The connection string defining this isolated fixture case.</param>
    /// <param name="mode">The mode defining this isolated fixture case.</param>
    protected SqliteToolingDbContextBase(
        string connectionString,
        SafeMigrationScaffoldingMode mode
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;
        _mode = mode;
    }

    /// <inheritdoc />
    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlite(
            _connectionString,
            provider => provider.MigrationsAssembly(typeof(SqliteToolingDbContext).Assembly.FullName));

        optionsBuilder.UseSqliteSafeMigrations<TContext>(configuration =>
        {
            configuration.UseScaffoldingMode(_mode);
            if (_mode == SafeMigrationScaffoldingMode.LegacyConvergence)
            {
                configuration.UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe);
            }
        });
    }

    /// <inheritdoc />
    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        var targetState = StringComparer.Ordinal.Equals(
            Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_SQLITE_TOOLING_STATE"),
            "target");

        // WHY: The tooling lifecycle includes real generated-column CREATE,
        // not only the separately rendered standalone generated ADD path.
        modelBuilder.Entity(
            "ScaffoldingGeneratedNode",
            entity =>
            {
                entity.ToTable(
                    "scaffolding_generated_nodes",
                    table =>
                        table.HasCheckConstraint("ck_scaffolding_generated_bounds", "\"RightBound\" > \"LeftBound\""));

                entity
                    .Property<int>("Id")
                    .ValueGeneratedNever();

                entity.HasKey("Id");
                entity.Property<long>("LeftBound");
                entity.Property<long>("RightBound");
                entity
                    .Property<bool>("HasDescendants")
                    .HasComputedColumnSql(
                        "\"RightBound\" > CASE WHEN \"LeftBound\" = 9223372036854775807 "
                        + "THEN \"LeftBound\" ELSE \"LeftBound\" + 1 END",
                        stored: false);

                entity.HasIndex("HasDescendants", "LeftBound", "RightBound");
            });

        modelBuilder.Entity<ToolingEntity>(entity =>
        {
            entity.ToTable("tooling_entities");
            entity.HasKey(value => value.Id);
            entity
                .Property(value => value.Code)
                .HasMaxLength(80)
                .IsRequired();

            entity
                .HasIndex(value => value.Code)
                .IsUnique();

            entity
                .Property<string?>("RenamedCode")
                .HasColumnName(
                    targetState
                        ? "renamed_code"
                        : "old_code")
                .HasMaxLength(32);

            if (targetState)
            {
                entity
                    .Property<string?>("AddedCode")
                    .HasMaxLength(32);
            }
            else
            {
                entity.Property<int?>("LegacyCode");
            }

            entity.HasData(
                new ToolingEntity
                {
                    Id = 1,
                    Code = "baseline",
                });
        });
    }

    // WHY: EF binds this fixture entity through writable property metadata.
    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class ToolingEntity
    {
        public int Id { get; set; }

        public string Code { get; set; } = string.Empty;
    }
}

/// <summary>
/// Provides the SQLite tooling db context factory fixture contract.
/// </summary>
// WHY: EF tooling discovers this factory to create its design-time context.
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SqliteToolingDbContextFactory : IDesignTimeDbContextFactory<SqliteToolingDbContext>
{
    /// <inheritdoc />
    public SqliteToolingDbContext CreateDbContext(
        string[] args
    ) => new(ReadConnectionString());

    internal static string ReadConnectionString()
        => Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_SQLITE_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "SAFE_MIGRATIONS_SQLITE_CONNECTION_STRING is required for SQLite tooling qualification.");
}

/// <summary>
/// Provides the SQLite legacy tooling db context factory fixture contract.
/// </summary>
// WHY: EF tooling discovers this factory to create its design-time context.
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SqliteLegacyToolingDbContextFactory : IDesignTimeDbContextFactory<SqliteLegacyToolingDbContext>
{
    /// <inheritdoc />
    public SqliteLegacyToolingDbContext CreateDbContext(
        string[] args
    ) => new(SqliteToolingDbContextFactory.ReadConnectionString());
}
