namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlGeneratedCaseIntegrationTests
{
    private const string AnalysisScopeSource = "generated_scope_rename_source";
    private const string AnalysisScopeTarget = "generated_scope_rename_target";

    /// <summary>Nested rename-source classification cannot replace another owner's generated-index evidence.</summary>
    /// <param name="includeRename">Whether an independent rename forces the nested native source classifier.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedIndexQualificationSurvivesIndependentRenameAnalysis(bool includeRename)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await SeedGeneratedAnalysisScopeOwnersAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("unrelated", Table);
        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_generated_scope_result", Table,
            [new ExpectedIndexKeyDefinition("has_descendants")]), SafeMigrationPolicy.ThrowIfDifferent);
        AddGeneratedAnalysisScopeRename(builder, includeRename);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-independent-rename-index-scope"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(async () =>
        {
            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
        });

        var postflight = await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-independent-rename-index-verify"), CancellationToken.None);

        var rows = failure is null ? await CountExpectedCaseRowsAsync(connectionString) : -1;
        var indexes = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() "
            + $"AND TABLE_NAME = '{Table}' AND INDEX_NAME = 'ix_generated_scope_result';");

        // Assert
        Assert.True(preflight.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(preflight));
        // WHY: The independent rename and rebound index must really apply;
        // a skipped source probe would not exercise the analyzer scope seam.
        Assert.All(preflight.Assessments,
            static assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Null(failure);
        Assert.True(postflight.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(postflight));
        Assert.Equal(5, rows);
        Assert.Equal(1, indexes);
    }

    /// <summary>A nested source probe cannot erase an accepted generated dependency before a source DROP.</summary>
    /// <param name="includeRename">Whether the stream also contains the independent nested rename source probe.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedDependencyExpirySurvivesIndependentRenameAnalysis(bool includeRename)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await SeedGeneratedAnalysisScopeOwnersAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropColumnIfExists("left_bound", Table);
        AddGeneratedAnalysisScopeRename(builder, includeRename);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-independent-rename-dependency-scope"), CancellationToken.None);

        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var expiredOwners = typeof(MySqlSafeMigrationProviderAnalyzer)
            .GetField("_expiredGeneratedDependencyOwners", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(analyzer) as HashSet<(string? Schema, string Table)>;

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var retainedSource = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + $"AND TABLE_NAME = '{Table}' AND COLUMN_NAME = 'left_bound';");

        var rows = await CountExpectedCaseRowsAsync(connectionString);

        // Assert
        // WHY: Core can independently reject the known dependency. Observe the
        // provider's accepted-mutation certificate too, so that rejection cannot
        // hide a nested probe disabling generated-set lifetime invalidation.
        Assert.NotNull(expiredOwners);
        Assert.Contains((null, Table), expiredOwners);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[0].Action);
        if (includeRename)
        {
            Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[2].Action);
            Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[3].Action);
        }

        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, preflight.Assessments[1].Action);
        Assert.True(preflight.Assessments[1].AnalysisCode
            is "generated_column_drop_dependency" or "generated_column_drop_unproven");
        Assert.Equal("doka_sm_unsupported", Assert.IsType<SafeMigrationExecutionException>(failure).GuardCode);
        Assert.Equal(1, retainedSource);
        Assert.Equal(5, rows);
    }

    /// <summary>Seeds independent physical owners without supplying a complete generated-owner model.</summary>
    private static async Task SeedGeneratedAnalysisScopeOwnersAsync(string connectionString)
    {
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE `{Table}` (id int NOT NULL PRIMARY KEY, left_bound bigint NOT NULL, "
            + "right_bound bigint NOT NULL, unrelated int NULL); "
            + $"CREATE TABLE `{AnalysisScopeSource}` (id int NOT NULL PRIMARY KEY, value int NOT NULL); "
            + $"INSERT INTO `{AnalysisScopeSource}` (id, value) VALUES (1, 7);");
        await SeedCaseRowsAsync(connectionString);
    }

    /// <summary>Forces a real rebound EnsureIndex analysis on a separate renamed native owner.</summary>
    private static void AddGeneratedAnalysisScopeRename(MigrationBuilder builder, bool includeRename)
    {
        if (!includeRename)
        {
            return;
        }

        builder.RenameTableIfExists(AnalysisScopeSource, newName: AnalysisScopeTarget);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_generated_scope_renamed_value", AnalysisScopeTarget,
            [new ExpectedIndexKeyDefinition("value")]), SafeMigrationPolicy.ThrowIfDifferent);
    }
}
