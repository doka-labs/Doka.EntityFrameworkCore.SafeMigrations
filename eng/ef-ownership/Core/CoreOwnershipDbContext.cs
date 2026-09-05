namespace Doka.EntityFrameworkCore.SafeMigrations.EfOwnership;

/// <summary>
/// Models the shared migration lineage which exists in every ownership-fixture
/// installation.
/// </summary>
public class CoreOwnershipDbContext : DbContext
{
    private const string StateVariable = "SAFE_MIGRATIONS_OWNERSHIP_STATE";

    /// <summary>Initializes the shared ownership-fixture context.</summary>
    /// <param name="options">The provider and migration-lineage options.</param>
    public CoreOwnershipDbContext(
        DbContextOptions<CoreOwnershipDbContext> options
    ) : base(options)
    {
    }

    /// <summary>Initializes a derived ownership-fixture context.</summary>
    /// <param name="options">The provider and migration-lineage options.</param>
    protected CoreOwnershipDbContext(
        DbContextOptions options
    ) : base(options)
    {
    }

    /// <inheritdoc />
    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfiguration(new CoreRoleConfiguration(UsesTargetSeedState()));
    }

    /// <summary>
    /// Excludes every table already present in the inherited model from the
    /// derived context's migration lineage.
    /// </summary>
    /// <param name="modelBuilder">The model containing only inherited mappings.</param>
    protected static void ExcludeCurrentModelFromMigrations(
        ModelBuilder modelBuilder
    )
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // WHY: Capture the inherited ownership boundary before the derived
        // context adds its own mappings. Excluding after that point would also
        // exclude the instance-owned tables this lineage must migrate.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.GetTableName() is not null)
            {
                entityType.SetIsTableExcludedFromMigrations(true);
            }

            foreach (var mappingFragment in entityType.GetMappingFragments(StoreObjectType.Table))
            {
                mappingFragment.IsTableExcludedFromMigrations = true;
            }
        }
    }

    /// <summary>Gets whether the fixture should expose its target seed state.</summary>
    protected static bool UsesTargetSeedState() => StringComparer.Ordinal.Equals(
        Environment.GetEnvironmentVariable(StateVariable),
        "target");
}

/// <summary>Represents a row owned exclusively by the shared Core lineage.</summary>
public sealed class CoreOwnershipRole
{
    /// <summary>Gets or sets the stable role identifier.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the role name.</summary>
    public string Name { get; set; } = string.Empty;
}

internal sealed class CoreRoleConfiguration(
    bool targetState
) : IEntityTypeConfiguration<CoreOwnershipRole>
{
    public void Configure(
        EntityTypeBuilder<CoreOwnershipRole> builder
    )
    {
        builder.ToTable("ownership_core_roles");
        builder.HasKey(role => role.Id);
        builder.Property(role => role.Name).HasMaxLength(64).IsRequired();

        builder.HasData(targetState
            ? TargetRows()
            : SourceRows());
    }

    private static CoreOwnershipRole[] SourceRows() =>
    [
        new() { Id = 1, Name = "administrator" },
        new() { Id = 2, Name = "member" },
    ];

    private static CoreOwnershipRole[] TargetRows() =>
    [
        new() { Id = 1, Name = "owner" },
        new() { Id = 3, Name = "auditor" },
    ];
}
