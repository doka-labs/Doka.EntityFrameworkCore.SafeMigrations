namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Exact builder constants avoid queries without changing sparse prerequisite classifications.</summary>
    /// <param name="nonconstantCount">The number of operations requiring a real prerequisite query.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public async Task Analyzer_PrunesConstantPrerequisitesButKeepsSparseCatalogProofs(int nonconstantCount)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await using var connection = new CatalogClassificationCountingConnection(new MySqlConnection(connectionString));
        context.Database.SetDbConnection(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        for (var index = 0; index < 65; index++)
        {
            builder.EnsureTable(
                new ExpectedTableDefinition(
                    $"constant_probe_{index}", [new ExpectedColumnDefinition("id", typeof(int), false, "int")]),
                SafeMigrationTableMode.StrictDefinition,
                SafeMigrationPolicy.ThrowIfDifferent);
            if (index < nonconstantCount)
            {
                builder.EnsureColumn(
                    $"missing_probe_{index}",
                    new ExpectedColumnDefinition("value", typeof(int), true, "int"),
                    SafeMigrationPolicy.ThrowIfDifferent);
            }
        }

        var operations = builder.Operations.Cast<SafeMigrationOperation>().ToArray();

        // Act
        var results = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, operations);

        // Assert
        Assert.Equal((nonconstantCount + 31) / 32, connection.PrerequisiteStatementCount);
        Assert.Equal(65 + nonconstantCount, results.Count);
        for (var ordinal = 0; ordinal < results.Count; ordinal++)
        {
            Assert.Equal(nonconstantCount > 0 && ordinal % 2 == 1
                ? SafeMigrationObservedState.PrerequisiteMissing
                : SafeMigrationObservedState.Missing, results[ordinal].ObservedState);
        }
    }

    /// <summary>Pruned gaps do not weaken prerequisite-result ownership checks.</summary>
    /// <param name="missingRows">Whether the server response omits rows or returns an unowned ordinal.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Analyzer_RejectsMalformedSparsePrerequisiteResults(bool missingRows)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await using var connection = new CatalogClassificationCountingConnection(
            new MySqlConnection(connectionString),
            missingRows ? CatalogClassificationResultFault.MissingPrerequisiteRows
                : CatalogClassificationResultFault.UnsubmittedPrerequisiteOrdinal);

        context.Database.SetDbConnection(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "pruned_parent", [new ExpectedColumnDefinition("id", typeof(int), false, "int")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureColumn(
            "absent_parent", new ExpectedColumnDefinition("id", typeof(int), true, "int"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var operations = builder.Operations.Cast<SafeMigrationOperation>().ToArray();

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, operations));

        // Assert
        Assert.True(connection.FaultWasInjected);
        Assert.Equal(1, connection.PrerequisiteStatementCount);
        Assert.Equal(0, connection.ClassificationStatementCount);
        Assert.Contains("prerequisite classifier", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Qualified cross-table probes share native transport and preserve complete fallback reports.</summary>
    [Fact]
    public async Task Analyzer_BatchesQualifiedNarrowingProbesAndPreservesReportParity()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var setup = new StringBuilder();
        for (var index = 0; index < 65; index++)
        {
            setup.Append($"CREATE TABLE `bounded_probe_{index}` (`value` varchar(20) NULL) "
                + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; ");
            var value = index % 3 == 0 ? "too-long-value" : "ok";
            setup.Append(CultureInfo.InvariantCulture, $"INSERT INTO `bounded_probe_{index}` VALUES ('{value}'); ");
        }

        await ExecuteSqlAsync(connectionString, setup.ToString());
        await using var fallbackContext = CreateContext(connectionString);
        await using var fallback = new CatalogClassificationCountingConnection(new MySqlConnection(connectionString));
        fallbackContext.Database.SetDbConnection(fallback);
        await using var nativeContext = CreateContext(connectionString);
        await using var native = new CatalogClassificationCountingConnection(
            new MySqlConnection(connectionString), nativeBatch: true);

        nativeContext.Database.SetDbConnection(native);
        var builder = new MigrationBuilder(fallbackContext.Database.ProviderName!);
        for (var index = 0; index < 65; index++)
        {
            builder.EnsureTable(
                new ExpectedTableDefinition(
                    $"missing_constant_{index}", [new ExpectedColumnDefinition("id", typeof(int), false, "int")]),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
            builder.EnsureColumn(
                $"bounded_probe_{index}",
                new ExpectedColumnDefinition("value", typeof(string), true, "varchar(5)", maxLength: 5),
                SafeMigrationPolicy.RepairIfSafe);
        }

        var operations = builder.Operations.Cast<SafeMigrationOperation>().ToArray();

        // Act
        var fallbackResults = await fallbackContext.GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(fallbackContext, operations);
        var nativeResults = await nativeContext.GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(nativeContext, operations);

        // Assert
        Assert.Equal(3, fallback.NarrowingEligibilityStatementCount);
        Assert.Equal(65, fallback.NarrowingDataStatementCount);
        Assert.Equal(3, fallback.ColumnDiagnosticStatementCount);
        Assert.Equal(65, native.NativeNarrowingStatementCount);
        Assert.Equal(9, native.NativeNarrowingBatchExecutionCount);
        Assert.Equal(8, native.LargestNativeBatchStatementCount);
        Assert.Equal(fallbackResults.Count, nativeResults.Count);
        for (var ordinal = 0; ordinal < operations.Length; ordinal++)
        {
            var expected = fallbackResults[ordinal];
            var actual = nativeResults[ordinal];
            Assert.Equal(expected.ObservedState, actual.ObservedState);
            Assert.Equal(expected.RepairCapability, actual.RepairCapability);
            Assert.Equal(expected.PostconditionSatisfied, actual.PostconditionSatisfied);
            Assert.Equal(expected.Code, actual.Code);
            Assert.Equal(expected.OperationalImpact, actual.OperationalImpact);
            Assert.Equal(expected.RequiresLiveDataProof, actual.RequiresLiveDataProof);
            Assert.Equal(expected.IsOpaqueProjectionUnknown, actual.IsOpaqueProjectionUnknown);
            Assert.Equal(expected.IsInvariantUnsupported, actual.IsInvariantUnsupported);
            Assert.Equal(expected.MatchedObjectName, actual.MatchedObjectName);
            Assert.Null(expected.IndexPhysicalEnvironment);
            Assert.Null(actual.IndexPhysicalEnvironment);
            Assert.Null(expected.ModelManagedDataEvidence);
            Assert.Null(actual.ModelManagedDataEvidence);
            Assert.Equal(
                expected.Differences.Select(difference => (difference.Facet, difference.Expected, difference.Actual)),
                actual.Differences.Select(difference => (difference.Facet, difference.Expected, difference.Actual)));
        }

        for (var index = 0; index < 65; index++)
        {
            var result = nativeResults[(index * 2) + 1];
            Assert.Equal(index % 3 == 0 ? SafeMigrationObservedState.DataBlocked : SafeMigrationObservedState.Different,
                result.ObservedState);
            Assert.True(result.RequiresLiveDataProof);
            Assert.Contains(result.Differences, difference => difference.Facet == "column_store_type");
        }
    }
}
