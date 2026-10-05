namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies complete SQL Server metadata chunks and owned inventory transport boundaries.</summary>
public sealed class SqlServerCatalogTailBatchingTests
{
    /// <summary>Headers retain 32-table statements while requested bindings retain 512-value statements.</summary>
    /// <param name="native">Whether the connection exposes native batching.</param>
    /// <param name="keys">Whether key snapshots, rather than column allocations, are captured.</param>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task MetadataChunks_PreserveStatementBoundsAndPhaseOwnership(
        bool native,
        bool keys
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext("Server=localhost;Database=catalog;Integrated Security=true;");
        var analyzer = Analyzer(context);
        await using var connection = new CatalogStatementTestConnection(native, MetadataResults);
        await using var transaction = connection.BeginTransaction();
        var operations = Operations(257, keys);

        // Act
        await CaptureAsync(analyzer, connection, transaction, operations, keys, CancellationToken.None);

        // Assert
        Assert.Equal(10, connection.Submissions.Count);
        int[] expectedCounts = native ? [8, 1, 1] : [];

        Assert.Equal(expectedCounts, connection.BatchCounts);
        Assert.All(connection.Submissions, statement =>
        {
            Assert.Equal(71, statement.Timeout);
            Assert.Same(transaction, statement.Transaction);
            Assert.Equal(native, statement.Native);
            Assert.InRange(Encoding.UTF8.GetByteCount(statement.Sql), 1, 4 * 1024 * 1024);
        });

        Assert.All(connection.Submissions.Take(8), statement => Assert.Equal(32, MetadataResults(
            statement.Sql, statement.Parameters).Rows.Count));
        Assert.Equal(1, MetadataResults(connection.Submissions[8].Sql, []).Rows.Count);
        Assert.Equal(257, MetadataResults(connection.Submissions[9].Sql, []).Rows.Count);
        Assert.Equal(native ? 3 : 0, connection.BatchesDisposed);
        Assert.Equal(native ? 0 : 10, connection.CommandsDisposed);
        Assert.Equal(native ? 3 : 10, connection.ReadersDisposed);
        Assert.Equal(0, connection.TransactionsDisposed);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>The fixture reads requested VALUES rather than the unrelated storage-type IN list.</summary>
    [Fact]
    public void BindingFixture_ExcludesNonRequestLiteralTuples()
    {
        // Arrange
        const string sql = "SELECT requested.[schema],requested.[table],requested.[name],c.column_id,"
            + "CASE WHEN ty.name IN(N'varchar',N'nvarchar',N'varbinary') THEN 0 ELSE 0 END "
            + "FROM (VALUES (N'dbo',N'tail_0000',N'id')) AS requested([schema],[table],[name]);";

        // Act
        using var result = MetadataResults(sql, []);

        // Assert
        var row = Assert.Single(result.Rows.Cast<DataRow>());
        Assert.Equal("dbo", row[0]);
        Assert.Equal("tail_0000", row[1]);
        Assert.Equal("id", row[2]);
    }

    /// <summary>The boundary at 512 bindings remains a two-statement transport, not 32-value fragments.</summary>
    /// <param name="native">Whether the connection exposes native batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ColumnBindings_Preserve512ValueStatementBoundary(bool native)
    {
        // Arrange
        using var context = new SafeMigrationDbContext("Server=localhost;Database=catalog;Integrated Security=true;");
        var analyzer = Analyzer(context);
        await using var connection = new CatalogStatementTestConnection(native, MetadataResults);

        // Act
        await CaptureAsync(analyzer, connection, null, Operations(513, false), false, CancellationToken.None);

        // Assert
        Assert.Equal(19, connection.Submissions.Count);
        int[] expectedCounts = native ? [8, 8, 1, 2] : [];

        Assert.Equal(expectedCounts, connection.BatchCounts);
        Assert.Equal(513, Snapshot(analyzer, false).Count);
        using var fullBindings = MetadataResults(connection.Submissions[17].Sql, []);
        using var tailBindings = MetadataResults(connection.Submissions[18].Sql, []);

        Assert.Equal(512, fullBindings.Rows.Count);
        Assert.Single(tailBindings.Rows.Cast<DataRow>());
        Assert.Equal("tail_0512", tailBindings.Rows[0][1]);
        Assert.Equal(native ? 4 : 19, connection.ReadersDisposed);
    }

