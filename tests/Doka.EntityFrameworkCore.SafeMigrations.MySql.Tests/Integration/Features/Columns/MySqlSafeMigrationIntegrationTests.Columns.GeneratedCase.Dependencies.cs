namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlGeneratedCaseIntegrationTests
{
    /// <summary>Combines an accepted source ADD with an independently captured live source.</summary>
    /// <param name="sourceAccepted">Whether the required source has an explicit non-null population value.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedExistingAndAddedSourcesRequireAnAcceptedSourceOperation(bool sourceAccepted)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` "
            + "(`id` int NOT NULL PRIMARY KEY, `right_bound` bigint NOT NULL); "
            + $"INSERT INTO `{Table}` VALUES (1, 3);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(Table, new ExpectedColumnDefinition("left_bound", typeof(long), false, "bigint",
            defaultValue: sourceAccepted ? SafeMigrationDefaultValue.Literal(1L) : SafeMigrationDefaultValue.None),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("mixed-generated-source-bindings"), CancellationToken.None);

        var exception = sourceAccepted
            ? await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations)) : null;

        var generated = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + $"AND TABLE_NAME = '{Table}' AND COLUMN_NAME = 'has_descendants';");

        // Assert
        Assert.Equal(sourceAccepted ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            report.Status);
        Assert.Equal(sourceAccepted ? SafeMigrationObservedState.Missing
                : SafeMigrationObservedState.PrerequisiteMissing,
            report.Assessments[^1].ObservedState);
        Assert.Null(exception);
        Assert.Equal(sourceAccepted ? 1 : 0, generated);
    }

    /// <summary>Generated SQL rechecks physical source metadata instead of trusting a prior Ready report.</summary>
    [GeneratedMariaDbFact]
    public async Task GeneratedAddRejectsDependencyRelaxedAfterPreflightAndSqlGeneration()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` "
            + "(`id` int NOT NULL PRIMARY KEY, `left_bound` bigint NOT NULL, `right_bound` bigint NOT NULL); "
            + $"INSERT INTO `{Table}` VALUES (1, 1, 3);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-case-metadata-race"), CancellationToken.None);

        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);
        await ExecuteSqlAsync(connectionString, $"ALTER TABLE `{Table}` MODIFY COLUMN `left_bound` bigint NULL;");
        var exception = await Record.ExceptionAsync(async () =>
        {
            foreach (var command in commands)
            {
                _ = await command.ExecuteNonQueryAsync(context.GetService<IRelationalConnection>(),
                    cancellationToken: CancellationToken.None);
            }
        });

        var generated = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + $"AND TABLE_NAME = '{Table}' AND COLUMN_NAME = 'has_descendants';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        AssertOwnedGuardFailure(exception);
        Assert.Equal(0, generated);
    }

    /// <summary>Literal result proofs never conceal a missing reference inside a CASE condition.</summary>
    /// <param name="freshTable">Whether the column belongs to a fresh CREATE instead of a populated ADD.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCaseConditionColumnIsRejectedWithoutTargetDdl(bool freshTable)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var definition = LiteralResultCaseColumn("missing_condition");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (freshTable)
        {
            builder.EnsureTable(new ExpectedTableDefinition(Table,
                [new ExpectedColumnDefinition("id", typeof(int), false, "int"), definition]),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }
        else
        {
            await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL PRIMARY KEY); "
                + $"INSERT INTO `{Table}` VALUES (17);");
            builder.EnsureColumn(Table, definition, SafeMigrationPolicy.ThrowIfDifferent);
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("missing-generated-case-condition"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var generated = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + $"AND TABLE_NAME = '{Table}' AND COLUMN_NAME = 'has_descendants';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal("generated_column_dependency_missing", Assert.Single(report.Assessments).AnalysisCode);
        AssertOwnedGuardFailure(exception);
        Assert.Equal(0, generated);
    }

    /// <summary>An UNKNOWN condition selects non-null ELSE without requiring a non-null condition source.</summary>
    /// <param name="booleanResults">Whether branches use Boolean instead of equivalent integral literals.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullableCaseConditionWithNonNullLiteralResultsRoundTrips(bool booleanResults)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` "
            + "(`id` int NOT NULL PRIMARY KEY, `nullable_condition` bigint NULL); "
            + $"INSERT INTO `{Table}` VALUES (1, NULL), (2, 1), (3, 0);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(Table, LiteralResultCaseColumn("nullable_condition", booleanResults),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("nullable-generated-case-condition"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(async () =>
        {
            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
        });

        var rows = exception is null ? await ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM `{Table}` WHERE (id = 1 AND has_descendants = 0) "
            + "OR (id = 2 AND has_descendants = 1) OR (id = 3 AND has_descendants = 0);") : -1;

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Null(exception);
        Assert.Equal(3, rows);
    }

    /// <summary>Keeps condition binding separate from the unconditional integral result guarantee.</summary>
    private static ExpectedColumnDefinition LiteralResultCaseColumn(string conditionColumn,
        bool booleanResults = false) => new(
        "has_descendants", typeof(bool), false, "tinyint(1)", computedExpression: SafeMigrationSql.Case(
        [
            new SafeMigrationSqlCaseWhenClause(SqlBinary(SqlColumn(conditionColumn),
                SafeMigrationSqlBinaryOperator.GreaterThan, SafeMigrationSql.Literal(0)),
                SafeMigrationSql.Literal(booleanResults ? true : (object)1)),
        ], SafeMigrationSql.Literal(booleanResults ? false : (object)0)), isStored: false);

    /// <summary>A required logical result cannot borrow nullable, generated or absent source guarantees.</summary>
    /// <param name="source">The independent invalid source contract.</param>
    [GeneratedMariaDbTheory]
    [InlineData("nullable")]
    [InlineData("generated")]
    [InlineData("missing")]
    public async Task FreshRequiredCaseRejectsUnprovenSources(string source)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var columns = CaseTable(stored: false).Columns.Where(static column => column.Name != "left_bound").ToList();
        if (source != "missing")
        {
            columns.Insert(1, new ExpectedColumnDefinition("left_bound", typeof(long), source == "nullable", "bigint",
                computedExpression: source == "generated" ? SafeMigrationSql.Literal(1) : null));
        }

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition(Table, columns), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("unproven-generated-case-source"), CancellationToken.None);

        var tables = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() "
            + $"AND TABLE_NAME = '{Table}';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Contains(report.Assessments, static assessment =>
            assessment.ObservedState == SafeMigrationObservedState.Unsupported
            && assessment.Code is "generated_column_nullability" or "generated_column_dependency_missing");
        Assert.Equal(0, tables);
    }

    /// <summary>Current non-null values never replace a missing physical guarantee for future generated rows.</summary>
    [GeneratedMariaDbFact]
    public async Task PopulatedNullableDependencyRejectsRequiredCaseWithoutAddingColumn()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` "
            + "(`id` int NOT NULL PRIMARY KEY, `left_bound` bigint NULL, `right_bound` bigint NOT NULL); "
            + $"INSERT INTO `{Table}` VALUES (1, 1, 3);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("nullable-generated-dependency"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var generated = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + $"AND TABLE_NAME = '{Table}' AND COLUMN_NAME = 'has_descendants';");
        var originalRows = await ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM `{Table}` WHERE id = 1 AND left_bound = 1 AND right_bound = 3;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal("generated_column_nullability", Assert.Single(report.Assessments).AnalysisCode);
        AssertOwnedGuardFailure(exception);
        Assert.Equal(0, generated);
        Assert.Equal(1, originalRows);
    }

    /// <summary>Dependency changes supersede captured facts without penalizing unrelated safe additions.</summary>
    /// <param name="change">The preceding accepted structural operation.</param>
    /// <param name="matchingTarget">Whether the computed target already matches the initial catalog.</param>
    /// <param name="ready">Whether current accepted metadata still proves the logical required result.</param>
    [GeneratedMariaDbTheory]
    [InlineData("nullable", false, false)]
    [InlineData("nullable", true, false)]
    [InlineData("drop", false, false)]
    [InlineData("rename", false, false)]
    [InlineData("unrelated", false, true)]
    [InlineData("replace", false, true)]
    public async Task OrderedGeneratedSourcesUseAcceptedMetadataRatherThanStaleLiveFacts(string change,
        bool matchingTarget, bool ready)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` "
            + "(`id` int NOT NULL PRIMARY KEY, `left_bound` bigint NOT NULL, `right_bound` bigint NOT NULL);");
        if (matchingTarget)
        {
            var initial = new MigrationBuilder(context.Database.ProviderName!);
            initial.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);
            await ExecuteOperationsAsync(context, initial.Operations);
        }

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        switch (change)
        {
            case "nullable":
                builder.AlterColumnIfDifferent(Table,
                    new ExpectedColumnDefinition("left_bound", typeof(long), true, "bigint"),
                    new ExpectedColumnDefinition("left_bound", typeof(long), false, "bigint"),
                    SafeMigrationPolicy.RepairIfSafe);
                break;
            case "drop":
                builder.DropColumnIfExists("left_bound", Table);
                break;
            case "rename":
                builder.RenameColumnIfExists("left_bound", Table, "renamed_left");
                break;
            case "unrelated":
                builder.EnsureColumn(Table, new ExpectedColumnDefinition("unrelated", typeof(int), true, "int"),
                    SafeMigrationPolicy.ThrowIfDifferent);
                break;
            case "replace":
                builder.DropColumnIfExists("left_bound", Table);
                builder.EnsureColumn(Table, new ExpectedColumnDefinition("left_bound", typeof(long), false, "bigint",
                    defaultValue: SafeMigrationDefaultValue.Literal(1L)), SafeMigrationPolicy.ThrowIfDifferent);
                break;
        }

        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("ordered-generated-source-proof"), CancellationToken.None);

        // Assert
        Assert.Equal(ready ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(ready ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.PrerequisiteMissing,
            report.Assessments[^1].ObservedState);
    }

    /// <summary>A newly created owner supplies accepted source definitions instead of old live metadata.</summary>
    [Fact]
    public async Task CreatedOwnerThenGeneratedAddAndIndexAreApplicable()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var table = CaseTable(stored: false);
        var baseColumns = table.Columns.Where(static column => column.Name != "has_descendants");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition(Table, baseColumns, primaryKey: table.PrimaryKey),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_created_case", Table,
            [new ExpectedIndexKeyDefinition("has_descendants")]), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("created-generated-owner"), CancellationToken.None);
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, static assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Null(exception);
    }

    /// <summary>Reports MariaDB-only semantic proof coverage honestly in the independently run MySQL suite.</summary>
    private sealed class GeneratedMariaDbFactAttribute : FactAttribute
    {
        /// <summary>Uses the engine selector shared with the assembly's native fixture.</summary>
        public GeneratedMariaDbFactAttribute()
        {
            if (IsMySqlEngine())
            {
                Skip = "MariaDB generated-column logical nullability proof is not the MySQL physical NOT NULL path.";
            }
        }
    }

    /// <summary>Excludes the engine that does not use a semantic nullability substitute.</summary>
    private sealed class GeneratedMariaDbTheoryAttribute : TheoryAttribute
    {
        /// <summary>Uses the engine selector shared with the assembly's native fixture.</summary>
        public GeneratedMariaDbTheoryAttribute()
        {
            if (IsMySqlEngine())
            {
                Skip = "MariaDB generated-column logical nullability proof is not the MySQL physical NOT NULL path.";
            }
        }
    }

    /// <summary>Matches the fixture's default-MariaDB environment policy without starting another engine.</summary>
    private static bool IsMySqlEngine() => string.Equals(
        Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_MYSQL_ENGINE")?.Trim(), "mysql",
        StringComparison.OrdinalIgnoreCase);
}
