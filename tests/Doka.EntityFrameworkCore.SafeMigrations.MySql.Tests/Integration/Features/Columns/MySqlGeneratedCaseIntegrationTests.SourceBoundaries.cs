namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlGeneratedCaseIntegrationTests
{
    /// <summary>Foreign captured quote syntax cannot borrow a native backtick expression certificate.</summary>
    /// <param name="sql">A foreign or mode-dependent source identifier spelling.</param>
    /// <param name="kind">The generated, standalone CHECK or fresh inline CHECK capture path.</param>
    [Theory]
    [InlineData("[id] > 0", "column")]
    [InlineData("\"id\" > 0", "column")]
    [InlineData("[id] > 0", "check")]
    [InlineData("\"id\" > 0", "check")]
    [InlineData("[id] > 0", "table-check")]
    [InlineData("\"id\" > 0", "table-check")]
    public async Task CapturedForeignQuoteRolesCannotMatchNativeContracts(string sql, string kind)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var check = SafeMigrationExpectedDefinitionFactory.From(new AddCheckConstraintOperation
        {
            Name = "ck_source_role", Table = Table, Sql = sql,
        });

        if (kind == "table-check")
        {
            builder.EnsureTable(new ExpectedTableDefinition(Table,
                [new ExpectedColumnDefinition("id", typeof(int), false, "int")], checkConstraints: [check]),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }
        else
        {
            await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL PRIMARY KEY, "
                + "native_result tinyint(1) GENERATED ALWAYS AS (id > 0) VIRTUAL, "
                + "CONSTRAINT ck_source_role CHECK(id > 0));");
            if (kind == "column")
            {
                builder.EnsureColumn(Table, SafeMigrationExpectedDefinitionFactory.From(new AddColumnOperation
                {
                    Name = "native_result", Table = Table, ClrType = typeof(bool), ColumnType = "tinyint(1)",
                    ComputedColumnSql = sql, IsStored = false, IsNullable = true,
                }), SafeMigrationPolicy.ThrowIfDifferent);
            }
            else
            {
                builder.EnsureCheckConstraint(check, SafeMigrationPolicy.ThrowIfDifferent);
            }
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-native-quote-boundary"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var owners = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() "
            + $"AND TABLE_NAME = '{Table}';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal("captured_expression_dialect_unverified", Assert.Single(report.Assessments).AnalysisCode);
        AssertOwnedGuardFailure(exception);
        Assert.Equal(kind == "table-check" ? 0 : 1, owners);
    }

    /// <summary>MariaDB's lossy supplementary source catalog is rejected before target DDL.</summary>
    /// <param name="kind">The generated, standalone CHECK or fresh inline CHECK source path.</param>
    [GeneratedMariaDbTheory]
    [InlineData("column")]
    [InlineData("check")]
    [InlineData("table-check")]
    public async Task CapturedSupplementaryLiteralIsRejectedBeforeDdl(string kind)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        if (kind != "table-check")
        {
            await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL PRIMARY KEY);");
        }

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var definition = SafeMigrationExpectedDefinitionFactory.From(new AddColumnOperation
        {
            Name = "source_result", Table = Table, ClrType = typeof(string), ColumnType = "varchar(20)",
            MaxLength = 20, IsNullable = true, IsStored = false,
            ComputedColumnSql = "CASE WHEN id > 0 THEN '\U0001F642' ELSE '' END",
        });

        var check = SafeMigrationExpectedDefinitionFactory.From(new AddCheckConstraintOperation
        {
            Name = "ck_supplementary_source", Table = Table, Sql = "id > 0 AND '\U0001F642' = '\U0001F642'",
        });

        if (kind == "column")
        {
            builder.EnsureColumn(Table, definition, SafeMigrationPolicy.ThrowIfDifferent);
        }
        else if (kind == "check")
        {
            builder.EnsureCheckConstraint(check, SafeMigrationPolicy.ThrowIfDifferent);
        }
        else
        {
            builder.EnsureTable(new ExpectedTableDefinition(Table,
                [new ExpectedColumnDefinition("id", typeof(int), false, "int")], checkConstraints: [check]),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-lossy-source-boundary"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + $"AND TABLE_NAME = '{Table}' AND COLUMN_NAME = 'source_result';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal("captured_expression_literal_catalog_unverifiable",
            Assert.Single(report.Assessments).AnalysisCode);
        AssertOwnedGuardFailure(exception);
        Assert.Equal(0, columns);
    }

    /// <summary>MySQL's encoded catalog preserves supplementary captured source without rewriting it.</summary>
    /// <param name="adoptExisting">Whether the native generated value predates the expected contract.</param>
    [GeneratedMySqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedSupplementaryMySqlSourceRoundTrips(bool adoptExisting)
    {
        // Arrange
        const string source = "CASE WHEN id > 0 THEN '\U0001F642' ELSE '' END";
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL PRIMARY KEY); "
            + $"INSERT INTO `{Table}` VALUES (1);");
        if (adoptExisting)
        {
            await ExecuteSqlAsync(connectionString, $"ALTER TABLE `{Table}` ADD COLUMN source_result varchar(20) "
                + $"GENERATED ALWAYS AS ({source}) VIRTUAL;");
        }

        var definition = SafeMigrationExpectedDefinitionFactory.From(new AddColumnOperation
        {
            Name = "source_result", Table = Table, ClrType = typeof(string), ColumnType = "varchar(20)",
            MaxLength = 20, IsNullable = true, IsStored = false, ComputedColumnSql = source,
        });

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(Table, definition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var exception = await Record.ExceptionAsync(async () =>
        {
            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
        });

        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-mysql-supplementary-replay"), CancellationToken.None);

        var result = await ScalarStringAsync(connectionString, $"SELECT source_result FROM `{Table}` WHERE id = 1;");

        // Assert
        Assert.Same(source, definition.CapturedComputedSql);
        Assert.True(exception is null, $"{exception?.Message}; " + DescribeGeneratedReport(report));
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal("\U0001F642", result);
    }

    /// <summary>Reports the encoded supplementary source contract only for its independently verified engine.</summary>
    private sealed class GeneratedMySqlTheoryAttribute : TheoryAttribute
    {
        /// <summary>Uses the fixture's engine selection without creating a second shared container.</summary>
        public GeneratedMySqlTheoryAttribute()
        {
            if (!IsMySqlEngine())
            {
                Skip = "MySQL encoded source metadata is not MariaDB's explicitly rejected lossy catalog path.";
            }
        }
    }

    /// <summary>Unambiguous source delimiter doubling retains author SQL and its native escaped display.</summary>
    [Fact]
    public async Task CapturedEscapedQuotePreservesSourceAndNativeResult()
    {
        // Arrange
        const string source = "CASE WHEN id > 0 THEN 'A''B' ELSE '' END";
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL PRIMARY KEY); "
            + $"INSERT INTO `{Table}` VALUES (1);");
        var definition = SafeMigrationExpectedDefinitionFactory.From(new AddColumnOperation
        {
            Name = "source_result", Table = Table, ClrType = typeof(string), ColumnType = "varchar(20)",
            MaxLength = 20, IsNullable = true, IsStored = false, ComputedColumnSql = source,
        });

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(Table, definition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var exception = await Record.ExceptionAsync(async () =>
        {
            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
        });

        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-delimiter-source-replay"), CancellationToken.None);

        var result = await ScalarStringAsync(connectionString, $"SELECT source_result FROM `{Table}` WHERE id = 1;");

        // Assert
        Assert.Same(source, definition.CapturedComputedSql);
        Assert.True(exception is null, $"{exception?.Message}; " + DescribeGeneratedReport(report));
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal("A'B", result);
    }
}
