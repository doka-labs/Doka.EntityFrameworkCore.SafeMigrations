namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies bounded catalog packing independently of SQL Server availability and network latency.</summary>
public sealed class SqlServerCatalogBatchingTests
{
    /// <summary>Both transports retain their actual execution budget across statement and batch boundaries.</summary>
    /// <param name="nativeBatch">Whether the connection supports native batching.</param>
    /// <param name="delayed">Whether each classifier requires a private binding scope.</param>
    /// <param name="count">The number of distinct classifiers in the capture.</param>
    [Theory]
    [InlineData(true, false, 256)]
    [InlineData(false, false, 256)]
    [InlineData(true, true, 256)]
    [InlineData(false, true, 256)]
    [InlineData(true, false, 257)]
    [InlineData(false, false, 257)]
    [InlineData(true, true, 257)]
    [InlineData(false, true, 257)]
    public async Task DistinctClassifiers_PreserveNativeAndSequentialExecutionBudgets(
        bool nativeBatch,
        bool delayed,
        int count
    )
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch);
        var plans = Enumerable.Range(0, count).Select(index => DistinctPlan(Plan(delayed), index)).ToArray();
        var results = new SafeMigrationProviderAnalysis[count];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, 71, plans, 0, results, CancellationToken.None);

        // Assert
        var statements = (count + (nativeBatch ? 8 : 32) - 1) / (nativeBatch ? 8 : 32);

        Assert.Equal(statements, connection.RecordedStatements.Count);
        Assert.Equal(nativeBatch ? (count + 255) / 256 : 0, connection.BatchExecutions);
        Assert.Equal(nativeBatch ? 0 : statements, connection.CommandExecutions);
        Assert.Equal(Enumerable.Range(0, count).Select(State), results.Select(analysis => analysis.ObservedState));
        Assert.All(connection.ObservedTimeouts, timeout => Assert.Equal(71, timeout));
    }

    /// <summary>Unordered rows remain owned by their statement across a complete multi-statement capture.</summary>
    /// <param name="nativeBatch">Whether the connection supports native batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MultipleUnorderedStatements_RetainEveryOriginalResult(bool nativeBatch)
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch)
        {
            TransformOrdinals = ordinals => ordinals.Reverse().ToArray(),
        };

        const int count = 257;
        var plans = Enumerable.Range(0, count).Select(index => DistinctPlan(Plan(false), index)).ToArray();
        var results = new SafeMigrationProviderAnalysis[count];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, plans, 0, results, CancellationToken.None);

        // Assert
        Assert.Equal(Enumerable.Range(0, count).Select(State), results.Select(analysis => analysis.ObservedState));
        Assert.Equal(nativeBatch ? 2 : 0, connection.BatchExecutions);
        Assert.Equal(nativeBatch ? 0 : 9, connection.CommandExecutions);
    }

    /// <summary>
    /// Groups alternating classifiers while retaining every original result slot and skipped classification.
    /// </summary>
    /// <param name="nativeBatch">Whether the connection exposes native ADO.NET batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedCapture_PacksBothModesAndRestoresOriginalOrdinals(bool nativeBatch)
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch);
        var plans = Enumerable.Range(0, 512)
            .Select(index => index % 9 == 3 ? null : Plan(index % 9 is not (1 or 8)) with
            {
                DifferentDifference = new SafeMigrationFacetDifference(
                    "test_contract", "expected-" + index, "actual-" + index),
            })
            .ToArray();

        const int captureStart = 517;
        var results = Enumerable.Repeat(Unsupported(), captureStart + plans.Length).ToArray();

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, 71, plans, captureStart, results, CancellationToken.None);

        // Assert
        Assert.InRange(
            connection.RecordedStatements.Count,
            1,
            plans.Length / SqlServerCatalogQueryLimits.MaximumOperationsPerStatement);

        Assert.Equal(nativeBatch ? 2 : 0, connection.BatchExecutions);
        Assert.Equal(nativeBatch ? 0 : connection.RecordedStatements.Count, connection.CommandExecutions);
        Assert.All(results.Take(captureStart), analysis => Assert.Equal(
            SafeMigrationObservedState.Unsupported, analysis.ObservedState));
        for (var index = 0; index < plans.Length; index++)
        {
            Assert.Equal(plans[index] is null ? SafeMigrationObservedState.Unsupported
                : State(captureStart + index), results[captureStart + index].ObservedState);
            if (plans[index] is { } plan && State(captureStart + index) == SafeMigrationObservedState.Different)
            {
                Assert.Same(plan.DifferentDifference, Assert.Single(results[captureStart + index].Differences));
            }
        }

        Assert.Equal(nativeBatch ? 2 : 0, connection.BatchesDisposed);
        Assert.Equal(nativeBatch ? 0 : connection.RecordedStatements.Count, connection.CommandsDisposed);
    }

    /// <summary>The complete mixed stress pattern stays bounded instead of fragmenting at each mode change.</summary>
    [Fact]
    public async Task HundredThousandMixedClassifiers_UseBoundedPackedCaptures()
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection();
        var metadata = Plan(false);
        var delayed = Plan(true);
        var results = Enumerable.Repeat(Unsupported(), 100_000).ToArray();

        // Act
        for (var start = 0; start < results.Length; start += 512)
        {
            var plans = Enumerable.Range(start, Math.Min(512, results.Length - start))
                .Select(index => (index % 9) switch
                {
                    3 => null,
                    1 or 8 => DistinctPlan(metadata, index),
                    _ => DistinctPlan(delayed, index),
                })
                .ToArray();

            await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
                connection, null, 180, plans, start, results, CancellationToken.None);
        }

        // Assert
        // WHY: Narrow statements inside wide batches are the point of the shape. Statement
        // count may grow, but the executed batch count is what costs a round trip and must
        // stay bounded by the same capture arithmetic as before.
        Assert.Equal(11_329, connection.RecordedStatements.Count);
        Assert.Equal(391, connection.BatchExecutions);
        Assert.All(
            connection.BatchStatementCounts,
            count => Assert.InRange(count, 1, SqlServerCatalogQueryLimits.MaximumStatementsPerBatch));

        Assert.All(connection.BatchPayloadBytes, bytes => Assert.InRange(bytes, 1, 4 * 1024 * 1024));
        Assert.All(connection.RecordedStatements.Zip(connection.RecordedParameters), recorded => Assert.InRange(
            recorded.First.StartsWith("EXEC sys.sp_executesql", StringComparison.Ordinal)
                ? recorded.Second.Count(parameter => parameter.ParameterName.StartsWith(
                    "@doka_ordinal", StringComparison.Ordinal))
                : recorded.First.Split("\nUNION ALL\n", StringSplitOptions.None).Length,
            1,
            SqlServerCatalogQueryLimits.MaximumOperationsPerStatement));
        for (var ordinal = 0; ordinal < results.Length; ordinal++)
        {
            Assert.Equal(ordinal % 9 == 3 ? SafeMigrationObservedState.Unsupported : State(ordinal),
                results[ordinal].ObservedState);
        }

        Assert.Equal(connection.BatchExecutions, connection.BatchesDisposed);
    }

    /// <summary>Skipped captures do not create a transport or overwrite classifications.</summary>
    /// <param name="planCount">The number of empty or already classified capture slots.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public async Task EmptyOrSkippedCapture_PerformsNoDatabaseWork(int planCount)
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection();
        var results = Enumerable.Repeat(Unsupported(), 32).ToArray();

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, new SqlServerSafeMigrationRuntimePlan?[planCount],
            0, results, CancellationToken.None);

        // Assert
        Assert.Empty(connection.RecordedStatements);
        Assert.Equal(0, connection.BatchExecutions);
        Assert.All(results, analysis => Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState));
    }

    /// <summary>Statement-local preambles force delayed binding even without an explicit delayed flag.</summary>
    [Fact]
    public async Task Preamble_RemainsInsideItsDelayedClassifier()
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection();
        var plan = Plan(false) with { CatalogPreambleSql = "DECLARE @probe int = 1;" };
        var results = new SafeMigrationProviderAnalysis[1];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, [plan], 0, results, CancellationToken.None);

        // Assert
        var statement = Assert.Single(connection.RecordedStatements);
        Assert.StartsWith("EXEC sys.sp_executesql N'", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_analysis", statement, StringComparison.Ordinal);
        Assert.Contains("N''DECLARE @probe int = 1;\nSELECT @doka_ordinal", statement, StringComparison.Ordinal);
        Assert.Equal(SafeMigrationObservedState.Missing, results[0].ObservedState);
    }

    /// <summary>Budget splitting includes delayed quote expansion and UTF-8, not just character counts.</summary>
    /// <param name="delayed">Whether the classifier is delayed and quoted.</param>
    /// <param name="nativeBatch">Whether the provider exposes native batching.</param>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task LargeClassifiers_SplitWithoutExceedingBatchPayload(
        bool delayed,
        bool nativeBatch
    )
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch);
        var plan = Plan(delayed) with
        {
            StateExpression = "N'missing' /*" + new string('\'', delayed ? 550_000 : 1_100_000) + "*/",
        };

        var results = new SafeMigrationProviderAnalysis[4];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, Enumerable.Range(0, 4).Select(index => DistinctPlan(plan, index)).ToArray(),
            0, results, CancellationToken.None);

        // Assert
        Assert.Equal(nativeBatch ? delayed ? 4 : 2 : 0, connection.BatchExecutions);
        Assert.Equal(nativeBatch ? 0 : delayed ? 4 : 2, connection.CommandExecutions);
        Assert.All(connection.BatchPayloadBytes, bytes => Assert.InRange(bytes, 1, 4 * 1024 * 1024));
        Assert.All(connection.RecordedStatements, statement => Assert.InRange(
            Encoding.UTF8.GetByteCount(statement), 1, 4 * 1024 * 1024));
        Assert.Equal(Enumerable.Range(0, 4).Select(State), results.Select(analysis => analysis.ObservedState));
    }

    /// <summary>A single oversized classifier is rejected before any catalog execution.</summary>
    /// <param name="delayed">Whether the classifier is delayed and quoted.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedClassifier_RejectsBeforeExecution(bool delayed)
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection();
        var plan = Plan(delayed) with { StateExpression = "N'missing' /*" + new string('x', 4 * 1024 * 1024) + "*/" };
        var results = new SafeMigrationProviderAnalysis[1];

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
                connection, null, null, [plan], 0, results, CancellationToken.None));

        // Assert
        Assert.Contains("operation 0 exceeds a bounded query limit", failure.Message, StringComparison.Ordinal);
        Assert.Empty(connection.RecordedStatements);
        Assert.Equal(0, connection.BatchExecutions);
        Assert.Equal(1, connection.BatchesDisposed);
    }

    /// <summary>Malformed result ordinals cannot associate evidence with a different operation.</summary>
    /// <param name="corruption">The returned-result violation.</param>
    /// <param name="nativeBatch">Whether the provider exposes native batching.</param>
    [Theory]
    [InlineData("duplicate", true)]
    [InlineData("missing", true)]
    [InlineData("unexpected", true)]
    [InlineData("extra", true)]
    [InlineData("duplicate", false)]
    [InlineData("missing", false)]
    [InlineData("unexpected", false)]
    [InlineData("extra", false)]
    public async Task InvalidResults_RejectAndDispose(
        string corruption,
        bool nativeBatch
    )
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch)
        {
            TransformOrdinals = ordinals => corruption switch
            {
                "duplicate" => [ordinals[0], ordinals[0]],
                "missing" => [ordinals[0]],
                "unexpected" => [99, ordinals[1]],
                "extra" => [.. ordinals, 99],
                _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
            },
        };

        var results = new SafeMigrationProviderAnalysis[2];

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
                connection, null, null, [DistinctPlan(Plan(false), 0), DistinctPlan(Plan(false), 1)],
                0, results, CancellationToken.None));

        // Assert
        Assert.Contains(corruption is "missing" or "extra" ? "inconsistent row count" : "invalid ordinal",
            failure.Message, StringComparison.Ordinal);
        Assert.Equal(nativeBatch ? 1 : 0, connection.BatchesDisposed);
        Assert.Equal(nativeBatch ? 0 : 1, connection.CommandsDisposed);
    }

    /// <summary>
    /// Classifier rows are matched by ordinal, so an engine may return them in any order.
    /// </summary>
    /// <param name="nativeBatch">Whether the connection exposes native ADO.NET batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnorderedResultRows_MatchTheirOwnClassifier(bool nativeBatch)
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch)
        {
            TransformOrdinals = ordinals => [ordinals[1], ordinals[0]],
        };

        var results = new SafeMigrationProviderAnalysis[2];
        SqlServerSafeMigrationRuntimePlan?[] plans =
        [
            DistinctPlan(Plan(false), 0),
            DistinctPlan(Plan(false), 1),
        ];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, plans, 0, results, CancellationToken.None);

        // Assert
        Assert.Equal(State(0), results[0].ObservedState);
        Assert.Equal(State(1), results[1].ObservedState);
        Assert.DoesNotContain("ORDER BY", Assert.Single(connection.RecordedStatements), StringComparison.Ordinal);
    }

    /// <summary>A cancelled capture does not create or execute commands.</summary>
    [Fact]
    public async Task AlreadyCancelledCapture_DoesNotExecute()
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var results = new SafeMigrationProviderAnalysis[1];

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
                connection, null, null, [Plan(true)], 0, results, cancellation.Token));

        // Assert
        Assert.Empty(connection.RecordedStatements);
        Assert.Equal(0, connection.BatchExecutions);
    }

    /// <summary>Unknown transport failures propagate and do not retain commands or native batches.</summary>
    /// <param name="nativeBatch">Whether the connection exposes native batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedExecution_DisposesTransport(bool nativeBatch)
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch) { ThrowOnExecute = true };
        var results = new SafeMigrationProviderAnalysis[1];

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
                connection, null, null, [Plan(true)], 0, results, CancellationToken.None));

        // Assert
        Assert.Equal("Injected catalog execution failure.", failure.Message);
        Assert.Equal(nativeBatch ? 1 : 0, connection.BatchesDisposed);
        Assert.Equal(nativeBatch ? 0 : 1, connection.CommandsDisposed);
        Assert.Null(results[0]);
    }

    /// <summary>
    /// A Unicode expression below the character bound still fails when its UTF-8 payload is too large.
    /// </summary>
    [Fact]
    public async Task OversizedUnicodeClassifier_RejectsByEncodedByteCount()
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection();
        var plan = Plan(false) with { StateExpression = "N'missing' /*" + new string('\u20ac', 1_400_000) + "*/" };
        var results = new SafeMigrationProviderAnalysis[1];

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
                connection, null, null, [plan], 0, results, CancellationToken.None));

        // Assert
        Assert.True(plan.StateExpression.Length < 4 * 1024 * 1024);
        Assert.Contains("utf8_payload_bytes=", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, connection.BatchExecutions);
    }

    /// <summary>Capture and result-slot boundaries are checked before any transport or SQL allocation.</summary>
    /// <param name="planCount">The requested capture size.</param>
    /// <param name="captureStart">The requested original start ordinal.</param>
    /// <param name="resultCount">The available original result slots.</param>
    [Theory]
    [InlineData(513, 0, 513)]
    [InlineData(1, -1, 1)]
    [InlineData(2, 0, 1)]
    [InlineData(1, 1, 1)]
    public async Task InvalidCaptureBoundary_RejectsWithoutDatabaseWork(
        int planCount,
        int captureStart,
        int resultCount
    )
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection();
        var plans = new SqlServerSafeMigrationRuntimePlan?[planCount];
        var results = new SafeMigrationProviderAnalysis[resultCount];

        // Act
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
                connection, null, null, plans, captureStart, results, CancellationToken.None));

        // Assert
        Assert.Empty(connection.RecordedStatements);
        Assert.Equal(0, connection.BatchExecutions);
    }

    /// <summary>Cancellation during result delivery retains the caller token and disposes the transport.</summary>
    /// <param name="nativeBatch">Whether the connection exposes native batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledRead_DisposesTransport(bool nativeBatch)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch)
        {
            TransformOrdinals = ordinals =>
            {
                cancellation.Cancel();

                return ordinals;
            },
        };

        var results = new SafeMigrationProviderAnalysis[1];

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
                connection, null, null, [Plan(false)], 0, results, cancellation.Token));

        // Assert
        Assert.Equal(cancellation.Token, connection.CancellationTokenSeen);
        Assert.Equal(nativeBatch ? 1 : 0, connection.BatchesDisposed);
        Assert.Equal(nativeBatch ? 0 : 1, connection.CommandsDisposed);
    }

    /// <summary>Every transport receives the caller's transaction and configured timeout unchanged.</summary>
    /// <param name="nativeBatch">Whether the provider exposes native batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Capture_ForwardsTimeoutAndCallerOwnedTransaction(bool nativeBatch)
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch);
        await using var transaction = await connection.BeginTransactionAsync();
        var plans = Enumerable.Range(0, 300).Select(index => Plan(index % 2 == 0)).ToArray();
        var results = new SafeMigrationProviderAnalysis[plans.Length];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, transaction, 71, plans, 0, results, CancellationToken.None);

        // Assert
        Assert.NotEmpty(connection.ObservedTimeouts);
        Assert.All(connection.ObservedTimeouts, timeout => Assert.Equal(71, timeout));
        Assert.All(connection.ObservedTransactions, observed => Assert.Same(transaction, observed));
        Assert.Same(connection, transaction.Connection);
        Assert.Equal(0, connection.TransactionsDisposed);
        Assert.All(results, Assert.NotNull);
    }

    /// <summary>The transaction probe observes actual disposal rather than an immutable connection property.</summary>
    [Fact]
    public async Task CallerTransactionProbe_RecordsExplicitDisposal()
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection();
        var transaction = await connection.BeginTransactionAsync();

        // Act
        await transaction.DisposeAsync();

        // Assert
        Assert.Equal(1, connection.TransactionsDisposed);
    }

    /// <summary>Cancellation after an actual successful row prevents further reads and later transports.</summary>
    /// <param name="nativeBatch">Whether the provider exposes native batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationAfterFirstRow_DoesNotReadOrExecuteFurther(bool nativeBatch)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch)
        {
            AfterRowRead = _ => cancellation.Cancel(),
        };

        var plans = Enumerable.Range(0, 257).Select(index => DistinctPlan(Plan(false), index)).ToArray();
        var results = new SafeMigrationProviderAnalysis[plans.Length];

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
                connection, null, null, plans, 0, results, cancellation.Token));

        // Assert
        Assert.Equal(1, connection.RowsRead);
        Assert.NotNull(results[0]);
        Assert.All(results.Skip(1), Assert.Null);
        Assert.Equal(1, connection.BatchExecutions + connection.CommandExecutions);
        Assert.Equal(cancellation.Token, connection.CancellationTokenSeen);
        Assert.Equal(nativeBatch ? 1 : 0, connection.BatchesDisposed);
        Assert.Equal(
            nativeBatch ? 0 : SqlServerCatalogQueryLimits.MaximumSequentialStatementsPerBatch,
            connection.CommandsDisposed);
    }

    /// <summary>A cancelled result-set transition cannot consume the next set or submit the next batch.</summary>
    [Fact]
    public async Task CancellationBetweenNativeResultSets_DoesNotConsumeLaterEvidence()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await using var connection = new SqlServerCatalogTestConnection
        {
            AfterNextResult = () => cancellation.Cancel(),
        };

        var plans = Enumerable.Range(0, 257).Select(index => DistinctPlan(Plan(false), index)).ToArray();
        var results = new SafeMigrationProviderAnalysis[plans.Length];

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
                connection, null, null, plans, 0, results, cancellation.Token));

        // Assert
        Assert.Equal(SqlServerCatalogQueryLimits.MaximumOperationsPerStatement, connection.RowsRead);
        Assert.All(results.Take(SqlServerCatalogQueryLimits.MaximumOperationsPerStatement), Assert.NotNull);
        Assert.All(results.Skip(SqlServerCatalogQueryLimits.MaximumOperationsPerStatement), Assert.Null);
        Assert.Equal(1, connection.BatchExecutions);
        Assert.Equal(cancellation.Token, connection.CancellationTokenSeen);
        Assert.Equal(1, connection.BatchesDisposed);
    }

    /// <summary>Cancellation while delivering the second statement prevents its reads and further execution.</summary>
    [Fact]
    public async Task CancellationDuringSecondSequentialCommand_DoesNotReadItOrExecuteFurther()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var deliveredStatements = 0;
        await using var connection = new SqlServerCatalogTestConnection(false)
        {
            TransformOrdinals = ordinals =>
            {
                if (++deliveredStatements == 2)
                {
                    cancellation.Cancel();
                }

                return ordinals;
            },
        };

        var plans = Enumerable.Range(0, 257).Select(index => DistinctPlan(Plan(false), index)).ToArray();
        var results = new SafeMigrationProviderAnalysis[plans.Length];

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
                connection, null, null, plans, 0, results, cancellation.Token));

        // Assert
        Assert.Equal(SqlServerCatalogQueryLimits.MaximumSequentialOperationsPerStatement, connection.RowsRead);
        Assert.All(results.Take(SqlServerCatalogQueryLimits.MaximumSequentialOperationsPerStatement), Assert.NotNull);
        Assert.All(results.Skip(SqlServerCatalogQueryLimits.MaximumSequentialOperationsPerStatement), Assert.Null);
        Assert.Equal(2, connection.CommandExecutions);
        Assert.Equal(SqlServerCatalogQueryLimits.MaximumSequentialStatementsPerBatch, connection.CommandsDisposed);
        Assert.Equal(cancellation.Token, connection.CancellationTokenSeen);
    }

    /// <summary>Delayed statement text varies only in the bound ordinal, not in the reusable inner SQL.</summary>
    [Fact]
    public void DelayedOrdinal_IsAnExplicitIntParameter()
    {
        // Arrange
        var plan = Plan(true);

        // Act
        var first = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogSelection(0, plan);
        var second = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogSelection(100_000, plan);

        // Assert
        Assert.Contains("N''SELECT @doka_ordinal,", first, StringComparison.Ordinal);
        Assert.Contains("N'@doka_ordinal int', @doka_ordinal = 0;", first, StringComparison.Ordinal);
        const string parameterBoundary = "', N'@doka_ordinal int'";
        Assert.Equal(first[..first.LastIndexOf(parameterBoundary, StringComparison.Ordinal)],
            second[..second.LastIndexOf(parameterBoundary, StringComparison.Ordinal)]);
    }

    /// <summary>Equal immutable plans share one baseline result while retaining every original result slot.</summary>
    /// <param name="delayed">Whether the classifier requires delayed binding.</param>
    /// <param name="nativeBatch">Whether the connection exposes native batching.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EqualPlans_UseOneClassifierAndRestoreSkippedOriginalSlots(bool delayed, bool nativeBatch)
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch);
        var plan = DistinctPlan(Plan(delayed), 42);
        var plans = Enumerable.Range(0, 512).Select(index => index == 7 ? null : plan with { }).ToArray();
        const int captureStart = 17;
        var results = Enumerable.Repeat(Unsupported(), captureStart + plans.Length).ToArray();

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, plans, captureStart, results, CancellationToken.None);

        // Assert
        Assert.Single(connection.RecordedStatements);
        Assert.Equal(1, connection.RowsRead);
        Assert.Equal(nativeBatch ? 1 : 0, connection.BatchExecutions);
        Assert.Equal(nativeBatch ? 0 : 1, connection.CommandExecutions);
        Assert.Equal(SafeMigrationObservedState.Different, results[captureStart].ObservedState);
        Assert.Same(plan.DifferentDifference, Assert.Single(results[captureStart].Differences));
        Assert.All(results.Take(captureStart), result => Assert.Equal(
            SafeMigrationObservedState.Unsupported, result.ObservedState));
        for (var index = 0; index < plans.Length; index++)
        {
            if (index == 7)
            {
                Assert.Equal(SafeMigrationObservedState.Unsupported, results[captureStart + index].ObservedState);
            }
            else
            {
                Assert.Same(results[captureStart], results[captureStart + index]);
            }
        }
    }

    /// <summary>Contract differences prevent reuse even when state SQL and object names are identical.</summary>
    [Fact]
    public async Task DifferentSafetyAndEvidenceContracts_AreNotCoalesced()
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection();
        var plan = Plan(false);
        SqlServerSafeMigrationRuntimePlan?[] plans =
        [
            plan,
            plan with { PhysicalTableSupportExpression = "1" },
            plan with { PrerequisiteExpression = "2" },
            plan with { StateEvaluationGuardExpression = "2" },
            plan with { StateEvaluationGuardFailureExpression = "N'missing'" },
            plan with { ColumnLayoutFailureExpression = "NULL" },
            plan with { DiagnosticEvidenceExpression = "NULL" },
            plan with { MatchedObjectNameExpression = "NULL" },
            plan with { ModelManagedRowEvidenceExpression = "NULL" },
            plan with { ModelManagedDependencyCountsExpression = "NULL" },
            plan with { ClassificationCodeExpression = "NULL" },
            plan with { CatalogPreambleSql = "DECLARE @probe int = 1;" },
            plan with { MayRequireNullabilityDataProof = true },
            DistinctPlan(plan, 42),
        ];

        var results = new SafeMigrationProviderAnalysis[plans.Length];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, plans, 0, results, CancellationToken.None);

        // Assert
        Assert.Equal(plans.Length, connection.RowsRead);
        Assert.Equal(Enumerable.Range(0, plans.Length).Select(State), results.Select(result => result.ObservedState));
    }

    /// <summary>Reuse never crosses captures or analyzer invocations, where live state may have changed.</summary>
    [Fact]
    public async Task RepeatedCapture_QueriesTheBaselineAgain()
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection();
        var first = new SafeMigrationProviderAnalysis[512];
        var second = new SafeMigrationProviderAnalysis[512];
        var plans = Enumerable.Repeat(Plan(false), 512).ToArray();

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, plans, 0, first, CancellationToken.None);
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, plans, 0, second, CancellationToken.None);

        // Assert
        Assert.Equal(2, connection.RecordedStatements.Count);
        Assert.Equal(2, connection.RowsRead);
        Assert.NotSame(first[0], second[0]);
        Assert.All(first, result => Assert.Same(first[0], result));
        Assert.All(second, result => Assert.Same(second[0], result));
    }

    /// <summary>Separately generated production column classifiers reuse their unchanged baseline contracts.</summary>
    [Fact]
    public async Task ProductionColumnPlans_AreReusedWithoutSharingPlanInstances()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Server=localhost;Database=packing;Integrated Security=true;");
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        await using var connection = new SqlServerCatalogTestConnection();
        var ensure = new SafeMigrationOperation(new EnsureColumnIntent("items",
            new ExpectedColumnDefinition("id", typeof(int), false, "int")), SafeMigrationPolicy.ThrowIfDifferent);

        var alter = new SafeMigrationOperation(new AlterColumnIntent("items",
            new ExpectedColumnDefinition("caption", typeof(string), false, "varchar(20)", maxLength: 20),
            new ExpectedColumnDefinition("caption", typeof(string), false, "varchar(10)", maxLength: 10)),
            SafeMigrationPolicy.RepairIfSafe);

        var plans = Enumerable.Range(0, 512).Select(index => catalog.Build(index % 2 == 0 ? ensure : alter)).ToArray();
        var results = new SafeMigrationProviderAnalysis[plans.Length];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, plans, 0, results, CancellationToken.None);

        // Assert
        Assert.NotSame(plans[0], plans[2]);
        Assert.Equal(plans[0], plans[2]);
        Assert.Equal(2, connection.RowsRead);
        for (var index = 0; index < results.Length; index++)
        {
            Assert.Same(results[index % 2], results[index]);
        }
    }

    private static SqlServerSafeMigrationRuntimePlan Plan(bool delayed)
        => new("N'missing'", "0", SafeMigrationRepairCapability.None, "0") { RequiresDelayedBinding = delayed };

    /// <summary>Gives a classifier a distinct immutable diagnostic contract without changing its SQL shape.</summary>
    private static SqlServerSafeMigrationRuntimePlan DistinctPlan(SqlServerSafeMigrationRuntimePlan plan, int ordinal)
        => plan with { DifferentDifference = new SafeMigrationFacetDifference("test_contract", ordinal.ToString(
            CultureInfo.InvariantCulture), "actual") };

    private static SafeMigrationProviderAnalysis Unsupported()
        => new(SafeMigrationObservedState.Unsupported, SafeMigrationRepairCapability.None, false, "test_unsupported");

    private static SafeMigrationObservedState State(int ordinal) => (ordinal % 3) switch
    {
        0 => SafeMigrationObservedState.Missing,
        1 => SafeMigrationObservedState.Matching,
        2 => SafeMigrationObservedState.Different,
        _ => throw new ArgumentOutOfRangeException(nameof(ordinal)),
    };
}
