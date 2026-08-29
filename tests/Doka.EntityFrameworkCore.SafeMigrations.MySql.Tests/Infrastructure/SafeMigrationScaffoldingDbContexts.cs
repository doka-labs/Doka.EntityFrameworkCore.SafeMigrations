namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public abstract class SafeMigrationScaffoldingDbContext : DbContext
{
    private readonly string _connectionString;
    private readonly SafeMigrationScaffoldingMode _mode;
    private readonly MySqlServerVersion _serverVersion;

    protected SafeMigrationScaffoldingDbContext(
        string connectionString,
        MySqlServerVersion serverVersion,
        SafeMigrationScaffoldingMode mode
    )
    {
        _connectionString = connectionString;
        _serverVersion = serverVersion;
        _mode = mode;
    }

    public DbSet<SafeMigrationScaffoldingUser> Users => Set<SafeMigrationScaffoldingUser>();

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseMySql(_connectionString, _serverVersion);
        optionsBuilder.UseMySqlSafeMigrations(options =>
        {
            options.UseScaffoldingMode(_mode);
            if (_mode == SafeMigrationScaffoldingMode.LegacyConvergence)
            {
                options.UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe);
            }
        });
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
        string connectionString,
        MySqlServerVersion serverVersion
    ) : base(connectionString, serverVersion, SafeMigrationScaffoldingMode.Strict) { }
}

public sealed class LegacySafeMigrationScaffoldingDbContext : SafeMigrationScaffoldingDbContext
{
    public LegacySafeMigrationScaffoldingDbContext(
        string connectionString,
        MySqlServerVersion serverVersion
    ) : base(connectionString, serverVersion, SafeMigrationScaffoldingMode.LegacyConvergence) { }
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
            MySqlDesignTimeContextConfiguration.ConnectionString(),
            MySqlDesignTimeContextConfiguration.ServerVersion());
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
            MySqlDesignTimeContextConfiguration.ConnectionString(),
            MySqlDesignTimeContextConfiguration.ServerVersion());
    }
}
