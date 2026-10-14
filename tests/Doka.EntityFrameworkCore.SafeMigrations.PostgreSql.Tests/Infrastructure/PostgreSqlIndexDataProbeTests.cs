namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Constrains native-qualified duplicate-row transport without runtime-dependent timing assertions.</summary>
public sealed class PostgreSqlIndexDataProbeTests
{
    /// <summary>
    /// Only qualified actual inputs permit row probes; metadata itself never certifies duplicate absence.
    /// </summary>
    /// <param name="state">The independently captured initial index state.</param>
    /// <param name="supported">Whether the real input opclass and native method permit binding.</param>
    /// <param name="duplicate">Whether actual qualifying rows violate the requested unique key.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Missing, true, true)]
    [InlineData(SafeMigrationObservedState.Missing, true, false)]
    [InlineData(SafeMigrationObservedState.Different, true, true)]
    [InlineData(SafeMigrationObservedState.Different, true, false)]
    [InlineData(SafeMigrationObservedState.Missing, false, true)]
    [InlineData(SafeMigrationObservedState.Different, false, true)]
    [InlineData(SafeMigrationObservedState.Matching, true, true)]
    [InlineData(SafeMigrationObservedState.Unsupported, true, true)]
    public async Task OnlyQualifiedMissingOrDifferentInputsReadRows(
        SafeMigrationObservedState state, bool supported, bool duplicate)
    {
        // Arrange
        using var context = Context();
        var analyzer = Analyzer(context);
        var operations = Operations(1);
        var results = Analyses(1, state);
        var previous = results[0];
        await CaptureAsync(analyzer, operations, results, supported);
        await using var connection = new CatalogStatementTestConnection(true,
            (sql, _) => RowResults(sql, duplicate));

        // Act
        await analyzer.PopulateIndexDataProofsAsync(connection, operations, results, 71, CancellationToken.None);

        // Assert
        var submitted = supported
            && state is SafeMigrationObservedState.Missing or SafeMigrationObservedState.Different;
        Assert.Equal(submitted ? 1 : 0, connection.Submissions.Count);
        Assert.Equal(submitted && duplicate && state == SafeMigrationObservedState.Missing
            ? SafeMigrationObservedState.DataBlocked : state, results[0].ObservedState);
        Assert.Equal(submitted && duplicate ? state == SafeMigrationObservedState.Different
            ? "index_replacement_data_blocked" : "classified_data_blocked" : previous.Code, results[0].Code);
        Assert.Equal(previous.Differences, results[0].Differences);
    }

    /// <summary>Native and compatible-wrapper dispatch retain the same finite statement and batch bounds.</summary>
    /// <param name="native">Whether the connection permits native batches.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QualifiedProbesRetainBoundedTransport(bool native)
    {
        // Arrange
        using var context = Context();
        var analyzer = Analyzer(context);
        var operations = Operations(290);
        var results = Analyses(operations.Length, SafeMigrationObservedState.Missing);
        await CaptureAsync(analyzer, operations, results, supported: true);
        await using var connection = new CatalogStatementTestConnection(native,
            (sql, _) => RowResults(sql, duplicate: false));

        // Act
        await analyzer.PopulateIndexDataProofsAsync(connection, operations, results, 71, CancellationToken.None);

        // Assert
        Assert.Equal(10, connection.Submissions.Count);
        Assert.Equal(native ? [8, 2] : Array.Empty<int>(), connection.BatchCounts);
        Assert.All(results, static result => Assert.Equal(SafeMigrationObservedState.Missing, result.ObservedState));
        Assert.All(connection.Submissions, static submission =>
        {
            Assert.Equal(71, submission.Timeout);
            Assert.InRange(Ordinals(submission.Sql).Count, 1,
                SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement);
            Assert.InRange(Encoding.UTF8.GetByteCount(submission.Sql), 1,
                SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes);
            Assert.Contains("GROUP BY", submission.Sql, StringComparison.Ordinal);
        });
    }

