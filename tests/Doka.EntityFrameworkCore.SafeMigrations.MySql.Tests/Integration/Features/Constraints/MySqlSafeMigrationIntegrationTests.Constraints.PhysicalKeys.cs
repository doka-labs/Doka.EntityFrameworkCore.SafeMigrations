namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task UniqueConstraintDropCanBeReplacedByAnEquivalentUniqueIndex()
    {
        const string table = "constraint_to_index";
        const string constraint = "uq_constraint_to_index_code";
        const string index = "ux_constraint_to_index_code";
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `{table}` ("
            + "`id` int NOT NULL, `code` int NOT NULL, PRIMARY KEY (`id`), "
            + $"CONSTRAINT `{constraint}` UNIQUE (`code`));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropUniqueConstraintIfExists(constraint, table);
        builder.CreateIndexIfNotExists(index, table, ["code"], unique: true);

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("constraint-to-index"));

        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[^1].Action);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() "
                + $"AND TABLE_NAME = '{table}' AND INDEX_NAME = '{index}' AND NON_UNIQUE = 0;"));
    }

    [Fact]
    public async Task UniqueIndexDropCanBeReplacedByAnEquivalentUniqueConstraint()
    {
        const string table = "index_to_constraint";
        const string index = "ux_index_to_constraint_code";
        const string constraint = "uq_index_to_constraint_code";
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `{table}` ("
            + "`id` int NOT NULL, `code` int NOT NULL, PRIMARY KEY (`id`), "
            + $"UNIQUE INDEX `{index}` (`code`));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists(index, table);
        builder.AddUniqueConstraintIfNotExists(constraint, table, ["code"]);

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("index-to-constraint"));

        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[^1].Action);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS "
                + "WHERE CONSTRAINT_SCHEMA = DATABASE() "
                + $"AND TABLE_NAME = '{table}' AND CONSTRAINT_NAME = '{constraint}' "
                + "AND CONSTRAINT_TYPE = 'UNIQUE';"));
    }

    [Fact]
    public async Task DroppedUniqueConstraintInvalidatesEquivalentUniqueIndexBeforeDataMutation()
    {
        const string table = "cross_kind_constraint_drop";
        const string physicalName = "uq_cross_kind_constraint_code";
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `{table}` ("
            + "`id` int NOT NULL, `code` int NOT NULL, PRIMARY KEY (`id`), "
            + $"CONSTRAINT `{physicalName}` UNIQUE (`code`)); "
            + $"INSERT INTO `{table}` VALUES (1, 7);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddUniqueConstraintIfNotExists(
            "uq_cross_kind_constraint_code_alias",
            table,
            ["code"]);
        builder.DropUniqueConstraintIfExists(physicalName, table);
        builder.InsertData(table, ["id", "code"], new object[,] { { 2, 7 } });
        builder.CreateIndexIfNotExists(
            "ux_cross_kind_constraint_code_alias",
            table,
            ["code"],
            unique: true);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("cross-kind-constraint-drop"));

        var replacement = report.Assessments[^1];

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, replacement.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, replacement.Action);
        Assert.Equal("projected_data_state_unknown", replacement.AnalysisCode);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS "
                + "WHERE CONSTRAINT_SCHEMA = DATABASE() "
                + $"AND TABLE_NAME = '{table}' AND CONSTRAINT_NAME = '{physicalName}';"));
    }

    [Fact]
    public async Task DroppedUniqueIndexInvalidatesEquivalentUniqueConstraintBeforeDataMutation()
    {
        const string table = "cross_kind_index_drop";
        const string physicalName = "ux_cross_kind_index_code";
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `{table}` ("
            + "`id` int NOT NULL, `code` int NOT NULL, PRIMARY KEY (`id`), "
            + $"UNIQUE INDEX `{physicalName}` (`code`)); "
            + $"INSERT INTO `{table}` VALUES (1, 7);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists(
            "ux_cross_kind_index_code_alias",
            table,
            ["code"],
            unique: true);
        builder.DropIndexIfExists(physicalName, table);
        builder.InsertData(table, ["id", "code"], new object[,] { { 2, 7 } });
        builder.AddUniqueConstraintIfNotExists(
            "uq_cross_kind_index_code_alias",
            table,
            ["code"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("cross-kind-index-drop"));

        var replacement = report.Assessments[^1];

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, replacement.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, replacement.Action);
        Assert.Equal("projected_data_state_unknown", replacement.AnalysisCode);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() "
                + $"AND TABLE_NAME = '{table}' AND INDEX_NAME = '{physicalName}';"));
    }

    [Fact]
    public async Task PrimaryKeyDoesNotSatisfyAnOrdinaryUniqueIndexContract()
    {
        const string table = "primary_is_not_index_alias";
        const string index = "ux_primary_is_not_index_alias_id";
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `{table}` (`id` int NOT NULL, PRIMARY KEY (`id`));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists(index, table, ["id"], unique: true);

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("primary-is-not-index-alias"));

        await ExecuteOperationsAsync(context, builder.Operations);

        var assessment = Assert.Single(preflight.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, assessment.Action);
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(DISTINCT INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS "
                + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' "
                + $"AND INDEX_NAME IN ('PRIMARY', '{index}');"));
    }

    [Fact]
    public async Task PrimaryIsRejectedAsAnOrdinaryIndexName()
    {
        const string table = "reserved_primary_index_name";
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `{table}` (`id` int NOT NULL);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("PRIMARY", table, ["id"], unique: true);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("reserved-primary-index-name"));

        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
        Assert.Equal("index_name_reserved_primary", assessment.Code);
    }

    [Fact]
    public async Task SameNameNonUniqueIndexPrecedesAnEquivalentUniqueConstraintAlias()
    {
        const string table = "unique_constraint_name_collision";
        const string requestedName = "uq_requested_code";
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `{table}` ("
            + "`id` int NOT NULL, `code` int NOT NULL, PRIMARY KEY (`id`), "
            + $"INDEX `{requestedName}` (`code`), CONSTRAINT `uq_existing_code` UNIQUE (`code`));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddUniqueConstraintIfNotExists(requestedName, table, ["code"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("unique-constraint-name-collision"));

        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
    }

    [Fact]
    public async Task SameNameIndexReplacementValidatesUnchangedTargetWidthBeforeDrop()
    {
        const string table = "wide_index_replacement";
        const string index = "ix_wide_index_replacement";
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `{table}` ("
            + "`id` int NOT NULL, `legacy_value` int NOT NULL, `wide_value` varchar(192) NOT NULL, "
            + $"PRIMARY KEY (`id`), INDEX `{index}` (`legacy_value`)) "
            + "ENGINE=InnoDB ROW_FORMAT=COMPACT DEFAULT CHARACTER SET utf8mb4;");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists(index, table);
        builder.CreateIndexIfNotExists(index, table, ["wide_value"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("wide-index-replacement"));

        var replacement = report.Assessments[^1];

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, replacement.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, replacement.Action);
        Assert.Equal("index_prefix_required_for_key_limit", replacement.Code);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() "
                + $"AND TABLE_NAME = '{table}' AND INDEX_NAME = '{index}' "
                + "AND COLUMN_NAME = 'legacy_value';"));
    }

    [Fact]
    public async Task CrossKindUniqueReplacementValidatesUnchangedTargetWidthBeforeDrop()
    {
        const string table = "wide_cross_kind_replacement";
        const string keyName = "uq_wide_cross_kind_replacement";
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `{table}` ("
            + "`id` int NOT NULL, `legacy_value` int NOT NULL, `wide_value` varchar(192) NOT NULL, "
            + $"PRIMARY KEY (`id`), INDEX `{keyName}` (`legacy_value`)) "
            + "ENGINE=InnoDB ROW_FORMAT=COMPACT DEFAULT CHARACTER SET utf8mb4;");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists(keyName, table);
        builder.AddUniqueConstraintIfNotExists(keyName, table, ["wide_value"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("wide-cross-kind-replacement"));

        var replacement = report.Assessments[^1];

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, replacement.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, replacement.Action);
        Assert.Equal("unique_constraint_exceeds_physical_limit", replacement.Code);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() "
                + $"AND TABLE_NAME = '{table}' AND INDEX_NAME = '{keyName}' "
                + "AND COLUMN_NAME = 'legacy_value';"));
    }

    [Fact]
    public async Task SameKindUniqueReplacementPreservesHiddenDuplicateEvidence()
    {
        const string table = "unique_replacement_duplicates";
        const string constraint = "uq_unique_replacement";
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `{table}` ("
            + "`id` int NOT NULL, `legacy_value` int NOT NULL, `target_value` int NOT NULL, "
            + $"PRIMARY KEY (`id`), CONSTRAINT `{constraint}` UNIQUE (`legacy_value`)); "
            + $"INSERT INTO `{table}` VALUES (1, 10, 7), (2, 20, 7);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropUniqueConstraintIfExists(constraint, table);
        builder.AddUniqueConstraintIfNotExists(constraint, table, ["target_value"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("unique-replacement-duplicates"));

        var replacement = report.Assessments[^1];

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, replacement.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, replacement.Action);
        Assert.Equal("unique_constraint_replacement_data_blocked", replacement.AnalysisCode);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS "
                + "WHERE CONSTRAINT_SCHEMA = DATABASE() "
                + $"AND TABLE_NAME = '{table}' AND CONSTRAINT_NAME = '{constraint}' "
                + "AND CONSTRAINT_TYPE = 'UNIQUE';"));
    }

    [Fact]
    public async Task WidePrimaryAndUniqueKeysAreRejectedBeforePhysicalCreation()
    {
        const string primaryTable = "wide_primary_key";
        const string uniqueTable = "wide_unique_constraint";
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `{primaryTable}` (`value` varchar(192) NOT NULL) "
            + "ENGINE=InnoDB ROW_FORMAT=COMPACT DEFAULT CHARACTER SET utf8mb4; "
            + $"CREATE TABLE `{uniqueTable}` (`id` int NOT NULL, `value` varchar(192) NOT NULL, "
            + "PRIMARY KEY (`id`)) ENGINE=InnoDB ROW_FORMAT=COMPACT DEFAULT CHARACTER SET utf8mb4;");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddPrimaryKeyIfNotExists("pk_wide_primary_key", primaryTable, ["value"]);
        builder.AddUniqueConstraintIfNotExists("uq_wide_unique_constraint", uniqueTable, ["value"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("wide-physical-keys"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Collection(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
                Assert.Equal("primary_key_exceeds_physical_limit", assessment.Code);
            },
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
                Assert.Equal("unique_constraint_exceeds_physical_limit", assessment.Code);
            });
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS "
                + "WHERE CONSTRAINT_SCHEMA = DATABASE() "
                + $"AND TABLE_NAME IN ('{primaryTable}', '{uniqueTable}') "
                + "AND CONSTRAINT_TYPE IN ('PRIMARY KEY', 'UNIQUE');"));
    }

    [Fact]
    public async Task NarrowPrimaryAndUniqueKeysRemainApplicableAtTheCompactLimit()
    {
        const string primaryTable = "narrow_primary_key";
        const string uniqueTable = "narrow_unique_constraint";
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `{primaryTable}` (`value` varchar(191) NOT NULL) "
            + "ENGINE=InnoDB ROW_FORMAT=COMPACT DEFAULT CHARACTER SET utf8mb4; "
            + $"CREATE TABLE `{uniqueTable}` (`id` int NOT NULL, `value` varchar(191) NOT NULL, "
            + "PRIMARY KEY (`id`)) ENGINE=InnoDB ROW_FORMAT=COMPACT DEFAULT CHARACTER SET utf8mb4;");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddPrimaryKeyIfNotExists("pk_narrow_primary_key", primaryTable, ["value"]);
        builder.AddUniqueConstraintIfNotExists("uq_narrow_unique_constraint", uniqueTable, ["value"]);

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("narrow-physical-keys"));

        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(
            preflight.Assessments,
            static assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(
            3,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS "
                + "WHERE CONSTRAINT_SCHEMA = DATABASE() "
                + $"AND TABLE_NAME IN ('{primaryTable}', '{uniqueTable}') "
                + "AND CONSTRAINT_TYPE IN ('PRIMARY KEY', 'UNIQUE');"));
    }

    [Fact]
    public async Task ExcessiveKeyPartCountsAreRejectedBeforePhysicalCreation()
    {
        const string indexTable = "excessive_index_parts";
        const string primaryTable = "excessive_primary_parts";
        const string uniqueTable = "excessive_unique_parts";
        var columns = Enumerable
            .Range(0, 17)
            .Select(index => $"value_{index.ToString("D2", CultureInfo.InvariantCulture)}")
            .ToArray();

        var columnDefinitions = string.Join(
            ", ",
            columns.Select(static column => $"`{column}` int NOT NULL"));

        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `{indexTable}` (`id` int NOT NULL, {columnDefinitions}, PRIMARY KEY (`id`)); "
            + $"CREATE TABLE `{primaryTable}` ({columnDefinitions}); "
            + $"CREATE TABLE `{uniqueTable}` (`id` int NOT NULL, {columnDefinitions}, PRIMARY KEY (`id`));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("ix_excessive_parts", indexTable, columns);
        builder.AddPrimaryKeyIfNotExists("pk_excessive_parts", primaryTable, columns);
        builder.AddUniqueConstraintIfNotExists("uq_excessive_parts", uniqueTable, columns);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("excessive-key-parts"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Collection(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
                Assert.Equal("index_too_many_key_parts", assessment.Code);
            },
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
                Assert.Equal("primary_key_too_many_columns", assessment.Code);
            },
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
                Assert.Equal("unique_constraint_too_many_columns", assessment.Code);
            });
    }

    [Fact]
    public async Task MaximumCompositeKeyNamesUseOrdinalIdentityWithoutGroupConcatTruncation()
    {
        const string primaryTable = "long_composite_primary";
        const string uniqueTable = "long_composite_unique";
        const string uniqueName = "uq_long_composite_unique";
        var columns = Enumerable
            .Range(0, 16)
            .Select(index => MySqlMaximumIdentifier($"c{index.ToString("D2", CultureInfo.InvariantCulture)}_"))
            .ToArray();

        var columnDefinitions = string.Join(
            ", ",
            columns.Select(static column => $"`{column}` int NOT NULL"));

        var keyColumns = string.Join(", ", columns.Select(static column => $"`{column}`"));
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `{primaryTable}` ({columnDefinitions}, PRIMARY KEY ({keyColumns})); "
            + $"CREATE TABLE `{uniqueTable}` (`id` int NOT NULL, {columnDefinitions}, PRIMARY KEY (`id`), "
            + $"CONSTRAINT `{uniqueName}` UNIQUE ({keyColumns}));");

        await using var context = CreateContext(connectionString);
        var matching = new MigrationBuilder(context.Database.ProviderName!);
        matching.AddPrimaryKeyIfNotExists("pk_long_composite_primary", primaryTable, columns);
        matching.AddUniqueConstraintIfNotExists(uniqueName, uniqueTable, columns);

        var runner = context.GetService<ISafeMigrationRunner>();
        var matchingReport = await runner.AnalyzeAsync(
            context,
            matching.Operations,
            new SafeMigrationRunOptions("long-composite-matching"));

        var reversed = columns.Reverse().ToArray();
        var different = new MigrationBuilder(context.Database.ProviderName!);
        different.AddPrimaryKeyIfNotExists("pk_long_composite_primary", primaryTable, reversed);
        different.AddUniqueConstraintIfNotExists(uniqueName, uniqueTable, reversed);

        var differentReport = await runner.AnalyzeAsync(
            context,
            different.Operations,
            new SafeMigrationRunOptions("long-composite-different"));

        Assert.Equal(SafeMigrationReportStatus.Ready, matchingReport.Status);
        Assert.All(
            matchingReport.Assessments,
            static assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
            });
        Assert.Equal(SafeMigrationReportStatus.Blocked, differentReport.Status);
        Assert.All(
            differentReport.Assessments,
            static assessment => Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState));
    }
}
