namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task PendingPreflight_DoesNotMisclassifyStrictOperationsAfterLegacyDataPreparationSql()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `mixed_records` (`id` int NOT NULL, `code` varchar(20) NOT NULL, "
            + "PRIMARY KEY (`id`)); "
            + "INSERT INTO `mixed_records` (`id`, `code`) VALUES (1, 'duplicate'), (2, 'duplicate');");

        await using var context = new MixedPendingStreamDbContext(connectionString, Fixture.ServerVersion);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzePendingMigrationsAsync(
            context,
            new SafeMigrationRunOptions("mixed-pending-stream"));

        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);

        var completed = await runner.AnalyzePendingMigrationsAsync(
            context,
            new SafeMigrationRunOptions("mixed-pending-stream-completed"));

        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);

        var distinctCodes = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(DISTINCT `code`) FROM `mixed_records`;");

        var indexCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'mixed_records' "
            + "AND INDEX_NAME = 'ux_mixed_records_code';");

        var markerCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'mixed_marker';");

        var historyCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM `__MixedPendingStreamMigrationsHistory`;");

        Assert.Equal(2, distinctCodes);
        Assert.Equal(1, indexCount);
        Assert.Equal(1, markerCount);
        Assert.Equal(3, historyCount);
        Assert.Equal(4, preflight.Assessments.Count);
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, preflight.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[2].Action);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[3].Action);
        Assert.Equal(SafeMigrationReportStatus.NoOperations, completed.Status);
        preflight.ThrowIfBlocked();
    }

    [Fact]
    public async Task PendingPreflight_IdentifiesTheSqlMigrationBehindLaterUncertainty()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `mixed_records` (`id` int NOT NULL, `code` varchar(20) NOT NULL, "
            + "PRIMARY KEY (`id`)); "
            + "INSERT INTO `mixed_records` (`id`, `code`) VALUES (1, 'duplicate'), (2, 'duplicate');");
        await using var context = new MixedPendingStreamDbContext(connectionString, Fixture.ServerVersion);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzePendingMigrationsAsync(
            context,
            new SafeMigrationRunOptions("mixed-pending-stream-diagnostics"));

        using var nonMatchingDocument = JsonDocument.Parse(
            SafeMigrationReportJson.SerializeToUtf8Bytes(
                preflight,
                SafeMigrationReportSelection.NonMatching));

        var uncertainAssessment = nonMatchingDocument.RootElement
            .GetProperty("assessments")
            .EnumerateArray()
            .Single(static assessment => assessment.GetProperty("ordinal").GetInt32() == 2);

        var origin = uncertainAssessment.GetProperty("deferredOrigin");

        Assert.Equal("runtime_validation_required", uncertainAssessment.GetProperty("code").GetString());
        Assert.Equal("20260925000002_MixedDataPreparation", origin.GetProperty("migrationId").GetString());
        Assert.Equal(1, origin.GetProperty("operationOrdinal").GetInt32());
        Assert.EndsWith(nameof(SqlOperation), origin.GetProperty("operationType").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PendingPreflight_AnalyzesStrictMigrationAfterDataPreparationWasApplied()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `mixed_records` (`id` int NOT NULL, `code` varchar(20) NOT NULL, "
            + "PRIMARY KEY (`id`)); "
            + "INSERT INTO `mixed_records` (`id`, `code`) VALUES (1, 'duplicate'), (2, 'duplicate');");

        await using var context = new MixedPendingStreamDbContext(connectionString, Fixture.ServerVersion);
        await context.Database.MigrateAsync(
            "20260925000002_MixedDataPreparation",
            CancellationToken.None);

        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzePendingMigrationsAsync(
            context,
            new SafeMigrationRunOptions("mixed-strict-pending-only"));

        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);

        var historyCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM `__MixedPendingStreamMigrationsHistory`;");

        var indexCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'mixed_records' "
            + "AND INDEX_NAME = 'ux_mixed_records_code';");

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(2, preflight.Assessments.Count);
        Assert.All(
            preflight.Assessments,
            static assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(3, historyCount);
        Assert.Equal(1, indexCount);
    }

    [Theory]
    [InlineData("ALTER TABLE `mixed_records` DROP COLUMN `code`;", "doka_sm_prerequisite_missing", 0)]
    [InlineData("UPDATE `mixed_records` SET `code` = 'duplicate';", "doka_sm_data_blocked", 0)]
    [InlineData("CREATE UNIQUE INDEX `ux_mixed_records_code` ON `mixed_records` (`id`);", "doka_sm_different", 1)]
    public async Task OpaqueSql_DoesNotBypassTheLaterUniqueIndexGuard(
        string sql,
        string expectedFailureCode,
        int expectedIndexCount
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `mixed_records` (`id` int NOT NULL, `code` varchar(20) NOT NULL, "
            + "PRIMARY KEY (`id`)); "
            + "INSERT INTO `mixed_records` (`id`, `code`) VALUES (1, 'first'), (2, 'second');");

        await using var context = new MixedPendingStreamDbContext(connectionString, Fixture.ServerVersion);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.Sql(sql);
        builder.CreateIndexIfNotExists(
            "ux_mixed_records_code",
            "mixed_records",
            ["code"],
            unique: true);

        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("opaque-sql-index-guard"));

        var failure = await Record.ExceptionAsync(
            () => ExecuteOperationsAsync(context, builder.Operations));

        var indexCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'mixed_records' "
            + "AND INDEX_NAME = 'ux_mixed_records_code';");

        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, preflight.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[1].Action);
        Assert.Null(preflight.Assessments[1].DeferredOrigin?.MigrationId);
        Assert.Equal(0, preflight.Assessments[1].DeferredOrigin?.OperationOrdinal);
        Assert.Equal(
            "projected_structure_state_unknown",
            preflight.Assessments[1].AnalysisCode);

        var providerFailure = Assert.IsType<MySqlException>(failure);
        Assert.Contains(expectedFailureCode, providerFailure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expectedIndexCount, indexCount);
    }

    [Fact]
    public async Task OpaqueSql_DoesNotReclassifyAnEarlierProvenBlocker()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `mixed_records` (`id` int NOT NULL, PRIMARY KEY (`id`));");
        await using var context = new MixedPendingStreamDbContext(connectionString, Fixture.ServerVersion);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("ux_mixed_records_code", "mixed_records", ["code"], unique: true);
        _ = builder.Sql("ALTER TABLE `mixed_records` ADD COLUMN `code` varchar(20) NOT NULL;");
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("opaque-sql-earlier-blocker"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, preflight.Assessments[0].Action);
        Assert.Null(preflight.Assessments[0].DeferredOrigin);
        Assert.Throws<SafeMigrationPreflightException>(preflight.ThrowIfBlocked);
    }

    [Fact]
    public async Task OpaqueSql_DoesNotDeferAnInvariantDatabaseQualifierMismatch()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = new MixedPendingStreamDbContext(connectionString, Fixture.ServerVersion);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.Sql("SELECT 1;");
        builder.CreateTableIfNotExists(
            "foreign_table",
            table => new { Id = table.Column<int>(type: "int", nullable: false) },
            schema: "foreign_database");

        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("opaque-sql-foreign-database"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, preflight.Assessments[1].Action);
        Assert.Equal("database_qualifier_mismatch", preflight.Assessments[1].AnalysisCode);
        Assert.Null(preflight.Assessments[1].DeferredOrigin);
    }

    [Fact]
    public async Task OpaqueSql_DoesNotDeferUnsupportedSchemaDrop()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        await using var context = new MixedPendingStreamDbContext(connectionString, Fixture.ServerVersion);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.Sql("SELECT 1;");
        builder.CreateTableIfNotExists(
            "opaque_candidate",
            table => new { Id = table.Column<int>(type: "int", nullable: false) });

        builder.DropSchemaIfExists(database);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("opaque-sql-unsupported-schema-drop"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, preflight.Assessments[2].Action);
        Assert.Equal("schema_operations", preflight.Assessments[2].AnalysisCode);
        Assert.Null(preflight.Assessments[2].DeferredOrigin);
        Assert.Throws<SafeMigrationPreflightException>(preflight.ThrowIfBlocked);
    }

    [Fact]
    public async Task OpaqueSql_DoesNotDeferUnsupportedOperationAnnotation()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = new MixedPendingStreamDbContext(connectionString, Fixture.ServerVersion);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.Sql("SELECT 1;");
        builder.CreateIndexIfNotExists("ix_unapproved", "missing_table", ["code"])
            .Annotation("Unapproved:Test", true);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("opaque-sql-unsupported-annotation"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, preflight.Assessments[1].Action);
        Assert.Equal("operation_annotation", preflight.Assessments[1].AnalysisCode);
        Assert.Null(preflight.Assessments[1].DeferredOrigin);
    }
}

