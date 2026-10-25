namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationScaffoldingTests
{
    /// <summary>Delegates a provider-certified database operation without inventing a safe contract.</summary>
    [Fact]
    public void ProviderAdapter_AllowsCertifiedDatabaseDefaultScaffolding()
    {
        // Arrange
        var generator = CreateOperationGenerator(
            SafeMigrationScaffoldingMode.Strict,
            providerOperationAdapters: [new TestProviderOperationAdapter()]);
        var builder = new IndentedStringBuilder();
        var operation = new AlterDatabaseOperation { Collation = "utf8mb4_bin" };

        // Act
        generator.Generate("migrationBuilder", [operation], builder);

        // Assert
        Assert.Contains("migrationBuilder.AlterDatabase(", builder.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Captures provider-certified index and column operations through safe entry points.</summary>
    [Fact]
    public void ProviderAdapter_AllowsAnnotatedIndexAndDropColumnScaffolding()
    {
        // Arrange
        var generator = CreateOperationGenerator(
            SafeMigrationScaffoldingMode.Strict,
            providerOperationAdapters: [new TestProviderOperationAdapter()]);
        var builder = new IndentedStringBuilder();
        var index = new CreateIndexOperation
        {
            Name = "ix_items_caption",
            Table = "items",
            Columns = ["caption"],
        };
        index["Test:ProviderMetadata"] = 1;

        var drop = new DropColumnOperation { Name = "legacy", Table = "items" };
        drop["Test:ProviderMetadata"] = 1;

        // Act
        generator.Generate("migrationBuilder", [index, drop], builder);

        // Assert
        var source = builder.ToString();

        Assert.Contains("migrationBuilder.CreateIndexIfNotExistsFromModel(", source, StringComparison.Ordinal);
        Assert.Contains("migrationBuilder.DropColumnIfExists(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("migrationBuilder.CreateIndex(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("migrationBuilder.DropColumn(", source, StringComparison.Ordinal);
    }

    /// <summary>Rejects ambiguous provider-adapter registration before source generation.</summary>
    [Fact]
    public void MultipleProviderAdapters_RejectBeforeScaffolding()
    {
        // Arrange
        ISafeMigrationProviderOperationAdapter[] adapters =
        [
            new TestProviderOperationAdapter(),
            new TestProviderOperationAdapter(),
        ];

        // Act
        var exception = Record.Exception(() => CreateOperationGenerator(
            SafeMigrationScaffoldingMode.Strict,
            providerOperationAdapters: adapters));

        // Assert
        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);

        Assert.Contains("at most one provider operation adapter", invalidOperation.Message, StringComparison.Ordinal);
    }

    /// <summary>Decorates operation generation while preserving provider metadata and snapshots.</summary>
    [Fact]
    public void DesignTimeServicesDecorateProviderGeneratorWithoutRewritingProviderArtifacts()
    {
        // Arrange
        var providerGenerator = new TestMigrationsCodeGenerator();
        var services = new ServiceCollection();
        new SafeMigrationDesignTimeServices().ConfigureDesignTimeServices(services);
        services.AddSingleton<ITypeMappingSource, NullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();
        services.Replace(ServiceDescriptor.Singleton<IMigrationsCodeGenerator>(providerGenerator));
        services.AddSingleton<IDbContextOptions>(new DbContextOptionsBuilder().Options);

        using var provider = services.BuildServiceProvider();
        // Act
        var generator = provider
            .GetRequiredService<IMigrationsCodeGeneratorSelector>()
            .Select("C#");

        var metadata = generator.GenerateMetadata(
            "Company.Migrations",
            typeof(SafeMigrationScaffoldingTests),
            "CreateUsers",
            "202608310001_CreateUsers",
            targetModel: null!);

        var snapshot = generator.GenerateSnapshot(
            "Company.Migrations",
            typeof(SafeMigrationScaffoldingTests),
            "ReviewContextModelSnapshot",
            model: null!);

        // Assert
        Assert.NotSame(providerGenerator, generator);
        Assert.Equal(providerGenerator.Language, generator.Language);
        Assert.Equal(providerGenerator.FileExtension, generator.FileExtension);
        Assert.Equal(TestMigrationsCodeGenerator.MetadataSource, metadata);
        Assert.Equal(TestMigrationsCodeGenerator.SnapshotSource, snapshot);
        Assert.Contains("using System;", metadata, StringComparison.Ordinal);
        Assert.Contains("new Guid(", metadata, StringComparison.Ordinal);
        Assert.Contains("using System;", snapshot, StringComparison.Ordinal);
        Assert.Contains("new Guid(", snapshot, StringComparison.Ordinal);
        Assert.Equal(1, providerGenerator.MetadataCallCount);
        Assert.Equal(1, providerGenerator.SnapshotCallCount);
    }

    /// <summary>Defers generator selection until provider services are registered.</summary>
    [Fact]
    public void DesignTimeServicesDeferGeneratorDecorationUntilProviderServicesExist()
    {
        // Arrange
        var services = new ServiceCollection();
        new SafeMigrationDesignTimeServices().ConfigureDesignTimeServices(services);
        services.AddSingleton<ITypeMappingSource, NullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();
        services.Replace(
            ServiceDescriptor.Singleton<IMigrationsCodeGenerator, TestMigrationsCodeGenerator>());
        services.AddSingleton<IDbContextOptions>(new DbContextOptionsBuilder().Options);

        using var provider = services.BuildServiceProvider();
        // Act
        var generator = provider
            .GetRequiredService<IMigrationsCodeGeneratorSelector>()
            .Select(language: null);

        // Assert
        Assert.IsType<SafeMigrationCSharpMigrationsGenerator>(generator);
    }

    /// <summary>Reports a missing provider generator at language selection time.</summary>
    [Fact]
    public void DesignTimeServicesRejectMissingProviderGeneratorAtSelectionTime()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<ITypeMappingSource, NullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();
        services.RemoveAll<IMigrationsCodeGenerator>();
        services.AddSingleton<IDbContextOptions>(new DbContextOptionsBuilder().Options);
        new SafeMigrationDesignTimeServices().ConfigureDesignTimeServices(services);

        using var provider = services.BuildServiceProvider();
        var selector = provider.GetRequiredService<IMigrationsCodeGeneratorSelector>();

        // Act
        var exception = Assert.Throws<OperationException>(() => selector.Select("C#"));

        // Assert
        Assert.Contains("No SafeMigrations code generator", exception.Message, StringComparison.Ordinal);
        Assert.Contains("C#", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Preserves provider language selection when safe scaffolding is disabled.</summary>
    [Fact]
    public void DesignTimeServicesPreserveProviderLanguageWhenScaffoldingIsDisabled()
    {
        // Arrange
        var csharpGenerator = new TestMigrationsCodeGenerator();
        var fsharpGenerator = new TestMigrationsCodeGenerator(language: "F#");
        var services = new ServiceCollection();
        new SafeMigrationDesignTimeServices().ConfigureDesignTimeServices(services);
        services.AddSingleton<ITypeMappingSource, NullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();
        services.RemoveAll<IMigrationsCodeGenerator>();
        services.AddSingleton<IMigrationsCodeGenerator>(csharpGenerator);
        services.AddSingleton<IMigrationsCodeGenerator>(fsharpGenerator);
        services.AddSingleton<IDbContextOptions>(new DbContextOptionsBuilder().Options);

        using var provider = services.BuildServiceProvider();
        var selector = provider.GetRequiredService<IMigrationsCodeGeneratorSelector>();
        // Act
        var generator = selector.Select("F#");

        var source = generator.GenerateMigration(
            "Company.Migrations",
            "CreateUsers",
            upOperations: [],
            downOperations: []);

        // Assert
        Assert.Equal("F#", generator.Language);
        Assert.Equal(TestMigrationsCodeGenerator.MigrationSource, source);
        Assert.Equal(0, csharpGenerator.MigrationCallCount);
        Assert.Equal(1, fsharpGenerator.MigrationCallCount);
    }

    /// <summary>Consumes both design-time guards before delegating operations to the provider.</summary>
    [Fact]
    public void EnabledMigrationsGeneratorConsumesTheGuardBeforeProviderDelegation()
    {
        // Arrange
        var providerGenerator = new TestMigrationsCodeGenerator(
            migrationSource: TestMigrationsCodeGenerator.CSharpMigrationSource);

        var generator = CreateMigrationsGenerator(providerGenerator, isEnabled: true);
        var upOperation = new SqlOperation { Sql = "SELECT 1;" };
        var downOperation = new SqlOperation { Sql = "SELECT 2;" };

        // Act
        var source = generator.GenerateMigration(
            "Company.Migrations",
            "CreateUsers",
            [new SafeMigrationDesignTimeServicesRequiredOperation(), upOperation],
            [new SafeMigrationDesignTimeServicesRequiredOperation(), downOperation]);

        // Assert
        Assert.Contains(
            "using Doka.EntityFrameworkCore.SafeMigrations;",
            source,
            StringComparison.Ordinal);
        Assert.Collection(providerGenerator.LastUpOperations!, operation => Assert.Same(upOperation, operation));
        Assert.Collection(providerGenerator.LastDownOperations!, operation => Assert.Same(downOperation, operation));
    }

    /// <summary>Rejects each invalid guard multiplicity before invoking the provider generator.</summary>
    /// <param name="repeated">Whether the guard is repeated rather than missing.</param>
    /// <param name="down">Whether the invalid guard belongs to Down rather than Up.</param>
    /// <param name="expectedMessage">The exact diagnostic fragment for that invalid multiplicity.</param>
    [Theory]
    [InlineData(false, false, "missing the design-time services guard")]
    [InlineData(true, false, "more than one design-time services guard")]
    [InlineData(false, true, "missing the design-time services guard")]
    [InlineData(true, true, "more than one design-time services guard")]
    public void EnabledMigrationsGeneratorRejectsInvalidGuardCount(
        bool repeated,
        bool down,
        string expectedMessage
    )
    {
        // Arrange
        var providerGenerator = new TestMigrationsCodeGenerator(
            migrationSource: TestMigrationsCodeGenerator.CSharpMigrationSource);

        var generator = CreateMigrationsGenerator(providerGenerator, isEnabled: true);
        var operation = new SqlOperation { Sql = "SELECT 1;" };
        MigrationOperation[] invalidOperations = repeated
            ? [new SafeMigrationDesignTimeServicesRequiredOperation(), operation,
                new SafeMigrationDesignTimeServicesRequiredOperation()]
            : [operation];

        MigrationOperation[] validOperations = [new SafeMigrationDesignTimeServicesRequiredOperation()];

        // Act
        var exception = Record.Exception(() => generator.GenerateMigration(
            "Company.Migrations",
            repeated ? "RepeatedGuard" : "MissingGuard",
            down ? validOperations : invalidOperations,
            down ? invalidOperations : validOperations));

        // Assert
        var invalid = Assert.IsType<InvalidOperationException>(exception);

        Assert.Contains(down ? "Down operations" : "Up operations", invalid.Message, StringComparison.Ordinal);
        Assert.Contains(expectedMessage, invalid.Message, StringComparison.Ordinal);
        Assert.Equal(0, providerGenerator.MigrationCallCount);
    }

    /// <summary>Fails closed when an ordinary generator encounters a required safe-services guard.</summary>
    [Fact]
    public void EfCoreGeneratorRejectsTheGuardWhenSafeMigrationsDesignServicesAreAbsent()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<ITypeMappingSource, NullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();

        using var provider = services.BuildServiceProvider();
        var generator = provider.GetRequiredService<ICSharpMigrationOperationGenerator>();
        var builder = new IndentedStringBuilder();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            generator.Generate(
                "migrationBuilder",
                [new SafeMigrationDesignTimeServicesRequiredOperation()],
                builder));

        // Assert
        Assert.Contains(
            nameof(SafeMigrationDesignTimeServicesRequiredOperation),
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>Rejects a language that enabled safe scaffolding cannot transform.</summary>
    [Fact]
    public void EnabledCodeGeneratorSelectorRejectsUnsupportedLanguage()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<ITypeMappingSource, NullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();

        using var provider = services.BuildServiceProvider();
        var selector = new SafeMigrationMigrationsCodeGeneratorSelector(
            [
                new TestMigrationsCodeGenerator(),
                new TestMigrationsCodeGenerator(language: "F#"),
            ],
            provider.GetRequiredService<ICSharpHelper>(),
            new SafeMigrationScaffoldingConfiguration(
                IsEnabled: true,
                Mode: SafeMigrationScaffoldingMode.Strict));

        // Act
        var exception = Assert.Throws<OperationException>(() => selector.Select("F#"));

        // Assert
        Assert.Contains("No SafeMigrations code generator", exception.Message, StringComparison.Ordinal);
        Assert.Contains("F#", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Activates provider generators registered by implementation type.</summary>
    [Fact]
    public void DesignTimeServicesActivateProviderImplementationTypeRegistration()
    {
        // Arrange
        var services = new ServiceCollection();
        new SafeMigrationDesignTimeServices().ConfigureDesignTimeServices(services);
        services.AddSingleton<ITypeMappingSource, NullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();
        services.Replace(
            ServiceDescriptor.Singleton<IMigrationsCodeGenerator, TestMigrationsCodeGenerator>());
        services.AddSingleton<IDbContextOptions>(new DbContextOptionsBuilder().Options);

        using var provider = services.BuildServiceProvider();
        // Act
        var generator = provider
            .GetRequiredService<IMigrationsCodeGeneratorSelector>()
            .Select("C#");

        var snapshot = generator.GenerateSnapshot(
            "Company.Migrations",
            typeof(SafeMigrationScaffoldingTests),
            "ReviewContextModelSnapshot",
            model: null!);

        // Assert
        Assert.IsType<SafeMigrationCSharpMigrationsGenerator>(generator);
        Assert.Equal(TestMigrationsCodeGenerator.SnapshotSource, snapshot);
    }

    /// <summary>Keeps the last matching provider registration for case-insensitive language selection.</summary>
    [Fact]
    public void CodeGeneratorSelectorPreservesLastLanguageMatch()
    {
        // Arrange
        var firstGenerator = new TestMigrationsCodeGenerator();
        var secondGenerator = new TestMigrationsCodeGenerator();
        var services = new ServiceCollection();
        new SafeMigrationDesignTimeServices().ConfigureDesignTimeServices(services);
        services.AddSingleton<ITypeMappingSource, NullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();
        services.RemoveAll<IMigrationsCodeGenerator>();
        services.AddSingleton<IMigrationsCodeGenerator>(firstGenerator);
        services.AddSingleton<IMigrationsCodeGenerator>(secondGenerator);
        services.AddSingleton<IDbContextOptions>(new DbContextOptionsBuilder().Options);

        using var provider = services.BuildServiceProvider();
        // Act
        var generator = provider
            .GetRequiredService<IMigrationsCodeGeneratorSelector>()
            .Select("c#");

        _ = generator.GenerateSnapshot(
            "Company.Migrations",
            typeof(SafeMigrationScaffoldingTests),
            "ReviewContextModelSnapshot",
            model: null!);

        // Assert
        Assert.Equal(0, firstGenerator.SnapshotCallCount);
        Assert.Equal(1, secondGenerator.SnapshotCallCount);
    }

    /// <summary>Preserves precedence for a legacy generator without a declared language.</summary>
    [Fact]
    public void CodeGeneratorSelectorPreservesLegacyGeneratorPrecedence()
    {
        // Arrange
        var legacyGenerator = new TestMigrationsCodeGenerator(language: null);
        var csharpGenerator = new TestMigrationsCodeGenerator();
        var services = new ServiceCollection();
        new SafeMigrationDesignTimeServices().ConfigureDesignTimeServices(services);
        services.AddSingleton<ITypeMappingSource, NullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();
        services.RemoveAll<IMigrationsCodeGenerator>();
        services.AddSingleton<IMigrationsCodeGenerator>(legacyGenerator);
        services.AddSingleton<IMigrationsCodeGenerator>(csharpGenerator);
        services.AddSingleton<IDbContextOptions>(new DbContextOptionsBuilder().Options);

        using var provider = services.BuildServiceProvider();
        // Act
        var generator = provider
            .GetRequiredService<IMigrationsCodeGeneratorSelector>()
            .Select("C#");

        _ = generator.GenerateMetadata(
            "Company.Migrations",
            typeof(SafeMigrationScaffoldingTests),
            "CreateUsers",
            "202608310001_CreateUsers",
            targetModel: null!);

        // Assert
        Assert.Equal(1, legacyGenerator.MetadataCallCount);
        Assert.Equal(0, csharpGenerator.MetadataCallCount);
    }

    /// <summary>Registers the generator selector once across repeated design-time setup.</summary>
    [Fact]
    public void DesignTimeServicesAreIdempotentBeforeProviderRegistration()
    {
        // Arrange
        var services = new ServiceCollection();
        var designTimeServices = new SafeMigrationDesignTimeServices();

        // Act
        designTimeServices.ConfigureDesignTimeServices(services);
        designTimeServices.ConfigureDesignTimeServices(services);

        // Assert
        Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(IMigrationsCodeGeneratorSelector));
    }
}
