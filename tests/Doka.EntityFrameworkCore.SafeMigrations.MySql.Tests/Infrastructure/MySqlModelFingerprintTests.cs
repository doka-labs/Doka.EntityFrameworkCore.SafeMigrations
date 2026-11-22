namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Protects stable canonical fingerprints and provider-specific relational facet sensitivity.</summary>
public sealed class MySqlModelFingerprintTests
{
    private const string ProviderContract = "Doka.EntityFrameworkCore.MySql";

    /// <summary>
    /// Requires relationally identical models to have the same fingerprint despite declaration order.
    /// </summary>
    [Fact]
    public void Create_IsStableAcrossRelationalDeclarationOrder()
    {
        // Arrange
        using var first = new FirstOrderContext();
        using var second = new SecondOrderContext();

        // Act
        var firstFingerprint = Fingerprint(first);
        var secondFingerprint = Fingerprint(second);

        // Assert
        Assert.Equal(firstFingerprint, secondFingerprint);
        Assert.StartsWith(
            "safe-relational-model:v1:Doka.EntityFrameworkCore.MySql:sha256:",
            firstFingerprint,
            StringComparison.Ordinal);

        Assert.Equal(64, firstFingerprint[(firstFingerprint.LastIndexOf(':') + 1)..].Length);

        // WHY: The unchanged v1 encoding includes the inverse-navigation convention dictionary's
        // generic type identity. Its framework assembly version advances from 10 to 11, not its schema facets.
        Assert.Equal(
            "safe-relational-model:v1:Doka.EntityFrameworkCore.MySql:sha256:"
            + "4fc050cbd0bd149d68235ae361c401999d0e0dcbadb8f81d05f040ea7f8dc20a",
            firstFingerprint);
    }

    /// <summary>Changes the fingerprint when a provider-specific relational facet changes.</summary>
    [Fact]
    public void Create_ChangesForMySqlRelationalFacets()
    {
        // Arrange
        using var baseline = new FirstOrderContext();
        using var changed = new ChangedFacetContext();

        // Act
        var baselineFingerprint = Fingerprint(baseline);
        var changedFingerprint = Fingerprint(changed);

        // Assert
        Assert.NotEqual(baselineFingerprint, changedFingerprint);
    }

    /// <summary>Fingerprints nested JSON containers without requiring nonexistent scalar-property mappings.</summary>
    [Fact]
    public void Create_HandlesNestedJsonContainerWithoutScalarPropertyMappings()
    {
        // Arrange
        using var context = new JsonArtifactContext();
        var model = context.GetService<IDesignTimeModel>()
            .Model;

        var jsonColumn = model
            .GetRelationalModel()
            .Tables
            .Single(static table => table.Name == "json_artifacts")
            .Columns
            .Single(static column => column.Name == "payload");

        // Act
        var first = SafeMigrationModelFingerprint.Create(model, ProviderContract);
        var second = SafeMigrationModelFingerprint.Create(model, ProviderContract);

        // Assert
        Assert.Empty(jsonColumn.PropertyMappings);
        Assert.Equal(first, second);
        Assert.StartsWith(
            "safe-relational-model:v1:Doka.EntityFrameworkCore.MySql:sha256:",
            first,
            StringComparison.Ordinal);
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

    private sealed class JsonArtifactContext : DbContext
    {
        protected override void OnConfiguring(
            DbContextOptionsBuilder optionsBuilder
        ) => optionsBuilder.UseMySql(
            "Server=localhost;Database=fingerprint;User ID=test;Password=test",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));

        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            modelBuilder.Entity<JsonArtifact>(entity =>
            {
                entity.ToTable("json_artifacts");
                entity.HasKey(static artifact => artifact.Id);
                entity.OwnsOne(
                    static artifact => artifact.Payload,
                    owned =>
                    {
                        owned.ToJson("payload");
                        owned.OwnsOne(static payload => payload.Details);
                    });
            });
        }
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class Alpha
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class Beta
    {
        public int Id { get; set; }

        public bool Enabled { get; set; }
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class JsonArtifact
    {
        public int Id { get; set; }

        public JsonArtifactPayload Payload { get; set; } = new();
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class JsonArtifactPayload
    {
        public string Name { get; set; } = string.Empty;

        public JsonArtifactDetails Details { get; } = new();
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class JsonArtifactDetails
    {
        public int Revision { get; set; }
    }
}