/// <summary>Hosts only the ordered mixed-stream regression migrations.</summary>
public sealed class MixedPendingStreamDbContext : DbContext
{
    private readonly string _connectionString;
    private readonly MySqlServerVersion _serverVersion;

    /// <summary>Initializes the isolated test context.</summary>
    /// <param name="connectionString">The database created for this test.</param>
    /// <param name="serverVersion">The active MySQL or MariaDB server version.</param>
    public MixedPendingStreamDbContext(
        string connectionString,
        MySqlServerVersion serverVersion
    )
    {
        _connectionString = connectionString;
        _serverVersion = serverVersion;
    }

    /// <inheritdoc />
    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseMySql(
            _connectionString,
            _serverVersion,
            provider => provider
                .MigrationsAssembly(typeof(MixedPendingStreamDbContext).Assembly.FullName)
                .MigrationsHistoryTable("__MixedPendingStreamMigrationsHistory"));

        optionsBuilder.UseMySqlSafeMigrations<MixedPendingStreamDbContext>();
    }
}

/// <summary>Represents the source-frozen legacy convergence baseline.</summary>
[DbContext(typeof(MixedPendingStreamDbContext))]
[Migration("20260925000001_MixedLegacyInit")]
public sealed class MixedLegacyInitMigration : Migration
{
    /// <inheritdoc />
    protected override void Up(
        MigrationBuilder migrationBuilder
    ) => migrationBuilder.CreateTableIfNotExists(
        "mixed_records",
        table => new
        {
            Id = table.Column<int>(type: "int", nullable: false),
            Code = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
        },
        constraints: table => table.PrimaryKey("PK_mixed_records", row => row.Id),
        policy: SafeMigrationPolicy.ExistenceOnly,
        mode: SafeMigrationTableMode.ConvergenceContainer);

