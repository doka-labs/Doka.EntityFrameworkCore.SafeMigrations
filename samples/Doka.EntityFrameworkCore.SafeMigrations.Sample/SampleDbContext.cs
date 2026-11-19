namespace Doka.EntityFrameworkCore.SafeMigrations.Sample;

/// <summary>Provides the canonical model used by the independently configured sample consumer.</summary>
internal sealed class SampleDbContext : DbContext
{
    /// <summary>Creates a sample context with an explicit application-owned provider configuration.</summary>
    /// <param name="options">The configured provider and SafeMigrations services.</param>
    public SampleDbContext(
        DbContextOptions<SampleDbContext> options
    ) : base(options) { }

    public DbSet<UserRecord> Users => Set<UserRecord>();

    public DbSet<OrderRecord> Orders => Set<OrderRecord>();

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        if (!optionsBuilder.IsConfigured)
        {
            throw new InvalidOperationException(
                "SampleDbContext requires provider configuration from the consuming application. "
                + "Configure UseMySql(...), UseNpgsql(...), UseSqlite(...), or UseSqlServer(...) with the matching "
                + "SafeMigrations provider registration.");
        }
    }

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<UserRecord>(entity =>
        {
            entity.ToTable("users");
            entity
                .HasKey(x => x.Id)
                .HasName("pk_users");

            entity
                .Property(x => x.Id)
                .HasColumnName("id");

            entity
                .Property(x => x.Email)
                .HasColumnName("email")
                .HasMaxLength(320)
                .IsRequired();

            entity
                .Property(x => x.DisplayName)
                .HasColumnName("display_name")
                .HasMaxLength(200);

            entity
                .HasAlternateKey(x => x.Email)
                .HasName("ux_users_email");
        });

        modelBuilder.Entity<OrderRecord>(entity =>
        {
            entity.ToTable("orders", table => table.HasCheckConstraint("ck_orders_total_non_negative", "total >= 0"));
            entity
                .HasKey(x => x.Id)
                .HasName("pk_orders");

            entity
                .Property(x => x.Id)
                .HasColumnName("id");

            entity
                .Property(x => x.UserId)
                .HasColumnName("user_id");

            entity
                .Property(x => x.Total)
                .HasColumnName("total")
                .HasPrecision(18, 2);

            entity
                .HasOne<UserRecord>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .HasConstraintName("fk_orders_users_user_id")
                .OnDelete(DeleteBehavior.Cascade);

            entity
                .HasIndex(x => x.UserId)
                .HasDatabaseName("ix_orders_user_id");
        });
    }
}
