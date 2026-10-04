namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>A newly projected table cannot manufacture the canonical predicate's missing column.</summary>
    /// <param name="filter">The canonical predicate referencing an absent physical column.</param>
    [Theory]
    [InlineData("missing IS NULL")]
    [InlineData("missing IS NOT NULL")]
    [InlineData("\"Missing\" IS NULL")]
    public async Task SimpleFilterNewTableMissingColumnRemainsBlocked(
        string filter
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA hierarchy;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            SimpleFilterTableDefinition(includePredicateColumn: false),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.Operations.Add(SimpleFilterBuilder(context, filter).Operations[0]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("simple-filter-projected-missing"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);
        Assert.Equal(0, await SimpleFilterIndexCountAsync(connectionString));
    }

    /// <summary>An accepted table or earlier column supplies the canonical predicate's exact prerequisite.</summary>
    /// <param name="filter">The canonical null-test predicate.</param>
    /// <param name="columnInTable">Whether the table operation itself supplies the predicate column.</param>
    [Theory]
    [InlineData("parent IS NULL", true)]
    [InlineData("parent IS NOT NULL", true)]
    [InlineData("parent IS NULL", false)]
    [InlineData("parent IS NOT NULL", false)]
    public async Task SimpleFilterEarlierTableOrColumnAllowsIndex(
        string filter,
        bool columnInTable
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA hierarchy;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            SimpleFilterTableDefinition(columnInTable),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        if (!columnInTable)
        {
            builder.EnsureColumn(
                "simple_filters",
                new ExpectedColumnDefinition("parent", typeof(int), true, "integer"),
                SafeMigrationPolicy.ThrowIfDifferent,
                schema: "hierarchy");
        }

        builder.Operations.Add(SimpleFilterBuilder(context, filter).Operations[0]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("simple-filter-projected-present"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationObservedState.Missing, report.Assessments[^1].ObservedState);
        Assert.Equal(0, await SimpleFilterIndexCountAsync(connectionString));
    }

    /// <summary>A matching table's compact projection cannot discard a missing raw-filter dependency.</summary>
    /// <param name="filter">The canonical predicate referencing an absent column.</param>
    /// <param name="mode">The table contract that enters the compact prerequisite projection.</param>
    [Theory]
    [InlineData("missing IS NULL", SafeMigrationTableMode.StrictDefinition)]
    [InlineData("missing IS NOT NULL", SafeMigrationTableMode.StrictDefinition)]
    [InlineData("missing IS NULL", SafeMigrationTableMode.ConvergenceContainer)]
    [InlineData("missing IS NOT NULL", SafeMigrationTableMode.ConvergenceContainer)]
    public async Task SimpleFilterMatchingTableMissingColumnRemainsBlocked(
        string filter,
        SafeMigrationTableMode mode
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE SCHEMA hierarchy; CREATE TABLE hierarchy.simple_filters (id integer NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            SimpleFilterTableDefinition(includePredicateColumn: false),
            mode,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.Operations.Add(SimpleFilterBuilder(context, filter).Operations[0]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("simple-filter-compact-missing"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);
    }

    /// <summary>An accepted column on an existing table supplies the compact projection's filter dependency.</summary>
    /// <param name="filter">The canonical null-test predicate.</param>
    [Theory]
    [InlineData("parent IS NULL")]
    [InlineData("parent IS NOT NULL")]
    public async Task SimpleFilterExistingTableEarlierColumnAllowsIndex(
        string filter
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE SCHEMA hierarchy; CREATE TABLE hierarchy.simple_filters (id integer NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            SimpleFilterTableDefinition(includePredicateColumn: false),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureColumn(
            "simple_filters",
            new ExpectedColumnDefinition("parent", typeof(int), true, "integer"),
            SafeMigrationPolicy.ThrowIfDifferent,
            schema: "hierarchy");
        builder.Operations.Add(SimpleFilterBuilder(context, filter).Operations[0]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("simple-filter-compact-present"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[2].Action);
    }

    /// <summary>A rejected noncanonical predicate remains unsupported after a projected table create.</summary>
    [Fact]
    public async Task SimpleFilterProjectedTableDoesNotAdmitComplexRawSql()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA hierarchy;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            SimpleFilterTableDefinition(includePredicateColumn: true),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.Operations.Add(SimpleFilterBuilder(context, "parent IS NULL OR true").Operations[0]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("simple-filter-projected-opaque"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[1].Action);
        Assert.Equal("opaque_sql_expression", report.Assessments[1].AnalysisCode);
    }

    /// <summary>Creates an exact source-frozen table with an optional nullable predicate column.</summary>
    private static ExpectedTableDefinition SimpleFilterTableDefinition(
        bool includePredicateColumn
    ) => new(
        "simple_filters",
        includePredicateColumn
            ? [
                new ExpectedColumnDefinition("id", typeof(int), false, "integer"),
                new ExpectedColumnDefinition("parent", typeof(int), true, "integer"),
            ]
            : [new ExpectedColumnDefinition("id", typeof(int), false, "integer")],
        schema: "hierarchy");
}
