namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Checks transport limits and result ownership for independent catalog probes.</summary>
public sealed class SafeMigrationCatalogProbeBatchTests
{
    /// <summary>Native and fallback transports preserve sparse caller ordinals and statement boundaries.</summary>
    /// <param name="native">Whether the test connection exposes a native batch.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProbeStatements_PackWithinBoundsAndPreserveOwnership(bool native)
    {
        // Arrange
        await using var connection = new CatalogProbeTestConnection(native);
        await using var transaction = connection.BeginTransaction();
        var values = new List<int>();

        // Act
        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection, 257, 4 * 1024 * 1024, 73, BuildStatement,
            (reader, offset, count, token) => ReadStatementAsync(reader, offset, count, values, token),
            CancellationToken.None, transaction);

        // Assert
        Assert.Equal(Enumerable.Range(0, 257).Select(index => (index * 2) + 501), values);
        Assert.Equal(native ? 2 : 0, connection.BatchExecutions);
        Assert.Equal(native ? 0 : 9, connection.CommandExecutions);
        Assert.Equal(9, connection.StatementCount);
        Assert.All(connection.BatchCounts, count => Assert.InRange(count, 1, 8));
        Assert.All(connection.Timeouts, timeout => Assert.Equal(73, timeout));
        Assert.All(connection.Transactions, actual => Assert.Same(transaction, actual));
        Assert.Equal(native ? 2 : 9, connection.Disposals);
        Assert.Equal(native ? 2 : 9, connection.ReaderDisposals);
        Assert.Equal(0, connection.ConnectionDisposals);
        Assert.Equal(0, connection.TransactionDisposals);
    }

    /// <summary>Aggregate payload limits split transport without losing individual probe results.</summary>
    /// <param name="native">Whether the test connection exposes native batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AggregatePayload_SplitsBeforeExceedingBudget(bool native)
    {
        // Arrange
        await using var connection = new CatalogProbeTestConnection(native);
        var values = new List<int>();

        // Act
        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection, 3, 100, null,
            (command, offset) => BuildStatement(command, offset, count: 1, parameterPayload: 60),
            (reader, offset, count, token) => ReadStatementAsync(reader, offset, count, values, token),
            CancellationToken.None);

        // Assert
        Assert.Equal([501, 503, 505], values);
        Assert.Equal(native ? 3 : 0, connection.BatchExecutions);
        Assert.Equal(native ? 0 : 3, connection.CommandExecutions);
        Assert.All(connection.BatchCounts, count => Assert.Equal(1, count));
    }

    /// <summary>Oversized SQL and parameter payloads are rejected before dispatch.</summary>
    /// <param name="parameterPayload">The synthetic parameter payload size.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task OversizedStatement_RejectsWithoutExecuting(int parameterPayload)
    {
        // Arrange
        await using var connection = new CatalogProbeTestConnection(native: true);

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SafeMigrationCatalogProbeBatch.ReadAsync(
                connection, 1, 10, null,
                (command, offset) =>
                {
                    var statement = BuildStatement(command, offset, count: 1, parameterPayload);
                    if (parameterPayload == 0)
                    {
                        command.CommandText = "             501";
                    }

                    return statement;
                },
                static (_, _, _, _) => Task.CompletedTask, CancellationToken.None));

        // Assert
        Assert.Contains("operation 501 exceeds a bounded query limit", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, connection.StatementCount);
        Assert.Equal(1, connection.Disposals);
    }

    /// <summary>Actual parameter cardinality is bounded per statement and across the native transport.</summary>
    /// <param name="parametersPerStatement">The synthetic parameter count in each probe statement.</param>
    [Theory]
    [InlineData(9000)]
    [InlineData(16001)]
    public async Task ParameterCardinality_BoundsBothStatementsAndBatches(int parametersPerStatement)
    {
        // Arrange
        await using var connection = new CatalogProbeTestConnection(native: true);
        var values = new List<int>();

        // Act
        var execution = SafeMigrationCatalogProbeBatch.ReadAsync(
            connection, 2, 4 * 1024 * 1024, null,
            (command, offset) =>
            {
                command.Parameters.AddRange(new object[parametersPerStatement]);

                return BuildStatement(command, offset, count: 1, parameterPayload: 0);
            },
            (reader, offset, count, token) => ReadStatementAsync(reader, offset, count, values, token),
            CancellationToken.None);
        var failure = await Record.ExceptionAsync(() => execution);

        // Assert
        if (parametersPerStatement > 16000)
        {
            Assert.IsType<InvalidOperationException>(failure);
            Assert.Equal(0, connection.StatementCount);
        }
        else
        {
            Assert.Null(failure);
            Assert.Equal([501, 503], values);
            Assert.Equal(2, connection.BatchExecutions);
            Assert.All(connection.BatchCounts, count => Assert.Equal(1, count));
        }
    }

    /// <summary>A provider cap splits immutable chunks without changing statement slots or the shared cap.</summary>
    /// <param name="native">Whether the test connection exposes native batching.</param>
    /// <param name="parameters">The number of parameters in each complete chunk.</param>
    [Theory]
    [InlineData(true, 512)]
    [InlineData(false, 512)]
    [InlineData(true, 2000)]
    [InlineData(false, 2000)]
    [InlineData(true, 2001)]
    [InlineData(false, 2001)]
    public async Task ProviderParameterCap_PreservesWholeChunksAndRejectsOversize(
        bool native,
        int parameters
    )
    {
        // Arrange
        await using var connection = new CatalogProbeTestConnection(native);
        var values = new List<int>();

        // Act
        var failure = await Record.ExceptionAsync(() => SafeMigrationCatalogProbeBatch.ReadAsync(
            connection, 9, 4 * 1024 * 1024, null,
            (command, offset) =>
            {
                command.Parameters.AddRange(new object[parameters]);

                return BuildStatement(command, offset, count: 1, parameterPayload: 0);
            },
            (reader, offset, count, token) => ReadStatementAsync(reader, offset, count, values, token),
            CancellationToken.None, maximumParameters: 2000));

        // Assert
        if (parameters > 2000)
        {
            Assert.IsType<InvalidOperationException>(failure);
            Assert.Equal(0, connection.StatementCount);
        }
        else
        {
            Assert.Null(failure);
            Assert.Equal(Enumerable.Range(0, 9).Select(index => (index * 2) + 501), values);
            Assert.Equal(native ? parameters == 512 ? 3 : 9 : 0, connection.BatchExecutions);
            Assert.Equal(native ? 0 : 9, connection.CommandExecutions);
            Assert.All(connection.BatchCounts, count => Assert.InRange(count * parameters, 1, 2000));
        }
    }

    /// <summary>A provider-specific cap can tighten but never expand the shared parameter budget.</summary>
    /// <param name="cap">The invalid caller-supplied parameter bound.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(16001)]
    public async Task InvalidProviderParameterCap_RejectsBeforeTransportCreation(int cap)
    {
        // Arrange
        await using var connection = new CatalogProbeTestConnection(native: true);

        // Act
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SafeMigrationCatalogProbeBatch.ReadAsync(
            connection, 1, 1024, null, BuildStatement,
            static (_, _, _, _) => Task.CompletedTask, CancellationToken.None, maximumParameters: cap));

        // Assert
        Assert.Equal(0, connection.Disposals);
        Assert.Equal(0, connection.StatementCount);
    }

    /// <summary>Callers cannot expand the shared hard payload bound.</summary>
    [Fact]
    public async Task ExcessivePayloadBound_RejectsBeforeTransportCreation()
    {
        // Arrange
        await using var connection = new CatalogProbeTestConnection(native: true);

        // Act
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SafeMigrationCatalogProbeBatch.ReadAsync(
            connection, 1, (4 * 1024 * 1024) + 1, null, BuildStatement,
            static (_, _, _, _) => Task.CompletedTask, CancellationToken.None));

        // Assert
        Assert.Equal(0, connection.Disposals);
        Assert.Equal(0, connection.StatementCount);
    }

    /// <summary>Absent, additional, empty, and reordered result sets cannot silently lose probe ownership.</summary>
    /// <param name="fault">The synthetic response corruption.</param>
    /// <param name="native">Whether the test connection exposes native batching.</param>
    [Theory]
    [InlineData("missing-set", true)]
    [InlineData("extra-set", true)]
    [InlineData("empty-set", true)]
    [InlineData("reversed", true)]
    [InlineData("extra-set", false)]
    [InlineData("empty-set", false)]
    [InlineData("reversed", false)]
    public async Task MalformedResults_RejectAndDispose(
        string fault,
        bool native
    )
    {
        // Arrange
        await using var connection = new CatalogProbeTestConnection(native) { Fault = fault };
        var consumedOffsets = new List<int>();

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SafeMigrationCatalogProbeBatch.ReadAsync(
                connection, 64, 4 * 1024 * 1024, null, BuildStatement,
                (reader, offset, count, token) =>
                {
                    consumedOffsets.Add(offset);

                    return ReadStatementAsync(reader, offset, count, [], token);
                },
                CancellationToken.None));

        // Assert
        Assert.Equal((fault, native) switch
        {
            ("missing-set", _) => "The catalog probe transport returned an inconsistent result-set count.",
            ("extra-set", true) => "The catalog probe transport returned an invalid result-set count.",
            ("reversed", _) => "Invalid probe result ordinal.",
            _ => "Missing probe result row.",
        }, failure.Message);
        int[] expectedOffsets = fault == "extra-set" ? [0, 32] : [0];

        Assert.Equal(expectedOffsets, consumedOffsets);
        Assert.Equal(native ? 1 : 0, connection.BatchExecutions);
        Assert.Equal(native ? 0 : 1, connection.CommandExecutions);
        Assert.Equal(native ? 1 : 2, connection.Disposals);
        Assert.Equal(1, connection.ReaderDisposals);
        Assert.Equal(0, connection.ConnectionDisposals);
    }

    /// <summary>Empty and already-cancelled phases never allocate a transport or dispatch a statement.</summary>
    /// <param name="cancelled">Whether the supplied cancellation token is cancelled.</param>
    /// <param name="count">The number of requested candidate slots.</param>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    public async Task EmptyOrCancelledPhase_PerformsNoDatabaseWork(
        bool cancelled,
        int count
    )
    {
        // Arrange
        await using var connection = new CatalogProbeTestConnection(native: true);
        using var cancellation = new CancellationTokenSource();
        if (cancelled)
        {
            cancellation.Cancel();
        }

        // Act
        var execution = SafeMigrationCatalogProbeBatch.ReadAsync(
            connection, count, 1024, null, BuildStatement,
            static (_, _, _, _) => Task.CompletedTask, cancellation.Token);
        if (cancelled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        }
        else
        {
            await execution;
        }

        // Assert
        Assert.Equal(0, connection.StatementCount);
        Assert.Equal(0, connection.BatchExecutions);
        Assert.Equal(0, connection.Disposals);
    }

    /// <summary>Cancellation during probe consumption disposes the native or fallback transport.</summary>
    /// <param name="native">Whether the test connection exposes native batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationDuringReading_DisposesTheTransport(bool native)
    {
        // Arrange
        await using var connection = new CatalogProbeTestConnection(native);
        await using var transaction = connection.BeginTransaction();
        using var cancellation = new CancellationTokenSource();
        var values = new List<int>();
        var consumedOffsets = new List<int>();

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SafeMigrationCatalogProbeBatch.ReadAsync(
            connection, 64, 1024 * 1024, null, BuildStatement,
            async (reader, offset, count, token) =>
            {
                consumedOffsets.Add(offset);
                await ReadStatementAsync(reader, offset, count, values, token);
                cancellation.Cancel();
            },
            cancellation.Token, transaction));

        // Assert
        Assert.Equal(native ? 1 : 2, connection.Disposals);
        Assert.Equal(native ? 1 : 0, connection.BatchExecutions);
        Assert.Equal(native ? 0 : 1, connection.CommandExecutions);
        Assert.Equal([0], consumedOffsets);
        Assert.Equal(Enumerable.Range(0, 32).Select(index => (index * 2) + 501), values);
        Assert.Equal(1, connection.ReaderDisposals);
        Assert.Equal(0, connection.ConnectionDisposals);
        Assert.Equal(0, connection.TransactionDisposals);
    }

    /// <summary>An invalid builder contract cannot bypass per-statement bounds or advance result ownership.</summary>
    /// <param name="count">The invalid candidate count produced by the builder.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(33)]
    [InlineData(2)]
    public async Task InvalidBuilderCount_RejectsBeforeExecution(int count)
    {
        // Arrange
        await using var connection = new CatalogProbeTestConnection(native: true);

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SafeMigrationCatalogProbeBatch.ReadAsync(
                connection, 1, 1024, null,
                (command, _) =>
                {
                    command.CommandText = "501";

                    return new SafeMigrationCatalogProbeStatement(count, ParameterPayloadBytes: 0, FirstOrdinal: 501);
                },
                static (_, _, _, _) => Task.CompletedTask, CancellationToken.None));

        // Assert
        Assert.Contains("invalid statement", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, connection.StatementCount);
        Assert.Equal(1, connection.Disposals);
    }

    private static SafeMigrationCatalogProbeStatement BuildStatement(
        SafeMigrationCatalogCommand command,
        int offset
    ) => BuildStatement(command, offset, Math.Min(32, 257 - offset), parameterPayload: 0);

    private static SafeMigrationCatalogProbeStatement BuildStatement(
        SafeMigrationCatalogCommand command,
        int offset,
        int count,
        int parameterPayload
    )
    {
        command.CommandText = string.Join(",", Enumerable.Range(offset, count).Select(index => (index * 2) + 501));

        return new SafeMigrationCatalogProbeStatement(count, parameterPayload, (offset * 2) + 501);
    }

    private static async Task ReadStatementAsync(
        System.Data.Common.DbDataReader reader,
        int offset,
        int count,
        List<int> values,
        CancellationToken token
    )
    {
        var row = 0;
        while (await reader.ReadAsync(token))
        {
            if (row >= count || reader.GetInt32(0) != ((offset + row) * 2) + 501)
            {
                throw new InvalidOperationException("Invalid probe result ordinal.");
            }

            values.Add(reader.GetInt32(0));
            row++;
        }

        if (row != count)
        {
            throw new InvalidOperationException("Missing probe result row.");
        }
    }
}
