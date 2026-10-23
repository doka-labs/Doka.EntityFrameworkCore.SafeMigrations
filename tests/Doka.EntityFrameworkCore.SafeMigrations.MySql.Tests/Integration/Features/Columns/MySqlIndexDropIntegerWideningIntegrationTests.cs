namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Qualifies lossless integer upgrades after ordinary index removal without stale source proofs.</summary>
public sealed class MySqlIndexDropIntegerWideningIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    private const string Table = "integer_upgrade";

    /// <summary>Shares the native engine while retaining an isolated database for each case.</summary>
    /// <param name="fixture">The assembly-owned configured native engine.</param>
    public MySqlIndexDropIntegerWideningIntegrationTests(MySqlEngineContainerFixture fixture) : base(fixture) { }

    /// <summary>Exact signed widening preserves all rows, including signed extremes, and supports replay.</summary>
    /// <param name="dropIndex">Whether the ordered stream first drops the original ordinary index.</param>
    /// <param name="alreadyDropped">Whether an interrupted earlier execution already removed that index.</param>
    /// <param name="qualified">Whether the operations explicitly name their current database.</param>
    /// <param name="completedWidenings">The already applied native prefix, before history was recorded.</param>
    /// <param name="columnAliases">Whether native column references use different valid letter casing.</param>
    [Theory]
    [InlineData(false, false, false, 0, false)]
    [InlineData(true, false, false, 0, false)]
    [InlineData(true, true, false, 0, false)]
    [InlineData(true, false, true, 0, false)]
    [InlineData(true, true, false, 1, false)]
    [InlineData(true, true, false, 2, false)]
    [InlineData(true, false, false, 0, true)]
    [InlineData(true, true, false, 1, true)]
    public async Task ExactIntegerUpgradePreservesRowsAndReplays(
        bool dropIndex,
        bool alreadyDropped,
        bool qualified,
        int completedWidenings,
        bool columnAliases
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, FixtureSql(alreadyDropped));
        await ApplyNativeWideningPrefixAsync(connectionString, completedWidenings);
        await using var context = CreateContext(connectionString);
        var schema = qualified ? new MySqlConnectionStringBuilder(connectionString).Database : null;
        var operations = UpgradeOperations(dropIndex, schema, columnAliases);
        var runner = context.GetService<ISafeMigrationRunner>();
        var before = await ReadCreateAsync(connectionString);

        // Act
        var preflight = await runner.AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("integer-upgrade"), CancellationToken.None);

        var afterAnalysis = await ReadCreateAsync(connectionString);
        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, operations);
        var replay = await runner.AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("integer-upgrade-replay"), CancellationToken.None);

        replay.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, operations);
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `integer_upgrade` WHERE (`id`=1 AND `left_value`=-2147483648 "
            + "AND `right_value`=2147483647 AND `position`=0 AND `payload`='first') "
            + "OR (`id`=2 AND `left_value`=0 AND `right_value`=1 AND `position`=2 AND `payload`='second');");

        var widened = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() "
            + "AND TABLE_NAME='integer_upgrade' AND COLUMN_NAME IN ('left_value','right_value','position') "
            + "AND DATA_TYPE='bigint' AND IS_NULLABLE='NO' AND COLUMN_DEFAULT IS NULL;");

        // Assert
        Assert.Equal(before, afterAnalysis);
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.Equal(2, rows);
        Assert.Equal(3, widened);
    }

    /// <summary>Three independently present index removals retain all three exact integer source proofs.</summary>
    [Fact]
    public async Task ThreePhysicalIndexDropsPreserveUpgradeExecutionAndReplay()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, FixtureSql(alreadyDropped: false)
            + "ALTER TABLE `integer_upgrade` ADD INDEX `ix_upgrade_right` (`right_value`), "
            + "ADD INDEX `ix_upgrade_position` (`position`);");
        await using var context = CreateContext(connectionString);
        var operations = UpgradeOperations(dropIndex: true, schema: null);
        operations.Insert(1, new SafeMigrationOperation(new DropIndexIntent("ix_upgrade_right", Table),
            SafeMigrationPolicy.ThrowIfDifferent));
        operations.Insert(2, new SafeMigrationOperation(new DropIndexIntent("ix_upgrade_position", Table),
            SafeMigrationPolicy.ThrowIfDifferent));
        var runner = context.GetService<ISafeMigrationRunner>();
        var before = await ReadCreateAsync(connectionString);

        // Act
        var preflight = await runner.AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("integer-upgrade-three-indexes"));

        var afterAnalysis = await ReadCreateAsync(connectionString);
        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, operations);
        var replay = await runner.AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("integer-upgrade-three-indexes-replay"));

        replay.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, operations);
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `integer_upgrade`;");

        // Assert
        Assert.Equal(before, afterAnalysis);
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments.Take(3), static item => Assert.Equal(SafeMigrationAction.Apply, item.Action));
        Assert.All(preflight.Assessments.Skip(3).Take(3),
            static item => Assert.Equal(SafeMigrationAction.Repair, item.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.Equal(2, rows);
    }

    /// <summary>Applies the actual pending upgrade once and leaves history stable on replay.</summary>
    /// <param name="alreadyDropped">Whether interruption removed the index before the migration was recorded.</param>
    /// <param name="completedWidenings">The already applied native prefix, before history was recorded.</param>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task PendingIntegerUpgradeExecutesAndReplaysHistory(bool alreadyDropped, int completedWidenings)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, FixtureSql(alreadyDropped));
        await ApplyNativeWideningPrefixAsync(connectionString, completedWidenings);
        await using var context = new IndexDropIntegerUpgradeDbContext(connectionString, Fixture.ServerVersion);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var pending = await runner.AnalyzePendingMigrationsAsync(context,
            new SafeMigrationRunOptions("integer-upgrade-pending"));

        var beforeHistory = await context.Database.GetAppliedMigrationsAsync();
        pending.ThrowIfBlocked();
        await context.Database.MigrateAsync();
        var applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        var replay = await runner.AnalyzePendingMigrationsAsync(context,
            new SafeMigrationRunOptions("integer-upgrade-completed"));

        replay.ThrowIfBlocked();
        await context.Database.MigrateAsync();
        var replayed = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `integer_upgrade` WHERE `payload` IN ('first','second');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, pending.Status);
        Assert.Empty(beforeHistory);
        Assert.Equal(["20261023000001_IndexDropIntegerUpgrade"], applied);
        Assert.Equal(applied, replayed);
        Assert.Empty(replay.Assessments);
        Assert.Equal(2, rows);
    }

    /// <summary>Native INTEGER aliases and unchanged literal defaults retain their exact source contract.</summary>
    /// <param name="literalDefault">Whether the authored source and target retain a zero literal default.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntegerAliasAndLiteralDefaultRetainNativeIdentity(bool literalDefault)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, FixtureSql(alreadyDropped: true)
            + (literalDefault
                ? "ALTER TABLE `integer_upgrade` MODIFY COLUMN `left_value` int NOT NULL DEFAULT 0;"
                : string.Empty));
        await using var context = CreateContext(connectionString);
        var operation = new SafeMigrationOperation(new AlterColumnIntent(Table,
            new ExpectedColumnDefinition("left_value", typeof(long), false, "bigint",
                defaultValue: literalDefault ? SafeMigrationDefaultValue.Literal(0L) : null),
            new ExpectedColumnDefinition("left_value", typeof(int), false, "INTEGER",
                defaultValue: literalDefault ? SafeMigrationDefaultValue.Literal(0) : null)),
            SafeMigrationPolicy.RepairIfSafe);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("integer-upgrade-source-alias"));

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, [operation]);
        var replay = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("integer-upgrade-source-alias-replay"));

        replay.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, [operation]);
        var columnDefault = await ScalarStringAsync(connectionString,
            "SELECT COALESCE(COLUMN_DEFAULT, '<none>') FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='integer_upgrade' AND COLUMN_NAME='left_value';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.Equal(literalDefault ? "0" : "<none>", columnDefault);
    }

    /// <summary>Source drift and unsafe native dependencies cannot borrow the declared widening contract.</summary>
    /// <param name="failure">The independently introduced source or dependency drift.</param>
    [Theory]
    [InlineData("unsigned")]
    [InlineData("comment")]
    [InlineData("default")]
    [InlineData("wrong_source")]
    [InlineData("foreign_key")]
    [InlineData("nullable")]
    public async Task IntegerUpgradeRejectsUnprovenSourceWithoutMutation(string failure)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, FixtureSql(alreadyDropped: false));
        var drift = failure switch
        {
            "unsigned" => "UPDATE `integer_upgrade` SET `left_value`=0; "
                + "ALTER TABLE `integer_upgrade` MODIFY COLUMN `left_value` int unsigned NOT NULL;",
            "comment" => "ALTER TABLE `integer_upgrade` MODIFY COLUMN `left_value` int NOT NULL COMMENT 'drift';",
            "default" => "ALTER TABLE `integer_upgrade` MODIFY COLUMN `left_value` int NOT NULL DEFAULT 7;",
            "wrong_source" => "ALTER TABLE `integer_upgrade` MODIFY COLUMN `left_value` bigint NOT NULL;",
            "nullable" => "ALTER TABLE `integer_upgrade` MODIFY COLUMN `left_value` int NULL;",
            "foreign_key" => "CREATE TABLE `integer_parent` (`value` int NOT NULL PRIMARY KEY); "
                + "INSERT INTO `integer_parent` VALUES (-2147483648),(0); "
                + "ALTER TABLE `integer_upgrade` ADD CONSTRAINT `fk_integer_parent` "
                + "FOREIGN KEY (`left_value`) REFERENCES `integer_parent` (`value`);",
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };

        await ExecuteSqlAsync(connectionString, drift);
        await using var context = CreateContext(connectionString);
        var operations = UpgradeOperations(dropIndex: true, schema: null);
        if (failure == "wrong_source")
        {
            operations[1] = new SafeMigrationOperation(new AlterColumnIntent(Table,
                new ExpectedColumnDefinition("left_value", typeof(long), false, "bigint"),
                new ExpectedColumnDefinition("left_value", typeof(short), false, "smallint")),
                SafeMigrationPolicy.RepairIfSafe);
            await ExecuteSqlAsync(connectionString,
                "ALTER TABLE `integer_upgrade` MODIFY COLUMN `left_value` int NOT NULL;");
        }

        var before = await ReadCreateAsync(connectionString);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("integer-upgrade-source-drift"), CancellationToken.None);

        var after = await ReadCreateAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.True(preflight.Assessments[1].Action?.RejectsExecution());
        Assert.Equal(before, after);
    }

    /// <summary>The runtime does not trust a clean earlier preflight after source metadata changes.</summary>
    [Fact]
    public async Task IntegerUpgradeRevalidatesSourceBeforeExecuting()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, FixtureSql(alreadyDropped: false));
        await using var context = CreateContext(connectionString);
        var operations = UpgradeOperations(dropIndex: false, schema: null);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("integer-upgrade-before-race"), CancellationToken.None);

        await ExecuteSqlAsync(connectionString,
            "ALTER TABLE `integer_upgrade` MODIFY COLUMN `left_value` int NOT NULL DEFAULT 7;");
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, operations));
        var remaining = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() "
            + "AND TABLE_NAME='integer_upgrade' AND DATA_TYPE='int';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        AssertOwnedGuardFailure(failure);
        Assert.Equal(4, remaining);
    }

    /// <summary>Only ordinary indexes issue lineage; special drops retain independent physical qualification.</summary>
    /// <param name="fullText">Whether the removed index has a special FULLTEXT contract.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexDropCertificatesRemainSeparateFromIndependentPhysicalQualification(bool fullText)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, FixtureSql(alreadyDropped: fullText)
            + (fullText ? "ALTER TABLE `integer_upgrade` ADD FULLTEXT INDEX `ix_upgrade_coordinates` (`payload`);"
                : string.Empty));
        await using var context = CreateContext(connectionString);
        var operations = UpgradeOperations(dropIndex: true, schema: null);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var analyses = await analyzer.AnalyzeAsync(context, operations.Cast<SafeMigrationOperation>().ToArray());
        var verifier = (ISafeMigrationProjectedColumnAnalyzer)analyzer;
        var drop = Assert.IsType<DropIndexIntent>(((SafeMigrationOperation)operations[0]).Intent);
        var certified = verifier.PreservesColumnSourcesAfterIndexDrop(drop, analyses[0]);
        var foreignCertificates = new[]
        {
            verifier.PreservesColumnSourcesAfterIndexDrop(
                new DropIndexIntent(drop.Name, "another_owner", drop.Schema), analyses[0]),
            verifier.PreservesColumnSourcesAfterIndexDrop(
                new DropIndexIntent(drop.Name, drop.Table, "another_database"), analyses[0]),
            verifier.PreservesColumnSourcesAfterIndexDrop(
                new DropIndexIntent("another_index", drop.Table, drop.Schema), analyses[0]),
            verifier.PreservesColumnSourcesAfterIndexDrop(drop,
                new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
                    SafeMigrationRepairCapability.None, false, "uncaptured")),
        };

        await analyzer.AnalyzeAsync(context, []);
        var expiredCertificate = verifier.PreservesColumnSourcesAfterIndexDrop(drop, analyses[0]);
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("integer-upgrade-special-index"));

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, operations);
        var replay = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("integer-upgrade-special-index-replay"));

        replay.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, operations);
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `integer_upgrade`;");

        // Assert
        Assert.Equal(!fullText, certified);
        Assert.All(foreignCertificates, static certificate => Assert.False(certificate));
        Assert.False(expiredCertificate);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.Equal(2, rows);
    }

    /// <summary>Opaque source changes require runtime classification instead of historical authority.</summary>
    [Fact]
    public async Task OpaqueSourceChangeDoesNotReuseTheEarlierWideningProof()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, FixtureSql(alreadyDropped: false));
        await using var context = CreateContext(connectionString);
        var operations = UpgradeOperations(dropIndex: true, schema: null);
        operations.Insert(1, new SqlOperation
        {
            Sql = "ALTER TABLE `integer_upgrade` MODIFY COLUMN `left_value` int NOT NULL DEFAULT 7;",
        });

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("integer-upgrade-opaque-source"));

        report.ThrowIfBlocked();
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, operations));
        var remaining = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() "
            + "AND TABLE_NAME='integer_upgrade' AND DATA_TYPE='int';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[2].Action);
        AssertOwnedGuardFailure(failure);
        Assert.Equal(4, remaining);
    }

    /// <summary>Independent live analyses cannot spend the same remaining composite-key bytes repeatedly.</summary>
    /// <param name="columnAliases">Whether native references use alternate valid member casing.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SiblingIntegerWideningsRespectTheCumulativeNativeKeyBudget(bool columnAliases)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, FixtureSql(alreadyDropped: false) + CompactKeyBudgetSql);
        await using var context = CreateContext(connectionString);
        var operations = UpgradeOperations(dropIndex: true, schema: null, columnAliases);
        var before = await ReadCreateAsync(connectionString);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("integer-upgrade-cumulative-key"));

        var after = await ReadCreateAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationAction.Repair, preflight.Assessments[1].Action);
        Assert.True(preflight.Assessments[2].Action?.RejectsExecution());
        Assert.Equal(before, after);
    }

    /// <summary>Runtime key checks remain fresh when a new native constraint consumes the preflight headroom.</summary>
    [Fact]
    public async Task IntegerUpgradeRechecksThePhysicalKeyBudgetAfterPreflight()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, FixtureSql(alreadyDropped: false));
        await using var context = CreateContext(connectionString);
        var operations = UpgradeOperations(dropIndex: false, schema: null);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("integer-upgrade-key-race"));

        await ExecuteSqlAsync(connectionString, CompactKeyBudgetSql);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, operations));
        var widened = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() "
            + "AND TABLE_NAME='integer_upgrade' AND DATA_TYPE='bigint';");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `integer_upgrade`;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        AssertOwnedGuardFailure(failure);
        Assert.Equal(1, widened);
        Assert.Equal(2, rows);
    }

    /// <summary>An incoming FK in another owned database forbids independent scalar or string repair.</summary>
    /// <param name="family">The independent runtime repair family using the shared native FK guard.</param>
    [Theory]
    [InlineData("integer")]
    [InlineData("varchar")]
    [InlineData("boolean")]
    public async Task CrossDatabaseIncomingForeignKeyRetainsPreflightAndRuntimeGuards(string family)
    {
        // Arrange
        // WHY: Per-test cleanup releases leases in creation order. Release
        // the referencing database before its principal, without disabling FKs.
        var childConnection = await CreateDatabaseAsync(CancellationToken.None);
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var sourceType = family == "integer" ? "int" : family == "varchar" ? "varchar(10)" : "bit(1)";
        var targetType = family == "integer" ? "bigint" : family == "varchar" ? "varchar(20)" : "tinyint(1)";
        var seedRows = family == "integer" ? "(1,-2147483648),(2,0)"
            : family == "varchar" ? "(1,'a'),(2,'b')" : "(1,b'0'),(2,b'1')";
        var referencedValue = family == "integer" ? "0" : family == "varchar" ? "'a'" : "b'0'";
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE `integer_upgrade` (`id` int NOT NULL PRIMARY KEY, `left_value` {sourceType} NOT NULL, "
            + "UNIQUE INDEX `uq_parent_value` (`left_value`)) ENGINE=InnoDB "
            + "DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `integer_upgrade` VALUES " + seedRows + ";");
        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        await ExecuteSqlAsync(childConnection,
            $"CREATE TABLE `integer_child` (`value` {sourceType} NOT NULL, CONSTRAINT `fk_other_database_parent` "
            + $"FOREIGN KEY (`value`) REFERENCES `{database}`.`integer_upgrade` (`left_value`)) ENGINE=InnoDB "
            + "DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `integer_child` VALUES (" + referencedValue + ");");
        await using var context = CreateContext(connectionString);
        MigrationOperation[] operations =
        [new SafeMigrationOperation(new AlterColumnIntent(Table,
                new ExpectedColumnDefinition("left_value",
                    family == "integer" ? typeof(long) : family == "varchar" ? typeof(string) : typeof(bool),
                    false, targetType, maxLength: family == "varchar" ? 20 : null),
                new ExpectedColumnDefinition("left_value",
                    family == "integer" ? typeof(int) : family == "varchar" ? typeof(string) : typeof(bool),
                    false, sourceType, maxLength: family == "varchar" ? 10 : null)),
            SafeMigrationPolicy.RepairIfSafe)];

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("integer-upgrade-cross-database-fk"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, operations));
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `integer_upgrade`;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.True(report.Assessments[0].Action?.RejectsExecution());
        AssertOwnedGuardFailure(failure);
        Assert.Equal(2, rows);
    }

    // WHY: COMPACT permits 767 key bytes. This live key starts at 760;
    // each independent INT-to-BIGINT analysis fits, but the second ordered
    // widening would reach 768 unless accepted sibling widths are included.
    private const string CompactKeyBudgetSql =
        "ALTER TABLE `integer_upgrade` ROW_FORMAT=COMPACT, ADD COLUMN `padding` varchar(187) NOT NULL DEFAULT ''; "
        + "ALTER TABLE `integer_upgrade` ADD INDEX `ix_live_budget` "
        + "(`padding`,`left_value`,`right_value`,`position`);";

    private static async Task<string> ReadCreateAsync(string connectionString)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SHOW CREATE TABLE `integer_upgrade`;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        return reader.GetString(1);
    }

    /// <summary>Reproduces nontransactional interruption without inventing an EF history row.</summary>
    private static async Task ApplyNativeWideningPrefixAsync(string connectionString, int count)
    {
        var names = new[] { "left_value", "right_value", "position" };
        foreach (var name in names.Take(count))
        {
            await ExecuteSqlAsync(connectionString,
                $"ALTER TABLE `integer_upgrade` MODIFY COLUMN `{name}` bigint NOT NULL;");
        }
    }

    private static string FixtureSql(bool alreadyDropped) =>
        "CREATE TABLE `integer_upgrade` (`id` int NOT NULL PRIMARY KEY, `left_value` int NOT NULL, "
        + "`right_value` int NOT NULL, `position` int NOT NULL, `payload` varchar(32) NOT NULL"
        + (alreadyDropped ? "" : ", INDEX `ix_upgrade_coordinates` (`left_value`,`right_value`,`position`)")
        + ") ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
        + "INSERT INTO `integer_upgrade` VALUES (1,-2147483648,2147483647,0,'first'),(2,0,1,2,'second');";

    internal static List<MigrationOperation> UpgradeOperations(bool dropIndex, string? schema,
        bool columnAliases = false)
    {
        var operations = new List<MigrationOperation>();
        if (dropIndex)
        {
            operations.Add(new SafeMigrationOperation(new DropIndexIntent("ix_upgrade_coordinates", Table, schema),
                SafeMigrationPolicy.ThrowIfDifferent));
        }

        foreach (var name in new[] { "left_value", "right_value", "position" })
        {
            var member = columnAliases ? name.ToUpperInvariant() : name;
            operations.Add(new SafeMigrationOperation(new AlterColumnIntent(Table,
                new ExpectedColumnDefinition(member, typeof(long), false, "bigint"),
                new ExpectedColumnDefinition(member, typeof(int), false, "int"), schema),
                SafeMigrationPolicy.RepairIfSafe));
        }

        operations.Add(new SafeMigrationOperation(new EnsureIndexIntent(new ExpectedIndexDefinition(
            "ix_upgrade_final", Table,
            [new ExpectedIndexKeyDefinition("left_value"), new ExpectedIndexKeyDefinition("right_value"),
                new ExpectedIndexKeyDefinition("position")], schema: schema)), SafeMigrationPolicy.ThrowIfDifferent));
        operations.Add(new SafeMigrationOperation(new EnsureCheckConstraintIntent(
            ExpectedCheckConstraintDefinition.FromExpression("ck_upgrade_order", Table,
                SafeMigrationSql.Binary(SafeMigrationSql.Identifier("left_value"),
                    SafeMigrationSqlBinaryOperator.LessThanOrEqual, SafeMigrationSql.Identifier("right_value")),
                schema)), SafeMigrationPolicy.ThrowIfDifferent));

        return operations;
    }
}

/// <summary>Hosts only the isolated persisted integer upgrade without an unrelated application model.</summary>
/// <param name="connectionString">The uniquely owned native test database.</param>
/// <param name="serverVersion">The native engine contract selected by the assembly fixture.</param>
public sealed class IndexDropIntegerUpgradeDbContext(string connectionString, MySqlServerVersion serverVersion)
    : DbContext
{
    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseMySql(connectionString, serverVersion,
            provider => provider.MigrationsAssembly(typeof(IndexDropIntegerUpgradeDbContext).Assembly.FullName)
                .MigrationsHistoryTable("__IntegerUpgradeHistory"));
        optionsBuilder.UseMySqlSafeMigrations<IndexDropIntegerUpgradeDbContext>();
    }
}

/// <summary>Retains the same exact-source index-removal and three-column widening stream in EF history.</summary>
[DbContext(typeof(IndexDropIntegerUpgradeDbContext))]
[Migration("20261023000001_IndexDropIntegerUpgrade")]
public sealed class IndexDropIntegerUpgradeMigration : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Operations.AddRange(
            MySqlIndexDropIntegerWideningIntegrationTests.UpgradeOperations(dropIndex: true, schema: null));

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("The isolated upgrade is forward-only.");
}
