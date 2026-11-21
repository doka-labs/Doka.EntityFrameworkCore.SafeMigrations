namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlEf11DefaultTextIntegrationTests
{
    private const string UnicodeCollation = "utf8mb4_unicode_ci";
    private const string BinaryCollation = "utf8mb4_bin";
    private const string TableName = "ef11_text_records";
    private const string SelectedGuid = "11111111-2222-3333-4444-555555555555";

    /// <summary>
    /// Owns a transient provider root, keeping shape and generated-assembly tests out of EF's global cache.
    /// </summary>
    private static ServiceProvider Services(
        bool safe
    )
    {
        var services = new ServiceCollection().AddEntityFrameworkDokaMySql();
        services.AddSingleton<IModelCacheKeyFactory, TextModelCacheKeyFactory>();
        if (safe)
        {
            services.AddEntityFrameworkDokaMySqlSafeMigrations();
        }

        return services.BuildServiceProvider();
    }

    /// <summary>Uses only public provider options and public SafeMigrations activation.</summary>
    private TextContext TextContextFor(
        ServiceProvider services,
        TextShape shape,
        bool safe,
        string connectionString = "Server=127.0.0.1;Port=1;Database=ef11_text_offline;User ID=unused;Pooling=false;",
        Assembly? migrationsAssembly = null
    )
    {
        var options = new DbContextOptionsBuilder()
            .UseInternalServiceProvider(services)
            .UseMySql(connectionString, Fixture.ServerVersion, provider =>
            {
                provider.DefaultCharSet("utf8mb4");
                provider.DefaultCollation(UnicodeCollation);
                provider.DefaultGuidFormat(DokaMySqlGuidFormat.Char36);
                if (migrationsAssembly is not null)
                {
                    provider.MigrationsAssembly(migrationsAssembly);
                }
            });

        if (safe)
        {
            options.UseMySqlSafeMigrations(configuration =>
                configuration.UseScaffoldingMode(SafeMigrationScaffoldingMode.Strict));
        }

        return new TextContext(options.Options, shape, Fixture.IsMariaDb);
    }

    /// <summary>Retains the design-time annotations discarded from the optimized runtime model.</summary>
    private static IModel DesignModel(
        DbContext context
    ) => context.GetService<IDesignTimeModel>().Model;

    /// <summary>Builds actual provider/SafeMigrations design-time services for the configured consumer.</summary>
    private static ServiceProvider CodeServices(
        DbContext context,
        bool safe
    )
    {
        var services = new ServiceCollection();
        services.AddEntityFrameworkDesignTimeServices();
        services.AddDbContextDesignTimeServices(context);
        new MySqlDesignTimeServices().ConfigureDesignTimeServices(services);
        if (safe)
        {
            new MySqlSafeMigrationDesignTimeServices().ConfigureDesignTimeServices(services);
        }

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Roundtrips the real snapshot through generated C# rather than hand-writing historical metadata.
    /// </summary>
    private static Assembly SnapshotAssembly(
        DbContext context,
        bool safe
    )
    {
        using var services = CodeServices(context, safe);
        var generator = services.GetRequiredService<IMigrationsCodeGeneratorSelector>().Select("C#");
        var snapshot = generator.GenerateSnapshot("Doka.Ef11TextGenerated", typeof(TextContext),
            "TextSnapshot", DesignModel(context));

        return MySqlGeneratedMigrationCompiler.Compile(snapshot);
    }

    /// <summary>
    /// Reconstructs executable Up/Down operations and their target metadata from real model differences.
    /// </summary>
    private static GeneratedTextMigration Scaffold(
        DbContext source,
        DbContext target,
        bool safe
    )
    {
        var sourceModel = DesignModel(source);
        var targetModel = DesignModel(target);
        var differ = target.GetService<IMigrationsModelDiffer>();
        var up = differ.GetDifferences(sourceModel.GetRelationalModel(), targetModel.GetRelationalModel());
        var down = differ.GetDifferences(targetModel.GetRelationalModel(), sourceModel.GetRelationalModel());
        using var services = CodeServices(target, safe);
        var generator = services.GetRequiredService<IMigrationsCodeGeneratorSelector>().Select("C#");
        const string generatedNamespace = "Doka.Ef11TextGenerated";
        const string migrationName = "TextDefaultsTransition";
        var migration = generator.GenerateMigration(generatedNamespace, migrationName, up, down);
        var metadata = generator.GenerateMetadata(generatedNamespace, typeof(TextContext), migrationName,
            "20261118000000_TextDefaultsTransition", targetModel);

        var assembly = MySqlGeneratedMigrationCompiler.Compile(migration, metadata);
        var reconstructed = (Migration)Activator.CreateInstance(
            assembly.GetType(generatedNamespace + "." + migrationName, throwOnError: true)!)!;

        reconstructed.ActiveProvider = target.Database.ProviderName!;
        _ = target.GetService<IModelRuntimeInitializer>().Initialize(reconstructed.TargetModel, designTime: true);

        return new GeneratedTextMigration(
            migration + metadata, reconstructed.UpOperations, reconstructed.DownOperations);
    }

    /// <summary>Runs the same public read-only preflight used by applications.</summary>
    private static Task<SafeMigrationRunReport> AnalyzeAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        CancellationToken cancellationToken
    ) => context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
        context, operations, new SafeMigrationRunOptions("ef11-default-text"), cancellationToken);

    /// <summary>Seeds all specialized physical types without adding automatic data-repair assumptions.</summary>
    private async Task SeedAsync(
        string connectionString,
        bool nullValue,
        CancellationToken cancellationToken
    )
    {
        var nativeColumn = Fixture.IsMariaDb
            ? ", `NativeId`"
            : string.Empty;

        var nativeValue = Fixture.IsMariaDb
            ? $", '{SelectedGuid}'"
            : string.Empty;

        var value = nullValue
            ? "NULL"
            : "'preserved'";

        await ExecuteSqlAsync(connectionString,
            $$"""
            INSERT INTO `{{TableName}}` (`Id`, `Value`, `ExternalId`, `Ascii`, `BinaryId`, `Payload`{{nativeColumn}})
            VALUES (1, {{value}}, '{{SelectedGuid}}', 'ascii', UNHEX('11111111222233334444555555555555'),
            '{"ok":true}'{{nativeValue}});
            """,
            cancellationToken);
    }

    /// <summary>Captures physical column definitions and stored values independently of the model.</summary>
    private static async Task<string> FactsAsync(
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT CONCAT(COLUMN_NAME, ':', COALESCE(CHARACTER_SET_NAME, ''), ':',
            COALESCE(COLLATION_NAME, ''), ':', LOWER(COLUMN_TYPE), ':', IS_NULLABLE)
            FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = '{TableName}' ORDER BY COLUMN_NAME;
            SELECT CONCAT('value=', COALESCE(`Value`, '<null>'), ';guid=', `ExternalId`,
            ';ascii=', `Ascii`, ';binary=', HEX(`BinaryId`), ';json=', JSON_EXTRACT(`Payload`, '$.ok'))
            FROM `{TableName}` ORDER BY `Id`;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new StringBuilder();
        do
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                result.AppendLine(reader.GetString(0));
            }
        }
        while (await reader.NextResultAsync(cancellationToken));

        return result.ToString();
    }

    /// <summary>Reads the actual selected column collation without trusting configured defaults.</summary>
    private static Task<string> ReadValueCollationAsync(
        string connectionString,
        CancellationToken cancellationToken
    ) => ScalarStringAsync(connectionString,
        $"""
        SELECT COLLATION_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
        AND TABLE_NAME = '{TableName}' AND COLUMN_NAME = 'Value';
        """,
        cancellationToken);

    private sealed record GeneratedTextMigration(
        string Source,
        IReadOnlyList<MigrationOperation> Up,
        IReadOnlyList<MigrationOperation> Down);

    /// <summary>Varies only declared source/target intent, never the physical server state.</summary>
    public sealed record TextShape
    {
        /// <summary>Gets whether equivalent defaults are also set by the fluent model API.</summary>
        public bool FluentModelDefaults { get; init; }

        /// <summary>Gets the table override independent of the global default.</summary>
        public string? TableCollation { get; init; }

        /// <summary>Gets whether the selected column explicitly fixes its collation.</summary>
        public bool ExplicitColumnCollation { get; init; }

        /// <summary>Gets whether the selected column is the Char36 Guid.</summary>
        public bool GuidColumn { get; init; }

        /// <summary>Gets the ordinary text column's maximum length.</summary>
        public int Length { get; init; } = 64;

        /// <summary>Gets whether the ordinary text column permits NULL.</summary>
        public bool Nullable { get; init; }

        /// <summary>Gets the ordinary text column's independently declared SQL default.</summary>
        public string? DefaultSql { get; init; }
    }

    /// <summary>Keeps the explicitly varied fixture shape in EF's real model cache identity.</summary>
    public sealed class TextModelCacheKeyFactory : IModelCacheKeyFactory
    {
        /// <inheritdoc />
        public object Create(DbContext context, bool designTime) => context is TextContext text
            ? (context.GetType(), text.Shape, text.IsMariaDb, designTime)
            : (context.GetType(), designTime);
    }

    /// <summary>Builds the same mapped model for provider-only and public SafeMigrations activation.</summary>
    public sealed class TextContext : DbContext
    {
        /// <summary>Creates one isolated source or target fixture context.</summary>
        /// <param name="options">The owned service provider and native connection options.</param>
        /// <param name="shape">The declared source/target facets.</param>
        /// <param name="isMariaDb">Whether native UUID and MariaDB JSON physical contracts apply.</param>
        public TextContext(DbContextOptions options, TextShape shape, bool isMariaDb) : base(options)
        {
            Shape = shape;
            IsMariaDb = isMariaDb;
        }

        /// <summary>Gets the model-affecting fixture facets.</summary>
        public TextShape Shape { get; }

        /// <summary>Gets whether the native MariaDB model contracts apply.</summary>
        public bool IsMariaDb { get; }

        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            if (Shape.FluentModelDefaults)
            {
                modelBuilder.HasCharSet("utf8mb4").UseCollation(UnicodeCollation);
            }

            var entity = modelBuilder.Entity("Ef11TextRecord");
            entity.ToTable(TableName);
            entity.Property<int>("Id").ValueGeneratedNever();
            entity.HasKey("Id");
            entity.Property<string>("Value").HasMaxLength(Shape.Length).IsRequired(!Shape.Nullable);
            entity.Property<Guid>("ExternalId").HasMySqlGuidFormat(DokaMySqlGuidFormat.Char36);
            entity.Property<string>("Ascii").HasMaxLength(16).IsRequired()
                .IsUnicode(false).UseCollation("ascii_general_ci");
            entity.Property<Guid>("BinaryId").HasMySqlGuidFormat(DokaMySqlGuidFormat.Binary16);
            entity.Property<string>("Payload").HasColumnType("json").IsRequired();
            if (IsMariaDb)
            {
                entity.Property<Guid>("NativeId").HasMySqlGuidFormat(DokaMySqlGuidFormat.Uuid);
            }

            if (Shape.TableCollation is not null)
            {
                entity.Metadata.SetAnnotation(RelationalAnnotationNames.Collation, Shape.TableCollation);
            }

            if (Shape.ExplicitColumnCollation)
            {
                entity.Property(Shape.GuidColumn
                    ? "ExternalId"
                    : "Value").UseCollation(UnicodeCollation);
            }

            if (Shape.DefaultSql is not null)
            {
                entity.Property<string>("Value").HasDefaultValueSql(Shape.DefaultSql);
            }
        }
    }
}
