namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Validates native preliminary guard state/reason transport independently of a database server.</summary>
public sealed class PostgreSqlStateEvaluationGuardEvidenceTests
{
    /// <summary>Only the exact operation's bounded reason family may qualify an Unsupported state.</summary>
    /// <param name="native">Whether ADO.NET native batching is available.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactGuardReasonsRetainOrderedScope(bool native)
    {
        // Arrange
        await using var connection = new CatalogStatementTestConnection(native, static (_, _) => Rows());
        await using var batch = Batch(connection);
        SafeMigrationObservedState?[] states = [null, null];

        // Act
        var reasons = await PostgreSqlSafeMigrationProviderAnalyzer.ReadStateEvaluationGuardBatchAsync(
            batch, states, Requests(), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Unsupported, states[0]);
        Assert.Null(states[1]);
        Assert.Equal("captured_sql_identifier", Assert.Single(reasons!).Value);
        Assert.Equal(0, Assert.Single(reasons!).Key);
        Assert.Equal(1, connection.ReadersDisposed);
    }

    /// <summary>Malformed identity, state, reason, width or cardinality cannot publish authorization.</summary>
    /// <param name="fault">The adversarial transport invariant violation.</param>
    [Theory]
    [InlineData("ordinal")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("width")]
    [InlineData("state-type")]
    [InlineData("code-type")]
    [InlineData("unknown-code")]
    [InlineData("foreign-family")]
    [InlineData("null-code")]
    [InlineData("orphan-code")]
    [InlineData("code-state")]
    public async Task MalformedGuardEvidenceCannotReturnReasons(string fault)
    {
        // Arrange
        await using var connection = new CatalogStatementTestConnection(true, (_, _) =>
        {
            var rows = Rows();
            switch (fault)
            {
                case "ordinal": rows.Rows[0][0] = 999; break;
                case "missing": rows.Rows.RemoveAt(1); break;
                case "duplicate": rows.Rows.Add(rows.Rows[0].ItemArray); break;
                case "width": rows.Columns.Add("extra", typeof(object)); break;
                case "state-type": rows.Rows[0][1] = 1; break;
                case "code-type": rows.Rows[0][2] = 1; break;
                case "unknown-code": rows.Rows[0][2] = "unrecognized_guard"; break;
                case "foreign-family":
                    rows.Rows[1][1] = "unsupported";
                    rows.Rows[1][2] = "captured_sql_identifier";
                    break;
                case "null-code": rows.Rows[0][2] = DBNull.Value; break;
                case "orphan-code": rows.Rows[1][2] = "generated_case_operand_type"; break;
                case "code-state": rows.Rows[0][1] = "matching"; break;
            }

            return rows;
        });

        await using var batch = Batch(connection);
        SafeMigrationObservedState?[] states = [null, null];

        // Act
        var exception = await Record.ExceptionAsync(() =>
            PostgreSqlSafeMigrationProviderAnalyzer.ReadStateEvaluationGuardBatchAsync(
                batch, states, Requests(), CancellationToken.None));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(1, connection.ReadersDisposed);
    }

    /// <summary>Cancellation disposes native transport rather than exposing a partial capability map.</summary>
    [Fact]
    public async Task CancelledGuardEvidenceDisposesReader()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await using var connection = new CatalogStatementTestConnection(true, static (_, _) => Rows())
        {
            BeforeRead = cancellation.Cancel,
        };

        await using var batch = Batch(connection);
        SafeMigrationObservedState?[] states = [null, null];

        // Act
        var exception = await Record.ExceptionAsync(() =>
            PostgreSqlSafeMigrationProviderAnalyzer.ReadStateEvaluationGuardBatchAsync(
                batch, states, Requests(), cancellation.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(1, connection.ReadersDisposed);
    }

    /// <summary>Creates only a registered catalog transport; no external connection is opened.</summary>
    private static SafeMigrationCatalogBatch Batch(CatalogStatementTestConnection connection)
    {
        var batch = new SafeMigrationCatalogBatch(connection, 71);
        batch.CreateCommand().CommandText = "SELECT 0, 'unsupported', 'captured_sql_identifier';";

        return batch;
    }

    /// <summary>Uses two distinct capability families to expose cross-operation proof borrowing.</summary>
    private static PostgreSqlSafeMigrationProviderAnalyzer.StateEvaluationGuardRequest[] Requests()
        => [new(0, "structured_check_operand_type", true), new(1, "generated_case_operand_type", false)];

    /// <summary>Returns a legitimate source refusal followed by a guard pass with no attached reason.</summary>
    private static DataTable Rows()
    {
        var rows = new DataTable();
        rows.Columns.Add("ordinal", typeof(object));
        rows.Columns.Add("state", typeof(object));
        rows.Columns.Add("code", typeof(object));
        rows.Rows.Add(0, "unsupported", "captured_sql_identifier");
        rows.Rows.Add(1, DBNull.Value, DBNull.Value);

        return rows;
    }
}
