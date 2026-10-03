namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies immutable 512-table MySQL physical and inventory statement transport contracts.</summary>
public sealed class MySqlCatalogTailBatchingTests
{
    /// <summary>Physical capture retains complete table chunks and returns only requested physical columns.</summary>
    /// <param name="native">Whether the connection exposes native batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PhysicalEnvironments_PackWholeChunksAndPreserveRequestedColumns(bool native)
    {
        // Arrange
        await using var connection = new CatalogStatementTestConnection(native, PhysicalResults);
        var operations = PhysicalOperations(4097);

        // Act
        var result = await MySqlSafeMigrationProviderAnalyzer.ReadIndexPhysicalEnvironmentsAsync(
            connection, operations, 4 * 1024 * 1024, 71, CancellationToken.None);

        // Assert
        Assert.Equal(4097, result.Count);
        Assert.All(result, pair => Assert.Equal(["id"], pair.Value.Columns!.Keys));
        Assert.Equal(10, connection.Submissions.Count);
        int[] expectedCounts = native ? [8, 1] : [];

        Assert.Equal(expectedCounts, connection.BatchCounts);
        Assert.All(connection.Submissions.Skip(1).Take(8), statement => Assert.Equal(512, statement.Parameters.Count));
        Assert.Single(connection.Submissions[^1].Parameters);
        Assert.All(connection.Submissions, statement => Assert.Equal(71, statement.Timeout));
        Assert.Equal(native ? 2 : 0, connection.BatchesDisposed);
        Assert.Equal(native ? 3 : 10, connection.ReadersDisposed);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Packet-qualified bounds split chunks without losing tables or parameter accounting.</summary>
    [Fact]
    public async Task PhysicalPayload_SplitsAtQualifiedPacketBudget()
    {
        // Arrange
        await using var connection = new CatalogStatementTestConnection(true, PhysicalResults);
        var operations = PhysicalOperations(1024, new string('x', 100));

        // Act
        var result = await MySqlSafeMigrationProviderAnalyzer.ReadIndexPhysicalEnvironmentsAsync(
            connection, operations, 90_000, 71, CancellationToken.None);

        // Assert
        Assert.Equal(1024, result.Count);
        Assert.Equal([1, 1], connection.BatchCounts);
        Assert.All(connection.Submissions.Skip(1), statement => Assert.InRange(
            Encoding.UTF8.GetByteCount(statement.Sql)
                + statement.Parameters.Sum(parameter => Encoding.UTF8.GetByteCount((string)parameter.Value!) + 32),
            1, 90_000));
    }

    /// <summary>Whole chunks reject evidence from another chunk and duplicate requested columns.</summary>
    /// <param name="fault">The injected ownership fault.</param>
    [Theory]
    [InlineData("reversed")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    public async Task PhysicalOwnership_RejectsMalformedEvidence(string fault)
    {
        // Arrange
        await using var connection = new CatalogStatementTestConnection(true, (sql, parameters) =>
        {
            var rows = PhysicalResults(sql, parameters);
            if (parameters.Count != 0)
            {
                if (fault == "duplicate")
                {
                    rows.Rows.Add(rows.Rows[0].ItemArray);
                }
                else if (fault == "foreign")
                {
                    rows.Rows[0][0] = "not_requested";
                }
            }

            return rows;
        })
        {
            ReverseResults = fault == "reversed",
        };

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MySqlSafeMigrationProviderAnalyzer.ReadIndexPhysicalEnvironmentsAsync(
                connection, PhysicalOperations(513), 4 * 1024 * 1024, 71, CancellationToken.None));

        // Assert
        Assert.Equal(1, connection.BatchesDisposed);
        Assert.Equal(2, connection.ReadersDisposed);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Cancellation after one physical row releases readers without returning a partial map.</summary>
    /// <param name="native">Whether the connection exposes native batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PhysicalCancellation_ReturnsNoPartialMap(bool native)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        await using var connection = new CatalogStatementTestConnection(native, PhysicalResults)
        {
            BeforeRead = () =>
            {
                if (++reads == 3)
                {
                    cancellation.Cancel();
                }
            },
        };

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MySqlSafeMigrationProviderAnalyzer.ReadIndexPhysicalEnvironmentsAsync(
                connection, PhysicalOperations(513), 4 * 1024 * 1024, 71, cancellation.Token));

        // Assert
        Assert.Equal(2, connection.ReadersDisposed);
        Assert.Equal(native ? 1 : 0, connection.BatchesDisposed);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Table and child inventory preserves ownership, ordinal case, and implicit JSON rules.</summary>
    /// <param name="native">Whether the connection exposes native batching.</param>
    /// <param name="mariaDb">Whether implicit JSON checks are MariaDB-generated.</param>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Inventory_PacksWholeScopesAndPreservesObjectRules(
        bool native,
        bool mariaDb
    )
    {
        // Arrange
        await using var connection = new CatalogStatementTestConnection(native, (sql, parameters) =>
            InventoryResults(sql, parameters, mariaDb));

        var expected = Inventory(4097);

        // Act
        var result = await MySqlSafeMigrationProviderAnalyzer.ReadUnexpectedInventoryAsync(
            connection, expected, mariaDb, 4 * 1024 * 1024, 71, CancellationToken.None);

        // Assert
        Assert.Equal(4098, result.Count);
        Assert.Equal("unexpected_table", result[0].Code);
        Assert.All(result.Skip(1), finding =>
        {
            Assert.Equal("unexpected_column", finding.Code);
            Assert.Equal("ID", finding.Name);
        });

        Assert.Equal(10, connection.Submissions.Count);
        int[] expectedCounts = native ? [8, 2] : [];

        Assert.Equal(expectedCounts, connection.BatchCounts);
        Assert.All(connection.Submissions.Skip(1).Take(8), statement => Assert.Equal(512, statement.Parameters.Count));
        Assert.Single(connection.Submissions[^1].Parameters);
        Assert.Equal(native ? 2 : 10, connection.ReadersDisposed);
    }

