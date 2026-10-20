namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises generated CASE columns without a consumer model or an unrelated migration stream.</summary>
public sealed partial class MySqlGeneratedCaseIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    private const string Table = "generated_case_columns";

    /// <summary>Uses the shared native engine and independently leased test databases.</summary>
    /// <param name="fixture">The assembly-owned MySQL or MariaDB engine.</param>
    public MySqlGeneratedCaseIntegrationTests(MySqlEngineContainerFixture fixture) : base(fixture) { }

    /// <summary>Metadata collation cannot hide observable case drift in generated CASE string results.</summary>
    [Fact]
    public async Task GeneratedCaseLiteralCaseDriftRemainsDifferent()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL PRIMARY KEY); "
            + $"INSERT INTO `{Table}` VALUES (1);");
        var initial = new MigrationBuilder(context.Database.ProviderName!);
        initial.EnsureColumn(Table, StringCaseColumn("Alpha"), SafeMigrationPolicy.ThrowIfDifferent);
        var creationFailure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, initial.Operations));
        var native = creationFailure is null
            ? null : await GeneratedNativeDisplayAsync(connectionString, "string_result");
        var changed = new MigrationBuilder(context.Database.ProviderName!);
        changed.EnsureColumn(Table, StringCaseColumn("alpha"), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, changed.Operations,
            new SafeMigrationRunOptions("generated-case-literal-drift"), CancellationToken.None);

        var original = await ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM `{Table}` WHERE CAST(string_result AS BINARY) = CAST('Alpha' AS BINARY);");

        // Assert
        Assert.True(creationFailure is null, $"{creationFailure?.Message}; native={native}");
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(1, original);
    }

    /// <summary>Builds a nullable string result independently of MariaDB's integral non-null proof.</summary>
    private static ExpectedColumnDefinition StringCaseColumn(string result, string source = "id",
        string? prefix = null) => new("string_result",
        typeof(string),
        true, "varchar(20)", maxLength: 20, computedExpression: SafeMigrationSql.Case(
        [
            new SafeMigrationSqlCaseWhenClause(prefix is null
                    ? SqlColumnAndInt(source, SafeMigrationSqlBinaryOperator.GreaterThan, 0)
                    : SqlBinary(SqlBinary(SafeMigrationSql.Literal(prefix), SafeMigrationSqlBinaryOperator.Equal,
                        SafeMigrationSql.Literal(prefix)), SafeMigrationSqlBinaryOperator.And,
                        SqlColumnAndInt(source, SafeMigrationSqlBinaryOperator.GreaterThan, 0)),
                SafeMigrationSql.Literal(result)),
        ], SafeMigrationSql.Literal("Beta")), isStored: false);

    /// <summary>Native member names remain case-insensitive without folding generated literal values.</summary>
    /// <param name="result">The exact literal token, including empty, escaped and supplementary characters.</param>
    /// <param name="prefix">An earlier literal whose UTF-8 length must not shift later literal guards.</param>
    [Theory]
    [InlineData("Alpha", null)]
    [InlineData("", null)]
    [InlineData("A'\\lpha", null)]
    [InlineData("\U0001F642Alpha", "\U0001F642")]
    public async Task GeneratedCaseIdentifierCaseUsesNativeMemberIdentity(string result, string? prefix)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL PRIMARY KEY); "
            + $"INSERT INTO `{Table}` VALUES (1);");
        var initial = new MigrationBuilder(context.Database.ProviderName!);
        initial.EnsureColumn(Table, StringCaseColumn(result, prefix: prefix), SafeMigrationPolicy.ThrowIfDifferent);
        var creationFailure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, initial.Operations));
        var native = creationFailure is null
            ? null : await GeneratedNativeDisplayAsync(connectionString, "string_result");
        var equivalent = new MigrationBuilder(context.Database.ProviderName!);
        equivalent.EnsureColumn(Table, StringCaseColumn(result, "ID", prefix), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, equivalent.Operations,
            new SafeMigrationRunOptions("generated-case-identifier-identity"), CancellationToken.None);

        var actual = await ScalarStringAsync(connectionString,
            $"SELECT string_result FROM `{Table}` WHERE id = 1;");

        // Assert
        Assert.True(creationFailure is null, $"{creationFailure?.Message}; native={native}");
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(result, actual);
    }

    /// <summary>Admitted integral proof forms round-trip through the native deparser.</summary>
    /// <param name="sql">The structured expression captured from ordinary EF generated-column metadata.</param>
    /// <param name="expected">The native generated result for left_bound 2 and right_bound 3.</param>
    [Theory]
    [InlineData("TRUE", 1)]
    [InlineData("(left_bound + 1) * right_bound", 9)]
    [InlineData("left_bound - (right_bound - 1)", 0)]
    [InlineData("NOT (left_bound > right_bound)", 1)]
    [InlineData("NOT left_bound", 0)]
    [InlineData("NOT NOT left_bound", 1)]
    [InlineData("NOT (left_bound > 0 AND right_bound > 0)", 0)]
    [InlineData("left_bound + 1 IS NULL", 0)]
    [InlineData("left_bound BETWEEN 0 AND right_bound", 1)]
    [InlineData("left_bound IN (0, right_bound)", 0)]
    public async Task RequiredIntegralExpressionFormsRoundTripNativeCatalog(string sql, int expected)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` "
            + "(`id` int NOT NULL PRIMARY KEY, `left_bound` bigint NOT NULL, `right_bound` bigint NOT NULL); "
            + $"INSERT INTO `{Table}` VALUES (1, 2, 3);");
        var definition = SafeMigrationExpectedDefinitionFactory.From(new AddColumnOperation
        {
            Name = "integral_result", Table = Table, ClrType = typeof(long), ColumnType = "bigint",
            ComputedColumnSql = sql, IsStored = false, IsNullable = false,
        });

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(Table, definition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("integral-generated-forms"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(async () =>
        {
            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
        });

        var postflight = await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("integral-generated-forms-replay"), CancellationToken.None);

        var result = exception is null
            ? await ScalarIntAsync(connectionString, $"SELECT integral_result FROM `{Table}` WHERE id = 1;") : -1;
        var native = exception is null ? null : await GeneratedNativeDisplayAsync(connectionString, "integral_result");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.True(exception is null,
            $"{exception?.Message}; native={native}; " + DescribeGeneratedReport(postflight));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(expected, result);
    }

    /// <summary>A required generated predicate can be classified before its table exists.</summary>
    /// <param name="stored">Whether the authored generated value is stored rather than virtual.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshRequiredCasePreflightIsApplicable(bool stored)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(CaseTable(stored), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fresh-generated-case"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(report.Assessments).Action);
    }

    /// <summary>Round-trips the exact native facets without mutating input rows.</summary>
    /// <param name="stored">Whether the authored generated value is stored rather than virtual.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshRequiredCaseExecutesAndReplaysAtSignedBoundaries(bool stored)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(CaseTable(stored), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var exception = await Record.ExceptionAsync(async () =>
        {
            await ExecuteOperationsAsync(context, builder.Operations);
            await SeedCaseRowsAsync(connectionString);
            await ExecuteOperationsAsync(context, builder.Operations);
        });

        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fresh-generated-case-replay"), CancellationToken.None);

        var computedRows = exception is null ? await CountExpectedCaseRowsAsync(connectionString) : -1;

        // Assert
        Assert.True(exception is null, $"{exception}; " + DescribeGeneratedReport(report));
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(5, computedRows);
    }

    /// <summary>A populated ADD and dependent index retain values and ordered prerequisites.</summary>
    /// <param name="stored">Whether the authored generated value is stored rather than virtual.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PopulatedRequiredCaseAddAndIndexReplayWithoutDataChanges(bool stored)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` "
            + "(`id` int NOT NULL PRIMARY KEY, `left_bound` bigint NOT NULL, `right_bound` bigint NOT NULL);");
        await SeedCaseRowsAsync(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(Table, CaseColumn(stored), policy: SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_generated_case", Table,
            [new ExpectedIndexKeyDefinition("has_descendants"), new ExpectedIndexKeyDefinition("id")]),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("populated-generated-case-add"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(async () =>
        {
            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
        });

        var postflight = await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("populated-generated-case-replay"), CancellationToken.None);

        var computedRows = exception is null ? await CountExpectedCaseRowsAsync(connectionString) : -1;

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Null(exception);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(5, computedRows);
    }

    /// <summary>Supported expressions never relax expression, storage or declared native type drift.</summary>
    /// <param name="facet">The one independently changed generated-column facet.</param>
    [Theory]
    [InlineData("expression")]
    [InlineData("storage")]
    [InlineData("type")]
    public async Task GeneratedCaseDriftRemainsBlocked(string facet)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var initial = new MigrationBuilder(context.Database.ProviderName!);
        initial.EnsureTable(CaseTable(stored: false), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        await ExecuteOperationsAsync(context, initial.Operations);
        await SeedCaseRowsAsync(connectionString);
        var changed = new MigrationBuilder(context.Database.ProviderName!);
        changed.EnsureColumn(Table, CaseColumn(stored: facet == "storage",
            offset: facet == "expression" ? 2 : 1, storeType: facet == "type" ? "smallint" : "tinyint(1)"),
            policy: SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, changed.Operations,
            new SafeMigrationRunOptions("generated-case-drift"), CancellationToken.None);

        var rows = await CountExpectedCaseRowsAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(5, rows);
    }

    /// <summary>Explicit opaque SQL remains unsupported even when it resembles a supported CASE expression.</summary>
    [Fact]
    public async Task ExplicitOpaqueGeneratedCaseRemainsRejected()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition(Table,
        [
            new ExpectedColumnDefinition("id", typeof(int), false, "int"),
            new ExpectedColumnDefinition("has_descendants", typeof(bool), true, "tinyint(1)",
                computedExpression: SafeMigrationSql.Opaque("CASE WHEN id > 0 THEN 1 ELSE 0 END"), isStored: false),
        ]), SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("opaque-generated-case"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Contains(report.Assessments, static assessment =>
            assessment.ObservedState == SafeMigrationObservedState.Unsupported
            && assessment.Code == "opaque_sql_expression");
    }

    /// <summary>Builds the immutable physical contract independently of EF model conventions.</summary>
    private static ExpectedTableDefinition CaseTable(bool stored) => new(Table,
    [
        new ExpectedColumnDefinition("id", typeof(int), false, "int"),
        new ExpectedColumnDefinition("left_bound", typeof(long), false, "bigint"),
        new ExpectedColumnDefinition("right_bound", typeof(long), false, "bigint"),
        CaseColumn(stored),
    ], primaryKey: new ExpectedPrimaryKeyDefinition("PRIMARY", Table, ["id"]));

    /// <summary>Keeps the overflow guard as CASE instead of relying on Boolean evaluation order.</summary>
    private static ExpectedColumnDefinition CaseColumn(bool stored, int offset = 1, string storeType = "tinyint(1)",
        string leftColumn = "left_bound") =>
        new("has_descendants", typeof(bool), false, storeType,
            computedExpression: SqlBinary(SqlColumn("right_bound"), SafeMigrationSqlBinaryOperator.GreaterThan,
                SafeMigrationSql.Case(
                [
                    new SafeMigrationSqlCaseWhenClause(
                        SqlBinary(SqlColumn(leftColumn), SafeMigrationSqlBinaryOperator.Equal,
                            SafeMigrationSql.Literal(long.MaxValue)), SqlColumn(leftColumn)),
                ], SqlBinary(SqlColumn(leftColumn), SafeMigrationSqlBinaryOperator.Add,
                    SafeMigrationSql.Literal(offset)))), isStored: stored);

    /// <summary>Includes both signed extrema and ordinary true/false generated outcomes.</summary>
    private static Task SeedCaseRowsAsync(string connectionString) => ExecuteSqlAsync(connectionString,
        $"INSERT INTO `{Table}` (`id`, `left_bound`, `right_bound`) VALUES "
        + "(1, 1, 2), (2, 1, 3), (3, 9223372036854775807, 9223372036854775807), "
        + "(4, -9223372036854775808, -9223372036854775807), "
        + "(5, -9223372036854775808, -9223372036854775806);");

    /// <summary>Checks generated outputs while also retaining every original key and bound.</summary>
    private static Task<int> CountExpectedCaseRowsAsync(string connectionString) => ScalarIntAsync(connectionString,
        $"SELECT COUNT(*) FROM `{Table}` WHERE "
        + "(id = 1 AND left_bound = 1 AND right_bound = 2 AND has_descendants = 0) OR "
        + "(id = 2 AND left_bound = 1 AND right_bound = 3 AND has_descendants = 1) OR "
        + "(id = 3 AND left_bound = 9223372036854775807 AND right_bound = 9223372036854775807 "
        + "AND has_descendants = 0) OR "
        + "(id = 4 AND left_bound = -9223372036854775808 AND right_bound = -9223372036854775807 "
        + "AND has_descendants = 0) OR "
        + "(id = 5 AND left_bound = -9223372036854775808 AND right_bound = -9223372036854775806 "
        + "AND has_descendants = 1);");

    /// <summary>Retains bounded catalog differences when a native postcondition fails before replay.</summary>
    private static string DescribeGeneratedReport(SafeMigrationRunReport report) => string.Join("; ",
        report.Assessments.Select(assessment => $"{assessment.ObservedState}:{assessment.AnalysisCode}:"
            + string.Join(", ", assessment.Differences.Select(difference =>
                $"{difference.Facet}={difference.Expected}/{difference.Actual}"))));

    /// <summary>Captures only deterministic test-fixture metadata when an expression postcondition fails.</summary>
    private static Task<string> GeneratedNativeDisplayAsync(string connectionString, string column) =>
        ScalarStringAsync(connectionString, "SELECT CONCAT(COLUMN_TYPE, '|', IS_NULLABLE, '|', "
            + "COALESCE(COLUMN_DEFAULT, '<null>'), '|', GENERATION_EXPRESSION) FROM INFORMATION_SCHEMA.COLUMNS "
            + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{Table}' AND COLUMN_NAME = '{column}';");
}
