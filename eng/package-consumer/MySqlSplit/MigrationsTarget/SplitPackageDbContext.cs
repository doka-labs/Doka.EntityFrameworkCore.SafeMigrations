using System;
using Microsoft.EntityFrameworkCore;

namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.SplitTarget;

/// <summary>
/// Represents the migration-owning context in the split package-consumer
/// qualification fixture.
/// </summary>
public sealed class SplitPackageDbContext(
    DbContextOptions<SplitPackageDbContext> options
) : DbContext(options)
{
    /// <summary>Gets the entities owned by this migration lineage.</summary>
    public DbSet<SplitPackageEntity> Entities => Set<SplitPackageEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<SplitPackageEntity>(entity =>
        {
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Name).HasMaxLength(128).IsRequired();
        });
    }
}

/// <summary>Represents one row owned by the split qualification context.</summary>
public sealed class SplitPackageEntity
{
    /// <summary>Gets or sets the row identifier.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the required display name.</summary>
    public string Name { get; set; } = string.Empty;
}
