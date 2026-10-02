namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies reusable delayed SQL without sharing mutable evidence or result ownership.</summary>
public sealed class SqlServerStableDelayedClassifierTests
{
    /// <summary>The complete guarded classifier remains independent of its original result ordinal.</summary>
    [Fact]
    public void CompleteClassifier_UsesPrivateStableScopeWithoutResultAggregation()
    {
        // Arrange
        var plan = new SqlServerSafeMigrationRuntimePlan("N'missing'", "0", SafeMigrationRepairCapability.None, "0")
        {
            RequiresDelayedBinding = true,
            PhysicalTableSupportExpression = "@physical = 1",
            ColumnLayoutFailureExpression = "N'layout_failure'",
            ColumnCollationSupportExpression = "@collation = 1",
            DefaultValueSupportExpression = "@default = 1",
            IndexFilterSupportExpression = "@filter = 1",
            PrerequisiteExpression = "@prerequisite = 1",
            StateEvaluationGuardExpression = "@guard = 1",
            StateEvaluationGuardFailureExpression = "N'different'",
            CatalogPreambleSql = "DECLARE @proof int = 1;",
        };

        // Act
        var first = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogSelection(7, plan);
        var second = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogSelection(8192, plan);

        // Assert
        const string boundary = "', N'@doka_ordinal int'";
        Assert.Equal(first[..first.LastIndexOf(boundary, StringComparison.Ordinal)],
            second[..second.LastIndexOf(boundary, StringComparison.Ordinal)]);
        Assert.StartsWith("EXEC sys.sp_executesql N'", first, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO", first, StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_analysis", first, StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_layout_7", first, StringComparison.Ordinal);
        Assert.Contains("SELECT @doka_ordinal", first, StringComparison.Ordinal);
        Assert.Contains("DECLARE @doka_layout nvarchar(128)", first, StringComparison.Ordinal);
        Assert.True(first.IndexOf("@physical", StringComparison.Ordinal)
            < first.IndexOf("@doka_layout", StringComparison.Ordinal));
        Assert.True(first.IndexOf("@collation", StringComparison.Ordinal)
            < first.IndexOf("@default", StringComparison.Ordinal));
        Assert.True(first.IndexOf("@filter", StringComparison.Ordinal)
            < first.IndexOf("@prerequisite", StringComparison.Ordinal));
        Assert.True(first.IndexOf("@guard", StringComparison.Ordinal)
            < first.IndexOf("DECLARE @proof", StringComparison.Ordinal));
    }

    /// <summary>Each delayed operation owns one result set even when the total returned ordinals look valid.</summary>
    /// <param name="nativeBatch">Whether transport uses native ADO.NET batching.</param>
    /// <param name="corruption">The result ownership violation.</param>
    [Theory]
    [InlineData(true, "empty_then_two")]
    [InlineData(false, "empty_then_two")]
    [InlineData(true, "merged")]
    [InlineData(false, "merged")]
    [InlineData(true, "extra_set")]
    [InlineData(false, "extra_set")]
    [InlineData(true, "missing_set")]
    [InlineData(false, "missing_set")]
    [InlineData(true, "duplicate")]
    [InlineData(false, "duplicate")]
    [InlineData(true, "reversed")]
    [InlineData(false, "reversed")]
    public async Task DelayedResultOwnership_RejectsMalformedResultSets(bool nativeBatch, string corruption)
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch)
        {
            TransformResultSets = sets => corruption switch
            {
                "empty_then_two" => [[], [sets[0][0], sets[1][0]]],
                "merged" => [[sets[0][0], sets[1][0]]],
                "extra_set" => [.. sets, []],
                "missing_set" => [sets[0]],
                "duplicate" => [sets[0], sets[0]],
                "reversed" => [sets[1], sets[0]],
                _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
            },
        };

        var first = new SqlServerSafeMigrationRuntimePlan("N'missing'", "0", SafeMigrationRepairCapability.None, "0")
        {
            RequiresDelayedBinding = true,
        };