    /// <summary>Cross-chunk, foreign, and repeated bindings never publish an incomplete snapshot.</summary>
    /// <param name="native">Whether the connection exposes native batching.</param>
    /// <param name="fault">The injected binding ownership fault.</param>
    [Theory]
    [InlineData(true, "cross_chunk")]
    [InlineData(false, "cross_chunk")]
    [InlineData(true, "foreign")]
    [InlineData(false, "foreign")]
    [InlineData(true, "duplicate")]
    [InlineData(false, "duplicate")]
    public async Task ColumnBindingOwnership_RejectsBeyond512ValueBoundary(
        bool native,
        string fault
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext("Server=localhost;Database=catalog;Integrated Security=true;");
        var analyzer = Analyzer(context);
        await using var connection = new CatalogStatementTestConnection(native, (sql, parameters) =>
        {
            var rows = MetadataResults(sql, parameters);
            if (IsColumnBindingStatement(sql))
            {
                if (fault == "duplicate")
                {
                    rows.Rows.Add(rows.Rows[0].ItemArray);
                }
                else
                {
                    rows.Rows[0][1] = fault == "cross_chunk" ? "tail_0512" : "foreign_table";
                }
            }

            return rows;
        });

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CaptureAsync(analyzer, connection, null, Operations(513, false), false, CancellationToken.None));

        // Assert
        Assert.Equal("SQL Server returned an unowned column layout binding.", failure.Message);
        Assert.Empty(Snapshot(analyzer, false));
        Assert.Equal(native ? 4 : 18, connection.ReadersDisposed);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Reordered or missing immutable statements cannot publish a partially captured snapshot.</summary>
    /// <param name="keys">Whether key metadata is captured.</param>
    /// <param name="fault">The injected transport ownership fault.</param>
    [Theory]
    [InlineData(false, "reversed")]
    [InlineData(true, "reversed")]
    [InlineData(true, "missing")]
    [InlineData(false, "extra")]
    public async Task MalformedMetadata_RejectsWithoutPublishing(
        bool keys,
        string fault
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext("Server=localhost;Database=catalog;Integrated Security=true;");
        var analyzer = Analyzer(context);
        await using var connection = new CatalogStatementTestConnection(true, MetadataResults)
        {
            ReverseResults = fault == "reversed",
            OmitLastResult = fault == "missing",
            ExtraResult = fault == "extra",
        };

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CaptureAsync(analyzer, connection, null, Operations(65, keys), keys, CancellationToken.None));

