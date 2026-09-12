namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task ProjectedVarcharWideningAuthorizesTheTargetPrefixAndReplaysAsNoOp()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `projected_index_width` ("
            + "`id` int NOT NULL, "
            + "`property` varchar(700) NOT NULL, "
            + "PRIMARY KEY (`id`)) ENGINE=InnoDB ROW_FORMAT=DYNAMIC "
            + "DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + "INSERT INTO `projected_index_width` (`id`, `property`) VALUES (1, 'preserved');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "projected_index_width",
            CharacterColumn("property", length: 800),
            SafeMigrationPolicy.RepairIfSafe);
        builder.EnsureIndex(
            Index(
                "ix_projected_index_width_property",
                "projected_index_width",
                new ExpectedIndexKeyDefinition(column: "property", prefixLength: 768)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("projected-index-width"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("projected-index-width-postflight"),
            CancellationToken.None);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("projected-index-width-replay"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationAction.Repair, preflight.Assessments[0].Action);
        Assert.Equal("projected_missing", preflight.Assessments[1].Code);
        Assert.Equal(SafeMigrationObservedState.Missing, preflight.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(
            800,
            await ScalarIntAsync(
                connectionString,
                "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'projected_index_width' "
                + "AND COLUMN_NAME = 'property';"));
        Assert.Equal(
            768,
            await ScalarIntAsync(
                connectionString,
                "SELECT SUB_PART FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'projected_index_width' "
                + "AND INDEX_NAME = 'ix_projected_index_width_property';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `projected_index_width` "
                + "WHERE `id` = 1 AND `property` = 'preserved';"));
    }

    [Fact]
    public async Task RejectedVarcharWideningCannotAuthorizeTheTargetPrefix()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `rejected_projected_width` ("
            + "`id` int NOT NULL, "
            + "`property` varchar(700) NOT NULL, "
            + "PRIMARY KEY (`id`)) ENGINE=InnoDB ROW_FORMAT=DYNAMIC "
            + "DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "rejected_projected_width",
            CharacterColumn("property", length: 800),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(
            Index(
                "ix_rejected_projected_width_property",
                "rejected_projected_width",
                new ExpectedIndexKeyDefinition(column: "property", prefixLength: 768)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("rejected-projected-width"),
                CancellationToken.None);

        var columnAssessment = report.Assessments[0];
        var indexAssessment = report.Assessments[1];

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, columnAssessment.Action);
        Assert.Equal(SafeMigrationObservedState.Unsupported, indexAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, indexAssessment.Action);
        Assert.Equal("index_prefix_required_for_key_limit", indexAssessment.Code);
        Assert.Equal(
            700,
            await ScalarIntAsync(
                connectionString,
                "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'rejected_projected_width' "
                + "AND COLUMN_NAME = 'property';"));
    }

    [Fact]
    public async Task ProjectedCompositeIndexCombinesTargetAndLiveColumnWidths()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `projected_composite_width` ("
            + "`id` int NOT NULL, "
            + "`left_value` varchar(700) NOT NULL, "
            + "`right_value` varchar(100) NOT NULL, "
            + "PRIMARY KEY (`id`)) ENGINE=InnoDB ROW_FORMAT=DYNAMIC "
            + "DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + "INSERT INTO `projected_composite_width` VALUES (1, 'left', 'right');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "projected_composite_width",
            CharacterColumn("left_value", length: 800),
            SafeMigrationPolicy.RepairIfSafe);
        builder.EnsureIndex(
            Index(
                "ix_projected_composite_width_values",
                "projected_composite_width",
                new ExpectedIndexKeyDefinition(column: "left_value", prefixLength: 767),
                new ExpectedIndexKeyDefinition(column: "right_value", prefixLength: 1)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("projected-composite-width"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("projected-composite-width-postflight"),
            CancellationToken.None);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("projected-composite-width-replay"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationAction.Repair, preflight.Assessments[0].Action);
        Assert.Equal(SafeMigrationObservedState.Missing, preflight.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
    }

    [Fact]
    public async Task ProjectedUniqueIndexUsesLiveDuplicateEvidenceBeforePhysicalRevalidation()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `projected_unique_width` ("
            + "`id` int NOT NULL, "
            + "`property` varchar(700) NOT NULL, "
            + "PRIMARY KEY (`id`)) ENGINE=InnoDB ROW_FORMAT=DYNAMIC "
            + "DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + "INSERT INTO `projected_unique_width` VALUES (1, 'left'), (2, 'right');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "projected_unique_width",
            CharacterColumn("property", length: 800),
            SafeMigrationPolicy.RepairIfSafe);
        builder.EnsureIndex(
            Index(
                "ux_projected_unique_width_property",
                "projected_unique_width",
                unique: true,
                new ExpectedIndexKeyDefinition(column: "property", prefixLength: 768)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("projected-unique-width"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("projected-unique-width-replay"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, preflight.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
    }

    [Fact]
    public async Task ProjectedUniqueIndexWithDuplicateRowsRemainsDataBlocked()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `projected_unique_blocked` ("
            + "`id` int NOT NULL, "
            + "`property` varchar(700) NOT NULL, "
            + "PRIMARY KEY (`id`)) ENGINE=InnoDB ROW_FORMAT=DYNAMIC "
            + "DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + "INSERT INTO `projected_unique_blocked` VALUES (1, 'duplicate'), (2, 'duplicate');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "projected_unique_blocked",
            CharacterColumn("property", length: 800),
            SafeMigrationPolicy.RepairIfSafe);
        builder.EnsureIndex(
            Index(
                "ux_projected_unique_blocked_property",
                "projected_unique_blocked",
                unique: true,
                new ExpectedIndexKeyDefinition(column: "property", prefixLength: 768)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("projected-unique-blocked"),
                CancellationToken.None);

        var indexAssessment = report.Assessments[1];

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, indexAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, indexAssessment.Action);
        Assert.Equal("data_blocked", indexAssessment.Code);
        Assert.Equal(
            700,
            await ScalarIntAsync(
                connectionString,
                "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'projected_unique_blocked' "
                + "AND COLUMN_NAME = 'property';"));
    }

    [Fact]
    public async Task ProjectedUniqueIndexRetainsDuplicateEvidenceBeforePhysicalFailure()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `projected_unique_physical_blocked` ("
            + "`id` int NOT NULL, "
            + "`property` varchar(700) NOT NULL, "
            + "PRIMARY KEY (`id`)) ENGINE=InnoDB ROW_FORMAT=DYNAMIC "
            + "DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + "INSERT INTO `projected_unique_physical_blocked` "
            + "VALUES (1, 'duplicate'), (2, 'duplicate');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "projected_unique_physical_blocked",
            CharacterColumn("property", length: 800),
            SafeMigrationPolicy.RepairIfSafe);
        builder.EnsureIndex(
            Index(
                "ux_projected_unique_physical_blocked_property",
                "projected_unique_physical_blocked",
                unique: true,
                new ExpectedIndexKeyDefinition(column: "property", prefixLength: 769)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("projected-unique-physical-blocked"),
                CancellationToken.None);

        var indexAssessment = report.Assessments[1];

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, indexAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, indexAssessment.Action);
        Assert.Equal("data_blocked", indexAssessment.Code);
        Assert.Equal(
            700,
            await ScalarIntAsync(
                connectionString,
                "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() "
                + "AND TABLE_NAME = 'projected_unique_physical_blocked' "
                + "AND COLUMN_NAME = 'property';"));
    }

    [Fact]
    public async Task ProviderAlterColumnAuthorizesTheFollowingTargetPrefix()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `provider_projected_index` ("
            + "`id` int NOT NULL, "
            + "`property` varchar(700) NOT NULL, "
            + "PRIMARY KEY (`id`)) ENGINE=InnoDB ROW_FORMAT=DYNAMIC "
            + "DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.AlterColumn<string>(
            name: "property",
            table: "provider_projected_index",
            type: "varchar(800)",
            maxLength: 800,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "varchar(700)",
            oldMaxLength: 700,
            oldNullable: false);
        builder.EnsureIndex(
            Index(
                "ix_provider_projected_index_property",
                "provider_projected_index",
                new ExpectedIndexKeyDefinition(column: "property", prefixLength: 768)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("provider-projected-index"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("provider-projected-index-postflight"),
            CancellationToken.None);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("provider-projected-index-replay"),
            CancellationToken.None);

        var providerAssessment = preflight.Assessments[0];
        var indexAssessment = preflight.Assessments[1];

        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, preflight.Status);
        Assert.False(providerAssessment.IsSafeOperation);
        Assert.Equal(SafeMigrationObservedState.Missing, indexAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, indexAssessment.Action);
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, postflight.Status);
        Assert.True(postflight.Assessments[1].PostconditionSatisfied);
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, replay.Status);
        Assert.Equal(SafeMigrationAction.NoOp, replay.Assessments[1].Action);
    }

    [Fact]
    public async Task ProjectedIndexBeyondTheTargetLimitRemainsBlockedBeforeMutation()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `projected_index_blocked` ("
            + "`id` int NOT NULL, "
            + "`left_value` varchar(700) NOT NULL, "
            + "`right_value` varchar(100) NOT NULL, "
            + "PRIMARY KEY (`id`)) ENGINE=InnoDB ROW_FORMAT=DYNAMIC "
            + "DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "projected_index_blocked",
            CharacterColumn("left_value", length: 800),
            SafeMigrationPolicy.RepairIfSafe);
        builder.EnsureIndex(
            Index(
                "ix_projected_index_blocked_values",
                "projected_index_blocked",
                new ExpectedIndexKeyDefinition(column: "left_value", prefixLength: 768),
                new ExpectedIndexKeyDefinition(column: "right_value", prefixLength: 1)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("projected-index-blocked"),
                CancellationToken.None);

        var indexAssessment = report.Assessments[1];

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, indexAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, indexAssessment.Action);
        Assert.Equal("index_key_exceeds_physical_limit", indexAssessment.Code);
        Assert.Equal(
            700,
            await ScalarIntAsync(
                connectionString,
                "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'projected_index_blocked' "
                + "AND COLUMN_NAME = 'left_value';"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'projected_index_blocked' "
                + "AND INDEX_NAME = 'ix_projected_index_blocked_values';"));
    }

    [Fact]
    public async Task MissingTableWithAnInvalidTargetPrefixRemainsBlocked()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.ConvergeTableFromModel(
            "missing_projected_index",
            table => new
            {
                id = table.Column<int>(type: "int", nullable: false),
                property = table.Column<string>(type: "varchar(767)", maxLength: 767, nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_missing_projected_index", value => value.id));
        builder.EnsureIndex(
            Index(
                "ix_missing_projected_index_property",
                "missing_projected_index",
                new ExpectedIndexKeyDefinition(column: "property", prefixLength: 768)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("missing-projected-index"),
                CancellationToken.None);

        var indexAssessment = Assert.Single(
            report.Assessments,
            static assessment => assessment.OperationKind == SafeMigrationOperationKind.EnsureIndex);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, indexAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, indexAssessment.Action);
        Assert.Equal("index_prefix_exceeds_target_column", indexAssessment.Code);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'missing_projected_index';"));
    }

    [Fact]
    public async Task AcceptedIndexThenProviderDropThenEnsureRecreatesTheIndex()
    {
        const string table = "accepted_index_drop";
        const string indexName = "ix_accepted_index_drop_property";
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `accepted_index_drop` ("
            + "`id` int NOT NULL, `property` int NOT NULL, "
            + "PRIMARY KEY (`id`), INDEX `ix_accepted_index_drop_property` (`property`));");

        await using var context = CreateContext(connectionString);
        var definition = Index(
            indexName,
            table,
            new ExpectedIndexKeyDefinition(column: "property"));

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(definition, SafeMigrationPolicy.ThrowIfDifferent);
        _ = builder.DropIndex(indexName, table);
        builder.EnsureIndex(definition, SafeMigrationPolicy.ThrowIfDifferent);

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("accepted-index-provider-drop"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("accepted-index-provider-drop-postflight"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, preflight.Status);
        Assert.Equal(SafeMigrationAction.NoOp, preflight.Assessments[0].Action);
        Assert.False(preflight.Assessments[1].IsSafeOperation);
        Assert.Equal(SafeMigrationObservedState.Missing, preflight.Assessments[2].ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[2].Action);
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, postflight.Status);
        Assert.True(postflight.Assessments[2].PostconditionSatisfied);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'accepted_index_drop' "
                + "AND INDEX_NAME = 'ix_accepted_index_drop_property';"));
    }

    [Fact]
    public async Task AcceptedIndexThenProviderColumnDropBlocksTheFollowingEnsure()
    {
        const string table = "accepted_index_column_drop";
        const string indexName = "ix_accepted_index_column_drop_property";
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `accepted_index_column_drop` ("
            + "`id` int NOT NULL, `property` int NOT NULL, "
            + "PRIMARY KEY (`id`), INDEX `ix_accepted_index_column_drop_property` (`property`));");

        await using var context = CreateContext(connectionString);
        var definition = Index(
            indexName,
            table,
            new ExpectedIndexKeyDefinition(column: "property"));

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(definition, SafeMigrationPolicy.ThrowIfDifferent);
        _ = builder.DropColumn("property", table);
        builder.EnsureIndex(definition, SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("accepted-index-column-drop"),
                CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.False(report.Assessments[1].IsSafeOperation);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, report.Assessments[2].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[2].Action);
        Assert.Equal("projected_structure_state_unknown", report.Assessments[2].AnalysisCode);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'accepted_index_column_drop' "
                + "AND COLUMN_NAME = 'property';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'accepted_index_column_drop' "
                + "AND INDEX_NAME = 'ix_accepted_index_column_drop_property';"));
    }

    private static ExpectedColumnDefinition CharacterColumn(
        string name,
        int length
    ) => new(
        name,
        typeof(string),
        isNullable: false,
        storeType: $"varchar({length.ToString(CultureInfo.InvariantCulture)})",
        maxLength: length);

    private static ExpectedIndexDefinition Index(
        string name,
        string table,
        params ExpectedIndexKeyDefinition[] keys
    ) => Index(name, table, unique: false, keys);

    private static ExpectedIndexDefinition Index(
        string name,
        string table,
        bool unique,
        params ExpectedIndexKeyDefinition[] keys
    ) => new(name, table, keys, unique: unique);
}
