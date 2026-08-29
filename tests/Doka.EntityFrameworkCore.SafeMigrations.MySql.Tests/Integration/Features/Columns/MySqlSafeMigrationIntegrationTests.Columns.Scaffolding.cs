namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task ScaffoldedAutoIncrementTable_PreservesGenerationAndIsIdempotent()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = AddScaffoldedAutoIncrementTable(builder, "scaffolded_identity");

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteSqlAsync(
            connectionString,
            "INSERT INTO `scaffolded_identity` (`display_name`) VALUES ('first');");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("scaffolded-identity"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(
            "auto_increment",
            await ScalarStringAsync(
                connectionString,
                "SELECT LOWER(EXTRA) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'scaffolded_identity' "
                + "AND COLUMN_NAME = 'id';"));
        Assert.Equal(1, await ScalarIntAsync(connectionString, "SELECT `id` FROM `scaffolded_identity`;"));
    }

    [Fact]
    public async Task ScaffoldedAutoIncrementTable_RejectsExistingNonGeneratedColumn()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `scaffolded_identity_drift` ("
            + "`id` int NOT NULL, `display_name` varchar(80) NULL, PRIMARY KEY (`id`));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = AddScaffoldedAutoIncrementTable(builder, "scaffolded_identity_drift");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("scaffolded-identity-drift"));

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(report.Assessments).ObservedState);
        Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            string.Empty,
            await ScalarStringAsync(
                connectionString,
                "SELECT EXTRA FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'scaffolded_identity_drift' "
                + "AND COLUMN_NAME = 'id';"));
    }

    [Fact]
    public async Task ScaffoldedOperationLevelAnnotation_IsRejectedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var operation = AddScaffoldedAutoIncrementTable(builder, "unsupported_scaffolded_annotation");
        operation["Test:UnsupportedOperationFacet"] = true;

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("unsupported-operation-facet"));

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(report.Assessments).ObservedState);
        Assert.Contains("doka_sm_unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'unsupported_scaffolded_annotation';"));
    }

    [Theory]
    [InlineData(SafeMigrationScaffoldingMode.Strict)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence)]
    public async Task ScaffoldedClientGuidRelationship_IsSupportedAndIdempotent(
        SafeMigrationScaffoldingMode mode
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);

        _ = AddScaffoldedTable(
            builder,
            mode,
            name: "client_guid_roots",
            columns: table => new
            {
                id = table
                    .Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
                    .Annotation(
                        "Doka:MySql:ValueGenerationStrategy",
                        MySqlValueGenerationStrategy.ClientGuid),
            },
            constraints: table => table.PrimaryKey("pk_client_guid_roots", value => value.id));

        _ = AddScaffoldedTable(
            builder,
            mode,
            name: "client_guid_leaves",
            columns: table => new
            {
                id = table
                    .Column<int>(type: "int", nullable: false)
                    .Annotation(
                        "Doka:MySql:ValueGenerationStrategy",
                        MySqlValueGenerationStrategy.AutoIncrement),
                root_id = table
                    .Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
                    .Annotation(
                        "Doka:MySql:ValueGenerationStrategy",
                        MySqlValueGenerationStrategy.None),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_client_guid_leaves", value => value.id);
                table.ForeignKey(
                    "fk_client_guid_leaves_roots",
                    value => value.root_id,
                    "client_guid_roots",
                    "id",
                    onDelete: ReferentialAction.Cascade);
            });

        _ = builder.CreateIndexIfNotExistsFromModel(
            "ix_client_guid_leaves_root_id",
            "client_guid_leaves",
            "root_id");

        var preflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("client-guid-relationship"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteSqlAsync(
            connectionString,
            "INSERT INTO `client_guid_roots` (`id`) VALUES ('9ca407b5-d320-442f-9b52-a41448759585');"
            + "INSERT INTO `client_guid_leaves` (`root_id`) "
            + "VALUES ('9ca407b5-d320-442f-9b52-a41448759585');");
        await ExecuteOperationsAsync(context, builder.Operations);

        var postflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("client-guid-relationship"));

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(
            preflight.Assessments,
            assessment => Assert.True(
                assessment.Action is SafeMigrationAction.Apply or SafeMigrationAction.NoOp,
                $"Unexpected preflight action: {assessment.Action}."));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(
            postflight.Assessments,
            assessment => Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState));
        Assert.Equal(1, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `client_guid_roots`;"));
        Assert.Equal(1, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `client_guid_leaves`;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScaffoldedUnsupportedColumnAnnotation_IsRejectedBeforeDdl(
        bool useUnknownAnnotation
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var tableName = useUnknownAnnotation ? "unsupported_unknown" : "unsupported_hilo";

        _ = builder.CreateTableIfNotExists(
            name: tableName,
            columns: table => new
            {
                id = useUnknownAnnotation
                    ? table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation("Test:UnknownColumnFacet", true)
                    : table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation(
                            "Doka:MySql:ValueGenerationStrategy",
                            MySqlValueGenerationStrategy.HiLo),
            },
            constraints: table => table.PrimaryKey($"pk_{tableName}", value => value.id));

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions(tableName));

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(report.Assessments).ObservedState);
        Assert.Contains("doka_sm_unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{tableName}';"));
    }

    private static SafeMigrationOperation AddScaffoldedAutoIncrementTable(
        MigrationBuilder builder,
        string tableName
    )
    {
        _ = builder.CreateTableIfNotExists(
            name: tableName,
            columns: table => new
            {
                id = table
                    .Column<int>(type: "int", nullable: false)
                    .Annotation(
                        "Doka:MySql:ValueGenerationStrategy",
                        MySqlValueGenerationStrategy.AutoIncrement),
                display_name = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: true),
            },
            constraints: table => table.PrimaryKey($"pk_{tableName}", value => value.id));

        return Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
    }

    private static OperationBuilder<SafeMigrationOperation> AddScaffoldedTable<TColumns>(
        MigrationBuilder builder,
        SafeMigrationScaffoldingMode mode,
        string name,
        Func<ColumnsBuilder, TColumns> columns,
        Action<CreateTableBuilder<TColumns>> constraints
    ) => mode switch
    {
        SafeMigrationScaffoldingMode.Strict => builder.CreateTableIfNotExists(
            name,
            columns,
            constraints: constraints),
        SafeMigrationScaffoldingMode.LegacyConvergence => builder.ConvergeTableFromModel(
            name,
            columns,
            constraints: constraints),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported scaffolding mode."),
    };
}
