namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies EF11's added assembly contract without bypassing canonical ownership.</summary>
public sealed class MySqlEf11MigrationAssemblyTests
{
    /// <summary>EF11 file-scoped output is not reindented or materialized a second time.</summary>
    /// <param name="newline">The exact provider-owned source newline.</param>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void FileScopedProviderSourceIsPreservedUnchanged(
        string newline
    )
    {
        // Arrange
        var source = string.Join(newline,
            "using Microsoft.EntityFrameworkCore.Migrations;", "", "namespace Probe;", "", "class M { }", "");

        // Act
        var result = SafeMigrationCSharpMigrationsGenerator.UseFileScopedNamespace(source, "Probe");

        // Assert
        Assert.Same(source, result);
    }

    /// <summary>Accepting native file-scoped output cannot hide an ambiguous or wrong namespace.</summary>
    /// <param name="source">The deliberately invalid provider output.</param>
    [Theory]
    [InlineData("namespace Probe;\nnamespace Probe;\nclass M { }\n")]
    [InlineData("namespace Probe;\nnamespace Probe\n{\n}\n")]
    [InlineData("namespace Wrong;\nclass M { }\n")]
    [InlineData("namespace ProbeExtra;\nclass M { }\n")]
    public void AmbiguousOrWrongProviderNamespaceRemainsRejected(
        string source
    )
    {
        // Arrange
        const string expectedNamespace = "Probe";

        // Act
        var failure = Record.Exception(() =>
            SafeMigrationCSharpMigrationsGenerator.UseFileScopedNamespace(source, expectedNamespace));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
    }

    /// <summary>The EF11 snapshot identity reaches the actual provider renderer unchanged.</summary>
    /// <param name="latestMigrationId">The optional identity introduced by EF11's framework signature.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("20261118000001_First")]
    public void SnapshotDelegationPreservesLatestMigrationIdentity(
        string? latestMigrationId
    )
    {
        // Arrange
        using var services = CreateServices();
        using var context = CreateContext(services);
        var designServices = new ServiceCollection();
        designServices.AddEntityFrameworkDesignTimeServices();
        designServices.AddDbContextDesignTimeServices(context);
        new MySqlDesignTimeServices().ConfigureDesignTimeServices(designServices);
        using var designProvider = designServices.BuildServiceProvider();
        var providerGenerator = designProvider.GetRequiredService<IMigrationsCodeGeneratorSelector>().Select("C#");
        var generator = new SafeMigrationCSharpMigrationsGenerator(
            providerGenerator,
            designProvider.GetRequiredService<ICSharpHelper>(),
            new SafeMigrationScaffoldingConfiguration(true, SafeMigrationScaffoldingMode.Strict));

        var model = context.GetService<IDesignTimeModel>().Model;
        var expected = providerGenerator.GenerateSnapshot(
            "Ef11AssemblyProbe", typeof(CanonicalContext), "ProbeSnapshot", model, latestMigrationId);

        // Act
        var actual = generator.GenerateSnapshot(
            "Ef11AssemblyProbe", typeof(CanonicalContext), "ProbeSnapshot", model, latestMigrationId);

        // Assert
        Assert.Equal(expected, actual);
        if (latestMigrationId is not null)
        {
            Assert.Contains(latestMigrationId, actual, StringComparison.Ordinal);
        }
    }

    /// <summary>Compiles both real provider partials without losing timestamp uniqueness or history identity.</summary>
    [Fact]
    public void CanonicalMigrationTypeNamesPreserveDistinctTimestampAndHistoryIdentities()
    {
        // Arrange
        using var services = CreateServices();
        using var context = CreateContext(services);
        var designServices = new ServiceCollection();
        designServices.AddEntityFrameworkDesignTimeServices();
        designServices.AddDbContextDesignTimeServices(context);
        new MySqlDesignTimeServices().ConfigureDesignTimeServices(designServices);
        using var designProvider = designServices.BuildServiceProvider();
        var providerGenerator = designProvider.GetRequiredService<IMigrationsCodeGeneratorSelector>().Select("C#");
        var generator = new SafeMigrationCSharpMigrationsGenerator(
            providerGenerator,
            designProvider.GetRequiredService<ICSharpHelper>(),
            new SafeMigrationScaffoldingConfiguration(true, SafeMigrationScaffoldingMode.Strict));

        var model = context.GetService<IDesignTimeModel>().Model;
        MigrationOperation[] operations = [new SafeMigrationDesignTimeServicesRequiredOperation()];
        const string firstId = "20261121021957_AddColumn";
        const string secondId = "20261121021958_AddColumn";

        // Act
        var firstSource = generator.GenerateMigration("Ef11AssemblyProbe", firstId, operations, operations);
        var firstMetadata = generator.GenerateMetadata(
            "Ef11AssemblyProbe", typeof(CanonicalContext), firstId, firstId, model);

        var secondSource = generator.GenerateMigration("Ef11AssemblyProbe", secondId, operations, operations);
        var secondMetadata = generator.GenerateMetadata(
            "Ef11AssemblyProbe", typeof(CanonicalContext), secondId, secondId, model);

        var assembly = MySqlGeneratedMigrationCompiler.Compile(
            firstSource, firstMetadata, secondSource, secondMetadata);

        var migrations = assembly.DefinedTypes
            .Where(static type => typeof(Migration).IsAssignableFrom(type))
            .OrderBy(static type => type.Name, StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.Equal(
            ["Migration20261121021957AddColumn", "Migration20261121021958AddColumn"],
            migrations.Select(static type => type.Name));

        Assert.Equal(
            [firstId, secondId],
            migrations.Select(static type => type.GetCustomAttribute<MigrationAttribute>()?.Id));

        Assert.All(migrations, static type => Assert.DoesNotContain('_', type.Name));
    }

    /// <summary>Late registration replaces previously cached empty migration and snapshot views.</summary>
    [Fact]
    public void AdditionalAssemblyInvalidatesPreviouslyEmptyViews()
    {
        // Arrange
        using var services = CreateServices();
        using var context = CreateContext(services);
        var migrations = context.GetService<IMigrationsAssembly>();
        var before = migrations.Migrations;
        var beforeSnapshot = migrations.ModelSnapshot;
        var additional = CompileAssembly("20261118000001_First", "First");

        // Act
        migrations.AddMigrations(additional);
        var after = migrations.Migrations;
        var afterSnapshot = migrations.ModelSnapshot;

        // Assert
        Assert.Empty(before);
        Assert.Null(beforeSnapshot);
        Assert.Single(after);
        Assert.NotSame(before, after);
        Assert.Equal("First", afterSnapshot?.Model.FindAnnotation("Ef11Probe")?.Value);
        Assert.Equal(typeof(MySqlEf11MigrationAssemblyTests).Assembly, migrations.Assembly);
        Assert.Equal("20261118000001_First", migrations.FindMigrationId("First"));
    }

    /// <summary>
    /// The latest assembly supplies the snapshot while every distinct migration remains discoverable.
    /// </summary>
    [Fact]
    public void LatestAssemblySuppliesSnapshotWithoutDiscardingEarlierMigrations()
    {
        // Arrange
        using var services = CreateServices();
        using var context = CreateContext(services);
        var migrations = context.GetService<IMigrationsAssembly>();
        migrations.AddMigrations(CompileAssembly("20261118000001_First", "First"));
        var earlierSnapshot = migrations.ModelSnapshot;
        var later = CompileAssembly("20261118000002_Second", "Second");

        // Act
        migrations.AddMigrations(later);
        var after = migrations.ModelSnapshot;

        // Assert
        Assert.Equal(2, migrations.Migrations.Count);
        Assert.Equal("Second", after?.Model.FindAnnotation("Ef11Probe")?.Value);
        Assert.NotSame(earlierSnapshot, after);
    }

    /// <summary>A missing latest snapshot never silently falls back to a historical model.</summary>
    [Fact]
    public void LatestAssemblyWithoutSnapshotDoesNotReuseHistoricalSnapshot()
    {
        // Arrange
        using var services = CreateServices();
        using var context = CreateContext(services);
        var migrations = context.GetService<IMigrationsAssembly>();
        migrations.AddMigrations(CompileAssembly("20261118000001_First", "First"));
        var earlierSnapshot = migrations.ModelSnapshot;
        var later = CompileAssembly("20261118000002_Second", snapshot: null);

        // Act
        migrations.AddMigrations(later);
        var after = migrations.ModelSnapshot;

        // Assert
        Assert.NotNull(earlierSnapshot);
        Assert.Null(after);
        Assert.Equal(2, migrations.Migrations.Count);
    }

    /// <summary>Re-registering an assembly selects it last without duplicating its migration identities.</summary>
    [Fact]
    public void RepeatedAssemblyRegistrationRetainsOneInventoryAndSelectsItsSnapshotLast()
    {
        // Arrange
        using var services = CreateServices();
        using var context = CreateContext(services);
        var migrations = context.GetService<IMigrationsAssembly>();
        var first = CompileAssembly("20261118000001_First", "First");
        migrations.AddMigrations(first);
        migrations.AddMigrations(CompileAssembly("20261118000002_Second", "Second"));

        // Act
        migrations.AddMigrations(first);
        var after = migrations.Migrations;

        // Assert
        Assert.Equal(2, after.Count);
        Assert.Equal("First", migrations.ModelSnapshot?.Model.FindAnnotation("Ef11Probe")?.Value);
    }

    /// <summary>The original configured assembly can be registered without duplicating its migration types.</summary>
    [Fact]
    public void ConfiguredAssemblyRegistrationRetainsItsOriginalInventory()
    {
        // Arrange
        using var services = CreateServices();
        var initial = CompileAssembly("20261118000001_First", "First");
        using var context = CreateContext(services, initial);
        var migrations = context.GetService<IMigrationsAssembly>();
        var before = migrations.Migrations;

        // Act
        migrations.AddMigrations(initial);
        var after = migrations.Migrations;

        // Assert
        Assert.Single(before);
        Assert.Single(after);
        Assert.Equal(before.Keys, after.Keys);
        Assert.Equal("First", migrations.ModelSnapshot?.Model.FindAnnotation("Ef11Probe")?.Value);
    }

    /// <summary>A derived runtime context cannot adopt migrations or snapshots owned by that derived type.</summary>
    [Fact]
    public void AdditionalAssemblyPreservesCanonicalContextBoundary()
    {
        // Arrange
        using var services = CreateServices();
        using var context = CreateContext(services);
        var migrations = context.GetService<IMigrationsAssembly>();
        var foreign = CompileAssembly("20261118000001_Foreign", "Foreign", derivedOwner: true);

        // Act
        migrations.AddMigrations(foreign);
        var after = migrations.Migrations;
        var snapshot = migrations.ModelSnapshot;

        // Assert
        Assert.Empty(after);
        Assert.Null(snapshot);
    }

    /// <summary>Different assemblies cannot silently replace an existing migration with the same identity.</summary>
    [Fact]
    public void ConflictingMigrationIdentitiesRemainRejected()
    {
        // Arrange
        using var services = CreateServices();
        using var context = CreateContext(services);
        var migrations = context.GetService<IMigrationsAssembly>();
        migrations.AddMigrations(CompileAssembly("20261118000001_Duplicate", "First"));
        migrations.AddMigrations(CompileAssembly("20261118000001_Duplicate", "Second"));

        // Act
        var failure = Record.Exception(() => migrations.Migrations);

        // Assert
        Assert.IsType<ArgumentException>(failure);
    }

    /// <summary>Rejecting a null registration preserves previously cached successful observations.</summary>
    [Fact]
    public void NullAssemblyDoesNotInvalidateExistingViews()
    {
        // Arrange
        using var services = CreateServices();
        using var context = CreateContext(services);
        var migrations = context.GetService<IMigrationsAssembly>();
        migrations.AddMigrations(CompileAssembly("20261118000001_First", "First"));
        var before = migrations.Migrations;
        var beforeSnapshot = migrations.ModelSnapshot;

        // Act
        var failure = Record.Exception(() => migrations.AddMigrations(null!));

        // Assert
        Assert.IsType<ArgumentNullException>(failure);
        Assert.Same(before, migrations.Migrations);
        Assert.Same(beforeSnapshot, migrations.ModelSnapshot);
    }

    /// <summary>Owns the provider root without process-wide model-cache effects between probes.</summary>
    private static ServiceProvider CreateServices() => new ServiceCollection()
        .AddEntityFrameworkDokaMySql()
        .AddEntityFrameworkDokaMySqlSafeMigrations<CanonicalContext>()
        .BuildServiceProvider();

    /// <summary>Uses a derived runtime context with an independently selected migrations assembly.</summary>
    private static DerivedContext CreateContext(
        ServiceProvider services,
        Assembly? migrationsAssembly = null
    )
    {
        var options = new DbContextOptionsBuilder()
            .UseInternalServiceProvider(services)
            .UseMySql(
                "Server=127.0.0.1;Port=1;Database=ef11_offline;User ID=unused;Pooling=false;",
                MySqlServerVersion.MariaDb(new Version(12, 3, 3)),
                provider => provider.MigrationsAssembly(
                    migrationsAssembly ?? typeof(MySqlEf11MigrationAssemblyTests).Assembly))
            .UseMySqlSafeMigrations<CanonicalContext>();

        return new DerivedContext(options.Options);
    }

    /// <summary>Compiles independent migration/snapshot sources against the actual consumer graph.</summary>
    private static Assembly CompileAssembly(
        string migrationId,
        string? snapshot,
        bool derivedOwner = false
    )
    {
        var owner = derivedOwner
            ? nameof(DerivedContext)
            : nameof(CanonicalContext);

        var snapshotSource = snapshot is null
            ? string.Empty
            : $$"""
              [DbContext(typeof(MySqlEf11MigrationAssemblyTests.{{owner}}))]
              public sealed class ProbeSnapshot : ModelSnapshot
              {
                  protected override void BuildModel(ModelBuilder modelBuilder)
                      => modelBuilder.HasAnnotation("Ef11Probe", "{{snapshot}}");
              }
              """;

        return MySqlGeneratedMigrationCompiler.Compile(
            $$"""
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Infrastructure;
            using Microsoft.EntityFrameworkCore.Migrations;
            using Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;
            namespace Ef11AssemblyProbe;
            [DbContext(typeof(MySqlEf11MigrationAssemblyTests.{{owner}}))]
            [Migration("{{migrationId}}")]
            public sealed class ProbeMigration : Migration
            {
                protected override void Up(MigrationBuilder migrationBuilder) { }
                protected override void Down(MigrationBuilder migrationBuilder) { }
            }
            {{snapshotSource}}
            """);
    }

    /// <summary>Owns migration discovery independently of the runtime derived context.</summary>
    public class CanonicalContext : DbContext
    {
        /// <summary>Creates the canonical context with explicit provider and assembly ownership.</summary>
        /// <param name="options">The configured internal root and migration assembly.</param>
        public CanonicalContext(DbContextOptions options) : base(options) { }
    }

    /// <summary>Exercises canonical filtering on a concrete derived runtime context.</summary>
    public sealed class DerivedContext : CanonicalContext
    {
        /// <summary>Creates the runtime context without changing its canonical migration owner.</summary>
        /// <param name="options">The explicitly owned provider configuration.</param>
        public DerivedContext(DbContextOptions options) : base(options) { }
    }
}