        // Assert
        Assert.Empty(Snapshot(analyzer, keys));
        Assert.Equal(1, connection.BatchesDisposed);
        Assert.Equal(1, connection.ReadersDisposed);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Cancellation during either metadata phase cannot publish a partial projection proof.</summary>
    /// <param name="native">Whether the connection exposes native batching.</param>
    /// <param name="keys">Whether key metadata is captured.</param>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task CancellationDuringMetadata_DisposesWithoutPublishing(
        bool native,
        bool keys
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext("Server=localhost;Database=catalog;Integrated Security=true;");
        var analyzer = Analyzer(context);
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        await using var connection = new CatalogStatementTestConnection(native, MetadataResults)
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CaptureAsync(analyzer, connection, null, Operations(65, keys), keys, cancellation.Token));

        // Assert
        Assert.Empty(Snapshot(analyzer, keys));
        Assert.Equal(native ? 1 : 0, connection.BatchesDisposed);
        Assert.Equal(1, connection.ReadersDisposed);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Final reads share one transport only after scope loading, and always precede owned cleanup.</summary>
    /// <param name="native">Whether the connection exposes native batching.</param>
    /// <param name="fault">The injected read failure.</param>
    [Theory]
    [InlineData(true, "none")]
    [InlineData(false, "none")]
    [InlineData(true, "reversed")]
    [InlineData(true, "cancelled")]
    [InlineData(false, "cancelled")]
    public async Task Inventory_FinalReadsRetainScopeAndCleanup(
        bool native,
        string fault
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext("Server=localhost;Database=catalog;Integrated Security=true;");
        var analyzer = Analyzer(context);
        using var cancellation = new CancellationTokenSource();
        await using var connection = new CatalogStatementTestConnection(native, InventoryResults)
        {
            ReverseResults = fault == "reversed",
            BeforeRead = fault == "cancelled" ? cancellation.Cancel : null,
        };

        await using var transaction = connection.BeginTransaction();
        var inventory = new SafeMigrationExpectedTableInventory("owned", "dbo", [], [], [], [], []);
        IReadOnlyList<SafeMigrationUnexpectedObject>? findings = null;

        // Act
        var failure = await Record.ExceptionAsync(async () => findings = await analyzer.ReadUnexpectedObjectsAsync(
            connection, transaction, [inventory], 71, cancellation.Token));

        // Assert
        if (fault == "none")
        {
            Assert.Null(failure);
            Assert.Equal(["unexpected_table", "unexpected_column"], findings!.Select(static item => item.Code));
        }
        else
        {
            if (fault == "cancelled")
            {
                Assert.IsAssignableFrom<OperationCanceledException>(failure);
            }
            else
            {
                Assert.IsType<InvalidOperationException>(failure);
            }

            Assert.Null(findings);
        }

        int[] expectedCounts = native ? [2] : [];

        Assert.Equal(expectedCounts, connection.BatchCounts);
        Assert.StartsWith("SELECT OBJECT_ID", connection.Submissions[0].Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE", connection.Submissions[1].Sql, StringComparison.Ordinal);
        Assert.StartsWith("INSERT INTO", connection.Submissions[2].Sql, StringComparison.Ordinal);
        Assert.Contains("DROP TABLE", connection.Submissions[^1].Sql, StringComparison.Ordinal);
        Assert.All(connection.Submissions, statement => Assert.Same(transaction, statement.Transaction));
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(0, connection.TransactionsDisposed);
    }

    private static Task CaptureAsync(
        SqlServerSafeMigrationProviderAnalyzer analyzer,
        CatalogStatementTestConnection connection,
        System.Data.Common.DbTransaction? transaction,
        SafeMigrationOperation[] operations,
        bool keys,
        CancellationToken token
    ) => keys ? analyzer.ReadProjectedKeySnapshotAsync(connection, transaction, operations, 71, token)
        : analyzer.ReadProjectedColumnLayoutsAsync(connection, transaction, operations, 71, token);

    private static SqlServerSafeMigrationProviderAnalyzer Analyzer(DbContext context)
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    private static System.Collections.IDictionary Snapshot(
        SqlServerSafeMigrationProviderAnalyzer analyzer,
        bool keys
    ) => (System.Collections.IDictionary)typeof(SqlServerSafeMigrationProviderAnalyzer)
        .GetField(keys ? "_projectedKeyTables" : "_projectedColumnLayouts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(analyzer)!;

    private static SafeMigrationOperation[] Operations(
        int count,
        bool keys
    ) => Enumerable.Range(0, count).Select(index => new SafeMigrationOperation(
        keys ? new EnsureIndexIntent(new ExpectedIndexDefinition("ix_id", $"tail_{index:D4}",
                [new ExpectedIndexKeyDefinition("id")]))
            : new EnsureColumnIntent($"tail_{index:D4}", new ExpectedColumnDefinition("id", typeof(int), true, "int")),
        SafeMigrationPolicy.ThrowIfDifferent)).ToArray();

    private static DataTable MetadataResults(
        string sql,
        IReadOnlyList<System.Data.Common.DbParameter> parameters
    )
    {
        Assert.Empty(parameters);
        if (sql.StartsWith("DECLARE @doka_key_tables", StringComparison.Ordinal))
        {
            var rows = Table(typeof(int), typeof(int), typeof(int), typeof(string), typeof(int),
                typeof(string), typeof(string));
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(sql,
                         @"INSERT @doka_key_tables VALUES \((\d+),[^\n]*, N'((?:[^']|'')*)', N'((?:[^']|'')*)'\);"))
            {
                rows.Rows.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), 1, -1, DBNull.Value, 1,
                    Unquote(match.Groups[2].Value), Unquote(match.Groups[3].Value));
            }

            return rows;
        }

        if (sql.StartsWith("SELECT requested.ordinal", StringComparison.Ordinal))
        {
            var rows = Table(typeof(int), typeof(string), typeof(string), typeof(int), typeof(bool), typeof(int),
                typeof(int), typeof(string), typeof(int));

            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(sql,
                         @"\((\d+), OBJECT_ID\(N'[^']*', N'U'\), N'((?:[^']|'')*)'\)"))
            {
                rows.Rows.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    Unquote(match.Groups[2].Value), "int", 4, true, 1, 1, DBNull.Value, 1);
            }

            return rows;
        }

        var binding = IsColumnBindingStatement(sql);
        var result = binding
            ? Table(typeof(string), typeof(string), typeof(string), typeof(int), typeof(int), typeof(bool))
            : Table(typeof(string), typeof(string), typeof(int), typeof(int), typeof(int),
                typeof(int), typeof(int), typeof(bool));

        // WHY: Type IN lists contain literal triples too, but only the FROM
        // VALUES region represents rows returned by the requested inventory.
        const string valuesMarker = "FROM (VALUES ";
        var valuesStart = sql.IndexOf(valuesMarker, StringComparison.Ordinal);
        Assert.True(valuesStart >= 0);
        valuesStart += valuesMarker.Length;
        var valuesEnd = sql.IndexOf(") AS requested", valuesStart, StringComparison.Ordinal);
        Assert.True(valuesEnd > valuesStart);
        var requestedValues = sql.Substring(valuesStart, valuesEnd - valuesStart);

        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     requestedValues,
                     binding ? @"\(N'((?:[^']|'')*)',N'((?:[^']|'')*)',N'((?:[^']|'')*)'\)"
                         : @"\(N'((?:[^']|'')*)',N'((?:[^']|'')*)'\)"))
        {
            if (binding)
            {
                result.Rows.Add(Unquote(match.Groups[1].Value), Unquote(match.Groups[2].Value),
                    Unquote(match.Groups[3].Value), 1, 0, false);
            }
            else
            {
                result.Rows.Add(Unquote(match.Groups[1].Value), Unquote(match.Groups[2].Value), 1, 4, 0, 0, 0, true);
            }
        }

        return result;
    }

    /// <summary>Identifies the requested column-binding statement separately from layout headers.</summary>
    private static bool IsColumnBindingStatement(string sql) => sql.StartsWith(
        "SELECT requested.[schema],requested.[table],requested.[name]", StringComparison.Ordinal);

    private static DataTable InventoryResults(
        string sql,
        IReadOnlyList<System.Data.Common.DbParameter> parameters
    )
    {
        Assert.Empty(parameters);
        var result = sql.StartsWith("SELECT 0", StringComparison.Ordinal)
            ? Table(typeof(int), typeof(string), typeof(string))
            : Table(typeof(int), typeof(int), typeof(string), typeof(string), typeof(string));

        if (result.Columns.Count == 3)
        {
            result.Rows.Add(0, "dbo", "extra_table");
        }
        else
        {
            result.Rows.Add(1, (int)SafeMigrationDatabaseObjectKind.Column, "dbo", "owned", "extra_column");
        }

        return result;
    }

    private static string Unquote(string value) => value.Replace("''", "'", StringComparison.Ordinal);

    private static DataTable Table(params Type[] columns)
    {
        var result = new DataTable();
        foreach (var column in columns)
        {
            result.Columns.Add("column" + result.Columns.Count, column);
        }

        return result;
    }
}
