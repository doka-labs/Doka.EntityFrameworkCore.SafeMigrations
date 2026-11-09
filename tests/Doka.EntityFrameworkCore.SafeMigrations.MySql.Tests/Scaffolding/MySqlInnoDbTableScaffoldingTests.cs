namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises complete provider-owned table metadata through generated consumer source.</summary>
public sealed class MySqlInnoDbTableScaffoldingTests
{
    /// <summary>The actual provider's table/key removal annotations use the same bounded classifier.</summary>
    /// <param name="kind">The native identity transition whose historical metadata is captured.</param>
    [Theory]
    [InlineData("drop_table")]
    [InlineData("rename_table")]
    [InlineData("drop_primary")]
    [InlineData("drop_unique")]
    public void ActualProviderRemovalAnnotationsRetainTheirIdentityContract(
        string kind
    )
    {
        // Arrange
        using var runtime = CreateRuntimeServices(safeFirst: false);
        using var context = new ExplicitModelContext(CreateOptions(runtime));
        var table = context
            .GetService<IDesignTimeModel>()
            .Model
            .GetRelationalModel()
            .Tables
            .Single();

        var annotations = context.GetService<IMigrationsAnnotationProvider>();
        var captured = kind switch
        {
            "rename_table" => annotations.ForRename(table),
            "drop_primary" or "drop_unique" => annotations.ForRemove(table.PrimaryKey!),
            _ => annotations.ForRemove(table),
        };

        var operation = CreateIdentityOperation(kind);
        foreach (var annotation in captured)
        {
            operation.AddAnnotation(annotation.Name, annotation.Value);
        }

        using var services = CreateServices();
        var generator = CreateGenerator(services, SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();

        // Act
        generator.Generate("migrationBuilder", [operation], builder);
        var normalized = Assert.IsType<SafeMigrationOperation>(Assert.Single(Reload(builder.ToString())
            .Operations));

        // Assert
        Assert.Equal("InnoDB", operation["Doka:MySql:StorageEngine"]);
        Assert.Equal(operation.GetType(),
            SafeMigrationStandardOperationFactory
                .Create(normalized.Intent)
                .GetType());
    }

    /// <summary>Removal annotations are historical facets, not new physical shape requirements.</summary>
    /// <param name="kind">The identity-only provider operation.</param>
    [Theory]
    [InlineData("drop_table")]
    [InlineData("rename_table")]
    [InlineData("drop_primary")]
    [InlineData("drop_unique")]
    public void CertifiedHistoricalTableFacetsPreserveIdentityContracts(
        string kind
    )
    {
        // Arrange
        var operation = CreateIdentityOperation(kind);
        operation.AddAnnotation("Doka:MySql:StorageEngine", "InnoDB");
        operation.AddAnnotation("Doka:MySql:CharSet", "utf8mb4");
        operation.AddAnnotation("Relational:Collation", "utf8mb4_unicode_ci");
        using var services = CreateServices();
        var generator = CreateGenerator(services, SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();

        // Act
        generator.Generate("migrationBuilder", [operation], builder);
        var reloaded = Reload(builder.ToString());
        var safe = Assert.IsType<SafeMigrationOperation>(Assert.Single(reloaded.Operations));
        var reconstructed = SafeMigrationStandardOperationFactory.Create(safe.Intent);

        // Assert
        Assert.Equal(operation.GetType(), reconstructed.GetType());
        Assert.Empty(safe.GetAnnotations());
        Assert.Equal(3,
            operation
                .GetAnnotations()
                .Count());
    }

    /// <summary>Identity certification cannot swallow unknown or invalid inherited metadata.</summary>
    /// <param name="kind">The identity-only provider operation.</param>
    /// <param name="annotation">The unqualified metadata coordinate.</param>
    /// <param name="value">The unsupported metadata value.</param>
    [Theory]
    [InlineData("drop_table", "Other:Engine", "InnoDB")]
    [InlineData("rename_table", "Other:Engine", "InnoDB")]
    [InlineData("drop_primary", "Other:Engine", "InnoDB")]
    [InlineData("drop_unique", "Other:Engine", "InnoDB")]
    [InlineData("drop_table", "Doka:MySql:StorageEngine", "MyISAM")]
    [InlineData("rename_table", "Doka:MySql:StorageEngine", true)]
    [InlineData("drop_primary", "Doka:MySql:CharSet", " ")]
    [InlineData("drop_unique", "Relational:Collation", null)]
    public void UnsupportedHistoricalTableFacetsRemainRejected(
        string kind,
        string annotation,
        object? value
    )
    {
        // Arrange
        var operation = CreateIdentityOperation(kind);
        operation.AddAnnotation("Doka:MySql:StorageEngine", "InnoDB");
        operation.RemoveAnnotation(annotation);
        operation.AddAnnotation(annotation, value);
        using var services = CreateServices();
        var generator = CreateGenerator(services, SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();

        // Act
        var exception = Record.Exception(() => generator.Generate("migrationBuilder", [operation], builder));

        // Assert
        if (kind is "drop_primary" or "drop_unique")
        {
            Assert.IsType<InvalidOperationException>(exception);
        }
        else
        {
            Assert.IsType<NotSupportedException>(exception);
        }

        Assert.Empty(builder.ToString());
    }

    /// <summary>Actual EF scaffolding must compile both migration directions and the historical snapshot.</summary>
    [Fact]
    public void CompleteProviderScaffoldingRetainsEngineAcrossUpDownAndSnapshot()
    {
        // Arrange
        using var runtime = CreateRuntimeServices(safeFirst: false);
        using var context = new ExplicitModelContext(CreateOptions(runtime));

        // Act
        var migration = CompileCompleteMigration(context, out var snapshot);
        var up = migration
            .UpOperations
            .OfType<SafeMigrationOperation>()
            .Single();

        var down = migration
            .DownOperations
            .OfType<SafeMigrationOperation>()
            .Single();

        var snapshotModel = context
            .GetService<IModelRuntimeInitializer>()
            .Initialize(snapshot.Model, designTime: true);

        // Assert
        Assert.IsType<EnsureTableIntent>(up.Intent);
        Assert.IsType<DropTableIntent>(down.Intent);
        Assert.Equal("InnoDB",
            SafeMigrationStandardOperationFactory
                .Create(up.Intent)
                ["Doka:MySql:StorageEngine"]);

        Assert.False(context
            .GetService<IMigrationsModelDiffer>()
            .HasDifferences(
                snapshotModel.GetRelationalModel(),
                context
                    .GetService<IDesignTimeModel>()
                    .Model
                    .GetRelationalModel()));
    }

    /// <summary>Real model annotations survive scaffolding in either runtime-registration order.</summary>
    /// <param name="mode">The selected authoring mode.</param>
    /// <param name="safeFirst">Whether the SafeMigrations runtime services are registered first.</param>
    [Theory]
    [InlineData(SafeMigrationScaffoldingMode.Strict, false)]
    [InlineData(SafeMigrationScaffoldingMode.Strict, true)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, false)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, true)]
    public void RealProviderTableMetadataSurvivesScaffolding(
        SafeMigrationScaffoldingMode mode,
        bool safeFirst
    )
    {
        // Arrange
        using var runtime = CreateRuntimeServices(safeFirst);
        var options = CreateOptions(runtime);
        using var context = new ExplicitModelContext(options);
        var model = context.GetService<IDesignTimeModel>()
            .Model;

        var original = context
            .GetService<IMigrationsModelDiffer>()
            .GetDifferences(source: null, model.GetRelationalModel())
            .OfType<CreateTableOperation>()
            .Single();

        using var services = CreateServices();
        var generator = CreateGenerator(services, mode);
        var sourceBuilder = new IndentedStringBuilder();

        // Act
        generator.Generate("migrationBuilder", [original], sourceBuilder);
        var reloaded = Reload(sourceBuilder.ToString());
        var table = reloaded
            .Operations
            .OfType<SafeMigrationOperation>()
            .Single(candidate => candidate.Intent is EnsureTableIntent);

        var reconstructed = (CreateTableOperation)SafeMigrationStandardOperationFactory.Create(table.Intent);

        // Assert
        Assert.Equal("InnoDB", original["Doka:MySql:StorageEngine"]);
        Assert.Equal("InnoDB", reconstructed["Doka:MySql:StorageEngine"]);
    }

    /// <summary>The existing relational model fingerprint also binds an explicitly configured engine.</summary>
    [Fact]
    public void ModelFingerprintIncludesProviderEngineMetadata()
    {
        // Arrange
        using var runtime = CreateRuntimeServices(safeFirst: false);
        var options = CreateOptions(runtime);
        using var plain = new PlainModelContext(options);
        using var explicitEngine = new ExplicitModelContext(options);

        // Act
        var plainHash = SafeMigrationModelFingerprint.Create(
            plain.GetService<IDesignTimeModel>()
                .Model,
            "Doka.EntityFrameworkCore.MySql");

        var engineHash = SafeMigrationModelFingerprint.Create(
            explicitEngine.GetService<IDesignTimeModel>()
                .Model,
            "Doka.EntityFrameworkCore.MySql");

        // Assert
        Assert.NotEqual(plainHash, engineHash);
    }

    /// <summary>Both authoring modes retain an explicit transactional engine after compilation.</summary>
    /// <param name="mode">The selected authoring mode.</param>
    [Theory]
    [InlineData(SafeMigrationScaffoldingMode.Strict)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence)]
    public void ExplicitInnoDbTableScaffoldsAndReloads(
        SafeMigrationScaffoldingMode mode
    )
    {
        // Arrange
        var operation = CreateTable("InnoDB");
        using var services = CreateServices();
        var generator = CreateGenerator(services, mode);
        var sourceBuilder = new IndentedStringBuilder();

        // Act
        generator.Generate("migrationBuilder", [operation], sourceBuilder);
        var source = sourceBuilder.ToString();
        var migrationBuilder = Reload(source);
        var table = migrationBuilder
            .Operations
            .OfType<SafeMigrationOperation>()
            .Single(candidate => candidate.Intent is EnsureTableIntent);

        // Assert
        Assert.Contains(".Annotation(\"Doka:MySql:StorageEngine\", \"InnoDB\")",
            source,
            StringComparison.Ordinal);

        Assert.Contains("operationFactory:", source, StringComparison.Ordinal);
        Assert.Empty(table.GetAnnotations());
        var reconstructed = Assert.IsType<CreateTableOperation>(
            SafeMigrationStandardOperationFactory.Create(table.Intent));

        Assert.Equal("InnoDB", reconstructed["Doka:MySql:StorageEngine"]);
    }

