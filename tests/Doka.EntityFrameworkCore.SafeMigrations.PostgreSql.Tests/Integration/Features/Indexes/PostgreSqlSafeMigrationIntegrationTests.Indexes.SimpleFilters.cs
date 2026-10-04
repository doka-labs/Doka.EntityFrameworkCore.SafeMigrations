namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>A recognized filter column must exist before analysis accepts a standalone index.</summary>
    /// <param name="filter">The canonical predicate referencing an absent physical column.</param>
    [Theory]
    [InlineData("missing IS NULL")]
    [InlineData("missing IS NOT NULL")]
    [InlineData("\"Missing\" IS NULL")]
    public async Task SimpleFilterMissingColumnAnalysisRejectsBeforeDdl(
        string filter
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE SCHEMA hierarchy; CREATE TABLE hierarchy.simple_filters (id integer NOT NULL);"
            + "INSERT INTO hierarchy.simple_filters VALUES (1);");
        await using var context = CreateContext(connectionString);
        var builder = SimpleFilterBuilder(context, filter);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("simple-filter-missing-column"),
            CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, assessment.Action);
        Assert.Equal(0, await SimpleFilterIndexCountAsync(connectionString));
        Assert.Equal(1, await ScalarIntAsync(connectionString, "SELECT count(*) FROM hierarchy.simple_filters;"));
    }

    /// <summary>A missing canonical filter column rejects execution through the provider guard.</summary>
    /// <param name="filter">The canonical predicate referencing an absent physical column.</param>
    [Theory]
    [InlineData("missing IS NULL")]
    [InlineData("missing IS NOT NULL")]
    [InlineData("\"Missing\" IS NULL")]
    public async Task SimpleFilterMissingColumnExecutionRejectsBeforeDdl(
        string filter
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE SCHEMA hierarchy; CREATE TABLE hierarchy.simple_filters (id integer NOT NULL);"
            + "INSERT INTO hierarchy.simple_filters VALUES (1);");
        await using var context = CreateContext(connectionString);
        var builder = SimpleFilterBuilder(context, filter);

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        var postgres = Assert.IsType<PostgresException>(exception);
        Assert.Equal("P1004", postgres.SqlState);
        Assert.Equal("doka_sm_prerequisite_missing", postgres.MessageText);
        Assert.Equal(0, await SimpleFilterIndexCountAsync(connectionString));
        Assert.Equal(1, await ScalarIntAsync(connectionString, "SELECT count(*) FROM hierarchy.simple_filters;"));
    }

    /// <summary>Canonical null predicates execute without rewriting authored raw SQL or existing rows.</summary>
    /// <param name="filter">The canonical predicate.</param>
    /// <param name="column">The exact physical filter-column name.</param>
    [Theory]
    [InlineData("parent IS NULL", "parent")]
    [InlineData("parent IS NOT NULL", "parent")]
    [InlineData("\"Parent\" IS NULL", "Parent")]
    [InlineData("\"Parent\" IS NOT NULL", "Parent")]
    [InlineData("\"parent\"\"entry\" IS NULL", "parent\"entry")]
    [InlineData("\"parent entry\" IS NOT NULL", "parent entry")]
    public async Task SimpleFilterExecutionCreatesCanonicalIndex(
        string filter,
        string column
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await CreateSimpleFilterTableAsync(connectionString, column);
        await using var context = CreateContext(connectionString);
        var builder = SimpleFilterBuilder(context, filter);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(1, await SimpleFilterIndexCountAsync(connectionString));
        Assert.Equal(3, await ScalarIntAsync(connectionString, "SELECT count(*) FROM hierarchy.simple_filters;"));
        var definition = Assert.IsType<EnsureIndexIntent>(
            Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations)).Intent).Definition;

        Assert.Equal(filter, definition.Filter);
        Assert.Null(definition.StructuredFilter);
    }

    /// <summary>A second execution keeps one physical filtered index and preserves all seed rows.</summary>
    /// <param name="filter">The canonical null-test predicate.</param>
    [Theory]
    [InlineData("parent IS NULL")]
    [InlineData("parent IS NOT NULL")]
    public async Task SimpleFilterExecutionRerunIsIdempotent(
        string filter
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await CreateSimpleFilterTableAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var builder = SimpleFilterBuilder(context, filter);
        await ExecuteOperationsAsync(context, builder.Operations);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(1, await SimpleFilterIndexCountAsync(connectionString));
        Assert.Equal(3, await ScalarIntAsync(connectionString, "SELECT count(*) FROM hierarchy.simple_filters;"));
    }

    /// <summary>Catalog deparsing of a canonical predicate is recognized as the same physical index.</summary>
    /// <param name="filter">The canonical predicate.</param>
    /// <param name="column">The exact filter-column name, including provider-required quoting.</param>
    [Theory]
    [InlineData("parent IS NULL", "parent")]
    [InlineData("parent IS NOT NULL", "parent")]
    [InlineData("\"Parent\" IS NULL", "Parent")]
    [InlineData("\"parent\"\"entry\" IS NOT NULL", "parent\"entry")]
    public async Task SimpleFilterMatchingAnalysisIsNoOp(
        string filter,
        string column
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await CreateSimpleFilterTableAsync(connectionString, column);
        await using var context = CreateContext(connectionString);
        var builder = SimpleFilterBuilder(context, filter);
        await ExecuteOperationsAsync(context, builder.Operations);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("simple-filter-matching"));

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
        Assert.Equal(1, await SimpleFilterIndexCountAsync(connectionString));
    }

    /// <summary>Postflight verifies the preserved raw predicate against the physical catalog expression.</summary>
    /// <param name="filter">The canonical null-test predicate.</param>
    [Theory]
    [InlineData("parent IS NULL")]
    [InlineData("parent IS NOT NULL")]
    public async Task SimpleFilterPostflightVerifiesCatalogPredicate(
        string filter
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await CreateSimpleFilterTableAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var builder = SimpleFilterBuilder(context, filter);
        await ExecuteOperationsAsync(context, builder.Operations);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("simple-filter-postflight"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.True(Assert.Single(report.Assessments).PostconditionSatisfied);
    }

    /// <summary>Changing the filter while retaining an occupied name is a contract conflict.</summary>
    [Fact]
    public async Task SimpleFilterDriftAnalysisRejectsChangedPredicate()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await CreateSimpleFilterTableAsync(connectionString);
        await using var context = CreateContext(connectionString);
        await ExecuteOperationsAsync(context, SimpleFilterBuilder(context, "parent IS NULL").Operations);
        var drift = SimpleFilterBuilder(context, "parent IS NOT NULL");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            drift.Operations,
            new SafeMigrationRunOptions("simple-filter-drift"));

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
    }

    /// <summary>Runtime drift rejection preserves the original predicate and does not replace the index.</summary>
    [Fact]
    public async Task SimpleFilterDriftExecutionRejectsChangedPredicate()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await CreateSimpleFilterTableAsync(connectionString);
        await using var context = CreateContext(connectionString);
        await ExecuteOperationsAsync(context, SimpleFilterBuilder(context, "parent IS NULL").Operations);
        var drift = SimpleFilterBuilder(context, "parent IS NOT NULL");

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, drift.Operations));

        // Assert
        var postgres = Assert.IsType<PostgresException>(exception);
        Assert.Equal("P1001", postgres.SqlState);
        Assert.Equal("doka_sm_different", postgres.MessageText);
        Assert.Equal(1, await SimpleFilterIndexCountAsync(connectionString));
        Assert.Equal(
            "(parent IS NULL)",
            await ScalarStringAsync(
                connectionString,
                "SELECT pg_catalog.pg_get_expr(i.indpred, i.indrelid) FROM pg_catalog.pg_index i "
                + "JOIN pg_catalog.pg_class c ON c.oid = i.indexrelid "
                + "WHERE c.relname = 'simple_filter_lookup';"));
    }

    /// <summary>Builds a source-frozen standalone filtered-index operation through the public authoring path.</summary>
    private static MigrationBuilder SimpleFilterBuilder(
        DbContext context,
        string filter,
        bool unique = false,
        string name = "simple_filter_lookup"
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateCompositeIndexIfNotExistsFromModel(
            name,
            "simple_filters",
            ["id"],
            schema: "hierarchy",
            unique: unique,
            filter: filter);

        return builder;
    }

    /// <summary>Creates only test-owned schema and mixed null/non-null rows for the predicate boundary.</summary>
    private static Task CreateSimpleFilterTableAsync(
        string connectionString,
        string column = "parent"
    ) => ExecuteSqlAsync(
        connectionString,
        "CREATE SCHEMA hierarchy; CREATE TABLE hierarchy.simple_filters "
        + $"(id integer NOT NULL, {PostgreSqlIdentifier(column)} integer NULL);"
        + "INSERT INTO hierarchy.simple_filters VALUES (1, NULL), (2, 1), (3, 2);");

    /// <summary>Counts the independently managed filtered index in the exact test-owned schema.</summary>
    private static Task<int> SimpleFilterIndexCountAsync(
        string connectionString
    ) => ScalarIntAsync(
        connectionString,
        "SELECT count(*) FROM pg_catalog.pg_indexes "
        + "WHERE schemaname = 'hierarchy' AND indexname = 'simple_filter_lookup';");
}