    /// <summary>Incomplete, malformed or cancelled row packets cannot refine even an earlier valid result.</summary>
    /// <param name="fault">The deliberately violated packet or cancellation invariant.</param>
    [Theory]
    [InlineData("ordinal")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("boolean")]
    [InlineData("width")]
    [InlineData("cancel")]
    public async Task InvalidProbePublishesNoPartialResults(string fault)
    {
        // Arrange
        using var context = Context();
        var analyzer = Analyzer(context);
        var operations = Operations(2);
        var results = Analyses(operations.Length, SafeMigrationObservedState.Missing);
        var previous = results.ToArray();
        await CaptureAsync(analyzer, operations, results, supported: true);
        using var cancellation = new CancellationTokenSource();
        await using var connection = new CatalogStatementTestConnection(true, (sql, _) =>
        {
            var table = RowResults(sql, duplicate: true);
            switch (fault)
            {
                case "ordinal": table.Rows[1][0] = 99; break;
                case "duplicate": table.Rows[1][0] = table.Rows[0][0]; break;
                case "missing": table.Rows.RemoveAt(1); break;
                case "boolean": table.Rows[1][1] = DBNull.Value; break;
                case "width": table.Columns.Add("unexpected", typeof(int)); break;
            }

            return table;
        })
        {
            BeforeRead = fault == "cancel" ? cancellation.Cancel : null,
        };

        // Act
        var exception = await Record.ExceptionAsync(() => analyzer.PopulateIndexDataProofsAsync(
            connection, operations, results, 71, cancellation.Token));

        // Assert
        if (fault == "cancel")
        {
            Assert.IsAssignableFrom<OperationCanceledException>(exception);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(exception);
        }

        Assert.Same(previous[0], results[0]);
        Assert.Same(previous[1], results[1]);
        Assert.Equal(1, connection.ReadersDisposed);
    }

    /// <summary>Read-only metadata SQL never binds row grouping, while runtime keeps its independent proof.</summary>
    [Fact]
    public void MetadataClassificationDefersRowsWithoutChangingRuntimeGuard()
    {
        // Arrange
        using var context = Context();
        var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var operation = Operations(1)[0];

        // Act
        var analysis = builder.Build(operation, includeRuntimeIndexInputGuard: false);
        var runtime = builder.Build(operation);

        // Assert
        Assert.DoesNotContain("GROUP BY", analysis.RenderStateExpression(), StringComparison.Ordinal);
        Assert.Null(analysis.ClassificationCodeExpression);
        Assert.Contains("GROUP BY", runtime.RenderStateExpression(), StringComparison.Ordinal);
        Assert.Contains("index_replacement_data_blocked", runtime.ClassificationCodeExpression!,
            StringComparison.Ordinal);
    }

    private static SafeMigrationDbContext Context()
        => new("Host=127.0.0.1;Port=1;Database=index_data_probe;Username=test;Password=test",
            registerSafeMigrations: false);

    private static PostgreSqlSafeMigrationProviderAnalyzer Analyzer(DbContext context)
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    private static SafeMigrationOperation[] Operations(int count)
        => Enumerable.Range(0, count).Select(static ordinal => new SafeMigrationOperation(
            new EnsureIndexIntent(new ExpectedIndexDefinition("ix_" + ordinal, "members",
                [new ExpectedIndexKeyDefinition("id")], unique: true)),
            SafeMigrationPolicy.ThrowIfDifferent)).ToArray();

    private static SafeMigrationProviderAnalysis[] Analyses(int count, SafeMigrationObservedState state)
        => Enumerable.Range(0, count).Select(_ => new SafeMigrationProviderAnalysis(state,
            SafeMigrationRepairCapability.None, state == SafeMigrationObservedState.Matching, "initial_state",
            SafeMigrationOperationalImpact.NotApplicable,
            [new SafeMigrationFacetDifference("index_key_order", "id", "other")])).ToArray();

    private static async Task CaptureAsync(PostgreSqlSafeMigrationProviderAnalyzer analyzer,
        SafeMigrationOperation[] operations, SafeMigrationProviderAnalysis[] results, bool supported)
    {
        await using var connection = new CatalogStatementTestConnection(true, (sql, _) =>
        {
            var table = new DataTable();
            table.Columns.Add("ordinal", typeof(int));
            if (sql.Contains("a.atttypid::bigint", StringComparison.Ordinal))
            {
                for (var field = 1; field < 9; field++)
                {
                    table.Columns.Add("field_" + field, typeof(object));
                }

                foreach (var ordinal in Ordinals(sql))
                {
                    table.Rows.Add(ordinal, true, 23L, 0L,
                        DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, true);
                }
            }
            else
            {
                table.Columns.Add("proof", typeof(bool));
                foreach (var ordinal in Ordinals(sql))
                {
                    table.Rows.Add(ordinal, supported);
                }
            }

            return table;
        });

        await analyzer.PopulateLivePrerequisiteEvidenceAsync(connection, operations, results, 71,
            CancellationToken.None);
    }

    private static DataTable RowResults(string sql, bool duplicate)
    {
        var table = new DataTable();
        table.Columns.Add("ordinal", typeof(int));
        table.Columns.Add("duplicate", typeof(bool));
        foreach (var ordinal in Ordinals(sql))
        {
            table.Rows.Add(ordinal, duplicate);
        }

        return table;
    }

    private static List<int> Ordinals(string sql)
        => System.Text.RegularExpressions.Regex.Matches(sql, @"SELECT (\d+),")
            .Select(static match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
}
