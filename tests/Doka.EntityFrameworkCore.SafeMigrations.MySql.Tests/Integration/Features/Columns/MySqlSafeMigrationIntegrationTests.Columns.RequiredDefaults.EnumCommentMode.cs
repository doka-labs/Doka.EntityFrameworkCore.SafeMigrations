namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the ColumnsRequiredDefaultsEnumCommentMode migration contracts in an independent class collection.</summary>
public sealed partial class MySqlColumnsRequiredDefaultsEnumCommentModeIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlColumnsRequiredDefaultsEnumCommentModeIntegrationTests(MySqlEngineContainerFixture fixture) : base(fixture) { }

    private const string EnumCommentTargetComment = @"comment\path";

    /// <summary>
    /// Missing required domains cannot reinterpret their members through a native comment-mode scope.
    /// </summary>
    /// <param name="useSetStorage">Whether the authored domain uses SET rather than ENUM.</param>
    /// <param name="noBackslashEscapes">Whether the original session already preserves literal backslashes.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RequiredDefaultEnumCommentMissingAddRequiresOriginalNativeMode(
        bool useSetStorage,
        bool noBackslashEscapes
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, AtomicRequiredAddOriginalTableSql("enum_comment_items"));

        // WHY: MySqlConnector's string-parameter escaping must match the
        // explicitly selected server mode or replay compares altered domains.
        var migrationConnectionString = new MySqlConnectionStringBuilder(connectionString)
        {
            NoBackslashEscapes = noBackslashEscapes,
        }.ConnectionString;

        await using var context = CreateContext(migrationConnectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await AtomicRequiredAddSessionSqlAsync(context, "SET SESSION sql_mode = 'STRICT_ALL_TABLES"
            + (noBackslashEscapes ? ",NO_BACKSLASH_ESCAPES" : "") + "';");
        var operation = RequiredDefaultsEnumCommentOperation(useSetStorage);
        var originalFingerprint = SafeMigrationContractFingerprint.Create([operation]);
        var originalMode = await RequiredDefaultsEnumCommentSessionModeAsync(context);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("required-enum-comment-missing"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        SafeMigrationRunReport? replay = null;
        Exception? replayFailure = null;
        if (noBackslashEscapes && failure is null)
        {
            await AtomicRequiredAddSessionSqlAsync(context,
                "INSERT INTO `enum_comment_items` (`key`, `value`) VALUES (1, "
                + AtomicRequiredAddLiteral(@"slash\\tail") + ");");
            replay = await runner.AnalyzeAsync(context, [operation],
                new SafeMigrationRunOptions("required-enum-comment-replay"), CancellationToken.None);

            replayFailure = await Record.ExceptionAsync(() =>
                ExecuteOperationsAsync(context, [operation], CancellationToken.None));
        }

        var finalMode = await RequiredDefaultsEnumCommentSessionModeAsync(context);
        var state = await ReadAtomicRequiredAddStateAsync(connectionString, operation);
        var physicalType = await RequiredDefaultsEnumCommentPhysicalTypeAsync(connectionString);
        var commentHex = await RequiredDefaultsEnumCommentPhysicalCommentHexAsync(connectionString);
        var storedHex = noBackslashEscapes && failure is null
            ? await ScalarStringAsync(connectionString,
                "SELECT HEX(`value`) FROM `enum_comment_items` WHERE `key` = 1;")
            : "absent";

        // Assert
        var assessment = Assert.Single(preflight.Assessments);
        var expectedStatus = noBackslashEscapes ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked;
        Assert.True(expectedStatus == preflight.Status,
            $"Expected {expectedStatus}, actual {preflight.Status}; analysis={assessment.AnalysisCode}, "
            + $"decision={assessment.DecisionCode}, state={assessment.ObservedState}, action={assessment.Action}.");
        Assert.Equal(noBackslashEscapes ? SafeMigrationAction.Apply : SafeMigrationAction.RejectUnsupported,
            assessment.Action);
        if (noBackslashEscapes)
        {
            Assert.Null(failure);
            Assert.NotNull(replay);
            Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
            Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
            Assert.Null(replayFailure);
            Assert.Equal(new AtomicRequiredAddState(1, 0, 0, 1), state);
            Assert.Equal((useSetStorage ? "set" : "enum") + @"('slash\\\\tail')", physicalType);
            Assert.Equal(Convert.ToHexString(Encoding.UTF8.GetBytes(EnumCommentTargetComment)), commentHex);
            Assert.Equal(Convert.ToHexString(Encoding.UTF8.GetBytes(@"slash\\tail")), storedHex);
        }
        else
        {
            Assert.Equal("column_native_enum_mode_conflict", assessment.AnalysisCode);
            Assert.Contains("doka_sm_unsupported", AssertOwnedGuardFailure(failure).Message,
                StringComparison.Ordinal);
            Assert.Equal(new AtomicRequiredAddState(0, 0, 0, 0), state);
        }

        Assert.Equal(originalMode, finalMode);
        Assert.Equal(originalFingerprint, SafeMigrationContractFingerprint.Create([operation]));
    }

    /// <summary>
    /// An exact standard-decoded domain and comment remain a no-op without enabling another literal mode.
    /// </summary>
    /// <param name="useSetStorage">Whether the authored domain uses SET rather than ENUM.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredDefaultEnumCommentMatchingStandardTargetRemainsNoOp(bool useSetStorage)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await AtomicRequiredAddSessionSqlAsync(context, "SET SESSION sql_mode = 'STRICT_ALL_TABLES'; "
            + RequiredDefaultsEnumCommentStandardFixtureSql(useSetStorage, matchingComment: true));
        var operation = RequiredDefaultsEnumCommentOperation(useSetStorage);
        var originalFingerprint = SafeMigrationContractFingerprint.Create([operation]);
        var originalMode = await RequiredDefaultsEnumCommentSessionModeAsync(context);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("required-enum-comment-matching"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var finalMode = await RequiredDefaultsEnumCommentSessionModeAsync(context);
        var physicalType = await RequiredDefaultsEnumCommentPhysicalTypeAsync(connectionString);
        var commentHex = await RequiredDefaultsEnumCommentPhysicalCommentHexAsync(connectionString);
        var storedHex = await ScalarStringAsync(connectionString,
            "SELECT HEX(`value`) FROM `enum_comment_items` WHERE `key` = 1;");

        var state = await ReadAtomicRequiredAddStateAsync(connectionString, operation);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(report.Assessments).Action);
        Assert.Null(failure);
        Assert.Equal((useSetStorage ? "set" : "enum") + @"('slash\\tail')", physicalType);
        Assert.Equal(Convert.ToHexString(Encoding.UTF8.GetBytes(EnumCommentTargetComment)), commentHex);
        Assert.Equal(Convert.ToHexString(Encoding.UTF8.GetBytes(@"slash\tail")), storedHex);
        Assert.Equal(new AtomicRequiredAddState(1, 0, 0, 1), state);
        Assert.Equal(originalMode, finalMode);
        Assert.Equal(originalFingerprint, SafeMigrationContractFingerprint.Create([operation]));
    }

    /// <summary>
    /// Comment-only repair cannot replace a standard-decoded member domain inside a native comment scope.
    /// </summary>
    /// <param name="useSetStorage">Whether the authored domain uses SET rather than ENUM.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredDefaultEnumCommentStandardRepairRejectsBeforeDomainOrCommentChange(bool useSetStorage)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await AtomicRequiredAddSessionSqlAsync(context, "SET SESSION sql_mode = 'STRICT_ALL_TABLES'; "
            + RequiredDefaultsEnumCommentStandardFixtureSql(useSetStorage, matchingComment: false));
        var operation = RequiredDefaultsEnumCommentOperation(useSetStorage);
        var originalFingerprint = SafeMigrationContractFingerprint.Create([operation]);
        var originalMode = await RequiredDefaultsEnumCommentSessionModeAsync(context);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("required-enum-comment-repair-rejected"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var finalMode = await RequiredDefaultsEnumCommentSessionModeAsync(context);
        var physicalType = await RequiredDefaultsEnumCommentPhysicalTypeAsync(connectionString);
        var commentHex = await RequiredDefaultsEnumCommentPhysicalCommentHexAsync(connectionString);
        var storedHex = await ScalarStringAsync(connectionString,
            "SELECT HEX(`value`) FROM `enum_comment_items` WHERE `key` = 1;");

        var state = await ReadAtomicRequiredAddStateAsync(connectionString, operation);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, Assert.Single(report.Assessments).Action);
        Assert.Equal("column_native_enum_mode_conflict", Assert.Single(report.Assessments).AnalysisCode);
        Assert.Contains("doka_sm_unsupported", AssertOwnedGuardFailure(failure).Message,
            StringComparison.Ordinal);
        Assert.Equal((useSetStorage ? "set" : "enum") + @"('slash\\tail')", physicalType);
        Assert.Equal(Convert.ToHexString(Encoding.UTF8.GetBytes("legacy")), commentHex);
        Assert.Equal(Convert.ToHexString(Encoding.UTF8.GetBytes(@"slash\tail")), storedHex);
        Assert.Equal(new AtomicRequiredAddState(1, 0, 0, 1), state);
        Assert.Equal(originalMode, finalMode);
        Assert.Equal(originalFingerprint, SafeMigrationContractFingerprint.Create([operation]));
    }

    /// <summary>
    /// A matching standard-only member remains a no-op even when its unused native comment scope is invalid.
    /// </summary>
    /// <param name="useSetStorage">Whether the quoted-apostrophe member belongs to SET rather than ENUM.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredDefaultEnumCommentStandardOnlyMatchingTargetRemainsNoOp(bool useSetStorage)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await AtomicRequiredAddSessionSqlAsync(context, "SET SESSION sql_mode = 'STRICT_ALL_TABLES'; "
            + RequiredDefaultsEnumCommentStandardOnlyFixtureSql(useSetStorage));
        var operation = RequiredDefaultsEnumCommentStandardOnlyOperation(useSetStorage);
        var originalFingerprint = SafeMigrationContractFingerprint.Create([operation]);
        var originalMode = await RequiredDefaultsEnumCommentSessionModeAsync(context);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("required-enum-comment-standard-only-matching"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var postflight = await runner.VerifyAsync(context, [operation],
            new SafeMigrationRunOptions("required-enum-comment-standard-only-final"), CancellationToken.None);

        var finalMode = await RequiredDefaultsEnumCommentSessionModeAsync(context);
        var physicalType = await RequiredDefaultsEnumCommentPhysicalTypeAsync(connectionString);
        var commentHex = await RequiredDefaultsEnumCommentPhysicalCommentHexAsync(connectionString);
        var storedHex = await ScalarStringAsync(connectionString,
            "SELECT HEX(`value`) FROM `enum_comment_items` WHERE `key` = 1;");

        var state = await ReadAtomicRequiredAddStateAsync(connectionString, operation);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(preflight.Assessments).Action);
        Assert.Null(failure);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal((useSetStorage ? "set" : "enum") + "('quo''te')", physicalType);
        Assert.Equal(Convert.ToHexString(Encoding.UTF8.GetBytes(EnumCommentTargetComment)), commentHex);
        Assert.Equal(Convert.ToHexString(Encoding.UTF8.GetBytes("quo'te")), storedHex);
        Assert.Equal(new AtomicRequiredAddState(1, 0, 0, 1), state);
        Assert.Equal(originalMode, finalMode);
        Assert.Equal(originalFingerprint, SafeMigrationContractFingerprint.Create([operation]));
    }

    /// <summary>
    /// A standard-only member cannot enter an atomic add whose native comment scope invalidates its syntax.
    /// </summary>
    /// <param name="useSetStorage">Whether the quoted-apostrophe member belongs to SET rather than ENUM.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredDefaultEnumCommentStandardOnlyMissingTargetRejectsBeforeNativeScope(bool useSetStorage)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, AtomicRequiredAddOriginalTableSql("enum_comment_items"));
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await AtomicRequiredAddSessionSqlAsync(context, "SET SESSION sql_mode = 'STRICT_ALL_TABLES';");
        var operation = RequiredDefaultsEnumCommentStandardOnlyOperation(useSetStorage);
        var originalFingerprint = SafeMigrationContractFingerprint.Create([operation]);
        var originalMode = await RequiredDefaultsEnumCommentSessionModeAsync(context);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("required-enum-comment-standard-only-missing"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var finalMode = await RequiredDefaultsEnumCommentSessionModeAsync(context);
        var state = await ReadAtomicRequiredAddStateAsync(connectionString, operation);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, Assert.Single(report.Assessments).Action);
        Assert.Contains("doka_sm_unsupported", AssertOwnedGuardFailure(failure).Message,
            StringComparison.Ordinal);
        Assert.Equal(new AtomicRequiredAddState(0, 0, 0, 0), state);
        Assert.Equal(originalMode, finalMode);
        Assert.Equal(originalFingerprint, SafeMigrationContractFingerprint.Create([operation]));
    }

    /// <summary>Captures a standard-only escaped apostrophe without rewriting the authored member syntax.</summary>
    /// <param name="useSetStorage">Whether the authored domain uses SET rather than ENUM.</param>
    /// <returns>The immutable required target with its exact backslash comment.</returns>
    private static SafeMigrationOperation RequiredDefaultsEnumCommentStandardOnlyOperation(bool useSetStorage)
        => new(new EnsureColumnIntent("enum_comment_items",
                new ExpectedColumnDefinition("value", typeof(string), false,
                    (useSetStorage ? "set" : "enum") + @"('quo\'te')", comment: EnumCommentTargetComment)),
            SafeMigrationPolicy.RepairIfSafe);

    /// <summary>
    /// Creates the exact standard-decoded apostrophe member and one-backslash comment before migration.
    /// </summary>
    /// <param name="useSetStorage">Whether the physical domain uses SET rather than ENUM.</param>
    /// <returns>The fixed standard-mode DDL and hex-bound explicit member value.</returns>
    private static string RequiredDefaultsEnumCommentStandardOnlyFixtureSql(bool useSetStorage)
    {
        // WHY: A backslash-escaped apostrophe is valid only in the selected
        // standard mode. Matching emits no target DDL and must not validate an
        // unused native scope as though a new member were being created.
        return "CREATE TABLE `enum_comment_items` (`key` int NOT NULL PRIMARY KEY, `value` "
            + (useSetStorage ? "set" : "enum") + @"('quo\'te') NOT NULL COMMENT 'comment\\path'"
            + ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin; "
            + "INSERT INTO `enum_comment_items` (`key`, `value`) VALUES (1, "
            + AtomicRequiredAddLiteral("quo'te") + ");";
    }

    /// <summary>Captures the exact two-backslash raw member declaration and one-backslash target comment.</summary>
    /// <param name="useSetStorage">Whether the authored domain uses SET rather than ENUM.</param>
    /// <returns>The immutable required target with explicit safe-repair policy.</returns>
    private static SafeMigrationOperation RequiredDefaultsEnumCommentOperation(bool useSetStorage)
        => new(new EnsureColumnIntent("enum_comment_items",
                new ExpectedColumnDefinition("value", typeof(string), false,
                    (useSetStorage ? "set" : "enum") + @"('slash\\tail')", comment: EnumCommentTargetComment)),
            SafeMigrationPolicy.RepairIfSafe);

    /// <summary>Creates a real standard-mode domain with an independently declared exact or legacy comment.</summary>
    /// <param name="useSetStorage">Whether the physical domain uses SET rather than ENUM.</param>
    /// <param name="matchingComment">Whether the real comment matches the requested one-backslash target.</param>
    /// <returns>The fixed anonymous DDL and hex-bound explicit member value.</returns>
    private static string RequiredDefaultsEnumCommentStandardFixtureSql(bool useSetStorage, bool matchingComment)
    {
        // WHY: The caller explicitly selects standard native decoding. The
        // fixed comment literal has two raw backslashes, decoding to one;
        // value insertion and metadata HEX assertions avoid escaping ambiguity.
        var commentLiteral = matchingComment ? @"'comment\\path'" : "'legacy'";

        return "CREATE TABLE `enum_comment_items` (`key` int NOT NULL PRIMARY KEY, `value` "
            + (useSetStorage ? "set" : "enum") + @"('slash\\tail') NOT NULL COMMENT " + commentLiteral
            + ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin; "
            + "INSERT INTO `enum_comment_items` (`key`, `value`) VALUES (1, "
            + AtomicRequiredAddLiteral(@"slash\tail") + ");";
    }
}

