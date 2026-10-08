namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Measures redundant matching replay captures and verifies bounded transition-certificate ownership.</summary>
/// <param name="output">The test-owned sink for informational allocation and generation measurements.</param>
public sealed class SqlServerMatchingTransitionCaptureTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=matching_transition_capture;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Matching replays cannot use old-source widening certificates and should emit no second catalog capture.</summary>
    /// <param name="count">The number of distinct matching replay candidates.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(512)]
    public async Task MatchingReplay_DoesNotCaptureUnusedTransitionProofs(int count)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        using var analyzer = Analyzer(context);
        await using var connection = new SqlServerCatalogTestConnection(false);
        var operations = Enumerable.Range(0, count).Select(static index => Widening("items_" + index)).ToArray();
        var analyses = operations.Select(static _ => new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Matching, SafeMigrationRepairCapability.None, true, "classified_matching"))
            .ToArray();

        var thread = Environment.CurrentManagedThreadId;
        var stopwatch = new Stopwatch();

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Start();
        await analyzer.ReadProjectedColumnTransitionsAsync(connection, null, operations, analyses, true, 73,
            CancellationToken.None);
        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.Equal(thread, Environment.CurrentManagedThreadId);
        output.WriteLine("candidates={0} statements={1} executions={2} allocated-bytes={3} elapsed-ms={4:F3}",
            count, connection.RecordedStatements.Count, connection.CommandExecutions, allocated,
            stopwatch.Elapsed.TotalMilliseconds);
        Assert.Empty(connection.RecordedStatements);
    }

    /// <summary>Counterfactual former selection retains identical SQL work for an already matching replay.</summary>
    /// <param name="count">The number of distinct replay candidates measured in both paths.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(512)]
    public async Task MatchingSelection_MeasuresRemovedCaptureWork(int count)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        using var current = Analyzer(context);
        using var former = Analyzer(context);
        await using var currentConnection = new SqlServerCatalogTestConnection(false);
        await using var formerConnection = new SqlServerCatalogTestConnection(false)
        {
            TransformClassifierTable = static table =>
            {
                foreach (DataRow row in table.Rows)
                {
                    row["state"] = "matching";
                    row["postcondition"] = 1;
                }
            },
        };
        var operations = Enumerable.Range(0, count).Select(static index => Widening("items_" + index)).ToArray();
        var matching = operations.Select(static _ => Analysis(SafeMigrationObservedState.Matching)).ToArray();
        var different = operations.Select(static _ => Analysis(SafeMigrationObservedState.Different)).ToArray();

        // WHY: Selecting every eligible operation regardless of live state is
        // exactly the removed behavior. Supplying Different reproduces that
        // selection using the same generator and deterministic transport; this
        // measures local work, not engine latency. Neither timing is a CI gate.
        await former.ReadProjectedColumnTransitionsAsync(formerConnection, null, operations, different, true, 73,
            CancellationToken.None);
        formerConnection.RecordedStatements.Clear();
        var stopwatch = new Stopwatch();
        var thread = Environment.CurrentManagedThreadId;

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Start();
        await former.ReadProjectedColumnTransitionsAsync(formerConnection, null, operations, different, true, 73,
            CancellationToken.None);
        stopwatch.Stop();
        var formerBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        var formerMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        stopwatch.Restart();
        before = GC.GetAllocatedBytesForCurrentThread();
        await current.ReadProjectedColumnTransitionsAsync(currentConnection, null, operations, matching, true, 73,
            CancellationToken.None);
        stopwatch.Stop();
        var currentBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.Equal(thread, Environment.CurrentManagedThreadId);
        Assert.Empty(currentConnection.RecordedStatements);
        Assert.NotEmpty(formerConnection.RecordedStatements);
        output.WriteLine("candidates={0} former-statements={1} current-statements=0 former-bytes={2} current-bytes={3} "
            + "former-ms={4:F3} current-ms={5:F3}", count, formerConnection.RecordedStatements.Count, formerBytes,
            currentBytes, formerMilliseconds, stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>Unmatched widening keeps capture and dependency probes while matching siblings reserve no growth.</summary>
    [Fact]
    public async Task MixedReplay_DoesNotReserveUnusedSiblingGrowth()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        using var analyzer = Analyzer(context);
        await using var connection = new SqlServerCatalogTestConnection(false);
        var widening = Widening("items");
        var replay = new SafeMigrationOperation(new AlterColumnIntent("items",
            new ExpectedColumnDefinition("ReplayValue", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("ReplayValue", typeof(int), false, "int")), SafeMigrationPolicy.RepairIfSafe);

        SafeMigrationOperation[] operations = [widening, replay];
        SafeMigrationProviderAnalysis[] analyses =
            [Analysis(SafeMigrationObservedState.Different), Analysis(SafeMigrationObservedState.Matching)];

        // Act
        await analyzer.ReadProjectedColumnTransitionsAsync(connection, null, operations, analyses, true, 73,
            CancellationToken.None);

        // Assert
        Assert.Equal(2, connection.RecordedStatements.Count);
        Assert.Contains(connection.RecordedStatements,
            static sql => sql.Contains("+ 0 * 2 <= 8060)", StringComparison.Ordinal));
        Assert.DoesNotContain(connection.RecordedStatements,
            static sql => sql.Contains("ReplayValue", StringComparison.Ordinal));
    }

    /// <summary>Misaligned live results cannot authorize or prune another operation's physical capture.</summary>
    [Fact]
    public async Task MisalignedClassifications_AreRejectedBeforeCatalogAccess()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        using var analyzer = Analyzer(context);
        await using var connection = new SqlServerCatalogTestConnection(false);

        // Act
        var failure = await Record.ExceptionAsync(() => analyzer.ReadProjectedColumnTransitionsAsync(connection, null,
            [Widening("items")], [], true, 73, CancellationToken.None));

        // Assert
        Assert.Equal("liveAnalyses", Assert.IsType<ArgumentException>(failure).ParamName);
        Assert.Empty(connection.RecordedStatements);
    }

    /// <summary>Initial Matching cannot supply a source-bound repair certificate after earlier mutations change that source.</summary>
    /// <param name="mutation">The column identity, row layout or physical dependency changed before the later widening.</param>
    [Theory]
    [InlineData("source")]
    [InlineData("source_and_layout")]
    [InlineData("source_and_dependency")]
    [InlineData("layout")]
    [InlineData("dependency")]
    public async Task InitialMatching_EarlierMutationCannotInventSafeSourceProof(string mutation)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        using var analyzer = Analyzer(context);
        await using var connection = new SqlServerCatalogTestConnection(false);
        var widening = Widening("items");
        var matching = Analysis(SafeMigrationObservedState.Matching);
        var sourceChanges = mutation.StartsWith("source", StringComparison.Ordinal);
        var projection = new SafeMigrationPreflightProjection(providerOperationProjection: analyzer,
            projectedDependencyAnalyzer: analyzer) { CurrentOperationOrdinal = 0 };

        var model = new MigrationBuilder(context.Database.ProviderName!);
        model.CreateTableIfNotExists("items", table => new
        {
            Value = table.Column<long>(type: "bigint", nullable: false),
            Spare = table.Column<int>(type: "int", nullable: false),
        });
        var tableOperation = (SafeMigrationOperation)model.Operations[0];
        bool? observedDropExecution = null;
        var layoutSourceUnknown = mutation == "source_and_layout";

        // Act
        projection.Observe(tableOperation, matching, matching,
            SafeMigrationDecisionPlanner.Plan(tableOperation.Intent.Kind, matching.ObservedState, tableOperation.Policy));
        await analyzer.ReadProjectedColumnTransitionsAsync(connection, null, [widening], [matching], true, 73,
            CancellationToken.None);
        projection.CurrentOperationOrdinal = 1;
        if (sourceChanges)
        {
            projection.ObserveProviderPostcondition(new AlterColumnOperation { Table = "items", Name = "Value",
                ClrType = typeof(int), ColumnType = "int", OldColumn = new AddColumnOperation
                    { Table = "items", Name = "Value", ClrType = typeof(long), ColumnType = "bigint" } });
        }

        if (mutation != "source")
        {
            var operation = mutation.EndsWith("layout", StringComparison.Ordinal)
                ? new SafeMigrationOperation(new DropColumnIntent("Spare", "items"), SafeMigrationPolicy.ThrowIfDifferent)
                : new SafeMigrationOperation(new DropIndexIntent("IX_old", "items"), SafeMigrationPolicy.ThrowIfDifferent);

            var dropDecision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, matching.ObservedState,
                operation.Policy);

            observedDropExecution = dropDecision.ShouldExecute;
            projection.CurrentOperationOrdinal = 2;
            projection.Observe(operation, matching, matching, dropDecision);
        }

        projection.CurrentOperationOrdinal = 3;
        var result = projection.Project(widening, matching);
        var decision = SafeMigrationDecisionPlanner.Plan(widening.Intent.Kind, result.ObservedState, widening.Policy,
            result.RepairCapability);

        // Assert
        if (mutation != "source")
        {
            Assert.True(observedDropExecution);
        }

        Assert.Empty(connection.RecordedStatements);
        Assert.Equal(SafeMigrationRepairCapability.None, result.RepairCapability);
        Assert.Equal(layoutSourceUnknown ? SafeMigrationObservedState.PrerequisiteMissing
            : sourceChanges ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching,
            result.ObservedState);
        Assert.Equal(layoutSourceUnknown ? SafeMigrationAction.RejectPrerequisiteMissing
            : sourceChanges ? SafeMigrationAction.RejectDifferent : SafeMigrationAction.NoOp, decision.Action);
        Assert.Equal(layoutSourceUnknown ? "prerequisite_missing"
            : sourceChanges ? "alter_not_approved" : "matching_noop", decision.Code);
    }

    /// <summary>Creates a widening with a source contract that cannot match an already widened replay.</summary>
    private static SafeMigrationOperation Widening(string table)
        => new(new AlterColumnIntent(table,
            new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("Value", typeof(int), false, "int")), SafeMigrationPolicy.RepairIfSafe);

    /// <summary>Creates a scoped analyzer with the SQL Server mapping and identifier services.</summary>
    private static SqlServerSafeMigrationProviderAnalyzer Analyzer(DbContext context)
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    /// <summary>Creates the immutable initial state used only to select transition capture work.</summary>
    private static SafeMigrationProviderAnalysis Analysis(SafeMigrationObservedState state)
        => new(state, SafeMigrationRepairCapability.None, state == SafeMigrationObservedState.Matching,
            "classified_state");

}
