namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Preserves ordered narrowing eligibility while building only bounded transition SQL windows.</summary>
public sealed class PostgreSqlTransitionProbeWindowTests
{
    /// <summary>Each bounded statement preserves the original eligibility SQL and operation ordinals.</summary>
    /// <param name="count">The candidate count surrounding the transport boundary.</param>
    /// <param name="native">Whether the deterministic connection provides native batching.</param>
    [Theory]
    [InlineData(31, true)]
    [InlineData(32, true)]
    [InlineData(33, true)]
    [InlineData(511, true)]
    [InlineData(512, true)]
    [InlineData(513, true)]
    [InlineData(513, false)]
    [InlineData(4097, true)]
    public async Task EligibilityWindows_PreserveMaterializedSqlAndExactResults(
        int count,
        bool native
    )
    {
        // Arrange
        using var context = OfflineContext();
        var analyzer = Analyzer(context);
        var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var operations = Operations(count);
        var expected = operations.Chunk(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
            .Select((chunk, window) => string.Join(SafeMigrationCatalogQueryLimits.Separator,
                chunk.Select((operation, index) => EligibilitySql(builder, operation,
                    window * SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement + index)))
                + SafeMigrationCatalogQueryLimits.Trailer).ToArray();

        await using var connection = new CatalogStatementTestConnection(native, EligibilityRows);

        // Act
        var result = await ResolveAsync(analyzer, connection, operations,
            new SafeMigrationObservedState?[count], CancellationToken.None);

        // Assert
        Assert.Equal(expected, connection.Submissions.Select(static submission => submission.Sql));
        Assert.Equal(count, result.Length);
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            Assert.Equal(ordinal % 3 != 0, Fact(result.GetValue(ordinal), "IsTransitionEligible"));
            Assert.False(Fact(result.GetValue(ordinal), "IsRequired"));
            Assert.False(Fact(result.GetValue(ordinal), "IsBlocked"));
        }

        Assert.All(connection.Submissions, static submission =>
        {
            Assert.Equal(71, submission.Timeout);
            Assert.Empty(submission.Parameters);
            Assert.InRange(Encoding.UTF8.GetByteCount(submission.Sql), 1,
                SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes);
        });

        Assert.Equal(native ? (expected.Length + 7) / 8 : expected.Length, connection.ReadersDisposed);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Deduplication reuses exact contracts without losing skipped or repeated ordinal slots.</summary>
    [Fact]
    public async Task EligibilityWindows_KeepDeduplicatedAndShortCircuitedSlotsDistinct()
    {
        // Arrange
        using var context = OfflineContext();
        var operations = Operations(514);
        operations[512] = operations[1];
        operations[513] = operations[2];
        var states = new SafeMigrationObservedState?[operations.Length];
        states[0] = SafeMigrationObservedState.PrerequisiteMissing;
        states[511] = SafeMigrationObservedState.Unsupported;
        await using var connection = new CatalogStatementTestConnection(true, EligibilityRows);

        // Act
        var result = await ResolveAsync(Analyzer(context), connection, operations, states, CancellationToken.None);

        // Assert
        Assert.Null(result.GetValue(0));
        Assert.Null(result.GetValue(511));
        Assert.Equal(result.GetValue(1), result.GetValue(512));
        Assert.Equal(result.GetValue(2), result.GetValue(513));
        Assert.Equal(16, connection.Submissions.Count);
        var ordinals = connection.Submissions.SelectMany(static submission => Ordinals(submission.Sql)).ToArray();

        Assert.Equal(Enumerable.Range(1, 510), ordinals);
        Assert.DoesNotContain(512, ordinals);
        Assert.DoesNotContain(513, ordinals);
    }

    /// <summary>Shared row facts never replace distinct physical source-contract eligibility.</summary>
    /// <param name="native">Whether the connection transports both phases in native batches.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EligibilityWindows_RowFactsKeepIndependentSourceContracts(bool native)
    {
        // Arrange
        using var context = OfflineContext();
        var first = new SafeMigrationOperation(new AlterColumnIntent("window_rows",
            new ExpectedColumnDefinition("alpha", typeof(string), true, "character varying(40)"),
            new ExpectedColumnDefinition("alpha", typeof(string), true, "character varying(80)")),
            SafeMigrationPolicy.RepairIfSafe);

        var otherSource = new SafeMigrationOperation(new AlterColumnIntent("window_rows",
            new ExpectedColumnDefinition("alpha", typeof(string), true, "character varying(40)"),
            new ExpectedColumnDefinition("alpha", typeof(string), true, "character varying(60)")),
            SafeMigrationPolicy.RepairIfSafe);

        var otherColumn = new SafeMigrationOperation(new AlterColumnIntent("window_rows",
            new ExpectedColumnDefinition("beta", typeof(string), true, "character varying(10)"),
            new ExpectedColumnDefinition("beta", typeof(string), true, "character varying(80)")),
            SafeMigrationPolicy.RepairIfSafe);

        SafeMigrationOperation[] operations = [first, otherSource, otherColumn, first];
        await using var connection = new CatalogStatementTestConnection(native, (sql, parameters) =>
        {
            if (sql.StartsWith("SELECT COALESCE(bool_or(", StringComparison.Ordinal))
            {
                var facts = new DataTable();
                facts.Columns.Add("alpha", typeof(bool));
                facts.Columns.Add("beta", typeof(bool));
                facts.Rows.Add(true, false);

                return facts;
            }

            var rows = EligibilityRows(sql, parameters);
            foreach (DataRow row in rows.Rows)
            {
                row[1] = (int)row[0] != 1;
                row[2] = true;
            }

            return rows;
        });

        // Act
        var result = await ResolveAsync(Analyzer(context), connection, operations,
            new SafeMigrationObservedState?[operations.Length], CancellationToken.None);

        // Assert
        Assert.True(Fact(result.GetValue(0), "IsTransitionEligible"));
        Assert.True(Fact(result.GetValue(0), "IsRequired"));
        Assert.True(Fact(result.GetValue(0), "IsBlocked"));
        Assert.False(Fact(result.GetValue(1), "IsTransitionEligible"));
        Assert.False(Fact(result.GetValue(1), "IsRequired"));
        Assert.False(Fact(result.GetValue(1), "IsBlocked"));
        Assert.True(Fact(result.GetValue(2), "IsTransitionEligible"));
        Assert.True(Fact(result.GetValue(2), "IsRequired"));
        Assert.False(Fact(result.GetValue(2), "IsBlocked"));
        Assert.Equal(result.GetValue(0), result.GetValue(3));
        Assert.Equal(2, connection.Submissions.Count);
        Assert.Equal("SELECT COALESCE(bool_or(alpha IS NOT NULL AND char_length(alpha) > 40), FALSE), "
            + "COALESCE(bool_or(beta IS NOT NULL AND char_length(beta) > 10), FALSE) FROM window_rows;",
            connection.Submissions[1].Sql);
    }

    /// <summary>A corrupt eligibility window cannot assign a physical proof to the wrong operation.</summary>
    /// <param name="fault">The injected ordinal or result-cardinality fault.</param>
    [Theory]
    [InlineData("ordinal")]
    [InlineData("missing")]
    [InlineData("extra")]
    public async Task EligibilityWindows_RejectUnownedOrIncompleteRows(string fault)
    {
        // Arrange
        using var context = OfflineContext();
        await using var connection = new CatalogStatementTestConnection(true, (sql, parameters) =>
        {
            var rows = EligibilityRows(sql, parameters);
            if (fault == "ordinal")
            {
                rows.Rows[0][0] = -1;
            }
            else if (fault == "missing")
            {
                rows.Rows.RemoveAt(rows.Rows.Count - 1);
            }
            else
            {
                rows.Rows.Add(999, true, false);
            }

            return rows;
        });

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => ResolveAsync(
            Analyzer(context), connection, Operations(513), new SafeMigrationObservedState?[513],
            CancellationToken.None));

        // Assert
        Assert.Equal(1, connection.ReadersDisposed);
        Assert.Equal(1, connection.BatchesDisposed);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Cancellation within a window rejects the entire phase rather than returning partial evidence.</summary>
    [Fact]
    public async Task EligibilityWindows_CancellationDisposesTransportWithoutPartialEvidence()
    {
        // Arrange
        using var context = OfflineContext();
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        await using var connection = new CatalogStatementTestConnection(true, EligibilityRows)
        {
            BeforeRead = () =>
            {
                if (++reads == 2)
                {
                    cancellation.Cancel();
                }
            },
        };

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ResolveAsync(
            Analyzer(context), connection, Operations(513), new SafeMigrationObservedState?[513],
            cancellation.Token));

        // Assert
        Assert.Equal(1, connection.ReadersDisposed);
        Assert.Equal(1, connection.BatchesDisposed);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    private static SafeMigrationDbContext OfflineContext() => new(
        "Host=127.0.0.1;Database=unused;Username=unused", registerSafeMigrations: false);

    private static PostgreSqlSafeMigrationProviderAnalyzer Analyzer(DbContext context) => new(
        context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    private static SafeMigrationOperation[] Operations(int count) => Enumerable.Range(0, count)
        .Select(static index => new SafeMigrationOperation(new AlterColumnIntent("window_rows",
            new ExpectedColumnDefinition($"value_{index:D5}", typeof(string), true, "character varying(40)"),
            new ExpectedColumnDefinition($"value_{index:D5}", typeof(string), true, "character varying(80)")),
            SafeMigrationPolicy.RepairIfSafe)).ToArray();

    private static string EligibilitySql(
        PostgreSqlSafeMigrationCatalogSqlBuilder builder,
        SafeMigrationOperation operation,
        int ordinal
    )
    {
        var probe = builder.Build(operation, includeTransitionEvidence: true).DataProbe
            ?? throw new InvalidOperationException("The test operation must have a narrowing proof.");

        return $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, "
            + $"COALESCE(({probe.TransitionInvariantExpression}), FALSE), "
            + $"COALESCE(({probe.NarrowingExpression}), FALSE)";
    }

    private static DataTable EligibilityRows(string sql, IReadOnlyList<DbParameter> parameters)
    {
        var rows = new DataTable();
        rows.Columns.Add("ordinal", typeof(int));
        rows.Columns.Add("eligible", typeof(bool));
        rows.Columns.Add("narrowing", typeof(bool));
        foreach (var ordinal in Ordinals(sql))
        {
            rows.Rows.Add(ordinal, ordinal % 3 != 0, false);
        }

        return rows;
    }

    private static IEnumerable<int> Ordinals(string sql)
    {
        foreach (var selection in sql.Split(SafeMigrationCatalogQueryLimits.Separator, StringSplitOptions.None))
        {
            var start = selection.IndexOf("SELECT ", StringComparison.Ordinal) + 7;
            var end = selection.IndexOf(',', start);

            yield return int.Parse(selection.AsSpan(start, end - start), CultureInfo.InvariantCulture);
        }
    }

    private static bool Fact(object? result, string name)
    {
        Assert.NotNull(result);

        return Assert.IsType<bool>(result.GetType().GetProperty(name)!.GetValue(result));
    }

    /// <summary>Exercises the real private phase without adding an unused production API solely for tests.</summary>
    private static async Task<Array> ResolveAsync(
        PostgreSqlSafeMigrationProviderAnalyzer analyzer,
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationObservedState?[] states,
        CancellationToken token
    )
    {
        var method = typeof(PostgreSqlSafeMigrationProviderAnalyzer).GetMethod(
            "ResolveDataProbeResultsAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The narrowing resolution method is absent.");

        var task = (Task)(method.Invoke(analyzer, [connection, operations, states, 71, token])
            ?? throw new InvalidOperationException("The narrowing resolution task is absent."));

        await task;

        return (Array)(task.GetType().GetProperty("Result")!.GetValue(task)
            ?? throw new InvalidOperationException("The narrowing resolution result is absent."));
    }
}
