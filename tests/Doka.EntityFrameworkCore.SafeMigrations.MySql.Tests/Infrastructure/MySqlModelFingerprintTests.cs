namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlModelFingerprintTests
{
    private const string ProviderContract = "Doka.EntityFrameworkCore.MySql";

    [Fact]
    public void Create_IsStableAcrossRelationalDeclarationOrder()
    {
        using var first = new FirstOrderContext();
        using var second = new SecondOrderContext();

        var firstFingerprint = Fingerprint(first);
        var secondFingerprint = Fingerprint(second);

        Assert.Equal(firstFingerprint, secondFingerprint);
        Assert.StartsWith(
            "safe-relational-model:v1:Doka.EntityFrameworkCore.MySql:sha256:",
            firstFingerprint,
            StringComparison.Ordinal);
        Assert.Equal(64, firstFingerprint[(firstFingerprint.LastIndexOf(':') + 1)..].Length);
        Assert.Equal(
            "safe-relational-model:v1:Doka.EntityFrameworkCore.MySql:sha256:"
            + "a3d55db66df79776c8a0bda4f637d70c6fb9bb796aff1bcc7bf8afaf86109a98",
            firstFingerprint);
    }

    [Fact]
    public void Create_ChangesForMySqlRelationalFacets()
    {
        using var baseline = new FirstOrderContext();
        using var changed = new ChangedFacetContext();

        Assert.NotEqual(Fingerprint(baseline), Fingerprint(changed));
    }

    private static string Fingerprint(
        DbContext context
    ) => SafeMigrationModelFingerprint.Create(
        context.GetService<IDesignTimeModel>()
            .Model,
        ProviderContract);

    private abstract class FingerprintContext : DbContext
    {
        protected override void OnConfiguring(
            DbContextOptionsBuilder optionsBuilder
        ) => optionsBuilder.UseMySql(
            "Server=localhost;Database=fingerprint;User ID=test;Password=test",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));

        protected static void ConfigureAlpha(
            ModelBuilder modelBuilder,
            int maxLength
        )
        {
            modelBuilder.Entity<Alpha>(entity =>
            {
                entity.ToTable("alpha");
                entity.HasCharSet("utf8mb4");
                entity.HasKey(static value => value.Id);
                entity
                    .Property(static value => value.Name)
                    .HasMaxLength(maxLength)
                    .UseCollation("utf8mb4_bin");
                entity
                    .HasIndex(static value => value.Name)
                    .HasDatabaseName("ix_alpha_name");
            });
        }

        protected static void ConfigureBeta(
            ModelBuilder modelBuilder
        )
        {
            modelBuilder.Entity<Beta>(entity =>
            {
                entity.ToTable("beta");
                entity.HasKey(static value => value.Id);
                entity
                    .Property(static value => value.Enabled)
                    .HasDefaultValue(true);
            });
        }
    }

    private sealed class FirstOrderContext : FingerprintContext
    {
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            ConfigureAlpha(modelBuilder, 100);
            ConfigureBeta(modelBuilder);
        }
    }

    private sealed class SecondOrderContext : FingerprintContext
    {
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            ConfigureBeta(modelBuilder);
            ConfigureAlpha(modelBuilder, 100);
        }
    }

    private sealed class ChangedFacetContext : FingerprintContext
    {
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            ConfigureAlpha(modelBuilder, 101);
            ConfigureBeta(modelBuilder);
        }
    }

    private sealed class Alpha
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class Beta
    {
        public int Id { get; set; }

        public bool Enabled { get; set; }
    }
}
