namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Defines the shared structured column, key, and discriminator scaffolding fixture.</summary>
public abstract class SafeMigrationScaffoldingDbContext : DbContext
{
    private readonly string _connectionString;
    private readonly SafeMigrationScaffoldingMode _mode;
    private readonly MySqlServerVersion _serverVersion;

    /// <summary>Initializes the isolated model and explicit provider dependencies.</summary>
    /// <param name="connectionString">The uniquely owned native test database.</param>
    /// <param name="serverVersion">The selected native engine family and version contract.</param>
    /// <param name="mode">The explicitly selected strict or legacy-convergence scaffolding mode.</param>
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

    /// <summary>Gets the dependent user rows configured by this isolated scaffolding model.</summary>
    [UsedImplicitly]
    public DbSet<SafeMigrationScaffoldingUser> Users => Set<SafeMigrationScaffoldingUser>();

    /// <inheritdoc />
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

    /// <inheritdoc />
    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        // WHY: MySQL treats EF's schema metadata as a database qualifier. The
        // generated-tooling gate must therefore exercise the common
        // HasDefaultSchema(current database) migration shape end to end.
        modelBuilder.HasDefaultSchema(new MySqlConnectionStringBuilder(_connectionString).Database);

        // WHY: The CLI gate must compile and execute the same generated-column
        // and CHECK metadata bridge in strict and legacy scaffolding modes.
        modelBuilder.Entity(
            "ScaffoldingGeneratedNode",
            entity =>
            {
                entity.ToTable(
                    "scaffolding_generated_nodes",
                    table =>
                        table.HasCheckConstraint("ck_scaffolding_generated_bounds", "`RightBound` > `LeftBound`"));

                entity
                    .Property<int>("Id")
                    .ValueGeneratedNever();

                entity.HasKey("Id");
                entity.Property<long>("LeftBound");
                entity.Property<long>("RightBound");

                // WHY: The unquoted ID role also exercises MariaDB's native
                // keyword certificate through the actual CLI, script and bundle.
                entity
                    .Property<bool>("HasDescendants")
                    .HasComputedColumnSql(
                        "Id >= 0 AND `RightBound` > CASE WHEN `LeftBound` = 9223372036854775807 "
                        + "THEN `LeftBound` ELSE `LeftBound` + 1 END",
                        stored: false);

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

            entity
                .HasIndex(user => new
                {
                    user.TenantId,
                    user.Email,
                })
                .HasPrefixLength(0, 64);

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

/// <summary>Scaffolds the shared model through strict guarded operation policies.</summary>
public sealed class StrictSafeMigrationScaffoldingDbContext : SafeMigrationScaffoldingDbContext
{
    /// <summary>Initializes the isolated model and explicit provider dependencies.</summary>
    /// <param name="connectionString">The uniquely owned native test database.</param>
    /// <param name="serverVersion">The selected native engine family and version contract.</param>
    public StrictSafeMigrationScaffoldingDbContext(
        string connectionString,
        MySqlServerVersion serverVersion
    ) : base(connectionString, serverVersion, SafeMigrationScaffoldingMode.Strict) { }
}

/// <summary>Scaffolds the shared model through explicit legacy-convergence policies.</summary>
public sealed class LegacySafeMigrationScaffoldingDbContext : SafeMigrationScaffoldingDbContext
{
    /// <summary>Initializes the isolated model and explicit provider dependencies.</summary>
    /// <param name="connectionString">The uniquely owned native test database.</param>
    /// <param name="serverVersion">The selected native engine family and version contract.</param>
    public LegacySafeMigrationScaffoldingDbContext(
        string connectionString,
        MySqlServerVersion serverVersion
    ) : base(connectionString, serverVersion, SafeMigrationScaffoldingMode.LegacyConvergence) { }
}

/// <summary>Defines the dependent user row used to capture column and relationship scaffolding.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SafeMigrationScaffoldingUser
{
    /// <summary>Gets or sets the mapped row identity used by the isolated fixture model.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the tenant key participating in the scaffolded compound ownership contract.</summary>
    public int TenantId { get; set; }

    /// <summary>Gets or sets the mapped email value used by length, uniqueness, and seed-transition probes.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional principal key used by the scaffolded user relationship.</summary>
    public int? RequestId { get; set; }

    /// <summary>Gets or sets the optional request payload used to capture provider column facets.</summary>
    public string? RequestMetadata { get; set; }

    /// <summary>Gets or sets the optional principal navigation represented by the scaffolded foreign key.</summary>
    public SafeMigrationScaffoldingRequest? Request { get; set; }
}

/// <summary>Defines the principal request identity used by the scaffolded relationship.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SafeMigrationScaffoldingRequest
{
    /// <summary>Gets or sets the mapped row identity used by the isolated fixture model.</summary>
    public int Id { get; set; }
}

/// <summary>Defines the base row for numeric discriminator scaffolding.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public abstract class SafeMigrationScaffoldingWorkItem
{
    /// <summary>Gets or sets the mapped row identity used by the isolated fixture model.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the mapped text value used by discriminator and seed-transition probes.</summary>
    public string Caption { get; set; } = string.Empty;
}

/// <summary>Defines the locally owned numeric-discriminator subtype.</summary>
public sealed class SafeMigrationScaffoldingTask : SafeMigrationScaffoldingWorkItem;

/// <summary>Defines the externally owned numeric-discriminator subtype.</summary>
public sealed class SafeMigrationScaffoldingExternalWorkItem : SafeMigrationScaffoldingWorkItem;

/// <summary>Identifies locally and externally owned numeric discriminator values.</summary>
public enum SafeMigrationScaffoldingWorkItemKind
{
    /// <summary>Selects the locally owned task discriminator.</summary>
    Task,

    /// <summary>Selects the externally owned work-item discriminator.</summary>
    External,
}

/// <summary>Defines the base row for attributed string-discriminator scaffolding.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public abstract class SafeMigrationAttributedWorkItem
{
    /// <summary>Gets or sets the mapped row identity used by the isolated fixture model.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the mapped text value used by discriminator and seed-transition probes.</summary>
    public string Caption { get; set; } = string.Empty;
}

/// <summary>Selects the discriminator value declared by JsonStringEnumMemberName.</summary>
public sealed class SafeMigrationJsonNamedWorkItem : SafeMigrationAttributedWorkItem;

/// <summary>Selects the discriminator value declared by EnumMember.</summary>
public sealed class SafeMigrationContractNamedWorkItem : SafeMigrationAttributedWorkItem;

/// <summary>Selects the discriminator value derived from the unannotated enum member.</summary>
public sealed class SafeMigrationFallbackNamedWorkItem : SafeMigrationAttributedWorkItem;

/// <summary>Defines one side of the cyclic optional foreign-key scaffolding fixture.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SafeMigrationScaffoldingCycleLeft
{
    /// <summary>Gets or sets the mapped row identity used by the isolated fixture model.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the optional key referring to the right-hand cyclic principal.</summary>
    public int? RightId { get; set; }
}

/// <summary>Defines the other side of the cyclic optional foreign-key scaffolding fixture.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SafeMigrationScaffoldingCycleRight
{
    /// <summary>Gets or sets the mapped row identity used by the isolated fixture model.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the optional key referring to the left-hand cyclic principal.</summary>
    public int? LeftId { get; set; }
}

/// <summary>Defines JSON-named, contract-named, and fallback string discriminator identities.</summary>
public enum SafeMigrationAttributedWorkItemKind
{
    /// <summary>Uses the explicitly declared JSON string discriminator spelling.</summary>
    [JsonStringEnumMemberName("json-named")]
    JsonNamed,

    /// <summary>Uses the explicitly declared data-contract discriminator spelling.</summary>
    [EnumMember(Value = "contract-named")]
    ContractNamed,

    /// <summary>Uses the unannotated enum member name as the discriminator spelling.</summary>
    Fallback,
}

/// <summary>Creates the strict design-time context for real EF scaffolding probes.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class StrictSafeMigrationScaffoldingDbContextFactory
    : IDesignTimeDbContextFactory<StrictSafeMigrationScaffoldingDbContext>
{
    /// <inheritdoc />
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

/// <summary>Creates the legacy design-time context for real EF scaffolding probes.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class LegacySafeMigrationScaffoldingDbContextFactory
    : IDesignTimeDbContextFactory<LegacySafeMigrationScaffoldingDbContext>
{
    /// <inheritdoc />
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

/// <summary>Defines the shared seeded model used to scaffold managed-data transitions.</summary>
public abstract class SafeMigrationDataTransitionScaffoldingDbContext : DbContext
{
    private readonly string _connectionString;
    private readonly SafeMigrationScaffoldingMode _mode;
    private readonly MySqlServerVersion _serverVersion;

    /// <summary>Initializes the isolated model and explicit provider dependencies.</summary>
    /// <param name="connectionString">The uniquely owned native test database.</param>
    /// <param name="serverVersion">The selected native engine family and version contract.</param>
    /// <param name="mode">The explicitly selected strict or legacy-convergence scaffolding mode.</param>
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

    /// <summary>Gets the dependent user rows configured by this isolated scaffolding model.</summary>
    [UsedImplicitly]
    public DbSet<SafeMigrationDataTransitionUser> Users => Set<SafeMigrationDataTransitionUser>();

    /// <inheritdoc />
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

/// <summary>Captures managed-data transitions under strict scaffolding policy.</summary>
public sealed class StrictSafeMigrationDataTransitionScaffoldingDbContext
    : SafeMigrationDataTransitionScaffoldingDbContext
{
    /// <summary>Initializes the isolated model and explicit provider dependencies.</summary>
    /// <param name="connectionString">The uniquely owned native test database.</param>
    /// <param name="serverVersion">The selected native engine family and version contract.</param>
    public StrictSafeMigrationDataTransitionScaffoldingDbContext(
        string connectionString,
        MySqlServerVersion serverVersion
    ) : base(connectionString, serverVersion, SafeMigrationScaffoldingMode.Strict) { }
}

/// <summary>Captures managed-data transitions under legacy-convergence policy.</summary>
public sealed class LegacySafeMigrationDataTransitionScaffoldingDbContext
    : SafeMigrationDataTransitionScaffoldingDbContext
{
    /// <summary>Initializes the isolated model and explicit provider dependencies.</summary>
    /// <param name="connectionString">The uniquely owned native test database.</param>
    /// <param name="serverVersion">The selected native engine family and version contract.</param>
    public LegacySafeMigrationDataTransitionScaffoldingDbContext(
        string connectionString,
        MySqlServerVersion serverVersion
    ) : base(connectionString, serverVersion, SafeMigrationScaffoldingMode.LegacyConvergence) { }
}

/// <summary>Defines a seeded dependent user row whose values participate in transition capture.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SafeMigrationDataTransitionUser
{
    /// <summary>Gets or sets the mapped row identity used by the isolated fixture model.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the mapped email value used by length, uniqueness, and seed-transition probes.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional principal key used by the scaffolded user relationship.</summary>
    public int? RequestId { get; set; }

    /// <summary>Gets or sets the optional request payload used to capture provider column facets.</summary>
    public string? RequestMetadata { get; set; }

    /// <summary>Gets or sets the optional principal navigation represented by the scaffolded foreign key.</summary>
    public SafeMigrationDataTransitionRequest? Request { get; set; }
}

/// <summary>Defines a seeded principal request row for managed relationship transitions.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SafeMigrationDataTransitionRequest
{
    /// <summary>Gets or sets the mapped row identity used by the isolated fixture model.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the mapped text value used by discriminator and seed-transition probes.</summary>
    public string Caption { get; set; } = string.Empty;
}

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed class SafeMigrationDataTransitionAuxiliary
{
    public int Id { get; set; }
}

/// <summary>Creates strict seeded design-time models for real transition scaffolding.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class StrictSafeMigrationDataTransitionScaffoldingDbContextFactory
    : IDesignTimeDbContextFactory<StrictSafeMigrationDataTransitionScaffoldingDbContext>
{
    /// <inheritdoc />
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

/// <summary>Creates legacy seeded design-time models for real transition scaffolding.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class LegacySafeMigrationDataTransitionScaffoldingDbContextFactory
    : IDesignTimeDbContextFactory<LegacySafeMigrationDataTransitionScaffoldingDbContext>
{
    /// <inheritdoc />
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
