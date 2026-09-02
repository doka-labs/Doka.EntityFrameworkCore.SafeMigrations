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
        optionsBuilder.UsePostgreSqlSafeMigrations(options =>
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

public abstract class SafeMigrationDataTransitionScaffoldingDbContext : DbContext
{
    private readonly string _connectionString;
    private readonly SafeMigrationScaffoldingMode _mode;

    protected SafeMigrationDataTransitionScaffoldingDbContext(
        string connectionString,
        SafeMigrationScaffoldingMode mode
    )
    {
        _connectionString = connectionString;
        _mode = mode;
    }

    public DbSet<SafeMigrationDataTransitionUser> Users => Set<SafeMigrationDataTransitionUser>();

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseNpgsql(_connectionString);
        optionsBuilder.UsePostgreSqlSafeMigrations(options =>
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
        string connectionString
    ) : base(connectionString, SafeMigrationScaffoldingMode.Strict) { }
}

public sealed class LegacySafeMigrationDataTransitionScaffoldingDbContext
    : SafeMigrationDataTransitionScaffoldingDbContext
{
    public LegacySafeMigrationDataTransitionScaffoldingDbContext(
        string connectionString
    ) : base(connectionString, SafeMigrationScaffoldingMode.LegacyConvergence) { }
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
            PostgreSqlDesignTimeContextConfiguration.ConnectionString());
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
            PostgreSqlDesignTimeContextConfiguration.ConnectionString());
    }
}