        var second = first with { StateExpression = "N'matching'" };
        var results = new SafeMigrationProviderAnalysis[2];

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
                connection, null, null, [first, second], 0, results, CancellationToken.None));

        // Assert
        Assert.StartsWith("The SQL Server catalog batch returned", failure.Message, StringComparison.Ordinal);
        Assert.Equal(nativeBatch ? 1 : 0, connection.BatchesDisposed);
        Assert.Equal(nativeBatch ? 0 : 1, connection.CommandsDisposed);
        if (corruption == "empty_then_two")
        {
            Assert.All(results, Assert.Null);
        }
    }

    /// <summary>Original ordinals travel as counted int bindings, not heavyweight SQL template literals.</summary>
    /// <param name="nativeBatch">Whether transport uses native ADO.NET batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DelayedOrdinals_UseStableStatementSlotsAndRetainOriginalResultOwnership(bool nativeBatch)
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch);
        var first = new SqlServerSafeMigrationRuntimePlan("N'missing'", "0", SafeMigrationRepairCapability.None, "0")
        {
            RequiresDelayedBinding = true,
        };

        var second = first with { StateExpression = "N'matching'" };
        var results = new SafeMigrationProviderAnalysis[519];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, 71, [first, second], 517, results, CancellationToken.None);

        // Assert
        var statement = Assert.Single(connection.RecordedStatements);
        Assert.DoesNotContain("517", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("518", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_analysis", statement, StringComparison.Ordinal);
        Assert.Contains("@doka_ordinal = @doka_ordinal0", statement, StringComparison.Ordinal);
        Assert.Contains("@doka_ordinal = @doka_ordinal1", statement, StringComparison.Ordinal);
        var parameters = Assert.Single(connection.RecordedParameters);
        Assert.Equal([517, 518], parameters.Select(static parameter => Assert.IsType<int>(parameter.Value)));
        Assert.All(parameters, static parameter => Assert.Equal(SqlDbType.Int, parameter.SqlDbType));
        Assert.Equal(SafeMigrationObservedState.Matching, results[517].ObservedState);
        Assert.Equal(SafeMigrationObservedState.Different, results[518].ObservedState);
        Assert.Equal(2, connection.RowsRead);
        Assert.Equal(nativeBatch ? 1 : 0, connection.ParameterFactoriesDisposed);
    }

    /// <summary>The classifier's metadata shape is verified before any returned row becomes evidence.</summary>
    /// <param name="nativeBatch">Whether transport uses native ADO.NET batching.</param>
    /// <param name="extraField">Whether metadata has an extra field instead of a missing field.</param>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task DelayedMetadataShape_RejectsBeforeAcceptingRows(bool nativeBatch, bool extraField)
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch)
        {
            TransformClassifierTable = table =>
            {
                if (extraField)
                {
                    table.Columns.Add("unexpected", typeof(int));
                }
                else
                {
                    table.Columns.RemoveAt(table.Columns.Count - 1);
                }
            },
        };

        var plan = new SqlServerSafeMigrationRuntimePlan("N'missing'", "0", SafeMigrationRepairCapability.None, "0")
        {
            RequiresDelayedBinding = true,
        };

        var results = new SafeMigrationProviderAnalysis[1];

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
                connection, null, null, [plan], 0, results, CancellationToken.None));

        // Assert
        Assert.Equal("The SQL Server catalog batch returned an invalid result set.", failure.Message);
        Assert.Equal(0, connection.RowsRead);
        Assert.Null(results[0]);
        Assert.Equal(nativeBatch ? 1 : 0, connection.BatchesDisposed);
        Assert.Equal(nativeBatch ? 0 : 1, connection.CommandsDisposed);
    }

    /// <summary>Cancellation between isolated classifier sets never consumes a later operation's evidence.</summary>
    /// <param name="nativeBatch">Whether transport uses native ADO.NET batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DelayedResultTransition_CancellationPreservesOriginalOwnership(bool nativeBatch)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch)
        {
            AfterNextResult = () => cancellation.Cancel(),
        };

        var first = new SqlServerSafeMigrationRuntimePlan("N'missing'", "0", SafeMigrationRepairCapability.None, "0")
        {
            RequiresDelayedBinding = true,
        };

        var second = first with { StateExpression = "N'matching'" };
        var results = new SafeMigrationProviderAnalysis[2];

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
                connection, null, null, [first, second], 0, results, cancellation.Token));

        // Assert
        Assert.Equal(1, connection.RowsRead);
        Assert.NotNull(results[0]);
        Assert.Null(results[1]);
        Assert.Equal(1, connection.BatchExecutions + connection.CommandExecutions);
        Assert.Equal(cancellation.Token, connection.CancellationTokenSeen);
        Assert.Equal(nativeBatch ? 1 : 0, connection.BatchesDisposed);
        Assert.Equal(nativeBatch ? 0 : 1, connection.CommandsDisposed);
    }
}
