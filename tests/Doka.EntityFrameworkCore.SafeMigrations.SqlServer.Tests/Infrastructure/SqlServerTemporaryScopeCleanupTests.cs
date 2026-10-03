namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies invocation-owned cleanup, recovery quarantine, and original failure preservation.
/// </summary>
public sealed class SqlServerTemporaryScopeCleanupTests
{
    private const string ConnectionString =
        "Server=127.0.0.1,1433;Database=cleanup;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";
    private const string RecoveryStatusKey = "Doka.SafeMigrations.SqlServer.RecoveryStatus";
    private const string TemporarySuffix = "00000000000000000000000000000001";

    /// <summary>
    /// Uses a fresh cleanup token, a bounded timeout, and the borrowed transaction despite caller cancellation.
    /// </summary>
    [Theory]
    [InlineData("identifiers", null, 30)]
    [InlineData("inventory", 0, 30)]
    [InlineData("inventory", 7, 7)]
    [InlineData("identifiers", 90, 30)]
    public async Task Cleanup_UsesIndependentCancellationAndBoundedBorrowedTransaction(
        string scope,
        int? callerTimeout,
        int expectedTimeout
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = CreateAnalyzer(context);
        await using var connection = new CleanupConnection();
        await using var transaction = new CleanupTransaction(connection);
        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();
        var original = new OperationCanceledException(callerCancellation.Token);
        var temporaryTable = "#doka_sm_" + scope + "_" + TemporarySuffix;

        // Act
        await analyzer.CleanupTemporaryScopeAsync(connection, transaction, temporaryTable, callerTimeout, original);
        var validationFailure = Record.Exception(() => analyzer.ValidateContext(context));

        // Assert
        var command = Assert.Single(connection.Executions);
        Assert.Contains("DROP TABLE " + temporaryTable + ";", command.Sql, StringComparison.Ordinal);
        Assert.Same(transaction, command.Transaction);
        Assert.Equal(expectedTimeout, command.Timeout);
        Assert.True(command.CancellationToken.CanBeCanceled);
        Assert.False(command.CancellationWasRequested);
        Assert.NotEqual(callerCancellation.Token, command.CancellationToken);
        Assert.Equal(0, transaction.RollbackCount);
        Assert.Equal(0, transaction.CommitCount);
        Assert.Equal(0, transaction.DisposeCount);
        Assert.Equal(0, connection.CloseCount);
        Assert.False(original.Data.Contains(RecoveryStatusKey));
        Assert.Null(validationFailure);
    }

