namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Bounds direct classifier compilation without reducing transport or result ownership.</summary>
public sealed class SqlServerCatalogCompilationBudgetTests
{
    private const int CompilerBudget = 64 * 1024;

    /// <summary>Real strict table matchers split before a large UNION loses its compiler budget.</summary>
    /// <param name="nativeBatch">Whether the connection supports native batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StrictTables_RetainAllOriginalOrdinalsWithinCompilerBudget(bool nativeBatch)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Server=127.0.0.1,1433;Database=compiler;User ID=sa;Password=unused;TrustServerCertificate=True");

        await using var connection = new SqlServerCatalogTestConnection(nativeBatch)
        {
            TransformOrdinals = static ordinals => ordinals.Reverse().ToArray(),
        };

        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var plans = Enumerable.Range(0, 100).Select(index => catalog.Build(new SafeMigrationOperation(
            new EnsureTableIntent(new ExpectedTableDefinition("expected_perf_"
                + index.ToString("D4", CultureInfo.InvariantCulture),
                [new ExpectedColumnDefinition("id", typeof(int), false, "int")]),
                SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent))).ToArray();

        var results = new SafeMigrationProviderAnalysis[plans.Length];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, 180, plans, 0, results, CancellationToken.None);

        // Assert
        Assert.True(connection.RecordedStatements.Count > 4);
        Assert.All(connection.RecordedStatements, statement => Assert.InRange(
            Encoding.UTF8.GetByteCount(statement), 1, CompilerBudget));

