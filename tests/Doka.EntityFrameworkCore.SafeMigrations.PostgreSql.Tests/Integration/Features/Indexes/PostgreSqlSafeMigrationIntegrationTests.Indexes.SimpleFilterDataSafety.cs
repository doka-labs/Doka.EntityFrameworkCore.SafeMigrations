namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>A partial unique index does not reject duplicate keys outside its canonical predicate.</summary>
    /// <param name="negated">Whether the canonical predicate selects non-null instead of null rows.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SimpleFilterUniqueExecutionIgnoresDuplicatesOutsidePredicate(
        bool negated
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var rows = negated ? "(1, NULL), (1, NULL), (2, 1)" : "(1, 1), (1, 2), (2, NULL)";
        await ExecuteSqlAsync(
            connectionString,
            "CREATE SCHEMA hierarchy; CREATE TABLE hierarchy.simple_filters "
            + "(id integer NOT NULL, parent integer NULL);"
            + $"INSERT INTO hierarchy.simple_filters VALUES {rows};");
        await using var context = CreateContext(connectionString);
        var builder = SimpleFilterBuilder(context, negated ? "parent IS NOT NULL" : "parent IS NULL", unique: true);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(1, await SimpleFilterIndexCountAsync(connectionString));
        Assert.Equal(3, await ScalarIntAsync(connectionString, "SELECT count(*) FROM hierarchy.simple_filters;"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT count(*) FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class c ON c.oid = i.indexrelid "
                + "WHERE c.relname = 'simple_filter_lookup' AND i.indisunique AND i.indpred IS NOT NULL;"));
    }

    /// <summary>Duplicate keys inside the canonical predicate block preflight without creating an index.</summary>
    /// <param name="negated">Whether the canonical predicate selects non-null instead of null rows.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SimpleFilterUniqueAnalysisRejectsDuplicatesInsidePredicate(
        bool negated
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var rows = negated ? "(1, 1), (1, 2), (2, NULL)" : "(1, NULL), (1, NULL), (2, 1)";
        await ExecuteSqlAsync(
            connectionString,
            "CREATE SCHEMA hierarchy; CREATE TABLE hierarchy.simple_filters "
            + "(id integer NOT NULL, parent integer NULL);"
            + $"INSERT INTO hierarchy.simple_filters VALUES {rows};");
        await using var context = CreateContext(connectionString);
        var builder = SimpleFilterBuilder(context, negated ? "parent IS NOT NULL" : "parent IS NULL", unique: true);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("simple-filter-unique-duplicates"));

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, assessment.Action);
        Assert.Equal(0, await SimpleFilterIndexCountAsync(connectionString));
    }

    /// <summary>Runtime data checks apply the same canonical predicate before attempting unique index DDL.</summary>
    /// <param name="negated">Whether the canonical predicate selects non-null instead of null rows.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SimpleFilterUniqueExecutionRejectsDuplicatesInsidePredicate(
        bool negated
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var rows = negated ? "(1, 1), (1, 2), (2, NULL)" : "(1, NULL), (1, NULL), (2, 1)";
        await ExecuteSqlAsync(
            connectionString,
            "CREATE SCHEMA hierarchy; CREATE TABLE hierarchy.simple_filters "
            + "(id integer NOT NULL, parent integer NULL);"
            + $"INSERT INTO hierarchy.simple_filters VALUES {rows};");
        await using var context = CreateContext(connectionString);
        var builder = SimpleFilterBuilder(context, negated ? "parent IS NOT NULL" : "parent IS NULL", unique: true);

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        var postgres = Assert.IsType<PostgresException>(exception);
        Assert.Equal("P1003", postgres.SqlState);
        Assert.Equal("doka_sm_data_blocked", postgres.MessageText);
        Assert.Equal(0, await SimpleFilterIndexCountAsync(connectionString));
        Assert.Equal(3, await ScalarIntAsync(connectionString, "SELECT count(*) FROM hierarchy.simple_filters;"));
    }

    /// <summary>A same-shape canonical filter alias binds to the existing independently owned index.</summary>
    /// <param name="filter">The exact predicate of the independently named physical index.</param>
    [Theory]
    [InlineData("parent IS NULL")]
    [InlineData("parent IS NOT NULL")]
    public async Task SimpleFilterSemanticAliasAnalysisIsNoOp(
        string filter
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await CreateSimpleFilterTableAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var existing = SimpleFilterBuilder(context, filter, name: "simple_filter_legacy");
        await ExecuteOperationsAsync(context, existing.Operations);
        var expected = SimpleFilterBuilder(context, filter);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            expected.Operations,
            new SafeMigrationRunOptions("simple-filter-semantic-alias"));

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
        Assert.Equal(0, await SimpleFilterIndexCountAsync(connectionString));
    }

    /// <summary>An exact semantic alias remains a runtime no-op rather than creating a second physical index.</summary>
    [Fact]
    public async Task SimpleFilterSemanticAliasExecutionDoesNotCreateAnotherIndex()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await CreateSimpleFilterTableAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var existing = SimpleFilterBuilder(context, "parent IS NOT NULL", name: "simple_filter_legacy");
        await ExecuteOperationsAsync(context, existing.Operations);
        var expected = SimpleFilterBuilder(context, "parent IS NOT NULL");

        // Act
        await ExecuteOperationsAsync(context, expected.Operations);

        // Assert
        Assert.Equal(0, await SimpleFilterIndexCountAsync(connectionString));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT count(*) FROM pg_catalog.pg_indexes WHERE schemaname = 'hierarchy' "
                + "AND indexname IN ('simple_filter_lookup', 'simple_filter_legacy');"));
    }

    /// <summary>An index with a different canonical predicate is not an alias of the requested contract.</summary>
    [Fact]
    public async Task SimpleFilterDifferentPredicateAliasRemainsApplicable()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await CreateSimpleFilterTableAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var existing = SimpleFilterBuilder(context, "parent IS NULL", name: "simple_filter_legacy");
        await ExecuteOperationsAsync(context, existing.Operations);
        var expected = SimpleFilterBuilder(context, "parent IS NOT NULL");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            expected.Operations,
            new SafeMigrationRunOptions("simple-filter-nonidentity"));

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, assessment.Action);
    }
}
