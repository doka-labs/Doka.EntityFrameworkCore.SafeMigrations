namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public abstract class SafeMigrationScaffoldingDbContext : DbContext
{
    private readonly string _connectionString;
    private readonly SafeMigrationScaffoldingMode _mode;

    protected SafeMigrationScaffoldingDbContext(
        string connectionString,
        SafeMigrationScaffoldingMode mode
    )
    {
        _connectionString = connectionString;
        _mode = mode;
    }

    public DbSet<SafeMigrationScaffoldingUser> Users => Set<SafeMigrationScaffoldingUser>();

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseNpgsql(_connectionString);
        optionsBuilder.UsePostgreSqlSafeMigrations(options => options.UseScaffoldingMode(_mode));
    }

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<SafeMigrationScaffoldingUser>(entity =>
        {
            entity.ToTable("scaffolding_users");
            entity.HasKey(user => user.Id);

            entity
                .Property(user => user.Email)
                .HasMaxLength(320)
                .IsRequired();

            entity
                .HasIndex(user => user.Email)
                .IsUnique();
            entity.HasIndex(user => new
            {
                user.TenantId,
                user.Email,
            });
        });
    }
}

public sealed class StrictSafeMigrationScaffoldingDbContext : SafeMigrationScaffoldingDbContext
{
    public StrictSafeMigrationScaffoldingDbContext(
        string connectionString
    ) : base(connectionString, SafeMigrationScaffoldingMode.Strict) { }
}

public sealed class LegacySafeMigrationScaffoldingDbContext : SafeMigrationScaffoldingDbContext
{
    public LegacySafeMigrationScaffoldingDbContext(
        string connectionString
    ) : base(connectionString, SafeMigrationScaffoldingMode.LegacyConvergence) { }
}

public sealed class SafeMigrationScaffoldingUser
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public string Email { get; set; } = string.Empty;
}

public sealed class StrictSafeMigrationScaffoldingDbContextFactory
    : IDesignTimeDbContextFactory<StrictSafeMigrationScaffoldingDbContext>
{
    public StrictSafeMigrationScaffoldingDbContext CreateDbContext(
        string[] args
    )
    {
        ArgumentNullException.ThrowIfNull(args);

        return new StrictSafeMigrationScaffoldingDbContext(
            PostgreSqlDesignTimeContextConfiguration.ConnectionString());
    }
}

public sealed class LegacySafeMigrationScaffoldingDbContextFactory
    : IDesignTimeDbContextFactory<LegacySafeMigrationScaffoldingDbContext>
{
    public LegacySafeMigrationScaffoldingDbContext CreateDbContext(
        string[] args
    )
    {
        ArgumentNullException.ThrowIfNull(args);

        return new LegacySafeMigrationScaffoldingDbContext(
            PostgreSqlDesignTimeContextConfiguration.ConnectionString());
    }
}