    /// <summary>Foreign chunk objects and reversed table/child results are not inventory evidence.</summary>
    /// <param name="native">Whether the connection exposes native batching.</param>
    /// <param name="reverse">Whether the native statement result order is reversed.</param>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task InventoryOwnership_RejectsUnsubmittedScopes(
        bool native,
        bool reverse
    )
    {
        // Arrange
        await using var connection = new CatalogStatementTestConnection(native, (sql, parameters) =>
        {
            var rows = InventoryResults(sql, parameters, false);
            if (!reverse && parameters.Count != 0)
            {
                rows.Rows[0][1] = "foreign_table";
            }

            return rows;
        })
        {
            ReverseResults = reverse,
        };

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MySqlSafeMigrationProviderAnalyzer.ReadUnexpectedInventoryAsync(
                connection, Inventory(513), false, 4 * 1024 * 1024, 71, CancellationToken.None));

        // Assert
        Assert.Equal(native ? 1 : 2, connection.ReadersDisposed);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Inventory packet splits preserve whole chunks and results after rebuilding a deferred slot.</summary>
    [Fact]
    public async Task InventoryPayload_SplitsWholeScopesAtPacketBudget()
    {
        // Arrange
        await using var connection = new CatalogStatementTestConnection(true,
            (sql, parameters) => InventoryResults(sql, parameters, false));

        var expected = Inventory(1024, new string('x', 100));

        // Act
        var result = await MySqlSafeMigrationProviderAnalyzer.ReadUnexpectedInventoryAsync(
            connection, expected, false, 150_000, 71, CancellationToken.None);

        // Assert
        Assert.Equal(1025, result.Count);
        Assert.Equal([2, 1], connection.BatchCounts);
        Assert.All(connection.Submissions.Skip(1), statement => Assert.Equal(512, statement.Parameters.Count));
        Assert.All(connection.Submissions, statement => Assert.InRange(
            Encoding.UTF8.GetByteCount(statement.Sql)
                + statement.Parameters.Sum(parameter => Encoding.UTF8.GetByteCount((string)parameter.Value!) + 32),
            1, 150_000));
    }

    /// <summary>Inventory cancellation after a row releases native and fallback transport ownership.</summary>
    /// <param name="native">Whether the connection exposes native batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InventoryCancellation_DisposesWithoutReturningEvidence(bool native)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        await using var connection = new CatalogStatementTestConnection(native,
            (sql, parameters) => InventoryResults(sql, parameters, false))
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
            MySqlSafeMigrationProviderAnalyzer.ReadUnexpectedInventoryAsync(
                connection, Inventory(513), false, 4 * 1024 * 1024, 71, cancellation.Token));

        // Assert
        Assert.Equal(1, connection.ReadersDisposed);
        Assert.Equal(native ? 1 : 0, connection.BatchesDisposed);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    private static SafeMigrationOperation[] PhysicalOperations(
        int count,
        string prefix = ""
    ) => Enumerable.Range(0, count).Select(index => new SafeMigrationOperation(
        new EnsureIndexIntent(new ExpectedIndexDefinition("ix_id", $"{prefix}tail_{index:D4}",
            [new ExpectedIndexKeyDefinition("id")])),
        SafeMigrationPolicy.ThrowIfDifferent)).ToArray();

    private static Dictionary<string, SafeMigrationExpectedTableInventory> Inventory(
        int count,
        string prefix = ""
    )
        => Enumerable.Range(0, count).Select(index => new SafeMigrationExpectedTableInventory(
            $"{prefix}tail_{index:D4}", null,
            [new KeyValuePair<string, string?>("id", "int"), new KeyValuePair<string, string?>("json_data", "json")],
            [], [], [], [])).ToDictionary(static table => table.Table, StringComparer.Ordinal);

    private static DataTable PhysicalResults(
        string sql,
        IReadOnlyList<DbParameter> parameters
    )
    {
        if (sql.StartsWith("SELECT @@default_storage_engine", StringComparison.Ordinal))
        {
            var defaults = Table(typeof(string), typeof(string), typeof(int));
            defaults.Rows.Add("InnoDB", "Dynamic", 16384);

            return defaults;
        }

        var result = Table(typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(long), typeof(long), typeof(int), typeof(int), typeof(int));

        foreach (var parameter in parameters)
        {
            result.Rows.Add(parameter.Value!, "InnoDB", "Dynamic", "id", "int", DBNull.Value,
                DBNull.Value, 10, 0, DBNull.Value);
            result.Rows.Add(parameter.Value!, "InnoDB", "Dynamic", "not_requested", "int", DBNull.Value,
                DBNull.Value, 10, 0, DBNull.Value);
        }

        return result;
    }

    private static DataTable InventoryResults(
        string sql,
        IReadOnlyList<DbParameter> parameters,
        bool mariaDb
    )
    {
        var result = Table(typeof(string), typeof(string), typeof(string), typeof(bool));
        if (sql.Contains("SELECT 'table'", StringComparison.Ordinal))
        {
            result.Rows.Add("table", "extra_table", "extra_table", false);
        }
        else
        {
            foreach (var parameter in parameters)
            {
                result.Rows.Add("column", parameter.Value!, "id", false);
                result.Rows.Add("column", parameter.Value!, "ID", false);
                if (mariaDb)
                {
                    result.Rows.Add("check_constraint", parameter.Value!, "json_data", true);
                }
            }
        }

        return result;
    }

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
