namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Prerequisite failures between live operations do not fragment bounded classification.</summary>
    [Fact]
    public async Task Analyzer_PacksAcrossShortCircuitGapsAndCaptureBoundariesWithoutMixingResults()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `catalog_packing` (`id` int NOT NULL);");
        await using var context = CreateContext(connectionString);
        await using var connection = new CatalogClassificationCountingConnection(new MySqlConnection(connectionString));
        context.Database.SetDbConnection(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        for (var ordinal = 0; ordinal < 600; ordinal++)
        {
            if (ordinal % 2 == 0)
            {
                builder.EnsureTable(
                    new ExpectedTableDefinition(
                        "catalog_packing",
                        [new ExpectedColumnDefinition("id", typeof(int), false, "int")]),
                    SafeMigrationTableMode.StrictDefinition,
                    SafeMigrationPolicy.ThrowIfDifferent);
            }
            else
            {
                builder.EnsureColumn(
                    $"missing_catalog_parent_{ordinal}",
                    new ExpectedColumnDefinition("id", typeof(int), true, "int"),
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
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `catalog_ownership` (`id` int NOT NULL);");
        await using var context = CreateContext(connectionString);
        await using var connection = new CatalogClassificationCountingConnection(
            new MySqlConnection(connectionString),
            missingRows
                ? CatalogClassificationResultFault.MissingRows
                : CatalogClassificationResultFault.UnsubmittedOrdinal);

        context.Database.SetDbConnection(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "catalog_ownership",
                [new ExpectedColumnDefinition("id", typeof(int), false, "int")]),
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