    /// <inheritdoc />
    protected override void Down(
        MigrationBuilder migrationBuilder
    ) => throw new NotSupportedException("The legacy baseline is forward-only.");
}

/// <summary>Prepares legacy data for the later strict unique index.</summary>
[DbContext(typeof(MixedPendingStreamDbContext))]
[Migration("20260925000002_MixedDataPreparation")]
public sealed class MixedDataPreparationMigration : Migration
{
    /// <inheritdoc />
    protected override void Up(
        MigrationBuilder migrationBuilder
    ) => migrationBuilder.Sql(
        "UPDATE `mixed_records` SET `code` = CONCAT(`code`, `id`) "
        + "WHERE `code` = 'duplicate';");

    /// <inheritdoc />
    protected override void Down(
        MigrationBuilder migrationBuilder
    ) => throw new NotSupportedException("The data preparation is forward-only.");
}

/// <summary>Represents a later strict migration in the same pending stream.</summary>
[DbContext(typeof(MixedPendingStreamDbContext))]
[Migration("20260925000003_MixedStrictObjects")]
public sealed class MixedStrictObjectsMigration : Migration
{
    /// <inheritdoc />
    protected override void Up(
        MigrationBuilder migrationBuilder
    )
    {
        migrationBuilder.CreateTableIfNotExists(
            "mixed_marker",
            table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_mixed_marker", row => row.Id));

        migrationBuilder.CreateIndexIfNotExists(
            "ux_mixed_records_code",
            "mixed_records",
            ["code"],
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(
        MigrationBuilder migrationBuilder
    ) => throw new NotSupportedException("The strict test migration is forward-only.");
}
