namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task VarcharWidening_RepairsWithoutDataLossAndReplaysAsNoOp()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `varchar_widening` ("
            + "`id` int NOT NULL, "
            + "`value` varchar(10) NULL DEFAULT 'legacy' COMMENT 'legacy', "
            + "PRIMARY KEY (`id`), "
            + "INDEX `ix_varchar_widening_value` (`value`), "
            + "INDEX `ix_varchar_widening_value_id` (`value`, `id`)) "
            + "ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + "INSERT INTO `varchar_widening` (`id`, `value`) VALUES (1, 'preserved');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "varchar_widening",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: true,
                storeType: "varchar(200)",
                maxLength: 200,
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("legacy")),
            SafeMigrationPolicy.RepairIfSafe);

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("varchar-widening"),
            CancellationToken.None);

        var providerAnalysis = await context
            .GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(
                context,
                builder.Operations.Cast<SafeMigrationOperation>().ToArray(),
                CancellationToken.None);

        var assessment = Assert.Single(preflight.Assessments);
        var providerAssessment = Assert.Single(providerAnalysis);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Repair, assessment.Action);
        Assert.Equal(SafeMigrationOperationalImpact.TableRewritePossible, assessment.OperationalImpact);
        Assert.Contains(assessment.Differences, difference => difference.Facet == "column_max_length");
        Assert.Contains(assessment.Differences, difference => difference.Facet == "column_comment_digest");
        Assert.False(providerAssessment.RequiresLiveDataProof);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("varchar-widening"),
            CancellationToken.None);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("varchar-widening-replay"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.True(Assert.Single(postflight.Assessments).PostconditionSatisfied);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(replay.Assessments).ObservedState);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `varchar_widening` "
                + "WHERE `id` = 1 AND `value` = 'preserved';"));
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'varchar_widening' "
                + "AND INDEX_NAME IN ('ix_varchar_widening_value', 'ix_varchar_widening_value_id') "
                + "AND SEQ_IN_INDEX = 1 AND COLUMN_NAME = 'value';"));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    public async Task VarcharRepair_RejectsForeignKeyDependentColumns(
        int targetLength
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `varchar_fk_parent` ("
            + "`code` varchar(10) NOT NULL, PRIMARY KEY (`code`)) ENGINE=InnoDB; "
            + "CREATE TABLE `varchar_fk_child` ("
            + "`id` int NOT NULL, `parent_code` varchar(10) NOT NULL, PRIMARY KEY (`id`), "
            + "INDEX `ix_varchar_fk_child_parent` (`parent_code`), "
            + "CONSTRAINT `fk_varchar_fk_child_parent` FOREIGN KEY (`parent_code`) "
            + "REFERENCES `varchar_fk_parent` (`code`)) ENGINE=InnoDB; "
            + "INSERT INTO `varchar_fk_parent` (`code`) VALUES ('core'); "
            + "INSERT INTO `varchar_fk_child` (`id`, `parent_code`) VALUES (1, 'core');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "varchar_fk_parent",
            new ExpectedColumnDefinition(
                "code",
                typeof(string),
                isNullable: false,
                storeType: $"varchar({targetLength})",
                maxLength: targetLength),
            SafeMigrationPolicy.RepairIfSafe);
        builder.EnsureColumn(
            "varchar_fk_child",
            new ExpectedColumnDefinition(
                "parent_code",
                typeof(string),
                isNullable: false,
                storeType: $"varchar({targetLength})",
                maxLength: targetLength),
            SafeMigrationPolicy.RepairIfSafe);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("varchar-foreign-key-blocked"),
                CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.All(report.Assessments, static assessment =>
        {
            Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
            Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
            Assert.Equal(SafeMigrationOperationalImpact.NotApplicable, assessment.OperationalImpact);
        });
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() "
                + "AND TABLE_NAME IN ('varchar_fk_parent', 'varchar_fk_child') "
                + "AND COLUMN_NAME IN ('code', 'parent_code') "
                + "AND CHARACTER_MAXIMUM_LENGTH = 10;"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
                + "WHERE CONSTRAINT_SCHEMA = DATABASE() "
                + "AND CONSTRAINT_NAME = 'fk_varchar_fk_child_parent';"));
    }

    [Fact]
    public async Task VarcharNarrowing_GroupsCharacterProofsAndPreservesUtf8AndTrailingSpaces()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `varchar_narrowing` ("
            + "`id` int NOT NULL, "
            + "`unicode_value` varchar(40) NULL, "
            + "`trailing_value` varchar(40) NULL, "
            + "`empty_value` varchar(40) NULL, "
            + "`required_value` varchar(40) NOT NULL, "
            + "PRIMARY KEY (`id`)) "
            + "ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + "CREATE TABLE `varchar_narrowing_single` ("
            + "`id` int NOT NULL, `value` varchar(40) NULL, PRIMARY KEY (`id`)) "
            + "ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + "INSERT INTO `varchar_narrowing` "
            + "(`id`, `unicode_value`, `trailing_value`, `empty_value`, `required_value`) "
            + "VALUES (1, REPEAT(CONVERT(0xF09F9880 USING utf8mb4), 5), "
            + "CONCAT('a', REPEAT(' ', 3)), NULL, 'exact'); "
            + "INSERT INTO `varchar_narrowing_single` (`id`, `value`) VALUES (1, 'fits');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        (string Name, bool IsNullable)[] columns =
        [
            ("unicode_value", true),
            ("trailing_value", true),
            ("empty_value", true),
            ("required_value", false),
        ];

        foreach (var column in columns)
        {
            builder.EnsureColumn(
                "varchar_narrowing",
                new ExpectedColumnDefinition(
                    column.Name,
                    typeof(string),
                    column.IsNullable,
                    storeType: "varchar(5)",
                    maxLength: 5),
                SafeMigrationPolicy.RepairIfSafe);
        }

        var singleBuilder = new MigrationBuilder(context.Database.ProviderName!);
        singleBuilder.EnsureColumn(
            "varchar_narrowing_single",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: true,
                storeType: "varchar(5)",
                maxLength: 5),
            SafeMigrationPolicy.RepairIfSafe);

        var singleQueryCount = await CountProviderAnalysisQuestionsAsync(
            context,
            singleBuilder.Operations);

        var groupedQueryCount = await CountProviderAnalysisQuestionsAsync(
            context,
            builder.Operations);

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("varchar-narrowing"),
            CancellationToken.None);

        var providerAnalysis = await context
            .GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(
                context,
                builder.Operations.Cast<SafeMigrationOperation>().ToArray(),
                CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(
            preflight.Assessments,
            static assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.Repair, assessment.Action);
                Assert.Equal(SafeMigrationOperationalImpact.TableRewritePossible, assessment.OperationalImpact);
            });
        Assert.All(providerAnalysis, static analysis => Assert.True(analysis.RequiresLiveDataProof));

        // WHY: The remaining analyzer statements are batched identically, so
        // equal command counts prove that four columns add only one table scan.
        Assert.Equal(singleQueryCount, groupedQueryCount);
        Assert.InRange(groupedQueryCount, 1, 8);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("varchar-narrowing"),
            CancellationToken.None);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("varchar-narrowing-replay"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.All(replay.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(
            5,
            await ScalarIntAsync(
                connectionString,
                "SELECT CHAR_LENGTH(`unicode_value`) FROM `varchar_narrowing` WHERE `id` = 1;"));
        Assert.Equal(
            4,
            await ScalarIntAsync(
                connectionString,
                "SELECT CHAR_LENGTH(`trailing_value`) FROM `varchar_narrowing` WHERE `id` = 1;"));
        Assert.Equal(
            5,
            await ScalarIntAsync(
                connectionString,
                "SELECT CHAR_LENGTH(`required_value`) FROM `varchar_narrowing` WHERE `id` = 1;"));
    }

    [Fact]
    public async Task VarcharNarrowing_RejectsOverlengthDataWithoutDisclosingTheValue()
    {
        const string privateValue = "private-overlength-value";

        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `varchar_narrowing_blocked` ("
            + "`id` int NOT NULL, `value` varchar(40) NULL, PRIMARY KEY (`id`)) "
            + "ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + "INSERT INTO `varchar_narrowing_blocked` (`id`, `value`) "
            + $"VALUES (1, 'fits'), (2, 'exact'), (3, '{privateValue}');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "varchar_narrowing_blocked",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: true,
                storeType: "varchar(5)",
                maxLength: 5),
            SafeMigrationPolicy.RepairIfSafe);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("varchar-narrowing-blocked"),
                CancellationToken.None);

        var assessment = Assert.Single(report.Assessments);
        var exception = Assert.Throws<SafeMigrationPreflightException>(report.ThrowIfBlocked);

        Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, assessment.Action);
        Assert.Equal("varchar_narrowing_value_too_long", assessment.AnalysisCode);
        Assert.Equal("data_blocked", assessment.DecisionCode);
        Assert.Same(report, exception.Report);
        Assert.DoesNotContain(privateValue, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            assessment.Differences,
            difference => difference.Expected.Contains(privateValue, StringComparison.Ordinal)
                || difference.Actual.Contains(privateValue, StringComparison.Ordinal));
        Assert.Equal(
            40,
            await ScalarIntAsync(
                connectionString,
                "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'varchar_narrowing_blocked' "
                + "AND COLUMN_NAME = 'value';"));
    }

    [Fact]
    public async Task VarcharNarrowing_RechecksDataImmediatelyBeforeMutation()
    {
        const string concurrentValue = "arrived-after-preflight";

        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `varchar_narrowing_race` ("
            + "`id` int NOT NULL, `value` varchar(40) NULL, PRIMARY KEY (`id`)) "
            + "ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + "INSERT INTO `varchar_narrowing_race` (`id`, `value`) VALUES (1, 'fits');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "varchar_narrowing_race",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: true,
                storeType: "varchar(5)",
                maxLength: 5),
            SafeMigrationPolicy.RepairIfSafe);

        var preflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("varchar-narrowing-race"),
                CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);

        await ExecuteSqlAsync(
            connectionString,
            $"INSERT INTO `varchar_narrowing_race` (`id`, `value`) VALUES (2, '{concurrentValue}');");

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        Assert.Contains("doka_sm_data_blocked", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            40,
            await ScalarIntAsync(
                connectionString,
                "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'varchar_narrowing_race' "
                + "AND COLUMN_NAME = 'value';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM `varchar_narrowing_race` WHERE `value` = '{concurrentValue}';"));
    }

    [Fact]
    public async Task VarcharNarrowing_PropagatesProbeCommandTimeoutWithoutMutation()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `varchar_narrowing_timeout` ("
            + "`id` int NOT NULL, `value` varchar(40) NULL, PRIMARY KEY (`id`)) "
            + "ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + "INSERT INTO `varchar_narrowing_timeout` (`id`, `value`) VALUES (1, 'fits');");

        await using var blocker = new MySqlConnection(connectionString);
        await blocker.OpenAsync(CancellationToken.None);
        await using (var lockCommand = blocker.CreateCommand())
        {
            lockCommand.CommandText = "LOCK TABLES `varchar_narrowing_timeout` WRITE;";

            _ = await lockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var context = CreateContext(connectionString);
        context.Database.SetCommandTimeout(1);
        var builder = BuildVarcharNarrowingOperation(
            context.Database.ProviderName!,
            "varchar_narrowing_timeout");

        _ = await Assert.ThrowsAsync<MySqlException>(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("varchar-narrowing-timeout"),
                CancellationToken.None));

        Assert.Equal(
            40,
            await ScalarIntAsync(
                connectionString,
                "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'varchar_narrowing_timeout' "
                + "AND COLUMN_NAME = 'value';"));
    }

    [Fact]
    public async Task VarcharNarrowing_PropagatesProbeCancellationWithoutMutation()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `varchar_narrowing_cancellation` ("
            + "`id` int NOT NULL, `value` varchar(40) NULL, PRIMARY KEY (`id`)) "
            + "ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + "INSERT INTO `varchar_narrowing_cancellation` (`id`, `value`) VALUES (1, 'fits');");

        await using var blocker = new MySqlConnection(connectionString);
        await blocker.OpenAsync(CancellationToken.None);
        await using (var lockCommand = blocker.CreateCommand())
        {
            lockCommand.CommandText = "LOCK TABLES `varchar_narrowing_cancellation` WRITE;";

            _ = await lockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var context = CreateContext(connectionString);
        var builder = BuildVarcharNarrowingOperation(
            context.Database.ProviderName!,
            "varchar_narrowing_cancellation");

        using var cancellation = new CancellationTokenSource();
        var analysis = context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("varchar-narrowing-cancellation"),
                cancellation.Token);

        await WaitForBlockedMySqlNarrowingProbeAsync(
            connectionString,
            "varchar_narrowing_cancellation",
            CancellationToken.None);
        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analysis);

        Assert.Equal(
            40,
            await ScalarIntAsync(
                connectionString,
                "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'varchar_narrowing_cancellation' "
                + "AND COLUMN_NAME = 'value';"));
    }

    [Fact]
    public async Task VarcharNarrowing_RejectsRepairWhenStrictSessionModeIsDisabled()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `varchar_narrowing_non_strict` ("
            + "`id` int NOT NULL, `value` varchar(40) NULL, PRIMARY KEY (`id`)) "
            + "ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + "INSERT INTO `varchar_narrowing_non_strict` (`id`, `value`) VALUES (1, 'fits');");

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await using (var command = context.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "SET SESSION sql_mode = '';";

            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "varchar_narrowing_non_strict",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: true,
                storeType: "varchar(5)",
                maxLength: 5),
            SafeMigrationPolicy.RepairIfSafe);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("varchar-narrowing-non-strict"),
                CancellationToken.None);

        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal(SafeMigrationOperationalImpact.NotApplicable, assessment.OperationalImpact);
        Assert.Equal(
            40,
            await ScalarIntAsync(
                connectionString,
                "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'varchar_narrowing_non_strict' "
                + "AND COLUMN_NAME = 'value';"));
    }

    [Fact]
    public async Task BooleanBitToTinyInt_PreservesTheCompleteValueDomainAndReplaysAsNoOp()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `boolean_repairs` ("
            + "`id` int NOT NULL, `required_value` bit(1) NOT NULL DEFAULT b'0', "
            + "`true_value` bit(1) NOT NULL DEFAULT b'1', "
            + "`nullable_value` bit(1) NULL DEFAULT NULL, PRIMARY KEY (`id`), "
            + "INDEX `ix_boolean_repairs_required` (`required_value`)) ENGINE=InnoDB; "
            + "INSERT INTO `boolean_repairs` (`id`, `required_value`, `true_value`, `nullable_value`) "
            + "VALUES (1, b'0', b'1', NULL), (2, b'1', b'1', b'0'), (3, b'1', b'1', b'1');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "boolean_repairs",
            new ExpectedColumnDefinition(
                "required_value",
                typeof(bool),
                isNullable: false,
                storeType: "tinyint(1)",
                defaultValue: SafeMigrationDefaultValue.Literal(false)),
            SafeMigrationPolicy.RepairIfSafe);
        builder.EnsureColumn(
            "boolean_repairs",
            new ExpectedColumnDefinition(
                "true_value",
                typeof(bool),
                isNullable: false,
                storeType: "tinyint(1)",
                defaultValue: SafeMigrationDefaultValue.Literal(true)),
            SafeMigrationPolicy.RepairIfSafe);
        builder.EnsureColumn(
            "boolean_repairs",
            new ExpectedColumnDefinition(
                "nullable_value",
                typeof(bool),
                isNullable: true,
                storeType: "tinyint(1)"),
            SafeMigrationPolicy.RepairIfSafe);

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("boolean-repair"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(
            preflight.Assessments,
            static assessment => Assert.Equal(SafeMigrationAction.Repair, assessment.Action));
        Assert.All(
            preflight.Assessments,
            static assessment =>
                Assert.Equal(SafeMigrationOperationalImpact.TableRewritePossible, assessment.OperationalImpact));

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("boolean-repair"),
            CancellationToken.None);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("boolean-repair-replay"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(replay.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(
            3,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `boolean_repairs` "
                + "WHERE (`id` = 1 AND `required_value` = 0 AND `true_value` = 1 "
                + "AND `nullable_value` IS NULL) "
                + "OR (`id` = 2 AND `required_value` = 1 AND `true_value` = 1 "
                + "AND `nullable_value` = 0) "
                + "OR (`id` = 3 AND `required_value` = 1 AND `true_value` = 1 "
                + "AND `nullable_value` = 1);"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'boolean_repairs' "
                + "AND INDEX_NAME = 'ix_boolean_repairs_required';"));
    }

    [Fact]
    public async Task BooleanRepair_RejectsExpressionDefaultsAndForeignKeyDependencies()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `boolean_default_blocked` (`value` bit(1) NULL DEFAULT (0 + 0)) ENGINE=InnoDB; "
            + "CREATE TABLE `boolean_parent` (`value` bit(1) NOT NULL, PRIMARY KEY (`value`)) ENGINE=InnoDB; "
            + "CREATE TABLE `boolean_child` (`id` int NOT NULL, `parent_value` bit(1) NOT NULL, "
            + "PRIMARY KEY (`id`), INDEX `ix_boolean_child_parent` (`parent_value`), "
            + "CONSTRAINT `fk_boolean_child_parent` FOREIGN KEY (`parent_value`) "
            + "REFERENCES `boolean_parent` (`value`)) ENGINE=InnoDB;");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "boolean_default_blocked",
            new ExpectedColumnDefinition(
                "value",
                typeof(bool),
                isNullable: true,
                storeType: "tinyint(1)"),
            SafeMigrationPolicy.RepairIfSafe);
        builder.EnsureColumn(
            "boolean_parent",
            new ExpectedColumnDefinition(
                "value",
                typeof(bool),
                isNullable: false,
                storeType: "tinyint(1)"),
            SafeMigrationPolicy.RepairIfSafe);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("boolean-contract-blocked"),
                CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.All(report.Assessments, static assessment =>
        {
            Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
            Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
            Assert.Equal(SafeMigrationOperationalImpact.NotApplicable, assessment.OperationalImpact);
        });
        Assert.Equal(
            "bit(1)",
            await ScalarStringAsync(
                connectionString,
                "SELECT LOWER(COLUMN_TYPE) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'boolean_default_blocked' "
                + "AND COLUMN_NAME = 'value';"));
        Assert.Equal(
            "bit(1)",
            await ScalarStringAsync(
                connectionString,
                "SELECT LOWER(COLUMN_TYPE) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'boolean_parent' "
                + "AND COLUMN_NAME = 'value';"));
    }

    [Theory]
    [InlineData("bit(2)", typeof(bool), "tinyint(1)")]
    [InlineData("bit(8)", typeof(bool), "tinyint(1)")]
    [InlineData("bit(64)", typeof(bool), "tinyint(1)")]
    [InlineData("bit(1)", typeof(int), "tinyint(1)")]
    [InlineData("tinyint(1)", typeof(bool), "bit(1)")]
    public async Task NeighboringBooleanTransitions_RemainBlocked(
        string liveStoreType,
        Type targetClrType,
        string targetStoreType
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `boolean_repair_blocked` (`value` {liveStoreType} NULL) ENGINE=InnoDB;");

        var originalStoreType = await ScalarStringAsync(
            connectionString,
            "SELECT LOWER(COLUMN_TYPE) FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'boolean_repair_blocked' "
            + "AND COLUMN_NAME = 'value';");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "boolean_repair_blocked",
            new ExpectedColumnDefinition(
                "value",
                targetClrType,
                isNullable: true,
                storeType: targetStoreType),
            SafeMigrationPolicy.RepairIfSafe);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("boolean-repair-blocked"),
                CancellationToken.None);

        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal(SafeMigrationOperationalImpact.NotApplicable, assessment.OperationalImpact);
        Assert.Equal(
            originalStoreType,
            await ScalarStringAsync(
                connectionString,
                "SELECT LOWER(COLUMN_TYPE) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'boolean_repair_blocked' "
                + "AND COLUMN_NAME = 'value';"));
    }

    [Theory]
    [InlineData("char(10)", "varchar(20)")]
    [InlineData("text", "varchar(20)")]
    [InlineData("varbinary(10)", "varchar(20)")]
    [InlineData("enum('a','b')", "varchar(20)")]
    [InlineData("set('a','b')", "varchar(20)")]
    [InlineData("varchar(10)", "char(20)")]
    public async Task NeighboringStringFamilyTransitions_RemainBlocked(
        string liveStoreType,
        string targetStoreType
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `string_family_blocked` (`value` {liveStoreType} NULL) ENGINE=InnoDB;");

        var originalStoreType = await ScalarStringAsync(
            connectionString,
            "SELECT LOWER(COLUMN_TYPE) FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'string_family_blocked' "
            + "AND COLUMN_NAME = 'value';");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "string_family_blocked",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: true,
                storeType: targetStoreType,
                maxLength: 20,
                isFixedLength: targetStoreType.StartsWith("char", StringComparison.Ordinal)),
            SafeMigrationPolicy.RepairIfSafe);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("string-family-blocked"),
                CancellationToken.None);

        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal(SafeMigrationOperationalImpact.NotApplicable, assessment.OperationalImpact);
        Assert.Equal(
            originalStoreType,
            await ScalarStringAsync(
                connectionString,
                "SELECT LOWER(COLUMN_TYPE) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'string_family_blocked' "
                + "AND COLUMN_NAME = 'value';"));
    }

    [Fact]
    public async Task VarcharRepair_RejectsUnknownStorageEngineAndCollationDrift()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `varchar_engine_blocked` (`value` varchar(10) NULL) "
            + "ENGINE=MyISAM DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + "CREATE TABLE `varchar_collation_blocked` (`value` varchar(10) NULL) "
            + "ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci;");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "varchar_engine_blocked",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: true,
                storeType: "varchar(20)",
                maxLength: 20),
            SafeMigrationPolicy.RepairIfSafe);
        builder.EnsureColumn(
            "varchar_collation_blocked",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: true,
                storeType: "varchar(20)",
                maxLength: 20,
                collation: new SafeMigrationCollationIdentifier("utf8mb4_unicode_ci")),
            SafeMigrationPolicy.RepairIfSafe);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("varchar-physical-contract"),
                CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.All(report.Assessments, static assessment =>
        {
            Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
            Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
            Assert.Equal(SafeMigrationOperationalImpact.NotApplicable, assessment.OperationalImpact);
        });
    }

    [Fact]
    public async Task VarcharWidening_RejectsTargetDeclaredRowAboveServerLimit()
    {
        const int fillerColumnCount = 12;

        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var fillerColumns = string.Join(
            ", ",
            Enumerable.Range(0, fillerColumnCount).Select(index => $"`f{index}` varchar(5000) NULL"));

        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `varchar_row_size_blocked` ({fillerColumns}, `value` varchar(10) NULL) "
            + "ENGINE=InnoDB ROW_FORMAT=DYNAMIC DEFAULT CHARACTER SET latin1;");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "varchar_row_size_blocked",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: true,
                storeType: "varchar(13000)",
                maxLength: 13000),
            SafeMigrationPolicy.RepairIfSafe);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("varchar-row-size-blocked"),
                CancellationToken.None);

        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal(SafeMigrationOperationalImpact.NotApplicable, assessment.OperationalImpact);
        Assert.Equal(
            10,
            await ScalarIntAsync(
                connectionString,
                "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'varchar_row_size_blocked' "
                + "AND COLUMN_NAME = 'value';"));
    }

    [Fact]
    public async Task BooleanRepair_RechecksCatalogShapeAndNullabilityBeforeMutation()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `boolean_repair_race` ("
            + "`id` int NOT NULL PRIMARY KEY, `value` bit(1) NULL); "
            + "INSERT INTO `boolean_repair_race` (`id`, `value`) VALUES (1, NULL);");

        await using var context = CreateContext(connectionString);
        var tightening = new MigrationBuilder(context.Database.ProviderName!);
        tightening.EnsureColumn(
            "boolean_repair_race",
            new ExpectedColumnDefinition(
                "value",
                typeof(bool),
                isNullable: false,
                storeType: "tinyint(1)",
                defaultValue: SafeMigrationDefaultValue.Literal(false)),
            SafeMigrationPolicy.RepairIfSafe);

        var runner = context.GetService<ISafeMigrationRunner>();
        var blocked = await runner.AnalyzeAsync(
            context,
            tightening.Operations,
            new SafeMigrationRunOptions("boolean-null-blocked"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationObservedState.DataBlocked, Assert.Single(blocked.Assessments).ObservedState);

        await ExecuteSqlAsync(
            connectionString,
            "UPDATE `boolean_repair_race` SET `value` = b'0';");

        var preflight = await runner.AnalyzeAsync(
            context,
            tightening.Operations,
            new SafeMigrationRunOptions("boolean-catalog-race"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);

        await ExecuteSqlAsync(
            connectionString,
            "ALTER TABLE `boolean_repair_race` MODIFY COLUMN `value` bit(2) NULL;");

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, tightening.Operations, CancellationToken.None));

        Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "bit(2)",
            await ScalarStringAsync(
                connectionString,
                "SELECT LOWER(COLUMN_TYPE) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'boolean_repair_race' "
                + "AND COLUMN_NAME = 'value';"));
    }

    private static async Task<int> CountProviderAnalysisQuestionsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations
    )
    {
        var safeOperations = operations
            .Cast<SafeMigrationOperation>()
            .ToArray();

        var questionsBefore = await ReadSessionQuestionsAsync(context);

        _ = await context
            .GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(context, safeOperations, CancellationToken.None);

        var questionsAfter = await ReadSessionQuestionsAsync(context);

        return questionsAfter - questionsBefore;
    }

    private static async Task<int> ReadSessionQuestionsAsync(
        DbContext context
    )
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(CancellationToken.None);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SHOW SESSION STATUS LIKE 'Questions';";

        // WHY: SHOW SESSION STATUS is supported by both MySQL and MariaDB,
        // whereas MySQL 8.4 does not expose INFORMATION_SCHEMA.SESSION_STATUS.
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        if (!await reader.ReadAsync(CancellationToken.None))
        {
            throw new InvalidOperationException(
                "The server did not return the session Questions status variable.");
        }

        return Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
    }

    private static MigrationBuilder BuildVarcharNarrowingOperation(
        string providerName,
        string table
    )
    {
        var builder = new MigrationBuilder(providerName);
        builder.EnsureColumn(
            table,
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: true,
                storeType: "varchar(5)",
                maxLength: 5),
            SafeMigrationPolicy.RepairIfSafe);

        return builder;
    }

    private static async Task WaitForBlockedMySqlNarrowingProbeAsync(
        string connectionString,
        string table,
        CancellationToken cancellationToken
    )
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.PROCESSLIST "
                + "WHERE ID <> CONNECTION_ID() AND INFO LIKE @probe;";
            command.Parameters.AddWithValue("@probe", $"%{table}%CHAR_LENGTH%");

            var count = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);

            if (count > 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        throw new TimeoutException("The MySQL narrowing proof did not reach its blocked data probe.");
    }
}
