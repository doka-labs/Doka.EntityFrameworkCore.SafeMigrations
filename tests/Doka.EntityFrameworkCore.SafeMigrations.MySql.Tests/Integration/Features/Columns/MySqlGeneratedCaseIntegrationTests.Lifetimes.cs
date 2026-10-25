namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlGeneratedCaseIntegrationTests
{
    /// <summary>An unrelated DROP preserves proven generated sources and independent index prerequisites.</summary>
    [Fact]
    public async Task UnrelatedDropPreservesGeneratedSourcesWithoutInventingIndexLayout()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL PRIMARY KEY, "
            + "left_bound bigint NOT NULL, right_bound bigint NOT NULL, unrelated int NULL);");
        await SeedCaseRowsAsync(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("unrelated", Table);
        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_unrelated_drop_case", Table,
            [new ExpectedIndexKeyDefinition("has_descendants")]), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-unrelated-drop"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(async () =>
        {
            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
        });

        var postflight = await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-unrelated-drop-verify"), CancellationToken.None);

        var rows = failure is null ? await CountExpectedCaseRowsAsync(connectionString) : -1;

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[0].Action);
        Assert.True(preflight.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(preflight));
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[2].Action);
        Assert.Null(failure);
        Assert.True(postflight.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(postflight));
        Assert.Equal(5, rows);
    }

    /// <summary>Owner recreation cannot reuse dependency facts from the dropped physical table.</summary>
    /// <param name="completeSources">Whether the new owner declares both required integral source members.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecreatedOwnerGeneratedDependenciesUseOnlyNewPhysicalLifetime(bool completeSources)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL PRIMARY KEY, "
            + "left_bound bigint NOT NULL, right_bound bigint NOT NULL);");
        var all = CaseTable(stored: false);
        var sources = all.Columns.Where(column => column.Name != "has_descendants"
            && (completeSources || column.Name != "right_bound"));

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists(Table);
        builder.EnsureTable(new ExpectedTableDefinition(Table, sources, primaryKey: all.PrimaryKey),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_recreated_case", Table,
            [new ExpectedIndexKeyDefinition("has_descendants")]), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-owner-recreation"), CancellationToken.None);

        // Assert
        Assert.Equal(completeSources ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            report.Status);
        Assert.Equal(completeSources ? SafeMigrationAction.Apply : SafeMigrationAction.RejectPrerequisiteMissing,
            report.Assessments[2].Action);
        Assert.Equal(completeSources ? SafeMigrationAction.Apply : SafeMigrationAction.RejectPrerequisiteMissing,
            report.Assessments[3].Action);
    }

    /// <summary>Final verification rewrites an earlier complete table contract through a known source rename.</summary>
    [GeneratedMariaDbFact]
    public async Task StrictGeneratedOwnerRenameAndReensurePreserveTerminalContract()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var renamed = CaseColumn(stored: false, leftColumn: "renamed_left");

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(CaseTable(stored: false), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.RenameColumnIfExists("left_bound", Table, "renamed_left");
        builder.EnsureColumn(Table, renamed, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-owner-source-rename"), CancellationToken.None);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var postflight = await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-owner-source-rename-verify"), CancellationToken.None);

        // Assert
        Assert.True(preflight.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(preflight));
        Assert.True(failure is null, failure?.ToString());
        Assert.True(postflight.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(postflight));
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
    }
}