    /// <summary>Unknown or nontransactional table metadata cannot become a certified table contract.</summary>
    /// <param name="annotation">The provider annotation name.</param>
    /// <param name="value">The unsupported annotation value.</param>
    [Theory]
    [InlineData("Doka:MySql:StorageEngine", "MyISAM")]
    [InlineData("Doka:MySql:StorageEngine", "InnoDB; DROP TABLE other")]
    [InlineData("Doka:MySql:StorageEngine", null)]
    [InlineData("Doka:MySql:StorageEngine", true)]
    [InlineData("Doka:MySql:StorageEngine", " InnoDB ")]
    [InlineData("Other:StorageEngine", "InnoDB")]
    public void UnsupportedTableMetadataRemainsRejected(
        string annotation,
        object? value
    )
    {
        // Arrange
        var operation = CreateTable(engine: null);
        operation.AddAnnotation(annotation, value);
        using var services = CreateServices();
        var generator = CreateGenerator(services, SafeMigrationScaffoldingMode.Strict);

        // Act
        var exception = Record.Exception(() =>
            generator.Generate("migrationBuilder", [operation], new IndentedStringBuilder()));

        // Assert
        Assert.IsType<NotSupportedException>(exception);
    }

    private static CreateTableOperation CreateTable(
        string? engine
    )
    {
        var operation = new CreateTableOperation { Name = "EngineNodes" };
        operation.Columns.Add(new AddColumnOperation
        {
            Name = "Id",
            Table = operation.Name,
            ClrType = typeof(long),
            ColumnType = "bigint",
            IsNullable = false,
        });

        operation.PrimaryKey = new AddPrimaryKeyOperation
        {
            Name = "PK_EngineNodes",
            Table = operation.Name,
            Columns = ["Id"],
        };

        if (engine is not null)
        {
            operation.AddAnnotation("Doka:MySql:StorageEngine", engine);
        }

        return operation;
    }

