namespace Doka.EntityFrameworkCore.SafeMigrations.Sample;

internal sealed class SampleDbContext : DbContext
{
    public DbSet<UserRecord> Users => Set<UserRecord>();

    public DbSet<OrderRecord> Orders => Set<OrderRecord>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            throw new InvalidOperationException(
                "SampleDbContext requires provider configuration from the consuming application. " +
                "Configure UseMySql(...) or UseNpgsql(...) together with UseMariaDbSafeMigrations() or UsePostgreSqlSafeMigrations().");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserRecord>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(x => x.Id).HasName("pk_users");
            entity.Property(x => x.Email).HasMaxLength(320).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(200);
        });

        modelBuilder.Entity<OrderRecord>(entity =>
        {
            entity.ToTable("orders");
            entity.HasKey(x => x.Id).HasName("pk_orders");
            entity.Property(x => x.Total).HasPrecision(18, 2);
        });
    }
}
