namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed class SqliteToolingDbContext : SqliteToolingDbContextBase<SqliteToolingDbContext>
{
    public SqliteToolingDbContext(
        string connectionString
    ) : base(connectionString, SafeMigrationScaffoldingMode.Strict)
    { }
}

public sealed class SqliteLegacyToolingDbContext : SqliteToolingDbContextBase<SqliteLegacyToolingDbContext>
{
    public SqliteLegacyToolingDbContext(
        string connectionString
    ) : base(connectionString, SafeMigrationScaffoldingMode.LegacyConvergence)
    { }
}

public abstract class SqliteToolingDbContextBase<TContext> : DbContext
    where TContext : DbContext
{
    private readonly string _connectionString;
    private readonly SafeMigrationScaffoldingMode _mode;

    protected SqliteToolingDbContextBase(
        string connectionString,
        SafeMigrationScaffoldingMode mode
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;
        _mode = mode;
    }

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

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<ToolingEntity>(entity =>
        {
            entity.ToTable("tooling_entities");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Code).HasMaxLength(80).IsRequired();
            entity.HasIndex(value => value.Code).IsUnique();
            entity.HasData(new ToolingEntity { Id = 1, Code = "baseline" });
        });
    }

    private sealed class ToolingEntity
    {
        public int Id { get; set; }

        public string Code { get; set; } = string.Empty;
    }
}

public sealed class SqliteToolingDbContextFactory : IDesignTimeDbContextFactory<SqliteToolingDbContext>
{
    public SqliteToolingDbContext CreateDbContext(
        string[] args
    ) => new(ReadConnectionString());

    internal static string ReadConnectionString()
        => Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_SQLITE_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "SAFE_MIGRATIONS_SQLITE_CONNECTION_STRING is required for SQLite tooling qualification.");
}

public sealed class SqliteLegacyToolingDbContextFactory : IDesignTimeDbContextFactory<SqliteLegacyToolingDbContext>
{
    public SqliteLegacyToolingDbContext CreateDbContext(
        string[] args
    ) => new(SqliteToolingDbContextFactory.ReadConnectionString());
}
