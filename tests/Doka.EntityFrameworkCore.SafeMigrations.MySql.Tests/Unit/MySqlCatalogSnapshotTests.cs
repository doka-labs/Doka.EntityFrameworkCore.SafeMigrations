namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies ownership, cleanup, and capability fallback for temporary catalog snapshots.</summary>
public sealed partial class MySqlCatalogSnapshotTests
{
    /// <summary>A pooled session must reset before reuse to be eligible for temporary catalog copies.</summary>
    [Fact]
    public async Task PooledSessionWithoutResetRetainsLiveCatalogWithoutCreatingTemporaryTables()
    {
        await using var connection = new SnapshotConnection { ConnectionString = "Pooling=true;ConnectionReset=false" };

        var snapshot = await MySqlCatalogSnapshot.TryCreateAsync(connection, null, CancellationToken.None);

        Assert.Null(snapshot);
        Assert.Empty(connection.Commands);
        Assert.Empty(connection.Tables);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>A nonpooled session is eligible because closing it destroys its temporary objects.</summary>
    [Fact]
    public async Task NonPooledSessionDoesNotRequireConnectionReset()
    {
        await using var connection = new SnapshotConnection { ConnectionString = "Pooling=false;ConnectionReset=false" };

        await using var snapshot = await MySqlCatalogSnapshot.TryCreateAsync(connection, null, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(9, connection.Commands.Count);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Concurrent snapshot lifetimes use isolated names and an explicit TEXT-capable storage engine.</summary>
    [Fact]
    public async Task CreationUsesUniqueBoundedNamesAndNeverDropsCallerTables()
    {
        await using var connection = new SnapshotConnection();
        connection.Tables.Add("__doka_sm_cat_columns");

        await using var first = Assert.IsType<MySqlCatalogSnapshot>(
            await MySqlCatalogSnapshot.TryCreateAsync(connection, 43, CancellationToken.None));

        await using var second = Assert.IsType<MySqlCatalogSnapshot>(
            await MySqlCatalogSnapshot.TryCreateAsync(connection, 43, CancellationToken.None));

        Assert.Equal(19, connection.Tables.Count);
        Assert.NotEqual(first.Resolve("INFORMATION_SCHEMA.COLUMNS"), second.Resolve("INFORMATION_SCHEMA.COLUMNS"));
        Assert.All(connection.Tables, table => Assert.InRange(table.Length, 1, 64));
        Assert.All(connection.Commands, command =>
        {
            Assert.StartsWith("CREATE TEMPORARY TABLE ", command.Sql, StringComparison.Ordinal);
            Assert.Contains(") ENGINE=InnoDB AS SELECT * FROM INFORMATION_SCHEMA.", command.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain("ALTER TABLE", command.Sql, StringComparison.Ordinal);
            Assert.Equal(43, command.Timeout);
        });
    }

    /// <summary>Incoming foreign keys remain visible when their dependent table belongs to another database.</summary>
    [Fact]
    public async Task SnapshotIncludesCrossDatabaseIncomingForeignKeys()
    {
        await using var connection = new SnapshotConnection();

        await using var snapshot = await MySqlCatalogSnapshot.TryCreateAsync(connection, null, CancellationToken.None);

        var command = Assert.Single(connection.Commands, command =>
            command.Sql.Contains("FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE", StringComparison.Ordinal));

        Assert.Contains("CONSTRAINT_SCHEMA = DATABASE() OR REFERENCED_TABLE_SCHEMA = DATABASE()", command.Sql);
        Assert.Contains("KEY `by_principal` (`REFERENCED_TABLE_SCHEMA`, `REFERENCED_TABLE_NAME`", command.Sql);
    }

    /// <summary>Repeated disposal issues one cleanup command containing only successfully created tables.</summary>
    [Fact]
    public async Task DisposalDropsOnlyOwnedTablesInOneCommandAndIsIdempotent()
    {
        await using var connection = new SnapshotConnection();
        connection.Tables.Add("caller_table");
        var snapshot = Assert.IsType<MySqlCatalogSnapshot>(
            await MySqlCatalogSnapshot.TryCreateAsync(connection, 31, CancellationToken.None));

        await snapshot.DisposeAsync();
        await snapshot.DisposeAsync();

        Assert.Equal(["caller_table"], connection.Tables);
        var cleanup = Assert.Single(connection.Commands, command =>
            command.Sql.StartsWith("DROP TEMPORARY TABLE", StringComparison.Ordinal));

        Assert.Equal(31, cleanup.Timeout);
        Assert.False(cleanup.Token.CanBeCanceled);
        Assert.DoesNotContain("caller_table", cleanup.Sql, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>A failed CREATE cleans earlier owned tables and retains the original failure.</summary>
    [Fact]
    public async Task PartialCreationFailureCleansOnlyAcknowledgedTablesAndPreservesOriginalException()
    {
        await using var connection = new SnapshotConnection();
        var failure = new InvalidOperationException("Catalog projection failed.");
        connection.Failure = (ordinal, _) => ordinal == 4 ? failure : null;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MySqlCatalogSnapshot.TryCreateAsync(connection, null, CancellationToken.None));

        Assert.Same(failure, actual);
        Assert.Empty(connection.Tables);
        Assert.Equal(5, connection.Commands.Count);
        Assert.Equal(3, connection.Commands[^1].Sql.Count(character => character == ',') + 1);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Ownership survives a command-disposal failure that follows successful server acknowledgement.</summary>
    [Fact]
    public async Task AcknowledgedCreationIsCleanedWhenCommandDisposalFails()
    {
        await using var connection = new SnapshotConnection();
        var failure = new InvalidOperationException("Command disposal failed.");
        connection.DisposalFailure = sql => sql.StartsWith("CREATE TEMPORARY TABLE ", StringComparison.Ordinal)
            ? failure
            : null;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MySqlCatalogSnapshot.TryCreateAsync(connection, null, CancellationToken.None));

        Assert.Same(failure, actual);
        Assert.Empty(connection.Tables);
        Assert.Equal(2, connection.Commands.Count);
        Assert.StartsWith("DROP TEMPORARY TABLE IF EXISTS ", connection.Commands[1].Sql, StringComparison.Ordinal);
        Assert.Contains(SnapshotConnection.ReadTableName(connection.Commands[0].Sql), connection.Commands[1].Sql);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>A command wrapper cannot replace the server execution error with its own disposal error.</summary>
    /// <param name="capabilityFailure">Whether the execution error would otherwise permit live-catalog fallback.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutionFailureRemainsPrimaryWhenCommandDisposalAlsoFails(
        bool capabilityFailure
    )
    {
        await using var connection = new SnapshotConnection();
        Exception executionFailure = capabilityFailure
            ? CreateServerException(MySqlErrorCode.DatabaseAccessDenied)
            : new InvalidOperationException("Execution failed.");

        var disposalFailure = new InvalidOperationException("Command disposal failed.");
        connection.Failure = (_, _) => executionFailure;
        connection.DisposalFailure = _ => disposalFailure;

        var actual = await Assert.ThrowsAnyAsync<Exception>(() =>
            MySqlCatalogSnapshot.TryCreateAsync(connection, null, CancellationToken.None));

        Assert.Same(executionFailure, actual);
        Assert.Same(disposalFailure, actual.Data["SafeMigrations.CatalogSnapshotCommandCleanupException"]);
        Assert.Single(connection.Commands);
        Assert.Empty(connection.Tables);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>A colliding CREATE does not make the preexisting target an owned cleanup object.</summary>
    [Fact]
    public async Task TableCollisionDoesNotDeleteTheUnownedTarget()
    {
        await using var connection = new SnapshotConnection();
        string? callerTable = null;
        connection.BeforeExecute = (ordinal, sql) =>
        {
            if (ordinal == 3)
            {
                callerTable = SnapshotConnection.ReadTableName(sql);
                connection.Tables.Add(callerTable);
            }
        };

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            MySqlCatalogSnapshot.TryCreateAsync(connection, null, CancellationToken.None));

        Assert.Equal(MySqlErrorCode.TableExists, exception.ErrorCode);
        Assert.Equal(callerTable, Assert.Single(connection.Tables));
        Assert.DoesNotContain(callerTable!, connection.Commands[^1].Sql, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Known optional-capability failures use live catalog analysis only after successful cleanup.</summary>
    /// <param name="code">The server error identifying an unavailable snapshot capability.</param>
    /// <param name="failedCommand">The one-based CREATE command that fails.</param>
    [Theory]
    [InlineData(MySqlErrorCode.DatabaseAccessDenied, 1)]
    [InlineData(MySqlErrorCode.TableAccessDenied, 1)]
    [InlineData(MySqlErrorCode.CannotExecuteInReadOnlyTransaction, 1)]
    [InlineData(MySqlErrorCode.UnknownStorageEngine, 1)]
    [InlineData(MySqlErrorCode.DatabaseAccessDenied, 4)]
    [InlineData(MySqlErrorCode.CannotExecuteInReadOnlyTransaction, 4)]
    [InlineData(MySqlErrorCode.UnknownStorageEngine, 4)]
    public async Task CreationFallsBackOnlyAfterPermittedCapabilityFailureHasBeenCleaned(
        MySqlErrorCode code,
        int failedCommand
    )
    {
        await using var connection = new SnapshotConnection();
        connection.Failure = (ordinal, _) => ordinal == failedCommand ? CreateServerException(code) : null;

        var snapshot = await MySqlCatalogSnapshot.TryCreateAsync(connection, null, CancellationToken.None);

        Assert.Null(snapshot);
        Assert.Empty(connection.Tables);
        Assert.Equal(failedCommand + (failedCommand > 1 ? 1 : 0), connection.Commands.Count);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Unrelated SQL, authentication, and timeout failures are never converted into fallback.</summary>
    /// <param name="code">The server error that must propagate unchanged.</param>
    [Theory]
    [InlineData(MySqlErrorCode.ParseError)]
    [InlineData(MySqlErrorCode.CommandTimeoutExpired)]
    [InlineData(MySqlErrorCode.AccessDenied)]
    [InlineData(MySqlErrorCode.NoDatabaseSelected)]
    public async Task CreationDoesNotFallbackForUnrelatedFailures(
        MySqlErrorCode code
    )
    {
        await using var connection = new SnapshotConnection();
        var failure = CreateServerException(code);
        connection.Failure = (ordinal, _) => ordinal == 2 ? failure : null;

        var actual = await Assert.ThrowsAsync<MySqlException>(() =>
            MySqlCatalogSnapshot.TryCreateAsync(connection, null, CancellationToken.None));

        Assert.Same(failure, actual);
        Assert.Empty(connection.Tables);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Cancellation preserves its token while cleanup uses an uncancelled token.</summary>
    [Fact]
    public async Task CancellationCleansPreviousTablesWithoutUsingTheCancelledToken()
    {
        await using var connection = new SnapshotConnection();
        using var cancellation = new CancellationTokenSource();
        connection.BeforeExecute = (ordinal, _) =>
        {
            if (ordinal == 4)
            {
                cancellation.Cancel();
            }
        };

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MySqlCatalogSnapshot.TryCreateAsync(connection, null, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(connection.Tables);
        Assert.False(connection.Commands[^1].Token.CanBeCanceled);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Failed cleanup prevents capability fallback and retains both original and cleanup errors.</summary>
    [Fact]
    public async Task CreationDoesNotFallbackWhenCapabilityFailureCleanupFails()
    {
        await using var connection = new SnapshotConnection();
        var failure = CreateServerException(MySqlErrorCode.DatabaseAccessDenied);
        var cleanupFailure = new InvalidOperationException("Drop failed.");
        connection.Failure = (ordinal, _) => ordinal switch
        {
            3 => failure,
            4 => cleanupFailure,
            _ => null,
        };

        var actual = await Assert.ThrowsAsync<MySqlException>(() =>
            MySqlCatalogSnapshot.TryCreateAsync(connection, null, CancellationToken.None));

        Assert.Same(failure, actual);
        Assert.Same(cleanupFailure, actual.Data["SafeMigrations.CatalogSnapshotCleanupException"]);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Empty(connection.Tables);
    }

    /// <summary>Analysis failures remain primary when cleanup also fails and the session must close.</summary>
    [Fact]
    public async Task CleanupAfterAnalysisFailurePreservesThePrimaryExceptionAndInvalidatesTheSession()
    {
        await using var connection = new SnapshotConnection();
        var snapshot = Assert.IsType<MySqlCatalogSnapshot>(
            await MySqlCatalogSnapshot.TryCreateAsync(connection, null, CancellationToken.None));

        var primary = new OperationCanceledException("Analysis cancelled.");
        var cleanupFailure = new InvalidOperationException("Drop failed.");
        connection.Failure = (_, _) => cleanupFailure;

        var cleaned = await snapshot.DisposeAfterFailureAsync(primary);
        await snapshot.DisposeAsync();

        Assert.False(cleaned);
        Assert.Same(cleanupFailure, primary.Data["SafeMigrations.CatalogSnapshotCleanupException"]);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Empty(connection.Tables);
        Assert.Equal(10, connection.Commands.Count);
    }

    /// <summary>Cleanup failure after successful analysis is observable rather than silently ignored.</summary>
    [Fact]
    public async Task NormalDisposalReportsCleanupFailureAndInvalidatesTheSession()
    {
        await using var connection = new SnapshotConnection();
        var snapshot = Assert.IsType<MySqlCatalogSnapshot>(
            await MySqlCatalogSnapshot.TryCreateAsync(connection, null, CancellationToken.None));

        var failure = new InvalidOperationException("Drop failed.");
        connection.Failure = (_, _) => failure;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.DisposeAsync().AsTask());

        Assert.Same(failure, actual);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Empty(connection.Tables);
    }

    /// <summary>A closed session receives no cleanup SQL and cannot replace the original analysis failure.</summary>
    [Fact]
    public async Task ClosedSessionDoesNotExecuteCleanupSqlOrReplaceThePrimaryFailure()
    {
        await using var connection = new SnapshotConnection();
        var snapshot = Assert.IsType<MySqlCatalogSnapshot>(
            await MySqlCatalogSnapshot.TryCreateAsync(connection, null, CancellationToken.None));

        var primary = new IOException("Connection lost.");
        connection.Close();

        var cleaned = await snapshot.DisposeAfterFailureAsync(primary);

        Assert.False(cleaned);
        Assert.IsType<InvalidOperationException>(primary.Data["SafeMigrations.CatalogSnapshotCleanupException"]);
        Assert.Equal(9, connection.Commands.Count);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    /// <summary>A successfully cleaned capability failure leaves the caller session available for a later attempt.</summary>
    [Fact]
    public async Task FailedCreationCanBeRetriedOnTheSameCleanSession()
    {
        await using var connection = new SnapshotConnection();
        connection.Failure = (ordinal, _) => ordinal == 3
            ? CreateServerException(MySqlErrorCode.DatabaseAccessDenied)
            : null;

        var unavailable = await MySqlCatalogSnapshot.TryCreateAsync(connection, null, CancellationToken.None);
        await using var retry = await MySqlCatalogSnapshot.TryCreateAsync(connection, null, CancellationToken.None);

        Assert.Null(unavailable);
        Assert.NotNull(retry);
        Assert.Equal(9, connection.Tables.Count);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Creates a realistic exception from the locked connector without opening a database connection.</summary>
    private static MySqlException CreateServerException(
        MySqlErrorCode code
    )
    {
        // WHY: MySqlConnector intentionally exposes no public exception constructor. The locked
        // driver's internal two-argument constructor supplies realistic codes without a server.
        var constructor = typeof(MySqlException).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(MySqlErrorCode), typeof(string)],
            modifiers: null);

        return (MySqlException)Assert.IsAssignableFrom<ConstructorInfo>(constructor)
            .Invoke([code, "Injected server error."]);
    }
}
