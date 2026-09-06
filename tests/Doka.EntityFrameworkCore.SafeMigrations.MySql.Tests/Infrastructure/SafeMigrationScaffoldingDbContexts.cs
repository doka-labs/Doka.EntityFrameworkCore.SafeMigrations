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

            entity.Ignore(user => user.Request);
            entity.Ignore(user => user.RequestId);
            entity.Ignore(user => user.RequestMetadata);

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

        modelBuilder.Ignore<SafeMigrationScaffoldingRequest>();
        modelBuilder.Entity<SafeMigrationScaffoldingTask>();
        modelBuilder.Entity<SafeMigrationScaffoldingExternalWorkItem>();

        modelBuilder.Entity<SafeMigrationScaffoldingWorkItem>(entity =>
        {
            entity.ToTable("scaffolding_work_items");
            entity.HasKey(workItem => workItem.Id);

            entity
                .Property(workItem => workItem.Caption)
                .HasMaxLength(128)
                .IsRequired();

            entity
                .Property<SafeMigrationScaffoldingWorkItemKind>("Discriminator")
                .HasConversion<int>();

            entity
                .HasDiscriminator<SafeMigrationScaffoldingWorkItemKind>("Discriminator")
                .HasValue<SafeMigrationScaffoldingTask>(SafeMigrationScaffoldingWorkItemKind.Task)
                .IsComplete(false);
        });

        // The metadata path must preserve the same discriminator contract as
        // the fluent HasValue path when EF scaffolds and reloads its snapshot.
        modelBuilder
            .Entity<SafeMigrationScaffoldingExternalWorkItem>()
            .Metadata
            .SetDiscriminatorValue(SafeMigrationScaffoldingWorkItemKind.External);

        modelBuilder.Entity<SafeMigrationJsonNamedWorkItem>();
        modelBuilder.Entity<SafeMigrationContractNamedWorkItem>();
        modelBuilder.Entity<SafeMigrationFallbackNamedWorkItem>();

        modelBuilder.Entity<SafeMigrationAttributedWorkItem>(entity =>
        {
            entity.ToTable("scaffolding_attributed_work_items");
            entity.HasKey(workItem => workItem.Id);

            entity
                .Property(workItem => workItem.Caption)
                .HasMaxLength(128)
                .IsRequired();

            entity
                .Property<SafeMigrationAttributedWorkItemKind>("AttributedDiscriminator")
                .HasConversion<SafeMigrationAttributedEnumValueConverter<SafeMigrationAttributedWorkItemKind>>()
                .HasMaxLength(
                    SafeMigrationAttributedEnumName.MaximumLength<SafeMigrationAttributedWorkItemKind>());

            entity
                .HasDiscriminator<SafeMigrationAttributedWorkItemKind>("AttributedDiscriminator")
                .HasValue<SafeMigrationJsonNamedWorkItem>(SafeMigrationAttributedWorkItemKind.JsonNamed)
                .HasValue<SafeMigrationFallbackNamedWorkItem>(SafeMigrationAttributedWorkItemKind.Fallback)
                .IsComplete(false);
        });

        // The second metadata value proves that custom provider strings remain
        // equivalent whether a consumer uses HasValue or SetDiscriminatorValue.
        modelBuilder
            .Entity<SafeMigrationContractNamedWorkItem>()
            .Metadata
            .SetDiscriminatorValue(SafeMigrationAttributedWorkItemKind.ContractNamed);
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

    public int? RequestId { get; set; }

    public string? RequestMetadata { get; set; }

    public SafeMigrationScaffoldingRequest? Request { get; set; }
}

public sealed class SafeMigrationScaffoldingRequest
{
    public int Id { get; set; }
}

public abstract class SafeMigrationScaffoldingWorkItem
{
    public int Id { get; set; }

    public string Caption { get; set; } = string.Empty;
}

public sealed class SafeMigrationScaffoldingTask : SafeMigrationScaffoldingWorkItem;

public sealed class SafeMigrationScaffoldingExternalWorkItem : SafeMigrationScaffoldingWorkItem;

public enum SafeMigrationScaffoldingWorkItemKind
{
    Task,
    External,
}

public abstract class SafeMigrationAttributedWorkItem
{
    public int Id { get; set; }

    public string Caption { get; set; } = string.Empty;
}

public sealed class SafeMigrationJsonNamedWorkItem : SafeMigrationAttributedWorkItem;

public sealed class SafeMigrationContractNamedWorkItem : SafeMigrationAttributedWorkItem;

public sealed class SafeMigrationFallbackNamedWorkItem : SafeMigrationAttributedWorkItem;

public enum SafeMigrationAttributedWorkItemKind
{
    [JsonStringEnumMemberName("json-named")]
    JsonNamed,

    [EnumMember(Value = "contract-named")]
    ContractNamed,

    Fallback,
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

            if (targetState)
            {
                entity
                    .Property(user => user.RequestMetadata)
                    .HasMaxLength(128);

                entity
                    .HasOne(user => user.Request)
                    .WithMany()
                    .HasForeignKey(user => user.RequestId)
                    .OnDelete(DeleteBehavior.SetNull);
            }
            else
            {
                entity.Ignore(user => user.Request);
                entity.Ignore(user => user.RequestId);
                entity.Ignore(user => user.RequestMetadata);
            }

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

        if (targetState)
        {
            modelBuilder.Entity<SafeMigrationDataTransitionRequest>(entity =>
            {
                entity.ToTable("scaffolding_transition_requests");
                entity.HasKey(request => request.Id);

                entity
                    .Property(request => request.Caption)
                    .HasMaxLength(128)
                    .IsRequired();
            });
        }
        else
        {
            modelBuilder.Ignore<SafeMigrationDataTransitionRequest>();
        }
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

    public int? RequestId { get; set; }

    public string? RequestMetadata { get; set; }

    public SafeMigrationDataTransitionRequest? Request { get; set; }
}

public sealed class SafeMigrationDataTransitionRequest
{
    public int Id { get; set; }

    public string Caption { get; set; } = string.Empty;
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
