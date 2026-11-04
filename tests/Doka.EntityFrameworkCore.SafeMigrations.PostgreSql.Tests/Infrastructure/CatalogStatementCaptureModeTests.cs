namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Separates diagnostic retention from the deterministic benchmark transport's execution.</summary>
public sealed class CatalogStatementCaptureModeTests
{
    private static readonly int[] s_values = [17, 29];

    /// <summary>Repeated reads preserve results and cleanup without retaining disabled observations.</summary>
    /// <param name="native">Whether the connection uses native batches or sequential commands.</param>
    /// <param name="capture">Whether the transport retains diagnostic observations.</param>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task RepeatedReads_PreserveResultsAndResourceCleanup(bool native, bool capture)
    {
        // Arrange
        const int repetitions = 16;
        var readers = new List<DbDataReader>();
        var calls = new List<(string Sql, object? Value)>();
        await using var connection = new CatalogStatementTestConnection(native, (sql, parameters) =>
        {
            var value = parameters[0].Value;
            calls.Add((sql, value));
            var rows = new DataTable();
            rows.Columns.Add("value", typeof(int));
            rows.Rows.Add(value);

            return rows;
        }) { CaptureObservations = capture };

        var results = new List<int>();

        // Act
        for (var repetition = 0; repetition < repetitions; repetition++)
        {
            results.AddRange(await ReadValuesAsync(connection, native, readers,
                cancellationToken: CancellationToken.None));
        }

        // Assert
        Assert.Equal(Enumerable.Range(0, repetitions).SelectMany(static _ => s_values), results);
        Assert.Equal(repetitions, calls.Count(static call => call is ("SELECT @value;", 17)));
        Assert.Equal(repetitions, calls.Count(static call => call is ("SELECT @value;", 29)));
        Assert.Equal(capture ? repetitions * 2 : 0, connection.Submissions.Count);
        Assert.Equal(capture && native ? repetitions : 0, connection.BatchCounts.Count);
        Assert.Equal(capture && !native ? repetitions * 2 : 0, connection.CommandsDisposed);
        Assert.Equal(capture && native ? repetitions : 0, connection.BatchesDisposed);
        Assert.Equal(capture ? native ? repetitions : repetitions * 2 : 0, connection.ReadersDisposed);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.All(readers, static reader => Assert.True(reader.IsClosed));
    }

    /// <summary>The default transport retains observations for existing provider regression assertions.</summary>
    [Fact]
    public void ObservationCapture_IsEnabledByDefault()
    {
        // Arrange
        using var connection = new CatalogStatementTestConnection(false, static (_, _) => new DataTable());

        // Assert
        Assert.True(connection.CaptureObservations);
    }

    /// <summary>Scalar execution and real transaction disposal remain active with diagnostics disabled.</summary>
    /// <param name="capture">Whether diagnostic counters and SQL submissions are retained.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScalarAndTransaction_PreserveExecutionWithoutMandatoryRecording(bool capture)
    {
        // Arrange
        var scalarCalls = 0;
        using var connection = new CatalogStatementTestConnection(false, static (_, _) => new DataTable())
        {
            CaptureObservations = capture,
            Scalar = _ => ++scalarCalls,
        };

        object? result;

        // Act
        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT 1;";
            result = command.ExecuteScalar();
        }

        // Assert
        Assert.Equal(1, result);
        Assert.Equal(1, scalarCalls);
        Assert.Equal(capture ? 1 : 0, connection.Submissions.Count);
        Assert.Equal(capture ? 1 : 0, connection.TransactionsDisposed);
        Assert.Equal(capture ? 1 : 0, connection.CommandsDisposed);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Reads both parameter values through actual native or fallback reader ownership.</summary>
    /// <param name="connection">The deterministic transport under test.</param>
    /// <param name="native">Whether both statements share a native batch.</param>
    /// <param name="readers">The observed readers used to verify actual cleanup independently of counters.</param>
    /// <returns>The ordered row values from every result set.</returns>
    /// <param name="cancellationToken">Cancels the owned operation before and during asynchronous work.</param>
    private static async Task<List<int>> ReadValuesAsync(
        CatalogStatementTestConnection connection,
        bool native,
        List<DbDataReader> readers,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var results = new List<int>();
        if (native)
        {
            await using var batch = connection.CreateBatch();
            foreach (var value in s_values)
            {
                var command = batch.CreateBatchCommand();
                command.CommandText = "SELECT @value;";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@value";
                parameter.Value = value;
                command.Parameters.Add(parameter);
                batch.BatchCommands.Add(command);
            }

            await using var reader = await batch.ExecuteReaderAsync(cancellationToken: cancellationToken);
            readers.Add(reader);
            do
            {
                while (await reader.ReadAsync(cancellationToken: cancellationToken))
                {
                    results.Add(reader.GetInt32(0));
                }
            } while (await reader.NextResultAsync(cancellationToken: cancellationToken));
        }
        else
        {
            foreach (var value in s_values)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT @value;";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@value";
                parameter.Value = value;
                command.Parameters.Add(parameter);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken: cancellationToken);
                readers.Add(reader);
                while (await reader.ReadAsync(cancellationToken: cancellationToken))
                {
                    results.Add(reader.GetInt32(0));
                }
            }
        }

        return results;
    }
}
