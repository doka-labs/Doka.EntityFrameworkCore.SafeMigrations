namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task AnnotatedColumns_RepairDefaultsAndCommentsWithoutLosingRows()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `annotated_column_repair` ("
            + "`id` int NOT NULL, "
            + "`is_active` tinyint(1) NOT NULL DEFAULT 0 COMMENT 'legacy', "
            + "`display_name` varchar(80) NULL DEFAULT '' COMMENT 'legacy', "
            + "`external_id` char(36) NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000' COMMENT 'legacy', "
            + "`occurred_at` datetime(6) NOT NULL DEFAULT '0001-01-01 00:00:00.000000' COMMENT 'legacy', "
            + "PRIMARY KEY (`id`));"
            + "INSERT INTO `annotated_column_repair` VALUES ("
            + "1, 1, 'preserved', '22222222-2222-2222-2222-222222222222', '2026-08-31 12:00:00.000000');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.ConvergeTableFromModel(
            name: "annotated_column_repair",
            columns: table => new
            {
                id = table
                    .Column<int>(type: "int", nullable: false)
                    .Annotation(
                        "Doka:MySql:ValueGenerationStrategy",
                        MySqlValueGenerationStrategy.None),
                is_active = table
                    .Column<bool>(type: "tinyint(1)", nullable: false, comment: "canonical")
                    .Annotation(
                        "Doka:MySql:ValueGenerationStrategy",
                        MySqlValueGenerationStrategy.None),
                display_name = table
                    .Column<string>(
                        type: "varchar(80)",
                        maxLength: 80,
                        nullable: false,
                        comment: "canonical")
                    .Annotation(
                        "Doka:MySql:ValueGenerationStrategy",
                        MySqlValueGenerationStrategy.None),
                external_id = table
                    .Column<Guid>(
                        type: "char(36)",
                        maxLength: 36,
                        fixedLength: true,
                        nullable: false,
                        comment: "canonical")
                    .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                    .Annotation(
                        "Doka:MySql:ValueGenerationStrategy",
                        MySqlValueGenerationStrategy.None),
                occurred_at = table
                    .Column<DateTime>(
                        type: "datetime(6)",
                        nullable: false,
                        comment: "canonical")
                    .Annotation(
                        "Doka:MySql:ValueGenerationStrategy",
                        MySqlValueGenerationStrategy.None),
            },
            constraints: table => table.PrimaryKey("pk_annotated_column_repair", value => value.id),
            policy: SafeMigrationPolicy.RepairIfSafe);

        var runner = context.GetService<ISafeMigrationRunner>();
        var options = new SafeMigrationRunOptions("annotated-column-repair");
        var preflight = await runner.AnalyzeAsync(context, builder.Operations, options);

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteSqlAsync(
            connectionString,
            "INSERT INTO `annotated_column_repair` "
            + "(`id`, `is_active`, `display_name`, `external_id`, `occurred_at`) VALUES "
            + "(2, 0, 'inserted', '33333333-3333-3333-3333-333333333333', "
            + "'2026-08-31 13:00:00.000000');");

        var postflight = await runner.VerifyAsync(context, builder.Operations, options);

        Assert.Contains(preflight.Assessments, static assessment => assessment.Action == SafeMigrationAction.Repair);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(2, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `annotated_column_repair`;"));
        Assert.Equal(
            4,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'annotated_column_repair' "
                + "AND COLUMN_NAME <> 'id' AND COLUMN_COMMENT = 'canonical' AND COLUMN_DEFAULT IS NULL;"));
    }

    [Fact]
    public async Task AnnotatedNullableColumn_WithNullRows_IsDataBlockedWithoutMutation()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `annotated_nullable_blocked` ("
            + "`value` varchar(80) NULL DEFAULT '' COMMENT 'legacy');"
            + "INSERT INTO `annotated_nullable_blocked` (`value`) VALUES (NULL);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.ConvergeTableFromModel(
            name: "annotated_nullable_blocked",
            columns: table => new
            {
                value = table
                    .Column<string>(
                        type: "varchar(80)",
                        maxLength: 80,
                        nullable: false,
                        comment: "canonical")
                    .Annotation(
                        "Doka:MySql:ValueGenerationStrategy",
                        MySqlValueGenerationStrategy.None),
            },
            policy: SafeMigrationPolicy.RepairIfSafe);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("annotated-nullable-blocked"));

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));
        var assessment = Assert.Single(
            report.Assessments,
            static candidate => candidate.ObjectName == "value");

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, assessment.Action);
        Assert.Contains("doka_sm_data_blocked", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'annotated_nullable_blocked' "
                + "AND COLUMN_NAME = 'value' AND IS_NULLABLE = 'YES' "
                + "AND COLUMN_DEFAULT IS NOT NULL AND COLUMN_COMMENT = 'legacy';"));
    }

    [Fact]
    public async Task RepairIfSafe_DoesNotRewriteAutoIncrementMismatch()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `annotated_identity_mismatch` (`id` int NOT NULL, PRIMARY KEY (`id`));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.ConvergeTableFromModel(
            name: "annotated_identity_mismatch",
            columns: table => new
            {
                id = table
                    .Column<int>(type: "int", nullable: false)
                    .Annotation(
                        "Doka:MySql:ValueGenerationStrategy",
                        MySqlValueGenerationStrategy.AutoIncrement),
            },
            constraints: table => table.PrimaryKey("pk_annotated_identity_mismatch", value => value.id),
            policy: SafeMigrationPolicy.RepairIfSafe);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("identity-mismatch"));

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        Assert.Contains(
            report.Assessments,
            static assessment => assessment is
            {
                ObjectName: "id",
                ObservedState: SafeMigrationObservedState.Different,
                Action: SafeMigrationAction.RejectDifferent,
            });
        Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            string.Empty,
            await ScalarStringAsync(
                connectionString,
                "SELECT EXTRA FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'annotated_identity_mismatch' "
                + "AND COLUMN_NAME = 'id';"));
    }

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
    [InlineData(
        SafeMigrationScaffoldingMode.Strict,
        DokaMySqlGuidFormat.Binary16)]
    [InlineData(
        SafeMigrationScaffoldingMode.Strict,
        DokaMySqlGuidFormat.Char36)]
    [InlineData(
        SafeMigrationScaffoldingMode.LegacyConvergence,
        DokaMySqlGuidFormat.Binary16)]
    [InlineData(
        SafeMigrationScaffoldingMode.LegacyConvergence,
        DokaMySqlGuidFormat.Char36)]
    public async Task ScaffoldedNativeGuidRelationship_IsSupportedAndIdempotent(
        SafeMigrationScaffoldingMode mode,
        DokaMySqlGuidFormat guidFormat
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var storeType = GuidStoreType(guidFormat);
        var maxLength = guidFormat == DokaMySqlGuidFormat.Binary16 ? 16 : 36;

        // Owned table splitting shares the owner's table and key operation.
        // The owned facet below keeps that generated shape in this full graph.
        _ = AddScaffoldedTable(
            builder,
            mode,
            name: "native_guid_roots",
            columns: table => new
            {
                id = table
                    .Column<Guid>(
                        type: storeType,
                        maxLength: maxLength,
                        fixedLength: true,
                        nullable: false)
                    .Annotation("Doka:MySql:GuidFormat", guidFormat),
                alternate_id = table
                    .Column<Guid>(
                        type: storeType,
                        maxLength: maxLength,
                        fixedLength: true,
                        nullable: false)
                    .Annotation("Doka:MySql:GuidFormat", guidFormat),
                owned_display_name = table.Column<string>(
                    type: "varchar(80)",
                    maxLength: 80,
                    nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_native_guid_roots", value => value.id);
                table.UniqueConstraint("uq_native_guid_roots_alternate_id", value => value.alternate_id);
            });

        _ = AddScaffoldedTable(
            builder,
            mode,
            name: "native_guid_composites",
            columns: table => new
            {
                tenant_id = table
                    .Column<Guid>(
                        type: storeType,
                        maxLength: maxLength,
                        fixedLength: true,
                        nullable: false)
                    .Annotation("Doka:MySql:GuidFormat", guidFormat),
                id = table
                    .Column<Guid>(
                        type: storeType,
                        maxLength: maxLength,
                        fixedLength: true,
                        nullable: false)
                    .Annotation("Doka:MySql:GuidFormat", guidFormat),
            },
            constraints: table => table.PrimaryKey(
                "pk_native_guid_composites",
                value => new { value.tenant_id, value.id }));

        _ = AddScaffoldedTable(
            builder,
            mode,
            name: "native_guid_leaves",
            columns: table => new
            {
                id = table
                    .Column<Guid>(
                        type: storeType,
                        maxLength: maxLength,
                        fixedLength: true,
                        nullable: false)
                    .Annotation("Doka:MySql:GuidFormat", guidFormat),
                root_id = table
                    .Column<Guid>(
                        type: storeType,
                        maxLength: maxLength,
                        fixedLength: true,
                        nullable: false)
                    .Annotation("Doka:MySql:GuidFormat", guidFormat),
                nullable_root_id = table
                    .Column<Guid>(
                        type: storeType,
                        maxLength: maxLength,
                        fixedLength: true,
                        nullable: true)
                    .Annotation("Doka:MySql:GuidFormat", guidFormat),
                root_alternate_id = table
                    .Column<Guid>(
                        type: storeType,
                        maxLength: maxLength,
                        fixedLength: true,
                        nullable: false)
                    .Annotation("Doka:MySql:GuidFormat", guidFormat),
                composite_tenant_id = table
                    .Column<Guid>(
                        type: storeType,
                        maxLength: maxLength,
                        fixedLength: true,
                        nullable: false)
                    .Annotation("Doka:MySql:GuidFormat", guidFormat),
                composite_id = table
                    .Column<Guid>(
                        type: storeType,
                        maxLength: maxLength,
                        fixedLength: true,
                        nullable: false)
                    .Annotation("Doka:MySql:GuidFormat", guidFormat),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_native_guid_leaves", value => value.id);
                table.ForeignKey(
                    "fk_native_guid_leaves_roots",
                    value => value.root_id,
                    "native_guid_roots",
                    "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    "fk_native_guid_leaves_nullable_roots",
                    value => value.nullable_root_id,
                    "native_guid_roots",
                    "id");
                table.ForeignKey(
                    "fk_native_guid_leaves_alternate_roots",
                    value => value.root_alternate_id,
                    "native_guid_roots",
                    "alternate_id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    "fk_native_guid_leaves_composites",
                    value => new { value.composite_tenant_id, value.composite_id },
                    "native_guid_composites",
                    ["tenant_id", "id"],
                    onDelete: ReferentialAction.Cascade);
            });

        _ = builder.CreateIndexIfNotExistsFromModel(
            "ix_native_guid_leaves_root_id",
            "native_guid_leaves",
            "root_id");
        _ = builder.CreateIndexIfNotExistsFromModel(
            "ix_native_guid_leaves_nullable_root_id",
            "native_guid_leaves",
            "nullable_root_id");
        _ = builder.CreateIndexIfNotExistsFromModel(
            "ix_native_guid_leaves_root_alternate_id",
            "native_guid_leaves",
            "root_alternate_id");
        _ = builder.CreateCompositeIndexIfNotExistsFromModel(
            "ix_native_guid_leaves_composite",
            "native_guid_leaves",
            ["composite_tenant_id", "composite_id"]);

        var runner = context.GetService<ISafeMigrationRunner>();
        var options = new SafeMigrationRunOptions($"native-guid-{mode}-{guidFormat}");
        var preflight = await runner.AnalyzeAsync(context, builder.Operations, options);

        await ExecuteOperationsAsync(context, builder.Operations);

        var postflight = await runner.VerifyAsync(context, builder.Operations, options);
        var rootId = GuidLiteral(guidFormat, "9ca407b5-d320-442f-9b52-a41448759585");
        var alternateId = GuidLiteral(guidFormat, "5cb81781-d64e-4599-847f-894d4f4ec2b1");
        var tenantId = GuidLiteral(guidFormat, "d98d9ec6-ae53-4470-b36c-f274a89a28f0");
        var compositeId = GuidLiteral(guidFormat, "ad92d4cb-ec29-4b6d-988f-b5b8f6522478");
        var leafId = GuidLiteral(guidFormat, "3e9743bd-7554-472a-9a6c-25555b91578f");

        await ExecuteSqlAsync(
            connectionString,
            "INSERT INTO `native_guid_roots` (`id`, `alternate_id`, `owned_display_name`) "
            + $"VALUES ({rootId}, {alternateId}, 'owned value');"
            + "INSERT INTO `native_guid_composites` (`tenant_id`, `id`) "
            + $"VALUES ({tenantId}, {compositeId});"
            + "INSERT INTO `native_guid_leaves` ("
            + "`id`, `root_id`, `nullable_root_id`, `root_alternate_id`, "
            + "`composite_tenant_id`, `composite_id`) "
            + $"VALUES ({leafId}, {rootId}, NULL, {alternateId}, {tenantId}, {compositeId});");
        await ExecuteOperationsAsync(context, builder.Operations);

        var replay = await runner.AnalyzeAsync(context, builder.Operations, options);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.DoesNotContain(
            preflight.Assessments,
            static assessment => assessment.ObservedState == SafeMigrationObservedState.Unsupported);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(
            10,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() "
                + "AND TABLE_NAME IN ('native_guid_roots', 'native_guid_composites', 'native_guid_leaves') "
                + $"AND LOWER(COLUMN_TYPE) = '{storeType}';"));
        Assert.Equal(1, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `native_guid_roots`;"));
        Assert.Equal(1, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `native_guid_composites`;"));
        Assert.Equal(1, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `native_guid_leaves`;"));
    }

    [Theory]
    [InlineData(UnsupportedGuidAnnotationCase.UndefinedValue)]
    [InlineData(UnsupportedGuidAnnotationCase.BinaryWithCharStoreType)]
    [InlineData(UnsupportedGuidAnnotationCase.CharWithBinaryStoreType)]
    [InlineData(UnsupportedGuidAnnotationCase.NonGuidClrType)]
    public async Task ScaffoldedInvalidGuidFormatAnnotation_IsRejectedBeforeDdl(
        UnsupportedGuidAnnotationCase testCase
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var guidFormat = testCase switch
        {
            UnsupportedGuidAnnotationCase.UndefinedValue =>
                (DokaMySqlGuidFormat)int.MaxValue,
            UnsupportedGuidAnnotationCase.BinaryWithCharStoreType =>
                DokaMySqlGuidFormat.Binary16,
            UnsupportedGuidAnnotationCase.CharWithBinaryStoreType
                or UnsupportedGuidAnnotationCase.NonGuidClrType =>
                DokaMySqlGuidFormat.Char36,
            _ => throw new ArgumentOutOfRangeException(nameof(testCase), testCase, "Unsupported test case."),
        };

        var storeType = testCase == UnsupportedGuidAnnotationCase.BinaryWithCharStoreType
            || testCase == UnsupportedGuidAnnotationCase.NonGuidClrType
                ? "char(36)"
                : "binary(16)";

        _ = builder.CreateTableIfNotExists(
            name: "invalid_guid_format",
            columns: table => new
            {
                id = testCase == UnsupportedGuidAnnotationCase.NonGuidClrType
                    ? table
                        .Column<string>(type: storeType, maxLength: 36, fixedLength: true, nullable: false)
                        .Annotation("Doka:MySql:GuidFormat", guidFormat)
                    : table
                        .Column<Guid>(type: storeType, fixedLength: true, nullable: false)
                        .Annotation("Doka:MySql:GuidFormat", guidFormat),
            },
            constraints: table => table.PrimaryKey("pk_invalid_guid_format", value => value.id));

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions($"invalid-{testCase}"));

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
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'invalid_guid_format';"));
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

    private static string GuidStoreType(
        DokaMySqlGuidFormat format
    ) => format switch
    {
        DokaMySqlGuidFormat.Binary16 => "binary(16)",
        DokaMySqlGuidFormat.Char36 => "char(36)",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported GUID format."),
    };

    private static string GuidLiteral(
        DokaMySqlGuidFormat format,
        string value
    ) => format switch
    {
        DokaMySqlGuidFormat.Binary16 => $"UNHEX(REPLACE('{value}', '-', ''))",
        DokaMySqlGuidFormat.Char36 => $"'{value}'",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported GUID format."),
    };

    public enum UnsupportedGuidAnnotationCase
    {
        UndefinedValue,
        BinaryWithCharStoreType,
        CharWithBinaryStoreType,
        NonGuidClrType,
    }
}
