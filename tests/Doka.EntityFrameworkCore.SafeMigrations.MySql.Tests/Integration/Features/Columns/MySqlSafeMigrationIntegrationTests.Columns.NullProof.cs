namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies operation-local NULL-proof safety against generic live MySQL and MariaDB databases.</summary>
public sealed partial class MySqlSafeMigrationIntegrationTests
{
    private const string NullProofVariablesClearedSql = "SELECT @doka_sm_nullability_blocked IS NULL "
        + "AND @doka_sm_data_blocked IS NULL AND @doka_sm_data_probe_required IS NULL "
        + "AND @doka_sm_column_repair_eligible IS NULL;";

    /// <summary>Repairs nullable scalar columns without NULL rows and safely replays on the same session.</summary>
    /// <param name="textColumn">Whether the live column contains strings rather than integers.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeNullProof_NoNullRowsRepairAndReplayWithoutChangingValues(
        bool textColumn
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var storeType = textColumn ? "varchar(40)" : "int";
        var values = textColumn ? "(1, 'preserved'), (2, '')" : "(1, 7), (2, 0)";
        var preservedPredicate = textColumn
            ? "(`id` = 1 AND BINARY `value` = BINARY 'preserved') "
                + "OR (`id` = 2 AND OCTET_LENGTH(`value`) = 0)"
            : "(`id` = 1 AND `value` = 7) OR (`id` = 2 AND `value` = 0)";

        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `null_proof_rows` (`id` int NOT NULL, `value` {storeType} NULL COMMENT 'legacy', "
            + "PRIMARY KEY (`id`)) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + $"INSERT INTO `null_proof_rows` (`id`, `value`) VALUES {values};");

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        // WHY: Analysis must use its inline live proof; runtime must initialize rather than trust stale session state.
        await context.Database.ExecuteSqlRawAsync("SET @doka_sm_nullability_blocked = TRUE;");
        var sessionBefore = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "null_proof_rows",
            new ExpectedColumnDefinition(
                "value",
                textColumn ? typeof(string) : typeof(int),
                isNullable: false,
                storeType: storeType,
                maxLength: textColumn ? 40 : null,
                comment: "canonical"),
            SafeMigrationPolicy.RepairIfSafe);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("null-proof-repair"));

        var providerAnalysis = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context,
            builder.Operations.Cast<SafeMigrationOperation>().ToArray(),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations);
        var cleanedAfterRepair = await ContextScalarIntAsync(context, NullProofVariablesClearedSql);
        await ExecuteOperationsAsync(context, builder.Operations);
        var cleanedAfterReplay = await ContextScalarIntAsync(context, NullProofVariablesClearedSql);
        var sessionAfter = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");
        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("null-proof-replay"));

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("null-proof-postflight"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(preflight.Assessments).ObservedState);
        Assert.Equal(SafeMigrationAction.Repair, Assert.Single(preflight.Assessments).Action);
        Assert.True(Assert.Single(providerAnalysis).RequiresLiveDataProof);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(replay.Assessments).ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.True(Assert.Single(postflight.Assessments).PostconditionSatisfied);
        Assert.Equal(1, cleanedAfterRepair);
        Assert.Equal(1, cleanedAfterReplay);
        Assert.Equal(sessionBefore, sessionAfter);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'null_proof_rows' "
                + "AND COLUMN_NAME = 'value' AND IS_NULLABLE = 'NO' AND COLUMN_COMMENT = 'canonical';"));
        Assert.Equal(2, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `null_proof_rows`;"));
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM `null_proof_rows` WHERE {preservedPredicate};"));
    }

    /// <summary>Rejects nullable scalar columns containing NULL without mutating their schema or rows.</summary>
    /// <param name="textColumn">Whether the live column contains strings rather than integers.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeNullProof_NullRowsRejectWithoutSchemaOrDataMutation(
        bool textColumn
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var storeType = textColumn ? "varchar(40)" : "int";
        var firstValue = textColumn ? "'preserved'" : "7";
        var preservedPredicate = textColumn
            ? "(`id` = 1 AND BINARY `value` = BINARY 'preserved') OR (`id` = 2 AND `value` IS NULL)"
            : "(`id` = 1 AND `value` = 7) OR (`id` = 2 AND `value` IS NULL)";

        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `null_proof_rows` (`id` int NOT NULL, `value` {storeType} NULL COMMENT 'legacy', "
            + "PRIMARY KEY (`id`)) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + $"INSERT INTO `null_proof_rows` (`id`, `value`) VALUES (1, {firstValue}), (2, NULL);");

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        // WHY: A stale successful proof must neither influence inline analysis nor admit a later runtime repair.
        await context.Database.ExecuteSqlRawAsync("SET @doka_sm_nullability_blocked = FALSE;");
        var sessionBefore = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "null_proof_rows",
            new ExpectedColumnDefinition(
                "value",
                textColumn ? typeof(string) : typeof(int),
                isNullable: false,
                storeType: storeType,
                maxLength: textColumn ? 40 : null,
                comment: "canonical"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("null-proof-blocked"));

        var providerAnalysis = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context,
            builder.Operations.Cast<SafeMigrationOperation>().ToArray(),
            CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var cleaned = await ContextScalarIntAsync(context, NullProofVariablesClearedSql);
        var sessionAfter = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");

        // Assert
        var providerException = Assert.IsType<MySqlException>(exception);
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, assessment.Action);
        Assert.True(Assert.Single(providerAnalysis).RequiresLiveDataProof);
        Assert.Contains("doka_sm_data_blocked", providerException.Message, StringComparison.Ordinal);
        Assert.Equal(1, cleaned);
        Assert.Equal(sessionBefore, sessionAfter);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'null_proof_rows' "
                + "AND COLUMN_NAME = 'value' AND IS_NULLABLE = 'YES' AND COLUMN_COMMENT = 'legacy';"));
        Assert.Equal(2, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `null_proof_rows`;"));
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM `null_proof_rows` WHERE {preservedPredicate};"));
    }

    /// <summary>Leaves a matching required column and its values unchanged and clears proof state.</summary>
    [Fact]
    public async Task RuntimeNullProof_MatchingRequiredColumnIsNoOp()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `null_proof_rows` (`id` int NOT NULL, `value` int NOT NULL, PRIMARY KEY (`id`)); "
            + "INSERT INTO `null_proof_rows` (`id`, `value`) VALUES (1, 7), (2, 0);");

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync("SET @doka_sm_nullability_blocked = TRUE;");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "null_proof_rows",
            new ExpectedColumnDefinition("value", typeof(int), isNullable: false, storeType: "int"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("null-proof-matching"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var cleaned = await ContextScalarIntAsync(context, NullProofVariablesClearedSql);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(report.Assessments).Action);
        Assert.Equal(1, cleaned);
        Assert.Equal(2, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `null_proof_rows`;"));
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `null_proof_rows` "
                + "WHERE (`id` = 1 AND `value` = 7) OR (`id` = 2 AND `value` = 0);"));
    }

    /// <summary>Creates a missing required column without querying a column that does not yet exist.</summary>
    [Fact]
    public async Task RuntimeNullProof_MissingColumnCreatesWithoutAnIdentifierDependentNullQuery()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `null_proof_rows` (`id` int NOT NULL);");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync("SET @doka_sm_nullability_blocked = TRUE;");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "null_proof_rows",
            new ExpectedColumnDefinition("value", typeof(int), isNullable: false, storeType: "int"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("null-proof-missing"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var cleaned = await ContextScalarIntAsync(context, NullProofVariablesClearedSql);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(report.Assessments).Action);
        Assert.Equal(1, cleaned);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'null_proof_rows' "
                + "AND COLUMN_NAME = 'value' AND IS_NULLABLE = 'NO';"));
        Assert.Equal(0, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `null_proof_rows`;"));
    }

    /// <summary>Preserves independent length and NULL blockers during one combined varchar repair.</summary>
    /// <param name="containsNull">Whether one live row contains NULL.</param>
    /// <param name="containsOverlength">Whether one live value exceeds the target length.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RuntimeNullProof_CombinedVarcharProofsRejectEitherIndependentBlocker(
        bool containsNull,
        bool containsOverlength
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var firstValue = containsOverlength ? "'too-long'" : "'fits'";
        var secondValue = containsNull ? "NULL" : "''";
        var blocked = containsNull || containsOverlength;
        var secondPredicate = containsNull ? "`value` IS NULL" : "OCTET_LENGTH(`value`) = 0";
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `null_proof_rows` (`id` int NOT NULL, `value` varchar(20) NULL COMMENT 'legacy', "
            + "PRIMARY KEY (`id`)) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + $"INSERT INTO `null_proof_rows` (`id`, `value`) VALUES (1, {firstValue}), (2, {secondValue});");

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "null_proof_rows",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: false,
                storeType: "varchar(5)",
                maxLength: 5,
                comment: "canonical"),
            SafeMigrationPolicy.RepairIfSafe);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("null-proof-combined"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var cleaned = await ContextScalarIntAsync(context, NullProofVariablesClearedSql);
        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("null-proof-combined-replay"));

        // Assert
        Assert.Equal(blocked ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(
            blocked ? SafeMigrationObservedState.DataBlocked : SafeMigrationObservedState.Different,
            Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(
            blocked ? SafeMigrationAction.RejectDataBlocked : SafeMigrationAction.Repair,
            Assert.Single(report.Assessments).Action);
        Assert.Equal(
            blocked ? SafeMigrationObservedState.DataBlocked : SafeMigrationObservedState.Matching,
            Assert.Single(replay.Assessments).ObservedState);

        if (blocked)
        {
            var providerException = Assert.IsType<MySqlException>(exception);

            Assert.Contains("doka_sm_data_blocked", providerException.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(exception);
            Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        }

        Assert.Equal(1, cleaned);
        Assert.Equal(
            blocked ? 20 : 5,
            await ScalarIntAsync(
                connectionString,
                "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'null_proof_rows' AND COLUMN_NAME = 'value';"));
        Assert.Equal(
            blocked ? 1 : 0,
            await ScalarIntAsync(
                connectionString,
                "SELECT IS_NULLABLE = 'YES' FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'null_proof_rows' AND COLUMN_NAME = 'value';"));
        Assert.Equal(
            blocked ? "legacy" : "canonical",
            await ScalarStringAsync(
                connectionString,
                "SELECT COLUMN_COMMENT FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'null_proof_rows' AND COLUMN_NAME = 'value';"));
        Assert.Equal(2, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `null_proof_rows`;"));
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM `null_proof_rows` WHERE (`id` = 1 AND BINARY `value` = BINARY {firstValue}) "
                + $"OR (`id` = 2 AND {secondPredicate});"));
    }

    /// <summary>Rechecks live rows after earlier repairs and intervening raw DDL or DML.</summary>
    /// <param name="sameColumn">Whether raw DDL makes the earlier repaired column nullable again.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeNullProof_OrderedOperationsDoNotReuseEvidenceAcrossRawDml(
        bool sameColumn
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `null_proof_rows` (`id` int NOT NULL, `first_value` int NULL, `second_value` int NULL, "
            + "PRIMARY KEY (`id`)); INSERT INTO `null_proof_rows` VALUES (1, 7, 9), (2, 0, 0);");

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync("SET @null_proof_after_rejection = NULL;");
        var sessionBefore = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");
        var secondColumn = sameColumn ? "first_value" : "second_value";
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "null_proof_rows",
            new ExpectedColumnDefinition("first_value", typeof(int), isNullable: false, storeType: "int"),
            SafeMigrationPolicy.RepairIfSafe);

        // WHY: The first operation materializes a successful proof; later raw SQL invalidates it before the next owner.
        _ = builder.Sql(
            (sameColumn ? "ALTER TABLE `null_proof_rows` MODIFY COLUMN `first_value` int NULL; " : string.Empty)
            + $"UPDATE `null_proof_rows` SET `{secondColumn}` = NULL WHERE `id` = 1;");

        builder.EnsureColumn(
            "null_proof_rows",
            new ExpectedColumnDefinition(secondColumn, typeof(int), isNullable: false, storeType: "int"),
            SafeMigrationPolicy.RepairIfSafe);

        _ = builder.Sql("SET @null_proof_after_rejection = 1;");

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var cleaned = await ContextScalarIntAsync(context, NullProofVariablesClearedSql);
        var laterStatementExecuted = await ContextScalarIntAsync(
            context,
            "SELECT @null_proof_after_rejection IS NOT NULL;");

        var sessionAfter = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");

        // Assert
        var providerException = Assert.IsType<MySqlException>(exception);

        Assert.Contains("doka_sm_data_blocked", providerException.Message, StringComparison.Ordinal);
        Assert.Equal(1, cleaned);
        Assert.Equal(0, laterStatementExecuted);
        Assert.Equal(sessionBefore, sessionAfter);
        Assert.Equal(
            sameColumn ? 1 : 0,
            await ScalarIntAsync(
                connectionString,
                "SELECT IS_NULLABLE = 'YES' FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'null_proof_rows' "
                + "AND COLUMN_NAME = 'first_value';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT IS_NULLABLE = 'YES' FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'null_proof_rows' "
                + $"AND COLUMN_NAME = '{secondColumn}';"));
        Assert.Equal(2, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `null_proof_rows`;"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM `null_proof_rows` WHERE `id` = 1 AND `{secondColumn}` IS NULL;"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `null_proof_rows` WHERE `id` = 2 AND `first_value` = 0 AND `second_value` = 0;"));
    }

    /// <summary>Rejects an ineligible physical type before treating nullable live rows as repairable.</summary>
    [Fact]
    public async Task RuntimeNullProof_IneligibleTypeRejectsWithoutMutation()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `null_proof_rows` (`id` int NOT NULL, `value` varchar(40) NULL COMMENT 'legacy'); "
            + "INSERT INTO `null_proof_rows` VALUES (1, NULL), (2, 'preserved');");

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "null_proof_rows",
            new ExpectedColumnDefinition("value", typeof(int), isNullable: false, storeType: "int"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("null-proof-ineligible"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var cleaned = await ContextScalarIntAsync(context, NullProofVariablesClearedSql);

        // Assert
        var providerException = Assert.IsType<MySqlException>(exception);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, Assert.Single(report.Assessments).Action);
        Assert.Contains("doka_sm_different", providerException.Message, StringComparison.Ordinal);
        Assert.Equal(1, cleaned);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'null_proof_rows' AND COLUMN_NAME = 'value' "
                + "AND DATA_TYPE = 'varchar' AND CHARACTER_MAXIMUM_LENGTH = 40 "
                + "AND IS_NULLABLE = 'YES' AND COLUMN_COMMENT = 'legacy';"));
        Assert.Equal(2, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `null_proof_rows`;"));
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `null_proof_rows` "
                + "WHERE (`id` = 1 AND `value` IS NULL) OR (`id` = 2 AND BINARY `value` = BINARY 'preserved');"));
    }

    /// <summary>Rejects foreign and case-mutated database qualifiers before probing NULL rows.</summary>
    /// <param name="caseMismatch">Whether only the case of the selected database qualifier is changed.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeNullProof_QualifierMismatchRejectsBeforeDataAdmission(
        bool caseMismatch
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        var qualifier = caseMismatch ? database.ToUpperInvariant() : "foreign_null_proof_database";
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `null_proof_rows` (`id` int NOT NULL, `value` int NULL COMMENT 'legacy'); "
            + "INSERT INTO `null_proof_rows` VALUES (1, NULL), (2, 7);");

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "null_proof_rows",
            new ExpectedColumnDefinition("value", typeof(int), isNullable: false, storeType: "int"),
            SafeMigrationPolicy.RepairIfSafe,
            schema: qualifier);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("null-proof-qualifier"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var cleaned = await ContextScalarIntAsync(context, NullProofVariablesClearedSql);

        // Assert
        var providerException = Assert.IsType<MySqlException>(exception);
        var assessment = Assert.Single(report.Assessments);

        Assert.NotEqual(database, qualifier);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
        Assert.Equal("database_qualifier_mismatch", assessment.AnalysisCode);
        Assert.Contains("doka_sm_unsupported", providerException.Message, StringComparison.Ordinal);
        Assert.Equal(1, cleaned);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'null_proof_rows' "
                + "AND COLUMN_NAME = 'value' AND IS_NULLABLE = 'YES' AND COLUMN_COMMENT = 'legacy';"));
        Assert.Equal(2, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `null_proof_rows`;"));
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `null_proof_rows` "
                + "WHERE (`id` = 1 AND `value` IS NULL) OR (`id` = 2 AND `value` = 7);"));
    }

    /// <summary>Unwinds interrupted prepared NULL proofs before retrying and replaying on the same session.</summary>
    /// <param name="failureMode">The provider error, caller cancellation, or timeout injected after PREPARE.</param>
    [Theory]
    [InlineData("error")]
    [InlineData("cancellation")]
    [InlineData("timeout")]
    public async Task RuntimeNullProof_SetupFailureCleansResourcesAndRecoversSameSession(
        string failureMode
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `null_proof_rows` (`id` int NOT NULL, `value` varchar(40) NULL COMMENT 'legacy', "
            + "PRIMARY KEY (`id`)) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + "INSERT INTO `null_proof_rows` VALUES (1, 'preserved'), (2, '');");

        await using var blocker = failureMode == "error"
            ? null
            : await AcquireRuntimeSetupLockAsync(connectionString);

        using var cancellation = new CancellationTokenSource();
        var observer = new MySqlRuntimeCommandInterceptor();
        // WHY: An interrupted row-lock wait errors on both engines; an interrupted SLEEP can return normally.
        var probe = failureMode == "error"
            ? "SELECT * FROM `runtime_missing_nullability_probe`;"
            : "SELECT `Id` FROM `runtime_setup_lock` WHERE `Id` = 1 FOR UPDATE;";

        observer.InjectFirstCompactedNullabilityProbeGroup(
            "SET @null_proof_prepared = 1; " + probe + " SET @null_proof_after_failure = 1;",
            failureMode == "cancellation" ? () => cancellation.CancelAfter(TimeSpan.FromMilliseconds(500)) : null);

        await using var context = CreateRuntimeContext(connectionString, observer);
        context.Database.SetCommandTimeout(failureMode == "timeout" ? 1 : 60);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "null_proof_rows",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: false,
                storeType: "varchar(40)",
                maxLength: 40,
                comment: "canonical"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var result = await ExecuteNullProofRecoveryAsync(context, observer, builder.Operations, cancellation.Token);

        // Assert
        if (failureMode == "cancellation")
        {
            Assert.IsAssignableFrom<OperationCanceledException>(result.Exception);
        }
        else
        {
            var exception = Assert.IsType<MySqlException>(result.Exception);
            if (failureMode == "timeout")
            {
                Assert.Equal(MySqlErrorCode.CommandTimeoutExpired, exception.ErrorCode);
                Assert.Equal(1, observer.InjectedCommandTimeout);
            }
            else
            {
                Assert.Equal(1146, exception.Number);
            }
        }

        Assert.True(observer.SetupWasInjected);
        Assert.True(observer.CompactedPrepareGroupWasInjected);
        Assert.True(observer.NullabilityProbePrepareGroupWasInjected);
        Assert.Equal(1, result.PreparedMarker);
        Assert.Equal(0, result.SentinelExecuted);
        Assert.Equal(0, result.BodiesBeforeRecovery);
        Assert.Equal(1, result.Cleanup.VariablesCleared);
        Assert.Equal(1146, result.Cleanup.TemporaryTableError);
        Assert.Equal(1243, result.Cleanup.PreparedStatementError);
        Assert.Equal(1, result.LegacySchemaBeforeRecovery);
        Assert.Equal(2, result.RowsBeforeRecovery);
        Assert.Equal(2, result.PreservedRowsBeforeRecovery);
        Assert.Equal(result.SessionBefore, result.SessionAfter);
        Assert.Equal(1, result.BodiesAfterRecovery);
        Assert.Equal(1, result.CanonicalSchemaAfterReplay);
        Assert.Equal(2, result.RowsAfterReplay);
        Assert.Equal(2, result.PreservedRowsAfterReplay);
        Assert.Equal(1, result.CleanedAfterRecovery);
        Assert.Equal(1, result.CleanedAfterReplay);
    }

    /// <summary>Captures failure evidence before retry or replay can overwrite interrupted operation state.</summary>
    /// <param name="context">The context retaining one open provider session for failure, recovery, and replay.</param>
    /// <param name="observer">The observer injecting a single acquired-resource NULL-proof interruption.</param>
    /// <param name="operations">The guarded nullable-to-required repair operation.</param>
    /// <param name="cancellationToken">The token used only for the interrupted attempt.</param>
    /// <returns>The captured cleanup, row, schema, body, and same-session evidence.</returns>
    private static async Task<RuntimeNullProofRecovery> ExecuteNullProofRecoveryAsync(
        DbContext context,
        MySqlRuntimeCommandInterceptor observer,
        IReadOnlyList<MigrationOperation> operations,
        CancellationToken cancellationToken
    )
    {
        const string preservedRowsSql = "SELECT COUNT(*) FROM `null_proof_rows` "
            + "WHERE (`id` = 1 AND BINARY `value` = BINARY 'preserved') "
            + "OR (`id` = 2 AND OCTET_LENGTH(`value`) = 0);";

        var sessionBefore = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");
        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, operations, cancellationToken));

        var bodiesBefore = observer.GuardedBodyCount;
        var cleanup = await ReadRuntimeCleanupProofAsync(context.Database.GetDbConnection());
        var preparedMarker = await ContextScalarIntAsync(context, "SELECT @null_proof_prepared IS NOT NULL;");
        var sentinel = await ContextScalarIntAsync(context, "SELECT @null_proof_after_failure IS NOT NULL;");
        var legacySchema = await ContextScalarIntAsync(
            context,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'null_proof_rows' AND COLUMN_NAME = 'value' "
            + "AND CHARACTER_MAXIMUM_LENGTH = 40 AND IS_NULLABLE = 'YES' AND COLUMN_COMMENT = 'legacy';");

        var rowsBefore = await ContextScalarIntAsync(context, "SELECT COUNT(*) FROM `null_proof_rows`;");
        var preservedBefore = await ContextScalarIntAsync(context, preservedRowsSql);
        context.Database.SetCommandTimeout(60);
        await ExecuteOperationsAsync(context, operations, CancellationToken.None);
        var bodiesAfterRecovery = observer.GuardedBodyCount;
        var cleanedAfterRecovery = await ContextScalarIntAsync(context, NullProofVariablesClearedSql);
        await ExecuteOperationsAsync(context, operations, CancellationToken.None);
        var cleanedAfterReplay = await ContextScalarIntAsync(context, NullProofVariablesClearedSql);
        var sessionAfter = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");
        var canonicalSchema = await ContextScalarIntAsync(
            context,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'null_proof_rows' AND COLUMN_NAME = 'value' "
            + "AND CHARACTER_MAXIMUM_LENGTH = 40 AND IS_NULLABLE = 'NO' AND COLUMN_COMMENT = 'canonical';");

        var rowsAfterReplay = await ContextScalarIntAsync(context, "SELECT COUNT(*) FROM `null_proof_rows`;");
        var preservedAfterReplay = await ContextScalarIntAsync(context, preservedRowsSql);

        return new RuntimeNullProofRecovery(
            exception,
            bodiesBefore,
            cleanup,
            preparedMarker,
            sentinel,
            sessionBefore,
            legacySchema,
            rowsBefore,
            preservedBefore,
            sessionAfter,
            bodiesAfterRecovery,
            canonicalSchema,
            rowsAfterReplay,
            preservedAfterReplay,
            cleanedAfterRecovery,
            cleanedAfterReplay);
    }

    /// <summary>Carries immutable interrupted-proof evidence and successful same-session retry and replay.</summary>
    private sealed record RuntimeNullProofRecovery(
        Exception? Exception,
        long BodiesBeforeRecovery,
        RuntimeCleanupProof Cleanup,
        int PreparedMarker,
        int SentinelExecuted,
        int SessionBefore,
        int LegacySchemaBeforeRecovery,
        int RowsBeforeRecovery,
        int PreservedRowsBeforeRecovery,
        int SessionAfter,
        long BodiesAfterRecovery,
        int CanonicalSchemaAfterReplay,
        int RowsAfterReplay,
        int PreservedRowsAfterReplay,
        int CleanedAfterRecovery,
        int CleanedAfterReplay
    );
}
