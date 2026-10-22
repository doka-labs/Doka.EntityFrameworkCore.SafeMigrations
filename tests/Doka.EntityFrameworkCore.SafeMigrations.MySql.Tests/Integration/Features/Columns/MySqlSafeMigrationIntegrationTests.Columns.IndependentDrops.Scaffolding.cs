namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlIndependentColumnDropIntegrationTests
{
    /// <summary>
    /// Real model differences survive compiled migration metadata, execution and an idempotent replay.
    /// </summary>
    /// <param name="mode">The active authoring mode.</param>
    /// <param name="script">Whether the compiled migration executes through generated SQL script commands.</param>
    [Theory]
    [InlineData(SafeMigrationScaffoldingMode.Strict, false)]
    [InlineData(SafeMigrationScaffoldingMode.Strict, true)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, false)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, true)]
    public async Task PublishedDokaScaffoldedExistingKeyReplacementAppliesAndReplays(
        SafeMigrationScaffoldingMode mode, bool script)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExistingKeyFixture("(1, 'a', 10), (2, 'b', 20)"));
        using var services = CreateScaffoldingServices();
        await using var source = new ReplacementSourceContext(ScaffoldingOptions(connectionString, services, mode));
        await using var target = new ReplacementExistingContext(ScaffoldingOptions(connectionString, services, mode));
        var generated = ScaffoldReplacement(source, target);
        var runner = target.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(target, generated.Operations,
            new SafeMigrationRunOptions("published-doka-existing-key"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteIndependentDropAsync(target, connectionString, generated.Operations, script);
        var final = await runner.VerifyAsync(target, generated.Operations,
            new SafeMigrationRunOptions("published-doka-existing-final"), CancellationToken.None);

        var replay = await runner.AnalyzeAsync(target, generated.Operations,
            new SafeMigrationRunOptions("published-doka-existing-replay"), CancellationToken.None);

        replay.ThrowIfBlocked();
        await ExecuteIndependentDropAsync(target, connectionString, generated.Operations, script);
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `replacement_items`;");
        var primary = await ReadReplacementPrimaryAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, final.Status);
        Assert.Equal(2, rows);
        Assert.Equal("event_id,tag_id", primary);
        Assert.Contains("DropColumnIfExists(", generated.Source, StringComparison.Ordinal);
        Assert.All(generated.Operations, static operation => Assert.IsType<SafeMigrationOperation>(operation));
    }

    /// <summary>
    /// The compiled producer path cannot use the old primary key to conceal duplicate or NULL targets.
    /// </summary>
    /// <param name="mode">The active authoring mode.</param>
    /// <param name="rows">Rows valid under the old key but invalid under the requested key.</param>
    [Theory]
    [InlineData(SafeMigrationScaffoldingMode.Strict, "(1, 'a', 10), (1, 'b', 10)")]
    [InlineData(SafeMigrationScaffoldingMode.Strict, "(1, 'a', NULL), (2, 'b', 20)")]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, "(1, 'a', 10), (1, 'b', 10)")]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, "(1, 'a', NULL), (2, 'b', 20)")]
    public async Task PublishedDokaScaffoldedKeyReplacementRejectsInvalidRows(
        SafeMigrationScaffoldingMode mode, string rows)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExistingKeyFixture(rows));
        using var services = CreateScaffoldingServices();
        await using var source = new ReplacementSourceContext(ScaffoldingOptions(connectionString, services, mode));
        await using var target = new ReplacementExistingContext(ScaffoldingOptions(connectionString, services, mode));
        var generated = ScaffoldReplacement(source, target);

        // Act
        var report = await target.GetService<ISafeMigrationRunner>().AnalyzeAsync(target, generated.Operations,
            new SafeMigrationRunOptions("published-doka-invalid-key"), CancellationToken.None);

        var oldPrimary = await ReadReplacementPrimaryAsync(connectionString);
        var retained = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `replacement_items`;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Contains(report.Assessments, static value => value.Action == SafeMigrationAction.RejectDataBlocked);
        Assert.Equal("event_id,legacy_value", oldPrimary);
        Assert.Equal(2, retained);
    }

    /// <summary>Published GUID-default metadata reaches execution without a synthetic ClientGuid override.</summary>
    /// <param name="mode">The active authoring mode.</param>
    /// <param name="script">Whether the reconstructed migration uses the generated script path.</param>
    [Theory]
    [InlineData(SafeMigrationScaffoldingMode.Strict, false)]
    [InlineData(SafeMigrationScaffoldingMode.Strict, true)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, false)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, true)]
    public async Task PublishedDokaScaffoldedUuidKeyPreservesNativeDefaultAndReplicationBoundary(
        SafeMigrationScaffoldingMode mode, bool script)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExistingKeyFixture("(1, 'a', 10), (2, 'b', 20)"));
        using var services = CreateScaffoldingServices();
        await using var source = new ReplacementSourceContext(ScaffoldingOptions(connectionString, services, mode));
        await using var target = new ReplacementUuidContext(ScaffoldingOptions(connectionString, services, mode));
        var generated = ScaffoldReplacement(source, target);
        var runner = target.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(target, generated.Operations,
            new SafeMigrationRunOptions("published-doka-uuid-key"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        var failure = await Record.ExceptionAsync(() =>
            ExecuteIndependentDropAsync(target, connectionString, generated.Operations, script));

        SafeMigrationRunReport? replay = null;
        if (Fixture.IsMariaDb)
        {
            replay = await runner.AnalyzeAsync(target, generated.Operations,
                new SafeMigrationRunOptions("published-doka-uuid-replay"), CancellationToken.None);

            replay.ThrowIfBlocked();
            await ExecuteIndependentDropAsync(target, connectionString, generated.Operations, script);
        }

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `replacement_items`;");
        var identifierColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'replacement_items' AND COLUMN_NAME = 'id';");

        var identifiers = Fixture.IsMariaDb
            ? await ScalarIntAsync(connectionString,
                "SELECT COUNT(DISTINCT `id`) FROM `replacement_items` WHERE CHAR_LENGTH(`id`) = 36;")
            : 0;

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, preflight.Status);
        Assert.Equal(2, rows);
        Assert.Contains("UUID()", generated.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("MySqlValueGenerationStrategy.ClientGuid", generated.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("Doka:MySql:RequiresExplicitBackfill", generated.Source, StringComparison.Ordinal);
        var added = Assert.Single(generated.Operations.OfType<SafeMigrationOperation>(),
            static operation => operation.Intent is EnsureColumnIntent { Definition.Name: "id" });

        var definition = Assert.IsType<EnsureColumnIntent>(added.Intent).Definition;
        Assert.True(MySqlSafeMigrationColumnMetadata.TryCreate(definition, out var metadata));
        // WHY: An implicit SQL-default GUID retains EF's store-generated OnAdd
        // contract without inventing an explicit None or ClientGuid strategy.
        Assert.Null(metadata.ValueGenerationStrategy);
        Assert.Equal(ValueGenerated.OnAdd,
            target.GetService<IDesignTimeModel>().Model.FindEntityType("ReplacementRow")!
                .FindProperty("id")!.ValueGenerated);
        Assert.Equal(DokaMySqlGuidFormat.Char36, metadata.GuidFormat);
        var defaultFunction = Assert.IsType<SafeMigrationSqlFunctionExpression>(
            definition.DefaultValue.StructuredExpression);

        Assert.Equal("UUID", defaultFunction.Name);
        Assert.Empty(defaultFunction.Arguments);
        if (Fixture.IsMariaDb)
        {
            Assert.Null(failure);
            Assert.Equal(SafeMigrationReportStatus.Ready, replay!.Status);
            Assert.Equal(1, identifierColumns);
            Assert.Equal(2, identifiers);
        }
        else
        {
            // WHY: MySQL forbids nondeterministic ALTER defaults under the unchanged
            // ROW/MIXED binlog. The integration test must not weaken that native boundary.
            Assert.Equal(1674, Assert.IsType<MySqlException>(failure).Number);
            Assert.Equal(0, identifierColumns);
        }
    }

    /// <summary>
    /// A compiled migration rechecks surviving data before resuming from its owned destructive prefix.
    /// </summary>
    /// <param name="mode">The active authoring mode.</param>
    /// <param name="rows">Rows independently valid under the original key.</param>
    /// <param name="blocked">Whether the requested replacement is invalid for these existing rows.</param>
    /// <param name="ensure">Whether to probe the explicit Ensure compatibility variant of the compiled ALTER.</param>
    [Theory]
    [InlineData(SafeMigrationScaffoldingMode.Strict, "(1, 'a', 10), (2, 'b', 20)", false, false)]
    [InlineData(SafeMigrationScaffoldingMode.Strict, "(1, 'a', 10), (1, 'b', 10)", true, false)]
    [InlineData(SafeMigrationScaffoldingMode.Strict, "(1, 'a', NULL), (2, 'b', 20)", true, false)]
    [InlineData(SafeMigrationScaffoldingMode.Strict, "(1, 'a', 10), (2, 'b', 20)", false, true)]
    [InlineData(SafeMigrationScaffoldingMode.Strict, "(1, 'a', 10), (1, 'b', 10)", true, true)]
    [InlineData(SafeMigrationScaffoldingMode.Strict, "(1, 'a', NULL), (2, 'b', 20)", true, true)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, "(1, 'a', 10), (2, 'b', 20)", false, false)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, "(1, 'a', 10), (1, 'b', 10)", true, false)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, "(1, 'a', NULL), (2, 'b', 20)", true, false)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, "(1, 'a', 10), (2, 'b', 20)", false, true)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, "(1, 'a', 10), (1, 'b', 10)", true, true)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, "(1, 'a', NULL), (2, 'b', 20)", true, true)]
    public async Task PublishedDokaScaffoldedKeyReplacementRecoversAfterIndependentDrop(
        SafeMigrationScaffoldingMode mode, string rows, bool blocked, bool ensure)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExistingKeyFixture(rows));
        using var services = CreateScaffoldingServices();
        await using var source = new ReplacementSourceContext(ScaffoldingOptions(connectionString, services, mode));
        await using var target = new ReplacementExistingContext(ScaffoldingOptions(connectionString, services, mode));
        var generated = ScaffoldReplacement(source, target);
        // WHY: This explicit compatibility variant probes Ensure's independent hook;
        // it is not claimed as output produced by Doka's model differ or scaffolder.
        var operations = generated.Operations.Select(operation => ensure
            && operation is SafeMigrationOperation { Intent: AlterColumnIntent alter }
                ? new SafeMigrationOperation(new EnsureColumnIntent(alter.Table, alter.Definition, alter.Schema),
                    SafeMigrationPolicy.RepairIfSafe)
                : operation).ToArray();

        var dropOrdinal = Array.FindIndex(operations, static operation =>
            operation is SafeMigrationOperation { Intent: DropColumnIntent });

        await ExecuteOperationsAsync(target, operations.Take(dropOrdinal + 1).ToArray());

        // Act
        var report = await target.GetService<ISafeMigrationRunner>().AnalyzeAsync(target, operations,
            new SafeMigrationRunOptions("published-doka-interrupted-key"), CancellationToken.None);

        if (!blocked)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(target, operations);
        }

        var primaryCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'replacement_items' AND CONSTRAINT_TYPE = 'PRIMARY KEY';");

        var legacyColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'replacement_items' AND COLUMN_NAME = 'legacy_value';");

        var retained = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `replacement_items`;");

        // Assert
        Assert.True(dropOrdinal > 0);
        Assert.Single(operations.OfType<SafeMigrationOperation>(), operation => ensure
            ? operation.Intent is EnsureColumnIntent
            : operation.Intent is AlterColumnIntent);
        Assert.Equal(blocked ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(blocked ? 0 : 1, primaryCount);
        Assert.Equal(0, legacyColumns);
        Assert.Equal(2, retained);
        if (blocked)
        {
            Assert.Contains(report.Assessments,
                static assessment => assessment.Action == SafeMigrationAction.RejectDataBlocked);
        }
    }

    /// <summary>Reads the physical key's ordered members without relying on the provider's generated name.</summary>
    private static Task<string> ReadReplacementPrimaryAsync(string connectionString) => ScalarStringAsync(
        connectionString,
        "SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION) "
        + "FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA = DATABASE() "
        + "AND TABLE_NAME = 'replacement_items' AND CONSTRAINT_NAME = 'PRIMARY';");

    /// <summary>
    /// Owns the exact provider services rather than retaining dynamic model roots in EF's global cache.
    /// </summary>
    private static ServiceProvider CreateScaffoldingServices() => new ServiceCollection()
        .AddEntityFrameworkDokaMySql()
        .AddEntityFrameworkDokaMySqlSafeMigrations()
        .BuildServiceProvider();

    /// <summary>Uses the active native engine and the real SafeMigrations design-time configuration.</summary>
    private DbContextOptions ScaffoldingOptions(string connectionString, ServiceProvider services,
        SafeMigrationScaffoldingMode mode)
    {
        var options = new DbContextOptionsBuilder()
            .UseInternalServiceProvider(services)
            .UseMySql(connectionString, Fixture.ServerVersion);

        options.UseMySqlSafeMigrations(configuration => configuration.UseScaffoldingMode(mode));

        return options.Options;
    }

    /// <summary>
    /// Compiles both real migration bodies and historical target metadata from actual model differences.
    /// </summary>
    private static GeneratedReplacement ScaffoldReplacement(DbContext source, DbContext target)
    {
        var sourceModel = source.GetService<IDesignTimeModel>().Model;
        var targetModel = target.GetService<IDesignTimeModel>().Model;
        var differences = target.GetService<IMigrationsModelDiffer>()
            .GetDifferences(sourceModel.GetRelationalModel(), targetModel.GetRelationalModel());

        var services = new ServiceCollection();
        services.AddEntityFrameworkDesignTimeServices();
        services.AddDbContextDesignTimeServices(target);
        new MySqlDesignTimeServices().ConfigureDesignTimeServices(services);
        new MySqlSafeMigrationDesignTimeServices().ConfigureDesignTimeServices(services);
        using var provider = services.BuildServiceProvider();
        var generator = provider.GetRequiredService<
            Microsoft.EntityFrameworkCore.Migrations.Design.IMigrationsCodeGeneratorSelector>().Select("C#");

        const string generatedNamespace = "Doka.GeneratedReplacementTests";
        const string migrationName = "PublishedDokaReplacement";
        var migration = generator.GenerateMigration(generatedNamespace, migrationName, differences, []);
        var metadata = generator.GenerateMetadata(generatedNamespace, target.GetType(), migrationName,
            "20261022000000_PublishedDokaReplacement", targetModel);

        var snapshot = generator.GenerateSnapshot(generatedNamespace, target.GetType(), "ReplacementSnapshot",
            targetModel);

        var assembly = MySqlGeneratedMigrationCompiler.Compile(migration, metadata, snapshot);
        var reconstructed = (Migration)Activator.CreateInstance(assembly.GetType(
            generatedNamespace + "." + migrationName, throwOnError: true)!)!;

        reconstructed.ActiveProvider = target.Database.ProviderName
            ?? throw new InvalidOperationException("The generated migration requires a configured provider.");

        // WHY: Force historical target construction too; compiling only the operation
        // body would miss the GUID/default metadata seam that originally hid this gap.
        _ = target.GetService<IModelRuntimeInitializer>()
            .Initialize(reconstructed.TargetModel, designTime: true).GetRelationalModel();

        return new GeneratedReplacement(migration + metadata + snapshot, reconstructed.UpOperations);
    }

    /// <summary>Retains complete rendered sources and operations reconstructed from the consumer assembly.</summary>
    private sealed record GeneratedReplacement(string Source, IReadOnlyList<MigrationOperation> Operations);

    /// <summary>Defines the old two-member key without injecting provider migration annotations.</summary>
    /// <param name="options">The owned service root and active native engine.</param>
    public sealed class ReplacementSourceContext(DbContextOptions options) : DbContext(options)
    {
        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureReplacementModel(modelBuilder,
            source: true, uuid: false);
    }

    /// <summary>Requests the surviving existing-member replacement key through ordinary fluent configuration.</summary>
    /// <param name="options">The owned service root and active native engine.</param>
    public sealed class ReplacementExistingContext(DbContextOptions options) : DbContext(options)
    {
        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureReplacementModel(modelBuilder,
            source: false, uuid: false);
    }

    /// <summary>Requests a new server-default GUID key through ordinary fluent configuration.</summary>
    /// <param name="options">The owned service root and active native engine.</param>
    public sealed class ReplacementUuidContext(DbContextOptions options) : DbContext(options)
    {
        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureReplacementModel(modelBuilder,
            source: false, uuid: true);
    }

    /// <summary>Shares stable model identity while letting the real provider infer each generation strategy.</summary>
    private static void ConfigureReplacementModel(ModelBuilder modelBuilder, bool source, bool uuid)
    {
        modelBuilder.UseCollation("utf8mb4_unicode_ci");
        modelBuilder.Entity("ReplacementRow", entity =>
        {
            entity.ToTable("replacement_items");
            entity.Property<int>("event_id").HasColumnType("int").ValueGeneratedNever();
            entity.Property<int?>("tag_id").HasColumnType("int").IsRequired(!source && !uuid);
            if (source)
            {
                entity.Property<string>("legacy_value").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired();
                entity.HasKey("event_id", "legacy_value").HasName("pk_replacement_items");
            }
            else if (uuid)
            {
                entity.Property<Guid>("id").HasMySqlGuidFormat(DokaMySqlGuidFormat.Char36)
                    .HasDefaultValueSql("UUID()");
                entity.HasKey("id").HasName("pk_replacement_items");
            }
            else
            {
                entity.HasKey("event_id", "tag_id").HasName("pk_replacement_items");
            }
        });
    }
}
