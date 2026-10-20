namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlGeneratedCaseIntegrationTests
{
    /// <summary>Ordinary EF numeric source tokens keep native expression domains during create and adoption.</summary>
    /// <param name="adoptExisting">Whether native DDL creates the column before SafeMigrations sees it.</param>
    /// <param name="sql">An expression whose numeric domain must not become a CLR-rendered integer.</param>
    [Theory]
    [InlineData(false, "1e0 / 3")]
    [InlineData(true, "1e0 / 3")]
    [InlineData(false, "9223372036854775808 / 3")]
    [InlineData(true, "9223372036854775808 / 3")]
    public async Task CapturedGeneratedNumericSourcePreservesNativeEvaluation(bool adoptExisting, string sql)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL PRIMARY KEY); "
            + $"INSERT INTO `{Table}` VALUES (1);");
        if (adoptExisting)
        {
            await ExecuteSqlAsync(connectionString, $"ALTER TABLE `{Table}` ADD COLUMN numeric_result double "
                + $"GENERATED ALWAYS AS ({sql}) VIRTUAL;");
        }

        var definition = SafeMigrationExpectedDefinitionFactory.From(new AddColumnOperation
        {
            Name = "numeric_result", Table = Table, ClrType = typeof(double), ColumnType = "double",
            ComputedColumnSql = sql, IsStored = false, IsNullable = true,
        });

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(Table, definition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-generated-numeric-source"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(async () =>
        {
            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
        });

        var postflight = await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-generated-numeric-replay"), CancellationToken.None);

        var unchanged = await ScalarIntAsync(connectionString, $"SELECT COUNT(*) FROM `{Table}` "
            + $"WHERE id = 1 AND numeric_result = ({sql});");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.True(exception is null, $"{exception}; " + DescribeGeneratedReport(postflight));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(1, unchanged);
    }

    /// <summary>Captured CHECK source retains its native floating-point arithmetic domain.</summary>
    [Fact]
    public async Task CapturedCheckNumericSourcePreservesNativeEvaluation()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL PRIMARY KEY); "
            + $"INSERT INTO `{Table}` VALUES (1);");
        var definition = SafeMigrationExpectedDefinitionFactory.From(new AddCheckConstraintOperation
        {
            Name = "ck_numeric_domain", Table = Table, Sql = "id > 0 AND 1e0 / 3 > 0.33332",
        });

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureCheckConstraint(definition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var exception = await Record.ExceptionAsync(async () =>
        {
            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
        });

        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-check-numeric-replay"), CancellationToken.None);

        // Assert
        Assert.True(exception is null, $"{exception}; " + DescribeGeneratedReport(report));
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
    }

    /// <summary>CHECK literals retain exact case independently of case-insensitive member identity.</summary>
    /// <param name="changeLiteral">Whether the expected CHECK has observable literal-case drift.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedCaseCheckSeparatesLiteralAndIdentifierIdentity(bool changeLiteral)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL PRIMARY KEY); "
            + $"INSERT INTO `{Table}` VALUES (1);");
        var initial = new MigrationBuilder(context.Database.ProviderName!);
        initial.EnsureCheckConstraint(StringCaseCheck("Alpha", "id"), SafeMigrationPolicy.ThrowIfDifferent);
        var creationFailure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, initial.Operations));
        var native = creationFailure is null ? null : await ScalarStringAsync(connectionString,
            "SELECT CHECK_CLAUSE FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND CONSTRAINT_NAME = 'ck_case_literal';");
        var expected = new MigrationBuilder(context.Database.ProviderName!);
        expected.EnsureCheckConstraint(StringCaseCheck(changeLiteral ? "alpha" : "Alpha", "ID"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, expected.Operations,
            new SafeMigrationRunOptions("case-check-token-identity"), CancellationToken.None);

        // Assert
        Assert.True(creationFailure is null, $"{creationFailure?.Message}; native={native}");
        Assert.Equal(changeLiteral ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready,
            report.Status);
        Assert.Equal(changeLiteral ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching,
            Assert.Single(report.Assessments).ObservedState);
    }

    /// <summary>Uses binary string comparison so literal-case drift changes the CHECK's native data contract.</summary>
    private static ExpectedCheckConstraintDefinition StringCaseCheck(string result, string source) =>
        ExpectedCheckConstraintDefinition.FromExpression("ck_case_literal", Table,
            SqlBinary(SafeMigrationSql.Collate(StringCaseColumn(result, source).ComputedExpression!, "utf8mb4_bin"),
                SafeMigrationSqlBinaryOperator.Equal, SafeMigrationSql.Literal("Alpha")));
}
