namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Checks immutable PostgreSQL inventory scopes without reducing the 512-table statements.</summary>
public sealed class PostgreSqlCatalogTailBatchingTests
{
    /// <summary>Explicit and default schemas pack complete inventory scopes without mixing statement results.</summary>
    /// <param name="native">Whether the connection exposes native batching.</param>
    /// <param name="distinctSchemas">Whether each expected table belongs to its own schema.</param>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task InventoryScopes_PreserveWholeChunksAndExactOwners(
        bool native,
        bool distinctSchemas
    )
    {
        // Arrange
        var expected = Inventory(4097, distinctSchemas);
        await using var connection = new CatalogStatementTestConnection(native,
            (sql, parameters) => Results(sql, parameters, distinctSchemas));

        // Act
        var findings = await PostgreSqlSafeMigrationProviderAnalyzer.ReadUnexpectedInventoryAsync(
            connection, expected, 71, CancellationToken.None);

        // Assert
        Assert.Equal(distinctSchemas ? 8194 : 4098, findings.Count);
        Assert.Equal(4097, findings.Count(static finding => finding.Code == "unexpected_column"));
        Assert.All(findings.Where(static finding => finding.Code == "unexpected_column"),
            finding => Assert.Equal("ID", finding.Name));
        Assert.Equal(distinctSchemas ? 18 : 10, connection.Submissions.Count);
        int[] expectedCounts = native ? distinctSchemas ? [8, 8, 2] : [8, 2] : [];

        Assert.Equal(expectedCounts, connection.BatchCounts);
        Assert.All(connection.Submissions, statement =>
        {
            Assert.Equal(71, statement.Timeout);
            Assert.Equal(native, statement.Native);
            Assert.InRange(statement.Parameters.Count, 0, distinctSchemas ? 1024 : 512);
            Assert.InRange(Encoding.UTF8.GetByteCount(statement.Sql), 1, 4 * 1024 * 1024);
        });

        Assert.Equal(native ? distinctSchemas ? 3 : 2 : distinctSchemas ? 18 : 10, connection.ReadersDisposed);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Reversed sets, foreign identities, and altered case cannot authorize inventory evidence.</summary>
    /// <param name="native">Whether the connection exposes native batching.</param>
    /// <param name="fault">The injected ownership fault.</param>
    [Theory]
    [InlineData(true, "reverse")]
    [InlineData(true, "schema")]
    [InlineData(false, "schema")]
    [InlineData(true, "table")]
    [InlineData(false, "table")]
    [InlineData(true, "case")]
    [InlineData(false, "case")]
    public async Task UnownedInventory_RejectsAndDisposes(
        bool native,
        string fault
    )
    {
        // Arrange
        await using var connection = new CatalogStatementTestConnection(native, (sql, parameters) =>
        {
            var rows = Results(sql, parameters, false);
            if (!sql.Contains("SELECT 'table'", StringComparison.Ordinal))
            {
                if (fault == "schema")
                {
                    rows.Rows[0][1] = "other_schema";
                }
                else if (fault is "table" or "case")
                {
                    rows.Rows[0][2] = fault == "case" ? "TAIL_0000" : "foreign_table";
                }
            }

            return rows;
        })
        {
            ReverseResults = fault == "reverse",
        };

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgreSqlSafeMigrationProviderAnalyzer.ReadUnexpectedInventoryAsync(
                connection, Inventory(513, false), 71, CancellationToken.None));

        // Assert
        Assert.Equal(native ? 1 : 2, connection.ReadersDisposed);
        Assert.Equal(native ? 1 : 0, connection.BatchesDisposed);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Cancelled inventory never returns a partially populated finding list.</summary>
    /// <param name="native">Whether the connection exposes native batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledInventory_ReturnsNoPartialEvidence(bool native)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        await using var connection = new CatalogStatementTestConnection(native,
            (sql, parameters) => Results(sql, parameters, false))
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
            PostgreSqlSafeMigrationProviderAnalyzer.ReadUnexpectedInventoryAsync(
                connection, Inventory(513, false), 71, cancellation.Token));

        // Assert
        Assert.Equal(1, connection.ReadersDisposed);
        Assert.Equal(native ? 1 : 0, connection.BatchesDisposed);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>A larger-than-budget catalog binding is rejected without submitting any child statement.</summary>
    [Fact]
    public async Task OversizedInventoryPayload_RejectsBeforeDispatch()
    {
        // Arrange
        await using var connection = new CatalogStatementTestConnection(true,
            (sql, parameters) => Results(sql, parameters, false));

        var expected = new[] { new SafeMigrationExpectedTableInventory(
            new string('x', 4 * 1024 * 1024), null, [], [], [], [], []) };

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgreSqlSafeMigrationProviderAnalyzer.ReadUnexpectedInventoryAsync(
                connection, expected, 71, CancellationToken.None));

        // Assert
        Assert.Empty(connection.Submissions);
        Assert.Equal(1, connection.BatchesDisposed);
    }

    private static SafeMigrationExpectedTableInventory[] Inventory(
        int count,
        bool distinctSchemas
    ) => Enumerable.Range(0, count).Select(index => new SafeMigrationExpectedTableInventory(
        $"tail_{index:D4}", distinctSchemas ? $"schema_{index:D4}" : null,
        [new KeyValuePair<string, string?>("id", "integer")], [], [], [], [])).ToArray();

    private static DataTable Results(
        string sql,
        IReadOnlyList<DbParameter> parameters,
        bool distinctSchemas
    )
    {
        var result = new DataTable();
        for (var index = 0; index < 5; index++)
        {
            result.Columns.Add("column" + index, typeof(string));
        }

        if (sql.Contains("SELECT 'table'", StringComparison.Ordinal))
        {
            if (parameters.Count == 0)
            {
                result.Rows.Add("table", "public", "extra_table", "extra_table", "public");
            }
            else
            {
                foreach (var parameter in parameters)
                {
                    result.Rows.Add("table", parameter.Value!, "extra_table", "extra_table", "public");
                }
            }
        }
        else
        {
            foreach (var name in parameters.Select(static parameter => (string)parameter.Value!)
                         .Where(static name => name.StartsWith("tail_", StringComparison.Ordinal)))
            {
                var schema = distinctSchemas ? string.Concat("schema_", name.AsSpan(5)) : "public";
                result.Rows.Add("column", schema, name, "id", "public");
                result.Rows.Add("column", schema, name, "ID", "public");
            }
        }

        return result;
    }
}