    /// <summary>
    /// Preserves cancellation or provider failure identity through every cleanup recovery outcome.
    /// </summary>
    [Theory]
    [InlineData(true, false, false, "connection_closed")]
    [InlineData(false, false, false, "connection_closed")]
    [InlineData(true, true, false, "connection_disposed")]
    [InlineData(false, true, false, "connection_disposed")]
    [InlineData(true, true, true, "connection_quarantined")]
    [InlineData(false, true, true, "connection_quarantined")]
    public async Task CleanupFailure_PreservesOriginalAndNeverExplicitlyRollsBackCaller(
        bool cancellation,
        bool closeFails,
        bool disposeFails,
        string expectedRecoveryStatus
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = CreateAnalyzer(context);
        await using var connection = new CleanupConnection
        {
            CleanupFailure = new InvalidOperationException("Injected cleanup SQL failure."),
            CloseFailuresRemaining = closeFails ? 1 : 0,
            DisposeFailuresRemaining = disposeFails ? 1 : 0,
        };

        await using var transaction = new CleanupTransaction(connection);
        Exception original = cancellation
            ? new OperationCanceledException("Injected caller cancellation.")
            : new InvalidOperationException("Injected original provider failure.");

        // Act
        var observed = await Record.ExceptionAsync(async () =>
        {
            try
            {
                throw original;
            }
            finally
            {
                await analyzer.CleanupTemporaryScopeAsync(connection, transaction,
                    "#doka_sm_inventory_" + TemporarySuffix, null, original);
            }
        });

        var quarantineFailure = Record.Exception(() => analyzer.ValidateContext(context));

        // Assert
        Assert.Same(original, observed);
        Assert.Equal(expectedRecoveryStatus, original.Data[RecoveryStatusKey]);
        Assert.Equal(1, connection.CloseCount);
        Assert.Equal(closeFails ? 1 : 0, connection.DisposeCount);
        Assert.Equal(0, transaction.RollbackCount);
        Assert.Equal(0, transaction.CommitCount);
        Assert.Equal(0, transaction.DisposeCount);
        Assert.Contains("requires a new context", Assert.IsType<InvalidOperationException>(quarantineFailure).Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Converts a failed cleanup after successful work into a sanitized fail-closed exception.
    /// </summary>
    [Theory]
    [InlineData(false, false, "connection_closed")]
    [InlineData(true, false, "connection_disposed")]
    [InlineData(true, true, "connection_quarantined")]
    public async Task SuccessfulWork_CleanupFailureIsSanitizedAndFailClosed(
        bool closeFails,
        bool disposeFails,
        string expectedRecoveryStatus
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = CreateAnalyzer(context);
        var cleanupFailure = new InvalidOperationException("Sensitive SQL and identifier sentinel.");
        await using var connection = new CleanupConnection
        {
            CleanupFailure = cleanupFailure,
            CloseFailuresRemaining = closeFails ? 1 : 0,
            DisposeFailuresRemaining = disposeFails ? 1 : 0,
        };

        // Act
        var observed = await Record.ExceptionAsync(() => analyzer.CleanupTemporaryScopeAsync(
            connection, null, "#doka_sm_identifiers_" + TemporarySuffix, null, null));

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(observed);
        Assert.Same(cleanupFailure, failure.InnerException);
        Assert.Equal(expectedRecoveryStatus, failure.Data[RecoveryStatusKey]);
        Assert.Contains("temporary-scope cleanup failed; recreate the context",
            failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Sensitive SQL", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TemporarySuffix, failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, connection.CloseCount);
        Assert.Equal(closeFails ? 1 : 0, connection.DisposeCount);
    }

    /// <summary>
    /// Rejects non-owned names before any command or recovery operation can affect the caller.
    /// </summary>
    [Theory]
    [InlineData("#caller_owned")]
    [InlineData("dbo.real_table")]
    [InlineData("#doka_sm_identifiers")]
    [InlineData("#doka_sm_inventory_not_a_guid")]
    [InlineData("#doka_sm_identifiers_00000000000000000000000000000001;DROP TABLE dbo.items")]
    public async Task Cleanup_NonOwnedNameIsRejectedBeforeCommand(string temporaryTable)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = CreateAnalyzer(context);
        await using var connection = new CleanupConnection();

        // Act
        var observed = await Record.ExceptionAsync(() =>
            analyzer.CleanupTemporaryScopeAsync(connection, null, temporaryTable, null, null));

        var validationFailure = Record.Exception(() => analyzer.ValidateContext(context));

        // Assert
        Assert.Equal("temporaryTable", Assert.IsType<ArgumentException>(observed).ParamName);
        Assert.Empty(connection.Executions);
        Assert.Equal(0, connection.CommandCount);
        Assert.Equal(0, connection.CloseCount);
        Assert.Equal(0, connection.DisposeCount);
        Assert.Null(validationFailure);
    }

    /// <summary>
    /// Rejects all catalog entry points after recovery rather than reusing an invalidated context.
    /// </summary>
    [Fact]
    public async Task QuarantinedAnalyzer_RejectsEveryCatalogEntryPointAndRequiresReplacement()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        using var replacementContext = new SafeMigrationDbContext(ConnectionString);
        var analyzer = CreateAnalyzer(context);
        var replacement = CreateAnalyzer(replacementContext);
        await using var connection = new CleanupConnection
        {
            CleanupFailure = new InvalidOperationException("Injected cleanup failure."),
        };

        var original = new OperationCanceledException("Injected cancellation.");

        // Act
        await analyzer.CleanupTemporaryScopeAsync(connection, null,
            "#doka_sm_inventory_" + TemporarySuffix, null, original);
        var failures = new[]
        {
            Record.Exception(() => analyzer.ValidateContext(context)),
            await Record.ExceptionAsync(() => analyzer.GetEnvironmentAsync(context)),
            await Record.ExceptionAsync(() => analyzer.AnalyzeAsync(context, [])),
            await Record.ExceptionAsync(() => analyzer.FindUnexpectedObjectsAsync(context, [])),
            await Record.ExceptionAsync(() => analyzer.AcquireAnalysisScopeAsync(context)),
        };

        var replacementFailure = Record.Exception(() => replacement.ValidateContext(replacementContext));

        // Assert
        Assert.All(failures, failure => Assert.Contains("requires a new context",
            Assert.IsType<InvalidOperationException>(failure).Message, StringComparison.Ordinal));
        Assert.Single(connection.Executions);
        Assert.Equal(1, connection.CloseCount);
        Assert.Null(replacementFailure);
    }

    /// <summary>
    /// Preserves the original analysis failure through outer connection recovery and owned or borrowed scope disposal.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnalyzeAndScopeDisposal_QuarantineDoesNotMaskOriginalFailure(bool callerOwnsTransaction)
    {
        // Arrange
        using var callerCancellation = new CancellationTokenSource();
        var original = new OperationCanceledException("Injected analysis cancellation.", callerCancellation.Token);
        await using var connection = new CleanupConnection
        {
            MainFailure = original,
            CallerCancellation = callerCancellation,
            CleanupFailure = new InvalidOperationException("Injected cleanup failure."),
            CloseFailuresRemaining = 1,
        };

        await using var transaction = new CleanupTransaction(connection)
        {
            DisposeFailuresRemaining = callerOwnsTransaction ? 0 : 1,
        };

        connection.Transaction = transaction;
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseSqlServer(connection);
        await using var context = new SafeMigrationDbContext(options.Options);
        if (callerOwnsTransaction)
        {
            await context.Database.UseTransactionAsync(transaction);
        }

        var analyzer = CreateAnalyzer(context);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("cleanup_items", new ExpectedColumnDefinition("Id", typeof(int), false, "int"),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureColumn("cleanup_items",
            new ExpectedColumnDefinition("Caption", typeof(string), true, "nvarchar(80)"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var observed = await Record.ExceptionAsync(async () =>
        {
            await using var scope = await analyzer.AcquireAnalysisScopeAsync(context);
            await analyzer.AnalyzeAsync(context, builder.Operations.Cast<SafeMigrationOperation>().ToArray(),
                callerCancellation.Token);
        });

        // Assert
        Assert.Same(original, observed);
        Assert.True(callerCancellation.IsCancellationRequested);
        Assert.Equal("connection_disposed", original.Data[RecoveryStatusKey]);
        Assert.Equal(1, connection.CloseCount);
        Assert.Equal(1, connection.DisposeCount);
        Assert.Equal(callerOwnsTransaction ? 0 : 1, transaction.DisposeCount);
        Assert.Equal(0, transaction.RollbackCount);
        Assert.DoesNotContain(connection.Executions,
            static command => command.Sql.Contains("sp_releaseapplock", StringComparison.Ordinal));
        var cleanup = Assert.Single(connection.Executions,
            static command => command.Sql.Contains("DROP TABLE", StringComparison.Ordinal));

        Assert.False(cleanup.CancellationWasRequested);
        Assert.NotEqual(callerCancellation.Token, cleanup.CancellationToken);
    }

    private static SqlServerSafeMigrationProviderAnalyzer CreateAnalyzer(DbContext context)
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    private sealed record CommandExecution(
        string Sql,
        int Timeout,
        System.Data.Common.DbTransaction? Transaction,
        bool CancellationWasRequested,
        CancellationToken CancellationToken
    );

    private sealed class CleanupConnection : System.Data.Common.DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = SqlServerTemporaryScopeCleanupTests.ConnectionString;

        public override string Database => "cleanup";
        public override string DataSource => "cleanup-fake";
        public override string ServerVersion => "16.0.0";
        public override ConnectionState State => _state;
        public List<CommandExecution> Executions { get; } = [];
        public CleanupTransaction? Transaction { get; set; }
        public CancellationTokenSource? CallerCancellation { get; init; }
        public Exception? MainFailure { get; init; }
        public Exception? CleanupFailure { get; init; }
        public int CloseFailuresRemaining { get; set; }
        public int DisposeFailuresRemaining { get; set; }
        public int CommandCount { get; private set; }
        public int CloseCount { get; private set; }
        public int DisposeCount { get; private set; }

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Open() => _state = ConnectionState.Open;

        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Open();

            return Task.CompletedTask;
        }

        public override void Close()
        {
            CloseCount++;
            if (CloseFailuresRemaining > 0)
            {
                CloseFailuresRemaining--;
                throw new InvalidOperationException("Injected connection-close failure.");
            }

            _state = ConnectionState.Closed;
        }

        public override Task CloseAsync()
        {
            try
            {
                Close();

                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
        }

        public override async ValueTask DisposeAsync()
        {
            DisposeCount++;
            await base.DisposeAsync();
            if (DisposeFailuresRemaining > 0)
            {
                DisposeFailuresRemaining--;
                throw new InvalidOperationException("Injected connection-dispose failure.");
            }

            _state = ConnectionState.Closed;
        }

        protected override System.Data.Common.DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            => Transaction ?? throw new InvalidOperationException("The test did not provide a transaction.");

        protected override System.Data.Common.DbCommand CreateDbCommand()
        {
            CommandCount++;

            return new CleanupCommand(this);
        }

        public void Record(
            CleanupCommand command,
            CancellationToken cancellationToken
        )
            => Executions.Add(new CommandExecution(command.CommandText, command.CommandTimeout,
                command.Transaction, cancellationToken.IsCancellationRequested, cancellationToken));
    }

    private sealed class CleanupCommand : System.Data.Common.DbCommand
    {
        private readonly CleanupConnection _connection;
        private readonly SqlCommand _parameterHost = new();
        private DataTable? _environment;

        public CleanupCommand(CleanupConnection connection)
        {
            _connection = connection;
            DbConnection = connection;
        }

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override System.Data.Common.DbConnection? DbConnection { get; set; }
        protected override System.Data.Common.DbTransaction? DbTransaction { get; set; }
        protected override System.Data.Common.DbParameterCollection DbParameterCollection => _parameterHost.Parameters;

        public override void Cancel() => throw new NotSupportedException();
        public override void Prepare() => throw new NotSupportedException();
        protected override System.Data.Common.DbParameter CreateDbParameter() => new SqlParameter();

        public override int ExecuteNonQuery() => ExecuteNonQueryAsync(CancellationToken.None).GetAwaiter().GetResult();

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            _connection.Record(this, cancellationToken);
            if (CommandText.Contains("DROP TABLE", StringComparison.Ordinal)
                && _connection.CleanupFailure is { } cleanupFailure)
            {
                return Task.FromException<int>(cleanupFailure);
            }

            if (CommandText.Contains("CREATE TABLE", StringComparison.Ordinal)
                && _connection.MainFailure is { } mainFailure)
            {
                _connection.CallerCancellation?.Cancel();

                return Task.FromException<int>(mainFailure);
            }

            return Task.FromResult(0);
        }

        public override object? ExecuteScalar() => ExecuteScalarAsync(CancellationToken.None).GetAwaiter().GetResult();

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            _connection.Record(this, cancellationToken);

            return Task.FromResult<object?>(CommandText.StartsWith("SELECT OBJECT_ID", StringComparison.Ordinal)
                ? DBNull.Value
                : 1);
        }

        protected override System.Data.Common.DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            _connection.Record(this, CancellationToken.None);
            if (!CommandText.StartsWith("SELECT SCHEMA_NAME()", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The test reached an unexpected catalog reader.");
            }

            _environment = new DataTable { Locale = CultureInfo.InvariantCulture };
            _environment.Columns.Add("schema", typeof(string));
            _environment.Columns.Add("collation", typeof(string));
            _environment.Columns.Add("metadata_visible", typeof(int));
            _environment.Columns.Add("default_is_dbo", typeof(int));
            _environment.Columns.Add("principal_id", typeof(int));
            _environment.Columns.Add("login_sid", typeof(string));
            _environment.Rows.Add("dbo", "Latin1_General_100_CI_AS", 1, 1, 1, "0x01");

            return _environment.CreateDataReader();
        }

        protected override Task<System.Data.Common.DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(ExecuteDbDataReader(behavior));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _environment?.Dispose();
                _parameterHost.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CleanupTransaction : System.Data.Common.DbTransaction
    {
        private readonly CleanupConnection _connection;

        public CleanupTransaction(CleanupConnection connection) => _connection = connection;

        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override System.Data.Common.DbConnection DbConnection => _connection;
        public int CommitCount { get; private set; }
        public int RollbackCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int DisposeFailuresRemaining { get; set; }

        public override void Commit() => CommitCount++;
        public override void Rollback() => RollbackCount++;

        public override async ValueTask DisposeAsync()
        {
            DisposeCount++;
            await base.DisposeAsync();
            if (DisposeFailuresRemaining > 0)
            {
                DisposeFailuresRemaining--;
                throw new InvalidOperationException("Injected owned transaction disposal failure.");
            }
        }
    }
}