    private static MigrationOperation CreateIdentityOperation(
        string kind
    ) => kind switch
    {
        "drop_table" => new DropTableOperation { Name = "EngineNodes" },
        "rename_table" => new RenameTableOperation
        {
            Name = "EngineNodes",
            NewName = "RenamedNodes",
        },
        "drop_primary" => new DropPrimaryKeyOperation
        {
            Name = "PK_EngineNodes",
            Table = "EngineNodes",
        },
        "drop_unique" => new DropUniqueConstraintOperation
        {
            Name = "UQ_EngineNodes",
            Table = "EngineNodes",
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Compiles complete EF migration sources for native lifecycle counterprobes.</summary>
    /// <param name="context">The complete provider-configured design-time model.</param>
    /// <param name="snapshot">The generated historical model snapshot.</param>
    /// <returns>The executable generated migration with its provider bound.</returns>
    internal static Migration CompileCompleteMigration(
        DbContext context,
        out ModelSnapshot snapshot
    )
    {
        var services = new ServiceCollection();
        services.AddEntityFrameworkDesignTimeServices();
        services.AddDbContextDesignTimeServices(context);
        new MySqlDesignTimeServices().ConfigureDesignTimeServices(services);
        new MySqlSafeMigrationDesignTimeServices().ConfigureDesignTimeServices(services);
        using var design = services.BuildServiceProvider();
        var scaffold = design
            .GetRequiredService<IMigrationsScaffolder>()
            .ScaffoldMigration("EngineLifetime",
                "GeneratedEngineLifetime",
                subNamespace: null,
                language: "C#",
                dryRun: true);

        var assembly = MySqlGeneratedMigrationCompiler.Compile(
            scaffold.MigrationCode,
            scaffold.MetadataCode,
            scaffold.SnapshotCode);

        var migration = (Migration)Activator.CreateInstance(
            assembly
                .GetTypes()
                .Single(type => type.IsSubclassOf(typeof(Migration))))!;

        migration.ActiveProvider = context.Database.ProviderName
            ?? throw new InvalidOperationException("The scaffolded context requires a provider.");

        snapshot = (ModelSnapshot)Activator.CreateInstance(
            assembly
                .GetTypes()
                .Single(type => type.IsSubclassOf(typeof(ModelSnapshot))))!;

        return migration;
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddEntityFrameworkDesignTimeServices();
        new MySqlDesignTimeServices().ConfigureDesignTimeServices(services);

        return services.BuildServiceProvider();
    }

    private static SafeMigrationCSharpMigrationOperationGenerator CreateGenerator(
        IServiceProvider services,
        SafeMigrationScaffoldingMode mode
    ) => new(
        services.GetRequiredService<CSharpMigrationOperationGeneratorDependencies>(),
        new SafeMigrationScaffoldingConfiguration(true, mode),
        [new MySqlSafeMigrationCreateIndexScaffoldingProjector()],
        [MySqlSafeMigrationProviderOperationAdapter.Instance]);

    private static MigrationBuilder Reload(
        string operationSource
    )
    {
        var source = $$"""
            using System;
            using Doka.EntityFrameworkCore.MySql;
            using Doka.EntityFrameworkCore.SafeMigrations;
            using Microsoft.EntityFrameworkCore.Migrations;

            public static class GeneratedEngineMigration
            {
                public static void Up(MigrationBuilder migrationBuilder)
                {
                    {{operationSource}}
                }
            }
            """;

        var assembly = MySqlGeneratedMigrationCompiler.Compile(source);
        var method = assembly.GetType("GeneratedEngineMigration", throwOnError: true)!.GetMethod("Up")!;
        var migrationBuilder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        method
            .CreateDelegate<Action<MigrationBuilder>>()(migrationBuilder);

        return migrationBuilder;
    }

    private static ServiceProvider CreateRuntimeServices(
        bool safeFirst
    )
    {
        var services = new ServiceCollection();
        if (safeFirst)
        {
            services.AddEntityFrameworkDokaMySqlSafeMigrations();
            services.AddEntityFrameworkDokaMySql();
        }
        else
        {
            services.AddEntityFrameworkDokaMySql();
            services.AddEntityFrameworkDokaMySqlSafeMigrations();
        }

        return services.BuildServiceProvider();
    }

    private static DbContextOptions CreateOptions(
        IServiceProvider services
    )
    {
        var builder = new DbContextOptionsBuilder();
        builder.UseInternalServiceProvider(services);
        builder.UseMySql("Server=localhost;Database=engine_scaffolding;User ID=test;",
            MySqlServerVersion.MariaDb(new Version(12, 3, 3)));

        builder.UseMySqlSafeMigrations();

        return builder.Options;
    }

    /// <summary>Uses the same entity identity for the plain and explicit-engine model controls.</summary>
    /// <summary>Provides a public context identity for the generated migration metadata.</summary>
    public class PlainModelContext : DbContext
    {
        internal PlainModelContext(
            DbContextOptions options
        ) : base(options) { }

        protected virtual bool HasExplicitEngine => false;

        /// <inheritdoc />
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            var entity = modelBuilder.Entity<EngineRecord>();
            entity.ToTable("EngineNodes");
            entity.HasKey(value => value.Id);
            entity
                .Property(value => value.Id)
                .ValueGeneratedNever();

            if (HasExplicitEngine)
            {
                entity.UseStorageEngine("InnoDB");
            }
        }
    }

    /// <summary>Explicitly configures the provider-owned table storage engine.</summary>
    public sealed class ExplicitModelContext : PlainModelContext
    {
        internal ExplicitModelContext(
            DbContextOptions options
        ) : base(options) { }

        protected override bool HasExplicitEngine => true;
    }

    private sealed class EngineRecord
    {
        public long Id { get; set; }
    }
}
