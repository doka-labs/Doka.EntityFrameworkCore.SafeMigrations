namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Verifies SafeMigrations Scaffolding Db Context contracts in independently owned test state.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public abstract class SafeMigrationScaffoldingDbContext : DbContext
{
    private readonly string _connectionString;
    private readonly SafeMigrationScaffoldingMode _mode;

    /// <summary>Initializes the fixture with its configured dependencies and ownership boundary.</summary>
    protected SafeMigrationScaffoldingDbContext(
        string connectionString,
        SafeMigrationScaffoldingMode mode
    )
    {
        _connectionString = connectionString;
        _mode = mode;
    }

    /// <summary>Exposes the mapped users used by scaffolding and ownership qualification.</summary>
    public DbSet<SafeMigrationScaffoldingUser> Users => Set<SafeMigrationScaffoldingUser>();

    /// <inheritdoc />
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

    /// <inheritdoc />
    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        // WHY: Both real scaffolding modes must preserve parsed CASE and CHECK
        // definitions through compilation, scripts, bundles and native replay.
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
                        stored: true);

                entity.HasIndex("HasDescendants", "LeftBound", "RightBound");
            });

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
            });

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

        modelBuilder.Entity<SafeMigrationScaffoldingCycleLeft>(entity =>
        {
            entity.ToTable("scaffolding_cycle_left");
            entity.HasKey(value => value.Id);
            entity
                .HasOne<SafeMigrationScaffoldingCycleRight>()
                .WithMany()
                .HasForeignKey(value => value.RightId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<SafeMigrationScaffoldingCycleRight>(entity =>
        {
            entity.ToTable("scaffolding_cycle_right");
            entity.HasKey(value => value.Id);
            entity
                .HasOne<SafeMigrationScaffoldingCycleLeft>()
                .WithMany()
                .HasForeignKey(value => value.LeftId)
                .OnDelete(DeleteBehavior.NoAction);
        });
    }
}

/// <summary>
/// Verifies Strict SafeMigrations Scaffolding Db Context contracts in independently owned test state.
/// </summary>
public sealed class StrictSafeMigrationScaffoldingDbContext : SafeMigrationScaffoldingDbContext
{
    /// <summary>Initializes the fixture with its configured dependencies and ownership boundary.</summary>
    public StrictSafeMigrationScaffoldingDbContext(
        string connectionString
    ) : base(connectionString, SafeMigrationScaffoldingMode.Strict) { }
}

/// <summary>
/// Verifies Legacy SafeMigrations Scaffolding Db Context contracts in independently owned test state.
/// </summary>
public sealed class LegacySafeMigrationScaffoldingDbContext : SafeMigrationScaffoldingDbContext
{
    /// <summary>Initializes the fixture with its configured dependencies and ownership boundary.</summary>
    public LegacySafeMigrationScaffoldingDbContext(
        string connectionString
    ) : base(connectionString, SafeMigrationScaffoldingMode.LegacyConvergence) { }
}

/// <summary>Verifies SafeMigrations Scaffolding User contracts in independently owned test state.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SafeMigrationScaffoldingUser
{
    /// <summary>Gets or sets the mapped id fixture value.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the mapped tenant id fixture value.</summary>
    public int TenantId { get; set; }

    /// <summary>Gets or sets the mapped email fixture value.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Gets or sets the mapped request id fixture value.</summary>
    public int? RequestId { get; set; }

    /// <summary>Gets or sets the mapped request metadata fixture value.</summary>
    public string? RequestMetadata { get; set; }

    /// <summary>Gets or sets the mapped request fixture value.</summary>
    public SafeMigrationScaffoldingRequest? Request { get; set; }
}

/// <summary>Verifies SafeMigrations Scaffolding Request contracts in independently owned test state.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SafeMigrationScaffoldingRequest
{
    /// <summary>Gets or sets the mapped id fixture value.</summary>
    public int Id { get; set; }
}

/// <summary>Verifies SafeMigrations Scaffolding Work Item contracts in independently owned test state.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public abstract class SafeMigrationScaffoldingWorkItem
{
    /// <summary>Gets or sets the mapped id fixture value.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the mapped caption fixture value.</summary>
    public string Caption { get; set; } = string.Empty;
}

/// <summary>Verifies SafeMigrations Scaffolding Task contracts in independently owned test state.</summary>
public sealed class SafeMigrationScaffoldingTask : SafeMigrationScaffoldingWorkItem;

/// <summary>
/// Verifies SafeMigrations Scaffolding External Work Item contracts in independently owned test state.
/// </summary>
public sealed class SafeMigrationScaffoldingExternalWorkItem : SafeMigrationScaffoldingWorkItem;

/// <summary>Defines distinct mapped work-item values used to qualify scaffolding and conversion.</summary>
public enum SafeMigrationScaffoldingWorkItemKind
{
    /// <summary>Retains this distinct fixture value and its configured serialization contract.</summary>
    Task,

    /// <summary>Retains this distinct fixture value and its configured serialization contract.</summary>
    External,
}

/// <summary>Verifies SafeMigrations Attributed Work Item contracts in independently owned test state.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public abstract class SafeMigrationAttributedWorkItem
{
    /// <summary>Gets or sets the mapped id fixture value.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the mapped caption fixture value.</summary>
    public string Caption { get; set; } = string.Empty;
}

/// <summary>Verifies SafeMigrations Json Named Work Item contracts in independently owned test state.</summary>
public sealed class SafeMigrationJsonNamedWorkItem : SafeMigrationAttributedWorkItem;

/// <summary>Verifies SafeMigrations Contract Named Work Item contracts in independently owned test state.</summary>
public sealed class SafeMigrationContractNamedWorkItem : SafeMigrationAttributedWorkItem;

/// <summary>Verifies SafeMigrations Fallback Named Work Item contracts in independently owned test state.</summary>
public sealed class SafeMigrationFallbackNamedWorkItem : SafeMigrationAttributedWorkItem;

/// <summary>Verifies SafeMigrations Scaffolding Cycle Left contracts in independently owned test state.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SafeMigrationScaffoldingCycleLeft
{
    /// <summary>Gets or sets the mapped id fixture value.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the mapped right id fixture value.</summary>
    public int? RightId { get; set; }
}

/// <summary>Verifies SafeMigrations Scaffolding Cycle Right contracts in independently owned test state.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SafeMigrationScaffoldingCycleRight
{
    /// <summary>Gets or sets the mapped id fixture value.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the mapped left id fixture value.</summary>
    public int? LeftId { get; set; }
}

/// <summary>Defines distinct mapped work-item values used to qualify scaffolding and conversion.</summary>
public enum SafeMigrationAttributedWorkItemKind
{
    /// <summary>Retains this distinct fixture value and its configured serialization contract.</summary>
    [JsonStringEnumMemberName("json-named")]
    JsonNamed,

    /// <summary>Retains this distinct fixture value and its configured serialization contract.</summary>
    [EnumMember(Value = "contract-named")]
    ContractNamed,

    /// <summary>Retains this distinct fixture value and its configured serialization contract.</summary>
    Fallback,
}

/// <summary>
/// Verifies Strict SafeMigrations Scaffolding Db Context Factory contracts in independently owned test state.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class StrictSafeMigrationScaffoldingDbContextFactory
    : IDesignTimeDbContextFactory<StrictSafeMigrationScaffoldingDbContext>
{
    /// <summary>Create Db Context.</summary>
    public StrictSafeMigrationScaffoldingDbContext CreateDbContext(
        string[] args
    )
    {
        ArgumentNullException.ThrowIfNull(args);

        return new StrictSafeMigrationScaffoldingDbContext(
            PostgreSqlDesignTimeContextConfiguration.ConnectionString());
    }
}

/// <summary>
/// Verifies Legacy SafeMigrations Scaffolding Db Context Factory contracts in independently owned test state.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class LegacySafeMigrationScaffoldingDbContextFactory
    : IDesignTimeDbContextFactory<LegacySafeMigrationScaffoldingDbContext>
{
    /// <summary>Create Db Context.</summary>
    public LegacySafeMigrationScaffoldingDbContext CreateDbContext(
        string[] args
    )
    {
        ArgumentNullException.ThrowIfNull(args);

        return new LegacySafeMigrationScaffoldingDbContext(
            PostgreSqlDesignTimeContextConfiguration.ConnectionString());
    }
}

/// <summary>
/// Verifies SafeMigrations Data Transition Scaffolding Db Context contracts in independently owned test state.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public abstract class SafeMigrationDataTransitionScaffoldingDbContext : DbContext
{
    private readonly string _connectionString;
    private readonly SafeMigrationScaffoldingMode _mode;

    /// <summary>Initializes the fixture with its configured dependencies and ownership boundary.</summary>
    protected SafeMigrationDataTransitionScaffoldingDbContext(
        string connectionString,
        SafeMigrationScaffoldingMode mode
    )
    {
        _connectionString = connectionString;
        _mode = mode;
    }

    /// <summary>Exposes the mapped users used by scaffolding and ownership qualification.</summary>
    public DbSet<SafeMigrationDataTransitionUser> Users => Set<SafeMigrationDataTransitionUser>();

    /// <inheritdoc />
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

    /// <inheritdoc />
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

            entity.HasData(
                targetState
                    ?
                    [
                        new SafeMigrationDataTransitionUser
                        {
                            Id = 1,
                            Email = "owner@example.test",
                        },
                        new SafeMigrationDataTransitionUser
                        {
                            Id = 3,
                            Email = "auditor@example.test",
                        },
                    ]
                    :
                    [
                        new SafeMigrationDataTransitionUser
                        {
                            Id = 1,
                            Email = "administrator@example.test",
                        },
                        new SafeMigrationDataTransitionUser
                        {
                            Id = 2,
                            Email = "member@example.test",
                        },
                    ]);
        });

        // WHY: EF can interpret a removed and added column on the same table
        // as a rename. A separate persistent table proves the DropColumn path.
        modelBuilder.Entity<SafeMigrationDataTransitionAuxiliary>(entity =>
        {
            entity.ToTable("scaffolding_transition_auxiliary");
            entity.HasKey(value => value.Id);

            if (!targetState)
            {
                entity.Property<int?>("LegacyCode");
            }
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

/// <summary>
/// Verifies Strict SafeMigrations Data Transition Scaffolding Db Context contracts in independently owned test
/// state.
/// </summary>
public sealed class StrictSafeMigrationDataTransitionScaffoldingDbContext
    : SafeMigrationDataTransitionScaffoldingDbContext
{
    /// <summary>Initializes the fixture with its configured dependencies and ownership boundary.</summary>
    public StrictSafeMigrationDataTransitionScaffoldingDbContext(
        string connectionString
    ) : base(connectionString, SafeMigrationScaffoldingMode.Strict) { }
}

/// <summary>
/// Verifies Legacy SafeMigrations Data Transition Scaffolding Db Context contracts in independently owned test
/// state.
/// </summary>
public sealed class LegacySafeMigrationDataTransitionScaffoldingDbContext
    : SafeMigrationDataTransitionScaffoldingDbContext
{
    /// <summary>Initializes the fixture with its configured dependencies and ownership boundary.</summary>
    public LegacySafeMigrationDataTransitionScaffoldingDbContext(
        string connectionString
    ) : base(connectionString, SafeMigrationScaffoldingMode.LegacyConvergence) { }
}

/// <summary>Verifies SafeMigrations Data Transition User contracts in independently owned test state.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SafeMigrationDataTransitionUser
{
    /// <summary>Gets or sets the mapped id fixture value.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the mapped email fixture value.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Gets or sets the mapped request id fixture value.</summary>
    public int? RequestId { get; set; }

    /// <summary>Gets or sets the mapped request metadata fixture value.</summary>
    public string? RequestMetadata { get; set; }

    /// <summary>Gets or sets the mapped request fixture value.</summary>
    public SafeMigrationDataTransitionRequest? Request { get; set; }
}

/// <summary>Verifies SafeMigrations Data Transition Request contracts in independently owned test state.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SafeMigrationDataTransitionRequest
{
    /// <summary>Gets or sets the mapped id fixture value.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the mapped caption fixture value.</summary>
    public string Caption { get; set; } = string.Empty;
}

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed class SafeMigrationDataTransitionAuxiliary
{
    public int Id { get; set; }
}

/// <summary>
/// Verifies Strict SafeMigrations Data Transition Scaffolding Db Context Factory contracts in independently owned
/// test state.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class StrictSafeMigrationDataTransitionScaffoldingDbContextFactory
    : IDesignTimeDbContextFactory<StrictSafeMigrationDataTransitionScaffoldingDbContext>
{
    /// <summary>Create Db Context.</summary>
    public StrictSafeMigrationDataTransitionScaffoldingDbContext CreateDbContext(
        string[] args
    )
    {
        ArgumentNullException.ThrowIfNull(args);

        return new StrictSafeMigrationDataTransitionScaffoldingDbContext(
            PostgreSqlDesignTimeContextConfiguration.ConnectionString());
    }
}

/// <summary>
/// Verifies Legacy SafeMigrations Data Transition Scaffolding Db Context Factory contracts in independently owned
/// test state.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class LegacySafeMigrationDataTransitionScaffoldingDbContextFactory
    : IDesignTimeDbContextFactory<LegacySafeMigrationDataTransitionScaffoldingDbContext>
{
    /// <summary>Create Db Context.</summary>
    public LegacySafeMigrationDataTransitionScaffoldingDbContext CreateDbContext(
        string[] args
    )
    {
        ArgumentNullException.ThrowIfNull(args);

        return new LegacySafeMigrationDataTransitionScaffoldingDbContext(
            PostgreSqlDesignTimeContextConfiguration.ConnectionString());
    }
}
