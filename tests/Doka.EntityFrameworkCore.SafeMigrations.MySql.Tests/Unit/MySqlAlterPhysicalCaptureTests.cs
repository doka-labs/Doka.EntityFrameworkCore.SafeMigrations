namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies bounded immutable physical capture and its fail-closed transport boundary.</summary>
public sealed class MySqlAlterPhysicalCaptureTests
{
    /// <summary>Wide operation streams capture each table once with separate column/index sets.</summary>
    /// <param name="native">Whether the test transport exposes native batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CaptureBatchesTablesAndRetainsCommandTimeout(bool native)
    {
        await using var connection = new CatalogStatementTestConnection(native, Results);
        var operations = Operations(513);
        var environments = Environments(operations);

        var result = await MySqlSafeMigrationProviderAnalyzer.ReadProjectedAlterPhysicalSnapshotsAsync(
            connection, operations, environments, 4 * 1024 * 1024, 71, CancellationToken.None);

        Assert.Equal(513, result.Count);
        Assert.Equal(4, connection.Submissions.Count);
        Assert.Equal([512, 512, 1, 1], connection.Submissions.Select(static statement => statement.Parameters.Count));
        Assert.All(connection.Submissions, statement => Assert.Equal(71, statement.Timeout));
        Assert.Equal(native ? [4] : Array.Empty<int>(), connection.BatchCounts);
        Assert.Equal(native ? 1 : 4, connection.ReadersDisposed);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Repeated transitions on one table reuse its physical catalog capture.</summary>
    [Fact]
    public async Task RepeatedTableTransitionsDoNotAddCaptureStatements()
    {
        await using var connection = new CatalogStatementTestConnection(true, Results);
        var operation = Operations(1)[0];
        var operations = Enumerable.Repeat(operation, 1000).ToArray();

        var result = await MySqlSafeMigrationProviderAnalyzer.ReadProjectedAlterPhysicalSnapshotsAsync(
            connection, operations, Environments(operations), 4 * 1024 * 1024, 71, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(2, connection.Submissions.Count);
        Assert.All(connection.Submissions, statement => Assert.Single(statement.Parameters));
    }

    /// <summary>Streams without a type transition retain the zero-query fast path.</summary>
    [Fact]
    public async Task OrdinaryOperationsDoNotCaptureAlterShapes()
    {
        await using var connection = new CatalogStatementTestConnection(true, Results);
        SafeMigrationOperation[] operations =
        [
            new(new EnsureColumnIntent("records", Column("varchar(80)")), SafeMigrationPolicy.ThrowIfDifferent),
        ];

        var result = await MySqlSafeMigrationProviderAnalyzer.ReadProjectedAlterPhysicalSnapshotsAsync(
            connection, operations, new Dictionary<string, SafeMigrationIndexPhysicalEnvironment>(),
            4 * 1024 * 1024, 71, CancellationToken.None);

        Assert.Empty(result);
        Assert.Empty(connection.Submissions);
    }

    /// <summary>Duplicate, foreign and misordered physical metadata never becomes reusable evidence.</summary>
    /// <param name="fault">The injected catalog ownership violation.</param>
    [Theory]
    [InlineData("foreign_column")]
    [InlineData("duplicate_column")]
    [InlineData("foreign_index")]
    [InlineData("index_ordinal")]
    public async Task MalformedCatalogEvidenceIsRejected(string fault)
    {
        await using var connection = new CatalogStatementTestConnection(true, (sql, parameters) =>
        {
            var result = Results(sql, parameters);
            var columns = sql.StartsWith("SELECT c.TABLE_NAME", StringComparison.Ordinal);
            if (columns
                && fault == "foreign_column"
                || !columns
                && fault == "foreign_index")
            {
                result.Rows[0][0] = "unowned_table";
            }
            else if (columns
                && fault == "duplicate_column")
            {
                result.Rows.Add(result.Rows[0].ItemArray);
            }
            else if (!columns
                && fault == "index_ordinal")
            {
                result.Rows[0][3] = 2;
            }

            return result;
        });

        var operations = Operations(1);

        var exception = await Record.ExceptionAsync(() =>
            MySqlSafeMigrationProviderAnalyzer.ReadProjectedAlterPhysicalSnapshotsAsync(
                connection, operations, Environments(operations), 4 * 1024 * 1024, 71, CancellationToken.None));

        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(1, connection.ReadersDisposed);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Cancellation discards partial evidence and releases reader ownership.</summary>
    [Fact]
    public async Task CancellationDoesNotReturnPartiallyCapturedTables()
    {
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        await using var connection = new CatalogStatementTestConnection(true, Results)
        {
            BeforeRead = () =>
            {
                if (++reads == 2)
                {
                    cancellation.Cancel();
                }
            },
        };

        var operations = Operations(2);

        var exception = await Record.ExceptionAsync(() =>
            MySqlSafeMigrationProviderAnalyzer.ReadProjectedAlterPhysicalSnapshotsAsync(
                connection, operations, Environments(operations), 4 * 1024 * 1024, 71, cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(1, connection.ReadersDisposed);
        Assert.Equal(1, connection.BatchesDisposed);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    private static SafeMigrationOperation[] Operations(int count) => Enumerable.Range(0, count)
        .Select(index => new SafeMigrationOperation(new AlterColumnIntent(
            $"physical_{index:D4}", Column("varchar(80)"), Column("longtext")), SafeMigrationPolicy.RepairIfSafe))
        .ToArray();

    private static Dictionary<string, SafeMigrationIndexPhysicalEnvironment> Environments(
        IEnumerable<SafeMigrationOperation> operations
    ) => operations.Select(static operation => ((AlterColumnIntent)operation.Intent).Table)
        .Distinct(StringComparer.Ordinal)
        .ToDictionary(static table => table, static _ => new SafeMigrationIndexPhysicalEnvironment(3072,
                StorageRowFormat: "Dynamic", StoragePageSize: 16384),
            StringComparer.Ordinal);

    private static ExpectedColumnDefinition Column(string type) => new(
        "value", typeof(string), isNullable: false, storeType: type);

    private static DataTable Results(
        string sql,
        IReadOnlyList<DbParameter> parameters
    )
    {
        var columns = sql.StartsWith("SELECT c.TABLE_NAME", StringComparison.Ordinal);
        var types = columns
            ? new[]
            {
                typeof(string), typeof(string), typeof(string), typeof(string), typeof(long),
                typeof(long), typeof(int), typeof(int), typeof(int), typeof(int), typeof(string),
                typeof(string), typeof(bool),
            }
            : [typeof(string), typeof(string), typeof(string), typeof(int), typeof(string), typeof(int), typeof(int)];

        var result = new DataTable { Locale = CultureInfo.InvariantCulture };
        for (var index = 0; index < types.Length; index++)
        {
            result.Columns.Add($"c{index.ToString(CultureInfo.InvariantCulture)}", types[index]);
        }

        foreach (var parameter in parameters)
        {
            if (columns)
            {
                result.Rows.Add(parameter.Value, "value", "longtext", "NO", uint.MaxValue,
                    uint.MaxValue, DBNull.Value, DBNull.Value, DBNull.Value, 4, "utf8mb4_unicode_ci", "utf8mb4", false);
            }
            else
            {
                result.Rows.Add(parameter.Value, "ix_value", "BTREE", 1, "value", 16, 1);
            }
        }

        return result;
    }
}
