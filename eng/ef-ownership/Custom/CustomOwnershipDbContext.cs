namespace Doka.EntityFrameworkCore.SafeMigrations.EfOwnership;

/// <summary>
/// Models an instance-owned migration lineage which extends, but does not own,
/// the shared Core schema and model-managed data.
/// </summary>
public sealed class CustomOwnershipDbContext : CoreOwnershipDbContext
{
    /// <summary>Initializes the instance-owned context.</summary>
    /// <param name="options">The provider and custom migration-lineage options.</param>
    public CustomOwnershipDbContext(
        DbContextOptions<CustomOwnershipDbContext> options
    ) : base(options) { }

    /// <inheritdoc />
    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);
        ExcludeCurrentModelFromMigrations(modelBuilder);
        modelBuilder.ApplyConfiguration(new CustomProfileConfiguration(UsesTargetSeedState()));
    }
}

/// <summary>Represents data owned by one instance-specific migration lineage.</summary>
public sealed class CustomOwnershipProfile
{
    /// <summary>Gets or sets the stable profile identifier.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the owning shared role identifier.</summary>
    public int CoreRoleId { get; set; }

    /// <summary>Gets or sets the profile name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the shared role used by this custom profile.</summary>
    public CoreOwnershipRole CoreRole { get; set; } = null!;
}

/// <summary>Maps instance-owned profiles without altering the inherited migration lineage.</summary>
internal sealed class CustomProfileConfiguration : IEntityTypeConfiguration<CustomOwnershipProfile>
{
    private readonly bool _targetState;

    /// <summary>Captures the requested immutable seed-state selection.</summary>
    /// <param name="targetState">Whether to use the target rather than source rows.</param>
    public CustomProfileConfiguration(
        bool targetState
    )
    {
        _targetState = targetState;
    }

    /// <inheritdoc />
    public void Configure(
        EntityTypeBuilder<CustomOwnershipProfile> builder
    )
    {
        builder.ToTable("ownership_custom_profiles");
        builder.HasKey(profile => profile.Id);
        builder
            .Property(profile => profile.Name)
            .HasMaxLength(64)
            .IsRequired();

        builder
            .HasOne(profile => profile.CoreRole)
            .WithMany()
            .HasForeignKey(profile => profile.CoreRoleId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired();

        builder.HasData(
            _targetState
                ? TargetRows()
                : SourceRows());
    }

    private static CustomOwnershipProfile[] SourceRows() =>
    [
        new()
        {
            Id = 10,
            CoreRoleId = 1,
            Name = "default",
        },
        new()
        {
            Id = 11,
            CoreRoleId = 1,
            Name = "legacy",
        },
    ];

    private static CustomOwnershipProfile[] TargetRows() =>
    [
        new()
        {
            Id = 10,
            CoreRoleId = 1,
            Name = "premium",
        },
        new()
        {
            Id = 12,
            CoreRoleId = 3,
            Name = "audit",
        },
    ];
}
