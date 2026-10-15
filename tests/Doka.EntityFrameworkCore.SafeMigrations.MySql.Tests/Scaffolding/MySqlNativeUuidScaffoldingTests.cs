namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Compiles generated native UUID calls and checks their reconstructed migration contracts.</summary>
public sealed class MySqlNativeUuidScaffoldingTests : IDisposable
{
    private readonly ServiceProvider _services;

    /// <summary>Owns the runtime provider used by this independent generated-migration test case.</summary>
    public MySqlNativeUuidScaffoldingTests()
    {
        // WHY: Explicit test ownership prevents offline model configurations
        // from accumulating in EF's process-wide service-provider cache.
        _services = new ServiceCollection()
            .AddEntityFrameworkDokaMySql()
            .AddEntityFrameworkDokaMySqlSafeMigrations()
            .BuildServiceProvider();
    }

    /// <summary>Disposes the runtime provider after the generated-migration context has been released.</summary>
    public void Dispose() => _services.Dispose();

    /// <summary>Strict and legacy table scaffolding retain native UUID columns, defaults, and primary keys.</summary>
    /// <param name="mode">The configured SafeMigrations authoring mode.</param>
    [Theory]
    [InlineData(SafeMigrationScaffoldingMode.Strict)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence)]
    public void GeneratedNativeTableRoundTripsProviderMetadata(
        SafeMigrationScaffoldingMode mode
    )
    {
        using var context = CreateContext();
        var original = CreateTableOperation(context);

        var result = GenerateAndReload(context, mode, original);

        var operation = Assert.Single(result.Operations.OfType<SafeMigrationOperation>(),
            value => value.Intent is EnsureTableIntent);

        var intent = Assert.IsType<EnsureTableIntent>(operation.Intent);

        Assert.Contains(
            mode == SafeMigrationScaffoldingMode.Strict ? ".CreateTableIfNotExists(" : ".ConvergeTableFromModel(",
            result.Source,
            StringComparison.Ordinal);
        Assert.Equal(
            mode == SafeMigrationScaffoldingMode.Strict
                ? SafeMigrationTableMode.StrictDefinition
                : SafeMigrationTableMode.ConvergenceContainer,
            intent.Mode);
        Assert.Equal(mode == SafeMigrationScaffoldingMode.Strict ? 1 : 5, result.Operations.Count);
        Assert.Equal(original.Columns.Count, intent.Definition.Columns.Count);
        Assert.Equal(original.PrimaryKey!.Columns, intent.Definition.PrimaryKey!.Columns);
        Assert.All(original.Columns, column => AssertColumnRoundTrip(
            column,
            Assert.Single(intent.Definition.Columns, value => value.Name == column.Name)));
    }

    /// <summary>Standalone native UUID columns retain their complete contract in both scaffolding modes.</summary>
    /// <param name="mode">The configured SafeMigrations authoring mode.</param>
    /// <param name="nullable">Whether to generate the optional native UUID column.</param>
    [Theory]
    [InlineData(SafeMigrationScaffoldingMode.Strict, false)]
    [InlineData(SafeMigrationScaffoldingMode.Strict, true)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, false)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, true)]
    public void GeneratedNativeColumnRoundTripsProviderMetadata(
        SafeMigrationScaffoldingMode mode,
        bool nullable
    )
    {
        using var context = CreateContext();
        var table = CreateTableOperation(context);
        var original = table.Columns.Single(value =>
            value.Name == (nullable ? nameof(NativeRecord.OptionalId) : nameof(NativeRecord.DefaultId)));

        var result = GenerateAndReload(context, mode, original);

        var operation = Assert.IsType<SafeMigrationOperation>(Assert.Single(result.Operations));
        var intent = Assert.IsType<EnsureColumnIntent>(operation.Intent);

        Assert.Contains(".AddColumnIfNotExistsFromModel(", result.Source, StringComparison.Ordinal);
        AssertColumnRoundTrip(original, intent.Definition);
    }

    /// <summary>Checks both the public typed Doka contract and the immutable SafeMigrations definition.</summary>
    private static void AssertColumnRoundTrip(
        AddColumnOperation original,
        ExpectedColumnDefinition reloaded
    )
    {
        Assert.Equal(typeof(Guid), original.ClrType);
        Assert.Equal("uuid", original.ColumnType);
        Assert.Equal(original.Name, reloaded.Name);
        Assert.Equal(original.ClrType, reloaded.ClrType);
        Assert.Equal(original.ColumnType, reloaded.StoreType);
        Assert.Equal(original.IsNullable, reloaded.IsNullable);
        Assert.Null(reloaded.MaxLength);
        Assert.Null(reloaded.IsFixedLength);
        Assert.Equal(original.DefaultValue, reloaded.DefaultValue.LiteralValue);
        Assert.Equal(DokaMySqlGuidFormat.Uuid, original.GetMySqlMigrationMetadata().GuidFormat);
        Assert.True(MySqlSafeMigrationColumnMetadata.TryCreate(reloaded, out var metadata));
        Assert.Equal(DokaMySqlGuidFormat.Uuid, metadata.GuidFormat);
    }

    /// <summary>Obtains the real provider's model-differ output rather than guessing its UUID annotations.</summary>
    private static CreateTableOperation CreateTableOperation(
        NativeContext context
    )
    {
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(source: null, model.GetRelationalModel());

        return operations.OfType<CreateTableOperation>().Single();
    }

    /// <summary>Renders and compiles the same operation source a consumer migration executes.</summary>
    private static GeneratedResult GenerateAndReload(
        NativeContext context,
        SafeMigrationScaffoldingMode mode,
        MigrationOperation operation
    )
    {
        var services = new ServiceCollection();
        services.AddEntityFrameworkDesignTimeServices();
        new MySqlDesignTimeServices().ConfigureDesignTimeServices(services);
        // WHY: Reuse the active public mapping source, including its MariaDB
        // profile, instead of constructing or reflecting over Doka internals.
        services.AddSingleton<ITypeMappingSource>(context.GetService<ITypeMappingSource>());

        using var provider = services.BuildServiceProvider();
        var generator = new SafeMigrationCSharpMigrationOperationGenerator(
            provider.GetRequiredService<
                Microsoft.EntityFrameworkCore.Migrations.Design.CSharpMigrationOperationGeneratorDependencies>(),
            new SafeMigrationScaffoldingConfiguration(true, mode),
            [new MySqlSafeMigrationCreateIndexScaffoldingProjector()],
            [MySqlSafeMigrationProviderOperationAdapter.Instance]);

        var builder = new IndentedStringBuilder();
        generator.Generate("migrationBuilder", [operation], builder);
        var operationSource = builder.ToString();
        var source = $$"""
            using System;
            using Doka.EntityFrameworkCore.MySql;
            using Doka.EntityFrameworkCore.SafeMigrations;
            using Microsoft.EntityFrameworkCore.Migrations;

            public static class GeneratedNativeUuidMigration
            {
                public static void Up(MigrationBuilder migrationBuilder)
                {
                    {{operationSource}}
                }
            }
            """;

        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "NativeUuidMigration_" + Guid.NewGuid().ToString("N"),
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source)],
            CreateReferences(),
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

        using var output = new MemoryStream();
        var emitted = compilation.Emit(output);
        if (!emitted.Success)
        {
            throw new InvalidOperationException(
                "Generated native UUID migration did not compile: "
                + string.Join(Environment.NewLine, emitted.Diagnostics));
        }

        var assembly = Assembly.Load(output.ToArray());
        var method = assembly.GetType("GeneratedNativeUuidMigration", throwOnError: true)!.GetMethod("Up")!;
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        method.CreateDelegate<Action<MigrationBuilder>>()(migrationBuilder);

        return new GeneratedResult(operationSource, migrationBuilder.Operations);
    }

    /// <summary>References the active runtime and the exact published assemblies used by the test consumer.</summary>
    private static IEnumerable<Microsoft.CodeAnalysis.MetadataReference> CreateReferences()
    {
        var platformPaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator);

        // WHY: Test hosts differ in which non-framework assemblies they add to
        // their platform set. Explicit consumer references keep compilation
        // equivalent in Rider and dotnet test without loading local Doka code.
        var paths = platformPaths.Concat(
        [
            typeof(MigrationBuilder).Assembly.Location,
            typeof(DbContext).Assembly.Location,
            typeof(IInfrastructure<>).Assembly.Location,
            typeof(SafeMigrationOperation).Assembly.Location,
            typeof(DokaMySqlGuidFormat).Assembly.Location,
        ]);

        return paths.Distinct(StringComparer.Ordinal)
            .Select(static path => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path));
    }

    /// <summary>Creates a fully registered offline consumer against a UUID-capable MariaDB profile.</summary>
    private NativeContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<NativeContext>();
        builder.UseInternalServiceProvider(_services);
        builder.UseMySql(
            "Server=localhost;Database=native_uuid_scaffolding;User ID=test;",
            MySqlServerVersion.MariaDb(new Version(11, 8, 0)));
        builder.UseMySqlSafeMigrations();

        return new NativeContext(builder.Options);
    }

    /// <summary>Captures the rendered source and the operations reconstructed by compiled consumer code.</summary>
    /// <param name="Source">The generated operation body.</param>
    /// <param name="Operations">The reloaded SafeMigrations operations.</param>
    private sealed record GeneratedResult(string Source, IReadOnlyList<MigrationOperation> Operations);

    /// <summary>Supplies required, optional, and defaulted UUIDs to the real provider model differ.</summary>
    private sealed class NativeContext(DbContextOptions<NativeContext> options) : DbContext(options)
    {
        /// <inheritdoc />
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            var entity = modelBuilder.Entity<NativeRecord>();
            entity.ToTable("native_uuid_scaffolding");
            entity.Property(value => value.Id).HasMySqlGuidFormat(DokaMySqlGuidFormat.Uuid);
            entity.Property(value => value.OptionalId).HasMySqlGuidFormat(DokaMySqlGuidFormat.Uuid);
            entity.Property(value => value.DefaultId)
                .HasMySqlGuidFormat(DokaMySqlGuidFormat.Uuid)
                .HasDefaultValue(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        }
    }

    /// <summary>Represents the three native UUID authoring shapes.</summary>
    private sealed class NativeRecord
    {
        /// <summary>Gets or sets the native primary key.</summary>
        public Guid Id { get; set; }

        /// <summary>Gets or sets the optional native UUID.</summary>
        public Guid? OptionalId { get; set; }

        /// <summary>Gets or sets the required native UUID with a literal default.</summary>
        public Guid DefaultId { get; set; }
    }
}
