namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlGeneratedCaseIntegrationTests
{
    /// <summary>Native source independence authorizes a standalone DROP without a complete model candidate.</summary>
    [GeneratedMariaDbFact]
    public async Task StandaloneGeneratedOwnerUnrelatedDropMatchesNativeControlAndReplays()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE generated_sparse_drop (id int NOT NULL PRIMARY KEY, source int NULL, "
            + "unrelated int NULL, result int GENERATED ALWAYS AS (source + 1) VIRTUAL); "
            + "CREATE TABLE generated_sparse_control LIKE generated_sparse_drop; "
            + "INSERT INTO generated_sparse_drop (id, source, unrelated) VALUES (1, 3, 11), (2, 7, 13); "
            + "INSERT INTO generated_sparse_control (id, source, unrelated) "
            + "SELECT id, source, unrelated FROM generated_sparse_drop;");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("unrelated", "generated_sparse_drop");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var nativeFailure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString,
            "ALTER TABLE generated_sparse_control DROP COLUMN unrelated;"));

        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("standalone-generated-unrelated-drop"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var replay = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("standalone-generated-unrelated-drop-replay"), CancellationToken.None);

        var replayFailure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var unrelated = await SparseDropColumnCountAsync(connectionString, "unrelated");
        var retained = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM generated_sparse_drop "
            + "WHERE (id = 1 AND source = 3 AND result = 4) OR (id = 2 AND source = 7 AND result = 8);");

        // Assert
        Assert.Null(nativeFailure);
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(preflight.Assessments).Action);
        Assert.Null(failure);
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Null(replayFailure);
        Assert.Equal(0, unrelated);
        Assert.Equal(2, retained);
    }

    /// <summary>Runtime and emitted scripts obtain native independence without borrowing optional preflight.</summary>
    /// <param name="script">Whether the emitted script rather than ordinary migration commands executes.</param>
    [GeneratedMariaDbTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneOpaqueGeneratedUnrelatedDropExecutesWithoutPreflight(bool script)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE generated_sparse_drop (id int NOT NULL PRIMARY KEY, source int NULL, "
            + "unrelated int NULL, result int GENERATED ALWAYS AS (IF(source > 0, source + 1, 0)) VIRTUAL); "
            + "INSERT INTO generated_sparse_drop (id, source, unrelated) VALUES (1, 3, 11), (2, 7, 13);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("unrelated", "generated_sparse_drop");
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model,
            script ? MigrationsSqlGenerationOptions.Script : MigrationsSqlGenerationOptions.Default);

        var sql = string.Join(Environment.NewLine, commands.Select(static command => command.CommandText));

        // Act
        var failure = await Record.ExceptionAsync(async () =>
        {
            if (script)
            {
                await ExecuteMigrationScriptAsync(connectionString, sql);
                await ExecuteMigrationScriptAsync(connectionString, sql);
            }
            else
            {
                await ExecuteOperationsAsync(context, builder.Operations);
                await ExecuteOperationsAsync(context, builder.Operations);
            }
        });

        var unrelated = await SparseDropColumnCountAsync(connectionString, "unrelated");
        var retained = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM generated_sparse_drop "
            + "WHERE (id = 1 AND source = 3 AND result = 4) OR (id = 2 AND source = 7 AND result = 8);");

        // Assert
        Assert.Null(failure);
        Assert.Equal(0, unrelated);
        Assert.Equal(2, retained);
    }

    /// <summary>Actual bound identities, including escaped identifiers, cannot pass the absence certificate.</summary>
    /// <param name="physicalName">The independently declared source name.</param>
    /// <param name="operationName">The requested native-equivalent DROP spelling.</param>
    [GeneratedMariaDbTheory]
    [InlineData("Source", "source")]
    [InlineData("tick`source", "tick`source")]
    [InlineData("quote\"source", "quote\"source")]
    [InlineData("o'reilly", "o'reilly")]
    [InlineData("slash\\source", "slash\\source")]
    [InlineData("na\u00EFve", "na\u00EFve")]
    public async Task StandaloneGeneratedSourceDropRetainsEscapedPhysicalIdentity(
        string physicalName, string operationName)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var identifier = "`" + physicalName.Replace("`", "``", StringComparison.Ordinal) + "`";
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE generated_sparse_drop (id int NOT NULL PRIMARY KEY, " + identifier
            + " int NULL, unrelated int NULL, result int GENERATED ALWAYS AS (" + identifier + " + 1) VIRTUAL); "
            + "INSERT INTO generated_sparse_drop (id, " + identifier + ", unrelated) VALUES (1, 3, 11);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists(operationName, "generated_sparse_drop");

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("standalone-generated-source-identity"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var retained = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM generated_sparse_drop WHERE id = 1 AND " + identifier + " = 3 AND result = 4;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, Assert.Single(preflight.Assessments).Action);
        Assert.Equal("doka_sm_unsupported", Assert.IsType<SafeMigrationExecutionException>(failure).GuardCode);
        Assert.Equal(1, retained);
    }

    /// <summary>Textual occurrences remain unproven rather than misclassified as bound source dependencies.</summary>
    /// <param name="expression">The native expression containing an ambiguous target-name occurrence.</param>
    /// <param name="extraColumn">An optional distinct member whose name contains the target name.</param>
    [GeneratedMariaDbTheory]
    [InlineData("CHAR_LENGTH('unrelated') + source", "")]
    [InlineData("unrelated_prefix + source", ", unrelated_prefix int NULL")]
    public async Task StandaloneGeneratedDropAmbiguousOccurrencesRemainConservative(
        string expression, string extraColumn)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE generated_sparse_drop (id int NOT NULL PRIMARY KEY, source int NULL, "
            + "unrelated int NULL" + extraColumn + ", result int GENERATED ALWAYS AS (" + expression + ") VIRTUAL); "
            + "INSERT INTO generated_sparse_drop (id, source, unrelated) VALUES (1, 3, 11); "
            + "CREATE TABLE generated_sparse_control LIKE generated_sparse_drop;");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("unrelated", "generated_sparse_drop");

        // Act
        var nativeFailure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString,
            "ALTER TABLE generated_sparse_control DROP COLUMN unrelated;"));

        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("standalone-generated-ambiguous-occurrence"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var unrelated = await SparseDropColumnCountAsync(connectionString, "unrelated");

        // Assert
        Assert.Null(nativeFailure);
        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal("generated_column_drop_unproven", Assert.Single(preflight.Assessments).AnalysisCode);
        Assert.Equal("doka_sm_unsupported", Assert.IsType<SafeMigrationExecutionException>(failure).GuardCode);
        Assert.Equal(1, unrelated);
    }

    /// <summary>Source absence does not falsely certify that the owner has no surviving generated members.</summary>
    [GeneratedMariaDbFact]
    public async Task StandaloneGeneratedDropIndependenceDoesNotClaimGeneratedSetEmpty()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE generated_sparse_drop (source int NULL, unrelated int NULL, "
            + "result int GENERATED ALWAYS AS (source + 1) VIRTUAL);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("unrelated", "generated_sparse_drop");

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context,
            builder.Operations.Cast<SafeMigrationOperation>().ToArray());

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.False(analysis.DropLeavesNoGeneratedColumns);
    }

    /// <summary>Native quote modes cannot hide a source or invalidate a genuinely absent unrelated name.</summary>
    /// <param name="mode">The migration session's native quote and escape settings.</param>
    /// <param name="dropSource">Whether the requested member is actually referenced.</param>
    [GeneratedMariaDbTheory]
    [InlineData("ANSI_QUOTES", false)]
    [InlineData("ANSI_QUOTES", true)]
    [InlineData("NO_BACKSLASH_ESCAPES", false)]
    [InlineData("NO_BACKSLASH_ESCAPES", true)]
    [InlineData("ANSI_QUOTES,NO_BACKSLASH_ESCAPES", false)]
    [InlineData("ANSI_QUOTES,NO_BACKSLASH_ESCAPES", true)]
    public async Task StandaloneGeneratedDropRespectsNativeSessionQuoteModes(string mode, bool dropSource)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await using var sessionMode = context.Database.GetDbConnection().CreateCommand();
        sessionMode.CommandText = "SET SESSION sql_mode = @mode;";
        var modeParameter = sessionMode.CreateParameter();
        modeParameter.ParameterName = "@mode";
        modeParameter.Value = mode;
        sessionMode.Parameters.Add(modeParameter);
        await sessionMode.ExecuteNonQueryAsync(CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE generated_sparse_drop (id int NOT NULL PRIMARY KEY, `quote\"source` int NULL, "
            + "unrelated int NULL, result int GENERATED ALWAYS AS (`quote\"source` + 1) VIRTUAL); "
            + "INSERT INTO generated_sparse_drop (id, `quote\"source`, unrelated) VALUES (1, 3, 11);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists(dropSource ? "quote\"source" : "unrelated", "generated_sparse_drop");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("standalone-generated-quote-mode"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var retained = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM generated_sparse_drop WHERE id = 1 AND `quote\"source` = 3 AND result = 4;");

        var unrelated = await SparseDropColumnCountAsync(connectionString, "unrelated");

        // Assert
        Assert.Equal(dropSource ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(dropSource ? SafeMigrationAction.RejectUnsupported : SafeMigrationAction.Apply,
            Assert.Single(report.Assessments).Action);
        if (dropSource)
        {
            Assert.Equal("doka_sm_unsupported", Assert.IsType<SafeMigrationExecutionException>(failure).GuardCode);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.Equal(dropSource ? 1 : 0, unrelated);
        Assert.Equal(1, retained);
    }

    /// <summary>Long native expressions retain trailing source references without size-dependent shortcuts.</summary>
    /// <param name="dropSource">Whether the requested member is the trailing generated operand.</param>
    [GeneratedMariaDbTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneGeneratedDropChecksWholeEscapedNativeExpression(bool dropSource)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var text = new string('\'', 12000) + new string('x', 32768);
        var literal = "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE generated_sparse_drop (id int NOT NULL PRIMARY KEY, source int NULL, "
            + "unrelated int NULL, result int GENERATED ALWAYS AS (CHAR_LENGTH(" + literal + ") + source) VIRTUAL); "
            + "INSERT INTO generated_sparse_drop (id, source, unrelated) VALUES (1, 3, 11);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists(dropSource ? "source" : "unrelated", "generated_sparse_drop");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("standalone-generated-whole-expression"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var retained = await ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM generated_sparse_drop WHERE id = 1 AND source = 3 AND result = {text.Length + 3};");

        var displayLength = await ScalarIntAsync(connectionString,
            "SELECT CHAR_LENGTH(GENERATION_EXPRESSION) FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'generated_sparse_drop' AND COLUMN_NAME = 'result';");

        // Assert
        Assert.Equal(dropSource ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        if (dropSource)
        {
            Assert.Equal("doka_sm_unsupported", Assert.IsType<SafeMigrationExecutionException>(failure).GuardCode);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.True(displayLength > 56000);
        Assert.Equal(1, retained);
    }

    /// <summary>Missing targets remain no-ops even if surviving native expressions contain ambiguous text.</summary>
    /// <param name="ownerExists">Whether the table exists while the requested target member does not.</param>
    [GeneratedMariaDbTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneGeneratedDropMissingTargetsRemainNoOp(bool ownerExists)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        if (ownerExists)
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE TABLE generated_sparse_drop (id int NOT NULL PRIMARY KEY, "
                + "result int GENERATED ALWAYS AS (CHAR_LENGTH('unrelated')) VIRTUAL); "
                + "INSERT INTO generated_sparse_drop (id) VALUES (1);");
        }

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("unrelated", "generated_sparse_drop");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("standalone-generated-missing-target"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var owners = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'generated_sparse_drop';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(report.Assessments).Action);
        Assert.Null(failure);
        Assert.Equal(ownerExists ? 1 : 0, owners);
    }

    /// <summary>Accent-insensitive catalog comparison cannot exclude a distinct surviving generated member.</summary>
    [GeneratedMariaDbFact]
    public async Task StandaloneGeneratedDropRetainsAccentDistinctSurvivingDependency()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE generated_sparse_drop (id int NOT NULL PRIMARY KEY, a int NULL, "
            + "`\u00E1` int GENERATED ALWAYS AS (a + 1) VIRTUAL); "
            + "INSERT INTO generated_sparse_drop (id, a) VALUES (1, 3);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("a", "generated_sparse_drop");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("standalone-generated-accent-distinct-dependency"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var retained = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM generated_sparse_drop WHERE id = 1 AND a = 3 AND `\u00E1` = 4;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, Assert.Single(report.Assessments).Action);
        Assert.Equal("doka_sm_unsupported", Assert.IsType<SafeMigrationExecutionException>(failure).GuardCode);
        Assert.Equal(1, retained);
    }

    /// <summary>Missing accent-distinct targets are no-ops; native case aliases still remove their member.</summary>
    /// <param name="physicalName">The independently declared native column identity.</param>
    /// <param name="requestedName">The requested missing or case-equivalent DROP identity.</param>
    /// <param name="generated">Whether an independent generated member survives.</param>
    /// <param name="targetExists">Whether native identifier resolution binds the requested member.</param>
    [Theory]
    [InlineData("a", "\u00E1", false, false)]
    [InlineData("a", "\u00E1", true, false)]
    [InlineData("\u00E1", "a", false, false)]
    [InlineData("\u00E1", "a", true, false)]
    [InlineData("A", "\u00E1", false, false)]
    [InlineData("A", "\u00E1", true, false)]
    [InlineData("\u00C1", "a", false, false)]
    [InlineData("\u00C1", "a", true, false)]
    [InlineData("Source", "source", false, true)]
    public async Task StandaloneDropResolvesNativeMissingAndCaseEquivalentTargets(
        string physicalName, string requestedName, bool generated, bool targetExists)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var physical = "`" + physicalName + "`";
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE generated_sparse_drop (id int NOT NULL PRIMARY KEY, " + physical + " int NULL"
            + (generated ? ", result int GENERATED ALWAYS AS (1) VIRTUAL" : string.Empty) + "); "
            + "INSERT INTO generated_sparse_drop (id, " + physical + ") VALUES (1, 3); "
            + "CREATE TABLE generated_sparse_control LIKE generated_sparse_drop;");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists(requestedName, "generated_sparse_drop");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var nativeFailure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString,
            "ALTER TABLE generated_sparse_control DROP COLUMN `" + requestedName + "`;"));

        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("standalone-drop-native-missing-identity"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var replay = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("standalone-drop-native-missing-replay"), CancellationToken.None);

        var replayFailure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var verified = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("standalone-drop-native-missing-postflight"), CancellationToken.None);

        var physicalBytes = Convert.ToHexString(Encoding.UTF8.GetBytes(physicalName));
        var retainedMember = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'generated_sparse_drop' AND CAST(COLUMN_NAME AS BINARY) = 0x" + physicalBytes + ";");

        var retainedRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM generated_sparse_drop WHERE id = 1"
            + (targetExists ? string.Empty : " AND " + physical + " = 3")
            + (generated ? " AND result = 1" : string.Empty) + ";");

        // Assert
        if (targetExists)
        {
            Assert.Null(nativeFailure);
        }
        else
        {
            Assert.Equal(1091, Assert.IsType<MySqlException>(nativeFailure).Number);
        }

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(targetExists ? SafeMigrationAction.Apply : SafeMigrationAction.NoOp,
            Assert.Single(preflight.Assessments).Action);
        Assert.Equal(targetExists ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Missing,
            preflight.Assessments[0].ObservedState);
        Assert.Null(failure);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Null(replayFailure);
        Assert.True(Assert.Single(verified.Assessments).PostconditionSatisfied);
        Assert.Equal(targetExists ? 0 : 1, retainedMember);
        Assert.Equal(1, retainedRows);
    }

    /// <summary>Reads physical presence so a no-op implementation cannot satisfy DROP success assertions.</summary>
    private static Task<int> SparseDropColumnCountAsync(string connectionString, string name)
        => ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + $"AND TABLE_NAME = 'generated_sparse_drop' AND COLUMN_NAME = '{name}';");
}
