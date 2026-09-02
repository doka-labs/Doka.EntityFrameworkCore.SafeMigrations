namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task ProviderDropThenSafeCreateIndex_ProjectsTheOrderedReplacement()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `provider_index_replacement` (`code` int NOT NULL, "
            + "INDEX `ix_provider_index_replacement` (`code`));");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndex("ix_provider_index_replacement", "provider_index_replacement");
        builder.CreateIndexIfNotExists(
            "ix_provider_index_replacement",
            "provider_index_replacement",
            ["code"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("provider-index-replacement"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, preflight.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[1].Action);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(DISTINCT INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'provider_index_replacement' "
                + "AND INDEX_NAME = 'ix_provider_index_replacement' AND COLUMN_NAME = 'code';"));
    }

    [Fact]
    public async Task SafeDropThenSafeCreateIndex_ProjectsTheOrderedReplacement()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `safe_index_replacement` (`code` int NOT NULL, "
            + "INDEX `ix_safe_index_replacement` (`code`));");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("ix_safe_index_replacement", "safe_index_replacement");
        builder.CreateIndexIfNotExists(
            "ix_safe_index_replacement",
            "safe_index_replacement",
            ["code"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("safe-index-replacement"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[0].Action);
        Assert.Equal(SafeMigrationObservedState.Missing, preflight.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[1].Action);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(DISTINCT INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'safe_index_replacement' "
                + "AND INDEX_NAME = 'ix_safe_index_replacement' AND COLUMN_NAME = 'code';"));
    }

    [Fact]
    public async Task SafeDropThenMetadataAlterationsAndPrefixReplacement_ProjectsTheOrderedState()
    {
        const string indexName = "ix_annotation_records_tenant_id_code";
        const string tableName = "annotation_records";
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `{tableName}` ("
            + "`TenantId` int NOT NULL, "
            + "`Code` varchar(180) COLLATE utf8mb4_unicode_ci NOT NULL COMMENT 'source code comment', "
            + "`Description` varchar(240) NOT NULL DEFAULT 'source default', "
            + $"INDEX `{indexName}` (`TenantId`, `Code`(24))) "
            + "CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci COMMENT 'source table comment';");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);

        _ = builder.DropIndexIfExists(indexName, tableName);
        _ = builder.AlterTable(
            name: tableName,
            comment: "target table comment",
            oldComment: "source table comment");
        _ = builder.AlterColumn<string>(
            name: "Code",
            table: tableName,
            type: "varchar(180)",
            maxLength: 180,
            nullable: false,
            comment: "target code comment",
            collation: "utf8mb4_bin",
            oldClrType: typeof(string),
            oldType: "varchar(180)",
            oldMaxLength: 180,
            oldNullable: false,
            oldComment: "source code comment",
            oldCollation: "utf8mb4_unicode_ci");
        _ = builder.AlterColumn<string>(
            name: "Description",
            table: tableName,
            type: "varchar(240)",
            maxLength: 240,
            nullable: false,
            defaultValue: "target default",
            oldClrType: typeof(string),
            oldType: "varchar(240)",
            oldMaxLength: 240,
            oldNullable: false,
            oldDefaultValue: "source default");
        _ = builder.EnsureIndex(
            new ExpectedIndexDefinition(
                indexName,
                tableName,
                [
                    new ExpectedIndexKeyDefinition(column: "TenantId"),
                    new ExpectedIndexKeyDefinition(column: "Code", prefixLength: 48),
                ]),
            SafeMigrationPolicy.ThrowIfDifferent);

        var runner = context.GetService<ISafeMigrationRunner>();
        var options = new SafeMigrationRunOptions("metadata-prefix-index-replacement");

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            options,
            CancellationToken.None);

        var replacement = Assert.Single(
            preflight.Assessments,
            static assessment => assessment.OperationKind == SafeMigrationOperationKind.EnsureIndex);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            options,
            CancellationToken.None);

        var postflightDrop = Assert.Single(
            postflight.Assessments,
            static assessment => assessment.OperationKind == SafeMigrationOperationKind.DropIndex);

        var postflightReplacement = Assert.Single(
            postflight.Assessments,
            static assessment => assessment.OperationKind == SafeMigrationOperationKind.EnsureIndex);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            options,
            CancellationToken.None);

        var replayReplacement = Assert.Single(
            replay.Assessments,
            static assessment => assessment.OperationKind == SafeMigrationOperationKind.EnsureIndex);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, replacement.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, replacement.Action);
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, postflight.Status);
        Assert.True(postflightDrop.PostconditionSatisfied);
        Assert.Equal("postcondition_superseded", postflightDrop.Code);
        Assert.True(postflightReplacement.PostconditionSatisfied);
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, replay.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, replayReplacement.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, replayReplacement.Action);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{tableName}' "
                + $"AND INDEX_NAME = '{indexName}' AND SEQ_IN_INDEX = 2 "
                + "AND COLUMN_NAME = 'Code' AND SUB_PART = 48;"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{tableName}' "
                + "AND COLUMN_NAME = 'Code' AND COLLATION_NAME = 'utf8mb4_bin' "
                + "AND COLUMN_COMMENT = 'target code comment';"));
    }

    [Fact]
    public async Task ProviderDropThenUniqueCreate_PreservesDataBlockedClassificationAndExistingIndex()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `blocked_index_replacement` (`code` int NOT NULL, "
            + "INDEX `ix_blocked_index_replacement` (`code`)); "
            + "INSERT INTO `blocked_index_replacement` (`code`) VALUES (1), (1);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndex("ix_blocked_index_replacement", "blocked_index_replacement");
        builder.CreateIndexIfNotExists(
            "ix_blocked_index_replacement",
            "blocked_index_replacement",
            ["code"],
            unique: true);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("blocked-index-replacement"),
                CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, report.Assessments[1].Action);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(DISTINCT INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'blocked_index_replacement' "
                + "AND INDEX_NAME = 'ix_blocked_index_replacement';"));
    }

    [Fact]
    public async Task ForeignKeySupportIndexWithEquivalentShape_IsAnIdempotentNoOp()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `index_parent` (`id` int NOT NULL, PRIMARY KEY (`id`)); "
            + "CREATE TABLE `index_child` (`id` int NOT NULL, `parent_id` int NOT NULL, PRIMARY KEY (`id`), "
            + "CONSTRAINT `fk_index_child_parent` FOREIGN KEY (`parent_id`) "
            + "REFERENCES `index_parent` (`id`));");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("ix_index_child_parent_id", "index_child", ["parent_id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("foreign-key-support-index"),
            CancellationToken.None);

        var assessment = Assert.Single(preflight.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
        Assert.DoesNotContain(
            preflight.UnexpectedObjects,
            static unexpected => unexpected.ObjectKind == SafeMigrationDatabaseObjectKind.Index);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("foreign-key-support-index-postflight"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(postflight.Assessments).Action);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(DISTINCT INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'index_child' "
                + "AND INDEX_NAME = 'ix_index_child_parent_id';"));
    }

    [Fact]
    public async Task EquivalentUniqueIndexWithForeignKey_IsAnIdempotentNoOp()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `unique_index_parent` (`id` int NOT NULL, PRIMARY KEY (`id`)); "
            + "CREATE TABLE `unique_index_child` (`id` int NOT NULL, `parent_id` int NOT NULL, "
            + "PRIMARY KEY (`id`), UNIQUE INDEX `uq_unique_index_child_parent_id` (`parent_id`), "
            + "CONSTRAINT `fk_unique_index_child_parent` FOREIGN KEY (`parent_id`) "
            + "REFERENCES `unique_index_parent` (`id`));");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists(
            "ix_unique_index_child_parent_id",
            "unique_index_child",
            ["parent_id"],
            unique: true);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("foreign-key-unique-index-conflict"),
                CancellationToken.None);

        var assessment = Assert.Single(report.Assessments);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
        Assert.DoesNotContain(
            report.UnexpectedObjects,
            static unexpected => unexpected.ObjectKind == SafeMigrationDatabaseObjectKind.Index);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(DISTINCT INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'unique_index_child' "
                + "AND INDEX_NAME = 'ix_unique_index_child_parent_id';"));
    }
}