        Assert.Equal(Enumerable.Range(0, plans.Length).Select(State),
            results.Select(static item => item.ObservedState));
        Assert.Equal(nativeBatch ? (connection.RecordedStatements.Count + 7) / 8 : 0, connection.BatchExecutions);
        Assert.Equal(nativeBatch ? 0 : connection.RecordedStatements.Count, connection.CommandExecutions);
        Assert.All(connection.BatchStatementCounts.SkipLast(1), count => Assert.Equal(8, count));
        Assert.All(connection.BatchPayloadBytes, bytes => Assert.InRange(bytes, 1, 4 * 1024 * 1024));
        Assert.All(connection.ObservedTimeouts, timeout => Assert.Equal(180, timeout));
    }

    /// <summary>Cheap direct classifiers keep the established 32-operation statement width.</summary>
    [Fact]
    public async Task CheapDirectClassifiers_RetainFullStatementAndBatchPacking()
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection();
        var plans = Enumerable.Range(0, 65).Select(index => Plan(index, delayed: false)).ToArray();
        var results = new SafeMigrationProviderAnalysis[plans.Length];
        int[] expectedWidths = [32, 32, 1];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, plans, 0, results, CancellationToken.None);

        // Assert
        Assert.Equal(expectedWidths, connection.RecordedStatements.Select(StatementWidth));
        Assert.Equal(1, connection.BatchExecutions);
        Assert.Equal(Enumerable.Range(0, plans.Length).Select(State),
            results.Select(static item => item.ObservedState));
    }

    /// <summary>Real occupancy-certified missing-table plans keep the wide large-stream fast path.</summary>
    [Fact]
    public async Task KnownAbsentStrictTables_RetainThirtyTwoClassifierStatements()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Server=127.0.0.1,1433;Database=compiler;User ID=sa;Password=unused;TrustServerCertificate=True");

        await using var connection = new SqlServerCatalogTestConnection();
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var plans = Enumerable.Range(0, 65).Select(index => catalog.Build(new SafeMigrationOperation(
            new EnsureTableIntent(new ExpectedTableDefinition("absent_perf_"
                + index.ToString("D4", CultureInfo.InvariantCulture),
                [new ExpectedColumnDefinition("id", typeof(int), false, "int")]),
                SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent),
            targetTableKnownAbsent: true)).ToArray();

        var results = new SafeMigrationProviderAnalysis[plans.Length];
        int[] expectedWidths = [32, 32, 1];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, plans, 0, results, CancellationToken.None);

        // Assert
        Assert.Equal(expectedWidths, connection.RecordedStatements.Select(StatementWidth));
        Assert.Equal(1, connection.BatchExecutions);
        Assert.Equal(Enumerable.Range(0, plans.Length).Select(State),
            results.Select(static item => item.ObservedState));
    }

    /// <summary>Only the certified plain absence producer can waive the additional compiler split.</summary>
    /// <param name="kind">The authored work retained by the table classifier.</param>
    [Theory]
    [InlineData("plain")]
    [InlineData("existing")]
    [InlineData("default")]
    [InlineData("collation")]
    [InlineData("computed")]
    [InlineData("primary")]
    [InlineData("unique")]
    [InlineData("check")]
    [InlineData("foreign")]
    public void AbsenceHint_RequiresSimpleCertifiedWork(string kind)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Server=127.0.0.1,1433;Database=compiler;User ID=sa;Password=unused;TrustServerCertificate=True");

        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var id = kind == "collation"
            ? new ExpectedColumnDefinition("id", typeof(string), false, "varchar(10)",
                collation: new SafeMigrationCollationIdentifier("Latin1_General_100_BIN2"))
            : new ExpectedColumnDefinition("id", typeof(int), false, "int",
                defaultValue: kind == "default"
                    ? SafeMigrationDefaultValue.Literal(7) : SafeMigrationDefaultValue.None);

        var columns = kind == "computed"
            ? new[] { id, new ExpectedColumnDefinition("derived", typeof(int), true, "int",
                computedColumnSql: "[id] + 1") } : [id];

        var definition = new ExpectedTableDefinition("hint_items", columns,
            primaryKey: kind == "primary" ? new ExpectedPrimaryKeyDefinition("PK_hint", "hint_items", ["id"]) : null,
            uniqueConstraints: kind == "unique"
                ? [new ExpectedUniqueConstraintDefinition("UQ_hint", "hint_items", ["id"])] : [],
            checkConstraints: kind == "check"
                ? [new ExpectedCheckConstraintDefinition("CK_hint", "hint_items", "[id] > 0")] : [],
            foreignKeys: kind == "foreign"
                ? [new ExpectedForeignKeyDefinition("FK_hint", "hint_items", ["id"], "parent", ["id"])] : []);

        var operation = new SafeMigrationOperation(new EnsureTableIntent(definition,
            SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(operation, targetTableKnownAbsent: kind != "existing");

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Equal(kind == "plain", plan.IsSimpleAbsentTableClassifier);
    }

    /// <summary>Complex rows retain the conservative budget even when neighboring absence plans are cheap.</summary>
    [Fact]
    public async Task MixedComplexAndAbsenceClassifiers_KeepTheCompilerBudget()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Server=127.0.0.1,1433;Database=compiler;User ID=sa;Password=unused;TrustServerCertificate=True");

        await using var connection = new SqlServerCatalogTestConnection();
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var plans = Enumerable.Range(0, 100).Select(index => catalog.Build(new SafeMigrationOperation(
            new EnsureTableIntent(new ExpectedTableDefinition((index % 2 == 0 ? "cheap_" : "complex_")
                + index.ToString("D4", CultureInfo.InvariantCulture),
                [new ExpectedColumnDefinition("id", typeof(int), false, "int")]),
                SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent),
            targetTableKnownAbsent: index % 2 == 0)).ToArray();

        var results = new SafeMigrationProviderAnalysis[plans.Length];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, plans, 0, results, CancellationToken.None);

        // Assert
        Assert.Contains(connection.RecordedStatements, statement => statement.Contains(
                "cheap_", StringComparison.Ordinal)
            && statement.Contains("complex_", StringComparison.Ordinal));

        Assert.All(connection.RecordedStatements, statement => Assert.InRange(
            Encoding.UTF8.GetByteCount(statement), 1, CompilerBudget));

        Assert.Equal(Enumerable.Range(0, plans.Length).Select(State),
            results.Select(static item => item.ObservedState));
    }

    /// <summary>The conservative default and explicit hint remain distinct in exact-plan coalescing.</summary>
    [Fact]
    public async Task AbsenceHint_ParticipatesInRecordEqualityAndDeduplication()
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection();
        var conservative = new SqlServerSafeMigrationRuntimePlan("N'missing'", "0",
            SafeMigrationRepairCapability.None, "0");

        var cheap = conservative with { IsSimpleAbsentTableClassifier = true };
        var copy = cheap with { };
        var results = new SafeMigrationProviderAnalysis[3];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, [cheap, conservative, copy], 0, results, CancellationToken.None);

        // Assert
        Assert.False(conservative.IsSimpleAbsentTableClassifier);
        Assert.NotEqual(conservative, cheap);
        Assert.Equal(cheap, copy);
        Assert.Equal(2, StatementWidth(Assert.Single(connection.RecordedStatements)));
        Assert.Equal(State(0), results[0].ObservedState);
        Assert.Equal(State(1), results[1].ObservedState);
        Assert.Same(results[0], results[2]);
    }

    /// <summary>Delayed templates remain on their independent 32-operation and transport budgets.</summary>
    [Fact]
    public async Task DelayedClassifiers_DoNotUseTheDirectCompilationBudget()
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection();
        var plans = Enumerable.Range(0, 33).Select(index => Plan(index, delayed: true) with
        {
            StateExpression = "N'missing' /*" + new string('x', CompilerBudget + 1) + "*/",
        }).ToArray();

        var results = new SafeMigrationProviderAnalysis[plans.Length];
        int[] expectedWidths = [32, 1];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, plans, 0, results, CancellationToken.None);

        // Assert
        Assert.Equal(2, connection.RecordedStatements.Count);
        Assert.Equal(expectedWidths, connection.RecordedParameters.Select(parameters => parameters.Count(
            static parameter => parameter.ParameterName.StartsWith("@doka_ordinal", StringComparison.Ordinal))));

        Assert.True(Encoding.UTF8.GetByteCount(connection.RecordedStatements[0]) > CompilerBudget);
        Assert.Equal(1, connection.BatchExecutions);
        Assert.Equal(Enumerable.Range(0, plans.Length).Select(State),
            results.Select(static item => item.ObservedState));
    }

    /// <summary>The direct UNION boundary counts UTF-8 and its separator, including exact equality.</summary>
    /// <param name="delta">The encoded byte offset from the inclusive compiler budget.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task UnicodeBoundary_UsesEncodedStatementBytes(int delta)
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection();
        var first = Plan(0, delayed: false) with { StateExpression = "N'missing' /**/" };
        var second = Plan(1, delayed: false);
        var baseBytes = Encoding.UTF8.GetByteCount(
                SqlServerSafeMigrationProviderAnalyzer.BuildCatalogSelection(0, first))
            + Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator)
            + Encoding.UTF8.GetByteCount(SqlServerSafeMigrationProviderAnalyzer.BuildCatalogSelection(1, second));

        var padding = CompilerBudget + delta - baseBytes;
        var unicode = new string('\u20ac', padding / 3) + new string('x', padding % 3);
        first = first with { StateExpression = "N'missing' /*" + unicode + "*/" };
        var results = new SafeMigrationProviderAnalysis[2];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, [first, second], 0, results, CancellationToken.None);

        // Assert
        Assert.Equal(delta <= 0 ? 1 : 2, connection.RecordedStatements.Count);
        Assert.True(connection.RecordedStatements[0].Length
            < Encoding.UTF8.GetByteCount(connection.RecordedStatements[0]));

        Assert.All(connection.RecordedStatements, statement => Assert.InRange(
            Encoding.UTF8.GetByteCount(statement), 1, CompilerBudget));

        Assert.Equal(1, connection.BatchExecutions);
        Assert.Equal(new[] { State(0), State(1) }, results.Select(static item => item.ObservedState));
    }

    /// <summary>
    /// A larger standalone classifier remains admitted and shares its transport with later statements.
    /// </summary>
    [Fact]
    public async Task LargeStandaloneClassifiers_RemainAdmittedAndShareOneBatch()
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection();
        var plans = Enumerable.Range(0, 3).Select(index => Plan(index, delayed: false) with
        {
            StateExpression = "N'missing' /*" + new string('x', 100_000) + "*/",
        }).ToArray();

        var results = new SafeMigrationProviderAnalysis[plans.Length];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, plans, 0, results, CancellationToken.None);

        // Assert
        Assert.Equal(3, connection.RecordedStatements.Count);
        Assert.All(connection.RecordedStatements, statement => Assert.Equal(1, StatementWidth(statement)));
        Assert.All(connection.RecordedStatements, statement => Assert.InRange(
            Encoding.UTF8.GetByteCount(statement), CompilerBudget + 1, 4 * 1024 * 1024));

        Assert.Equal(1, connection.BatchExecutions);
        Assert.Equal(Enumerable.Range(0, plans.Length).Select(State),
            results.Select(static item => item.ObservedState));
    }

    /// <summary>New compiler splits cannot associate incomplete or foreign rows with another operation.</summary>
    /// <param name="nativeBatch">Whether the connection supports native batching.</param>
    /// <param name="fault">The injected third-statement result ownership violation.</param>
    [Theory]
    [InlineData(true, "missing")]
    [InlineData(false, "missing")]
    [InlineData(true, "duplicate")]
    [InlineData(false, "duplicate")]
    [InlineData(true, "foreign")]
    [InlineData(false, "foreign")]
    [InlineData(true, "extra")]
    [InlineData(false, "extra")]
    public async Task InvalidResultsAcrossCompilerSplit_RejectAndDispose(bool nativeBatch, string fault)
    {
        // Arrange
        var injected = false;
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch)
        {
            TransformOrdinals = ordinals =>
            {
                if (ordinals[0] != 4)
                {
                    return ordinals;
                }

                injected = true;

                return fault switch
                {
                    "missing" => [ordinals[0]],
                    "duplicate" => [ordinals[0], ordinals[0]],
                    "foreign" => [9999, ordinals[1]],
                    "extra" => [.. ordinals, 9999],
                    _ => throw new ArgumentOutOfRangeException(nameof(fault)),
                };
            },
        };

        var plans = Enumerable.Range(0, 10).Select(index => Plan(index, delayed: false) with
        {
            StateExpression = "N'missing' /*" + new string('x', 24_000) + "*/",
        }).ToArray();

        var results = new SafeMigrationProviderAnalysis[plans.Length];

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
                connection, null, null, plans, 0, results, CancellationToken.None));

        // Assert
        Assert.True(injected);
        Assert.Contains(fault is "missing" or "extra" ? "inconsistent row count" : "invalid ordinal",
            error.Message, StringComparison.Ordinal);

        Assert.True(connection.RecordedStatements.Count >= 3);
        Assert.Equal(Enumerable.Range(0, 4).Select(State), results.Take(4).Select(static item => item.ObservedState));
        Assert.Equal(nativeBatch ? 1 : 0, connection.BatchesDisposed);
    }

    /// <summary>Creates distinct contracts without changing the synthetic reader's result classification.</summary>
    private static SqlServerSafeMigrationRuntimePlan Plan(int ordinal, bool delayed)
        => new("N'missing'", "0", SafeMigrationRepairCapability.None, "0")
        {
            RequiresDelayedBinding = delayed,
            DifferentDifference = new SafeMigrationFacetDifference("probe", "expected", ordinal.ToString(
                CultureInfo.InvariantCulture)),
        };

    /// <summary>Counts only direct UNION branches, never nested SELECT clauses.</summary>
    private static int StatementWidth(string statement)
        => statement.Split(SafeMigrationCatalogQueryLimits.Separator, StringSplitOptions.None).Length;

    /// <summary>Mirrors the transport fixture's ordinal-dependent synthetic observation.</summary>
    private static SafeMigrationObservedState State(int ordinal)
        => (ordinal % 3) switch
        {
            0 => SafeMigrationObservedState.Missing,
            1 => SafeMigrationObservedState.Matching,
            _ => SafeMigrationObservedState.Different,
        };
}