public abstract partial class MySqlSafeMigrationIntegrationTests
{

    /// <summary>Reads the caller's retained native session mode before and after actual scoped execution.</summary>
    /// <param name="context">The already-open migration context.</param>
    /// <returns>The server's exact mode text without normalizing or changing it.</returns>
    protected static async Task<string> RequiredDefaultsEnumCommentSessionModeAsync(DbContext context)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT @@SESSION.sql_mode;";

        return Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None),
            CultureInfo.InvariantCulture) ?? "<null>";
    }
}

public sealed partial class MySqlColumnsRequiredDefaultsEnumCommentModeIntegrationTests : MySqlSafeMigrationIntegrationTests
{

    /// <summary>Reads the complete physical domain display without case folding or mode reinterpretation.</summary>
    /// <param name="connectionString">The isolated test owner.</param>
    /// <returns>The exact stored type display, or the scalar helper's missing sentinel.</returns>
    private static Task<string> RequiredDefaultsEnumCommentPhysicalTypeAsync(string connectionString)
        => ScalarStringAsync(connectionString,
            "SELECT COLUMN_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'enum_comment_items' AND COLUMN_NAME = 'value';");

    /// <summary>Reads exact comment bytes so a fixture escaping error cannot impersonate target ownership.</summary>
    /// <param name="connectionString">The isolated test owner.</param>
    /// <returns>The hexadecimal comment bytes, or the scalar helper's missing sentinel.</returns>
    private static Task<string> RequiredDefaultsEnumCommentPhysicalCommentHexAsync(string connectionString)
        => ScalarStringAsync(connectionString,
            "SELECT HEX(COLUMN_COMMENT) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'enum_comment_items' AND COLUMN_NAME = 'value';");
}
