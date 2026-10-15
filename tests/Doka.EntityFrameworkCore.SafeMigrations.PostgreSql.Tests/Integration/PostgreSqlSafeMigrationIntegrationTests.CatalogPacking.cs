namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the CatalogPacking PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlCatalogPackingIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlCatalogPackingIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Prerequisite failures between live operations do not fragment bounded classification.</summary>
    [Fact]
    public async Task Analyzer_PacksAcrossShortCircuitGapsAndCaptureBoundariesWithoutMixingResults()
    {
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE catalog_packing (id integer NOT NULL);");
        await using var context = CreateContext(connectionString);
        await using var connection = new CatalogClassificationCountingConnection(
            new NpgsqlConnection(connectionString));

        context.Database.SetDbConnection(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        for (var ordinal = 0; ordinal < 600; ordinal++)
        {
            if (ordinal % 2 == 0)
            {
                builder.EnsureTable(
                    new ExpectedTableDefinition(
                        "catalog_packing",
                        [new ExpectedColumnDefinition("id", typeof(int), false, "integer")]),
                    SafeMigrationTableMode.StrictDefinition,
                    SafeMigrationPolicy.ThrowIfDifferent);
            }
            else
            {
                builder.EnsureColumn(
                    $"missing_catalog_parent_{ordinal}",
                    new ExpectedColumnDefinition("id", typeof(int), true, "integer"),
                    SafeMigrationPolicy.ThrowIfDifferent);
            }
        }

        var operations = builder.Operations.Cast<SafeMigrationOperation>().ToArray();

        var results = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context,
            operations,
            CancellationToken.None);

        Assert.False(connection.CanCreateBatch);
        Assert.Equal(10, connection.ClassificationStatementCount);
        Assert.Equal(operations.Length, results.Count);
        for (var ordinal = 0; ordinal < results.Count; ordinal++)
        {
            Assert.Equal(
                ordinal % 2 == 0
                    ? SafeMigrationObservedState.Matching
                    : SafeMigrationObservedState.PrerequisiteMissing,
                results[ordinal].ObservedState);
            Assert.Equal(ordinal % 2 == 0, results[ordinal].PostconditionSatisfied);
        }
    }

    /// <summary>The actual provider reader rejects unowned or missing classifier results.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Analyzer_RejectsMalformedSubmittedResults(
        bool missingRows
    )
    {
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE catalog_ownership (id integer NOT NULL);");
        await using var context = CreateContext(connectionString);
        await using var connection = new CatalogClassificationCountingConnection(
            new NpgsqlConnection(connectionString),
            missingRows
                ? CatalogClassificationResultFault.MissingRows
                : CatalogClassificationResultFault.UnsubmittedOrdinal);

        context.Database.SetDbConnection(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "catalog_ownership",
                [new ExpectedColumnDefinition("id", typeof(int), false, "integer")]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        var operations = builder.Operations.Cast<SafeMigrationOperation>().ToArray();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, operations));

        Assert.True(connection.FaultWasInjected);
        Assert.Equal(1, connection.ClassificationStatementCount);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Contains("catalog classifier", exception.Message, StringComparison.Ordinal);
    }

}
