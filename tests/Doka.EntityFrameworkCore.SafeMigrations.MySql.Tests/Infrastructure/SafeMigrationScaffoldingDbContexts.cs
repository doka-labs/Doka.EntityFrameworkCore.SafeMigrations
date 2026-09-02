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
            }).HasPrefixLength(0, 64);

            entity.HasData(
                new SafeMigrationScaffoldingUser
                {
                    Id = 1,
                    TenantId = 7,
                    Email = "administrator@example.test",
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

public abstract class SafeMigrationDataTransitionScaffoldingDbContext : DbContext
{
    private readonly string _connectionString;
    private readonly SafeMigrationScaffoldingMode _mode;
    private readonly MySqlServerVersion _serverVersion;

    protected SafeMigrationDataTransitionScaffoldingDbContext(
        string connectionString,
        MySqlServerVersion serverVersion,
        SafeMigrationScaffoldingMode mode
    )
    {
        _connectionString = connectionString;
        _serverVersion = serverVersion;
        _mode = mode;
    }

    public DbSet<SafeMigrationDataTransitionUser> Users => Set<SafeMigrationDataTransitionUser>();

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
        var targetState = StringComparer.Ordinal.Equals(
            Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_MODEL_MANAGED_DATA_STATE"),
            "target");

        modelBuilder.Entity<SafeMigrationDataTransitionUser>(entity =>
        {
            entity.ToTable("scaffolding_transition_users");
            entity.HasKey(user => user.Id);

            entity
                .Property(user => user.Email)
                .HasMaxLength(320)
                .IsRequired();

            entity.HasData(targetState
                ?
                [
                    new SafeMigrationDataTransitionUser { Id = 1, Email = "owner@example.test", },
                    new SafeMigrationDataTransitionUser { Id = 3, Email = "auditor@example.test", },
                ]
                :
                [
                    new SafeMigrationDataTransitionUser { Id = 1, Email = "administrator@example.test", },
                    new SafeMigrationDataTransitionUser { Id = 2, Email = "member@example.test", },
                ]);
        });
    }
}

public sealed class StrictSafeMigrationDataTransitionScaffoldingDbContext
    : SafeMigrationDataTransitionScaffoldingDbContext
{
    public StrictSafeMigrationDataTransitionScaffoldingDbContext(
        string connectionString,
        MySqlServerVersion serverVersion
    ) : base(connectionString, serverVersion, SafeMigrationScaffoldingMode.Strict) { }
}

public sealed class LegacySafeMigrationDataTransitionScaffoldingDbContext
    : SafeMigrationDataTransitionScaffoldingDbContext
{
    public LegacySafeMigrationDataTransitionScaffoldingDbContext(
        string connectionString,
        MySqlServerVersion serverVersion
    ) : base(connectionString, serverVersion, SafeMigrationScaffoldingMode.LegacyConvergence) { }
}

public sealed class SafeMigrationDataTransitionUser
{
    public int Id { get; set; }

    public string Email { get; set; } = string.Empty;
}

public sealed class StrictSafeMigrationDataTransitionScaffoldingDbContextFactory
    : IDesignTimeDbContextFactory<StrictSafeMigrationDataTransitionScaffoldingDbContext>
{
    public StrictSafeMigrationDataTransitionScaffoldingDbContext CreateDbContext(
        string[] args
    )
    {
        ArgumentNullException.ThrowIfNull(args);

        return new StrictSafeMigrationDataTransitionScaffoldingDbContext(
            MySqlDesignTimeContextConfiguration.ConnectionString(),
            MySqlDesignTimeContextConfiguration.ServerVersion());
    }
}

public sealed class LegacySafeMigrationDataTransitionScaffoldingDbContextFactory
    : IDesignTimeDbContextFactory<LegacySafeMigrationDataTransitionScaffoldingDbContext>
{
    public LegacySafeMigrationDataTransitionScaffoldingDbContext CreateDbContext(
        string[] args
    )
    {
        ArgumentNullException.ThrowIfNull(args);

        return new LegacySafeMigrationDataTransitionScaffoldingDbContext(
            MySqlDesignTimeContextConfiguration.ConnectionString(),
            MySqlDesignTimeContextConfiguration.ServerVersion());
    }
}
