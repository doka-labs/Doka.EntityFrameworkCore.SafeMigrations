namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies client-side IDENTITY_INSERT recovery without requiring SQL Server.
/// </summary>
public sealed class SqlServerIdentityInsertCommandTests
{
    /// <summary>
    /// Recovers a failed migration on the original open connection and preserves its exception.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCommand_RecoversSameSessionAndPreservesOriginalException(bool asynchronous)
    {
        // Arrange
        using var session = new RecoveryConnection();
        var options = new DbContextOptionsBuilder().UseSqlServer(session).Options;
        using var context = new DbContext(options);
        var dependencies = context.GetService<MigrationsSqlGeneratorDependencies>();
        var analyzer = CreateAnalyzer(context);
        var failure = new InvalidOperationException("original migration failure");
        var originalCommand = new FailingCommand(dependencies, failure);
        var command = new SqlServerSafeMigrationIdentityInsertCommand(
            originalCommand,
            "SET IDENTITY_INSERT [dbo].[roles] OFF;",
            dependencies,
            analyzer);

        var connection = context.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalConnection>();

        // Act
        var exception = asynchronous
            ? await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync(connection))
            : Record.Exception(() => command.ExecuteNonQuery(connection));

        var retry = asynchronous
            ? await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync(connection))
            : Record.Exception(() => command.ExecuteNonQuery(connection));

        var quarantine = Record.Exception(() => analyzer.ValidateContext(context));

        // Assert
        Assert.Same(failure, exception);
        Assert.Same(failure, retry);
        Assert.Null(quarantine);
        Assert.Equal(2, originalCommand.ExecutionCount);
        Assert.Equal(ConnectionState.Open, session.State);
        Assert.Equal(2, session.Commands.Count);
        Assert.All(session.Commands, cleanup =>
        {
            Assert.Equal("SET IDENTITY_INSERT [dbo].[roles] OFF;", cleanup.Sql);
            Assert.Equal(30, cleanup.Timeout);
        });

        Assert.Equal(0, session.CloseCount);
    }

    /// <summary>
    /// Recovers canceled work independently of its token while retaining the caller's transaction.
    /// </summary>
    [Fact]
    public async Task CanceledCommand_RecoversWithIndependentTokenAndCurrentTransaction()
    {
        // Arrange
        using var session = new RecoveryConnection();
        var options = new DbContextOptionsBuilder().UseSqlServer(session).Options;
        using var context = new DbContext(options);
        using var transaction = session.BeginTransaction();
        using var contextTransaction = context.Database.UseTransaction(transaction);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var dependencies = context.GetService<MigrationsSqlGeneratorDependencies>();
        var analyzer = CreateAnalyzer(context);
        var failure = new OperationCanceledException(cancellation.Token);
        var command = new SqlServerSafeMigrationIdentityInsertCommand(
            new FailingCommand(dependencies, failure),
            "SET IDENTITY_INSERT [dbo].[roles] OFF;",
            dependencies,
            analyzer);

        var connection = context.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalConnection>();

        // Act
        var exception = await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync(
            connection,
            cancellationToken: cancellation.Token));

        // Assert
        Assert.Same(failure, exception);
        var cleanup = Assert.Single(session.Commands);
        Assert.True(cleanup.Token.CanBeCanceled);
        Assert.False(cleanup.Token.IsCancellationRequested);
        Assert.NotEqual(cancellation.Token, cleanup.Token);
        Assert.Same(transaction, cleanup.Transaction);
        Assert.Equal(ConnectionState.Open, session.State);
        var callerTransaction = Assert.IsType<RecoveryTransaction>(transaction);

        Assert.Equal(0, callerTransaction.CommitCount);
        Assert.Equal(0, callerTransaction.RollbackCount);
        Assert.Equal(0, callerTransaction.DisposeCount);
    }

    /// <summary>
    /// Quarantines an unrecoverable session without masking the original failure.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCleanup_ClosesSessionAndPreservesOriginalException(bool asynchronous)
    {
        // Arrange
        using var session = new RecoveryConnection { FailCleanup = true };
        var options = new DbContextOptionsBuilder().UseSqlServer(session).Options;
        using var context = new DbContext(options);
        var dependencies = context.GetService<MigrationsSqlGeneratorDependencies>();
        var analyzer = CreateAnalyzer(context);
        var failure = new OperationCanceledException("original cancellation");
        var command = new SqlServerSafeMigrationIdentityInsertCommand(
            new FailingCommand(dependencies, failure),
            "SET IDENTITY_INSERT [dbo].[roles] OFF;",
            dependencies,
            analyzer);

        var connection = context.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalConnection>();

        // Act
        var exception = asynchronous
            ? await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync(connection))
            : Record.Exception(() => command.ExecuteNonQuery(connection));

        var quarantine = Record.Exception(() => analyzer.ValidateContext(context));

        // Assert
        Assert.Same(failure, exception);
        Assert.Single(session.Commands);
        Assert.Equal(ConnectionState.Closed, session.State);
        Assert.Equal("session_closed",
            failure.Data[SqlServerSafeMigrationIdentityInsertCommand.RecoveryFailureDataKey]);
        Assert.Contains("requires a new context", Assert.IsType<InvalidOperationException>(quarantine).Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Falls back to disposal and rejects retries even when neither physical recovery boundary succeeds.
    /// </summary>
    [Theory]
    [InlineData(false, false, "session_disposed", ConnectionState.Closed)]
    [InlineData(true, false, "session_disposed", ConnectionState.Closed)]
    [InlineData(false, true, "session_quarantined", ConnectionState.Open)]
    [InlineData(true, true, "session_quarantined", ConnectionState.Open)]
    public async Task FailedCleanupAndClose_DisposalFallbackQuarantinesAllCommandRetries(
        bool asynchronous,
        bool disposalFails,
        string recoveryStatus,
        ConnectionState expectedState
    )
    {
        // Arrange
        using var session = new RecoveryConnection
        {
            FailCleanup = true,
            CloseFailuresRemaining = 1,
            DisposeFailuresRemaining = disposalFails ? 1 : 0,
        };

        var options = new DbContextOptionsBuilder().UseSqlServer(session).Options;
        using var context = new DbContext(options);
        using var transaction = session.BeginTransaction();
        using var contextTransaction = context.Database.UseTransaction(transaction);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var dependencies = context.GetService<MigrationsSqlGeneratorDependencies>();
        var analyzer = CreateAnalyzer(context);
        var quarantineAtClose = false;
        var quarantineAtDispose = false;
        session.OnClose = () =>
        {
            var rejected = Record.Exception(() => analyzer.ValidateContext(context));
            quarantineAtClose = rejected is InvalidOperationException;
        };

        session.OnDispose = () =>
        {
            var rejected = Record.Exception(() => analyzer.ValidateContext(context));
            quarantineAtDispose = rejected is InvalidOperationException;
        };

        var failure = new OperationCanceledException(cancellation.Token);
        var originalCommand = new FailingCommand(dependencies, failure);
        var command = new SqlServerSafeMigrationIdentityInsertCommand(
            originalCommand,
            "SET IDENTITY_INSERT [dbo].[roles] OFF;",
            dependencies,
            analyzer);

        var otherOriginal = new FailingCommand(dependencies, new InvalidOperationException("must not execute"));
        var alreadyGenerated = new SqlServerSafeMigrationGuardedCommand(otherOriginal, dependencies, analyzer);
        var connection = context.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalConnection>();

        // Act
        var observed = asynchronous
            ? await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync(connection,
                cancellationToken: cancellation.Token))
            : Record.Exception(() => command.ExecuteNonQuery(connection));

        var retry = asynchronous
            ? await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync(connection,
                cancellationToken: cancellation.Token))
            : Record.Exception(() => command.ExecuteNonQuery(connection));

        var laterCommand = asynchronous
            ? await Record.ExceptionAsync(() => alreadyGenerated.ExecuteNonQueryAsync(connection,
                cancellationToken: cancellation.Token))
            : Record.Exception(() =>
            {
                alreadyGenerated.ExecuteNonQuery(connection);
            });

        var quarantine = Record.Exception(() => analyzer.ValidateContext(context));

        // Assert
        Assert.Same(failure, observed);
        Assert.Equal(recoveryStatus, failure.Data[SqlServerSafeMigrationIdentityInsertCommand.RecoveryFailureDataKey]);
        Assert.Equal(1, session.CloseCount);
        Assert.Equal(1, session.DisposeCount);
        Assert.True(quarantineAtClose);
        Assert.True(quarantineAtDispose);
        Assert.Equal(expectedState, session.State);
        Assert.Equal(1, originalCommand.ExecutionCount);
        Assert.Equal(0, otherOriginal.ExecutionCount);
        Assert.All(new[] { retry, laterCommand, quarantine }, rejected =>
            Assert.Contains("requires a new context", Assert.IsType<InvalidOperationException>(rejected).Message,
                StringComparison.Ordinal));

        var cleanup = Assert.Single(session.Commands);

        Assert.Same(transaction, cleanup.Transaction);
        Assert.Equal(30, cleanup.Timeout);
        Assert.NotEqual(cancellation.Token, cleanup.Token);
        Assert.False(cleanup.Token.IsCancellationRequested);

        var callerTransaction = Assert.IsType<RecoveryTransaction>(transaction);

        Assert.Equal(0, callerTransaction.CommitCount);
        Assert.Equal(0, callerTransaction.RollbackCount);
        Assert.Equal(0, callerTransaction.DisposeCount);
    }

    /// <summary>
    /// Does not open a replacement session to clean up a connection that already closed.
    /// </summary>
    [Fact]
    public async Task ClosedConnection_DoesNotOpenANewSessionForRecovery()
    {
        // Arrange
        using var session = new RecoveryConnection();
        session.Close();
        var options = new DbContextOptionsBuilder().UseSqlServer(session).Options;
        using var context = new DbContext(options);
        var dependencies = context.GetService<MigrationsSqlGeneratorDependencies>();
        var analyzer = CreateAnalyzer(context);
        var failure = new OperationCanceledException("canceled before execution");
        var command = new SqlServerSafeMigrationIdentityInsertCommand(
            new FailingCommand(dependencies, failure),
            "SET IDENTITY_INSERT [dbo].[roles] OFF;",
            dependencies,
            analyzer);

        var connection = context.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalConnection>();

        // Act
        var exception = await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync(connection));

        // Assert
        Assert.Same(failure, exception);
        Assert.Empty(session.Commands);
        Assert.Equal(ConnectionState.Closed, session.State);
        Assert.Equal(0, session.OpenCount);
    }

    /// <summary>
    /// Delegates command metadata and execution without cloning the original SQL payload.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthyGuardedCommand_DelegatesOriginalMetadataAndArguments(bool asynchronous)
    {
        // Arrange
        using var session = new RecoveryConnection();
        var options = new DbContextOptionsBuilder().UseSqlServer(session).Options;
        using var context = new DbContext(options);
        using var cancellation = new CancellationTokenSource();
        var dependencies = context.GetService<MigrationsSqlGeneratorDependencies>();
        var original = new SuccessfulCommand(dependencies);
        var command = new SqlServerSafeMigrationGuardedCommand(original, dependencies, CreateAnalyzer(context));
        IReadOnlyDictionary<string, object?> parameters = new Dictionary<string, object?> { ["parameter"] = 7, };
        var connection = context.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalConnection>();

        original.Text = "SELECT original_virtual_command_text;";

        // Act
        var result = asynchronous
            ? await command.ExecuteNonQueryAsync(connection, parameters, cancellation.Token)
            : command.ExecuteNonQuery(connection, parameters);

        // Assert
        Assert.Equal(7, result);
        Assert.Same(original.CommandText, command.CommandText);
        Assert.Same(original.CommandLogger, command.CommandLogger);
        Assert.Equal(original.TransactionSuppressed, command.TransactionSuppressed);
        Assert.Same(parameters, original.Parameters);
        Assert.Equal(asynchronous ? cancellation.Token : CancellationToken.None, original.Token);
        Assert.Empty(session.Commands);
    }

    private static SqlServerSafeMigrationProviderAnalyzer CreateAnalyzer(DbContext context)
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    private sealed class FailingCommand : MigrationCommand
    {
        private readonly Exception _failure;

        public FailingCommand(
            MigrationsSqlGeneratorDependencies dependencies,
            Exception failure
        )
            : base(
                dependencies.CommandBuilderFactory.Create().Append("SELECT 1;").Build(),
                dependencies.CurrentContext.Context,
                dependencies.Logger)
        {
            _failure = failure;
        }

        public int ExecutionCount { get; private set; }

        public override int ExecuteNonQuery(
            Microsoft.EntityFrameworkCore.Storage.IRelationalConnection connection,
            IReadOnlyDictionary<string, object?>? parameterValues = null
        )
        {
            ExecutionCount++;

            throw _failure;
        }

        public override Task<int> ExecuteNonQueryAsync(
            Microsoft.EntityFrameworkCore.Storage.IRelationalConnection connection,
            IReadOnlyDictionary<string, object?>? parameterValues = null,
            CancellationToken cancellationToken = default
        )
        {
            ExecutionCount++;

            return Task.FromException<int>(_failure);
        }
    }

    private sealed class SuccessfulCommand(MigrationsSqlGeneratorDependencies dependencies)
        : MigrationCommand(
            dependencies.CommandBuilderFactory.Create().Append("SELECT 1;").Build(),
            dependencies.CurrentContext.Context,
            dependencies.Logger,
            transactionSuppressed: true)
    {
        public string Text { get; set; } = string.Empty;

        public IReadOnlyDictionary<string, object?>? Parameters { get; private set; }

        public CancellationToken Token { get; private set; }

        public override string CommandText => Text;

        public override int ExecuteNonQuery(
            Microsoft.EntityFrameworkCore.Storage.IRelationalConnection connection,
            IReadOnlyDictionary<string, object?>? parameterValues = null
        )
        {
            Parameters = parameterValues;

            return 7;
        }

        public override Task<int> ExecuteNonQueryAsync(
            Microsoft.EntityFrameworkCore.Storage.IRelationalConnection connection,
            IReadOnlyDictionary<string, object?>? parameterValues = null,
            CancellationToken cancellationToken = default
        )
        {
            Parameters = parameterValues;
            Token = cancellationToken;

            return Task.FromResult(7);
        }
    }

    private sealed class RecoveryConnection : System.Data.Common.DbConnection
    {
        private string _connectionString = "Server=localhost;Database=recovery;TrustServerCertificate=True";
        private ConnectionState _state = ConnectionState.Open;

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString
        {
            get => _connectionString;
            set => _connectionString = value ?? string.Empty;
        }

        public override string Database => "recovery";
        public override string DataSource => "localhost";
        public override string ServerVersion => "16.0";
        public override ConnectionState State => _state;

        public bool FailCleanup { get; init; }

        public int CloseFailuresRemaining { get; set; }

        public int DisposeFailuresRemaining { get; set; }

        public Action? OnClose { get; set; }

        public Action? OnDispose { get; set; }

        public int CloseCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int OpenCount { get; private set; }

        public List<CleanupTrace> Commands { get; } = [];

        public override void Open()
        {
            OpenCount++;
            _state = ConnectionState.Open;
        }

        public override void Close()
        {
            OnClose?.Invoke();
            CloseCount++;
            if (CloseFailuresRemaining > 0)
            {
                CloseFailuresRemaining--;

                throw new InvalidOperationException("close cannot recover this session");
            }

            _state = ConnectionState.Closed;
        }

        public override Task CloseAsync()
        {
            Close();

            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (!disposing)
                {
                    return;
                }

                OnDispose?.Invoke();
                DisposeCount++;
                if (DisposeFailuresRemaining > 0)
                {
                    DisposeFailuresRemaining--;

                    throw new InvalidOperationException("dispose cannot recover this session");
                }

                _state = ConnectionState.Closed;
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        protected override System.Data.Common.DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            => new RecoveryTransaction(this, isolationLevel);

        protected override System.Data.Common.DbCommand CreateDbCommand() => new RecoveryCommand(this);
    }

    private sealed class RecoveryTransaction : System.Data.Common.DbTransaction
    {
        private readonly RecoveryConnection _connection;

        public RecoveryTransaction(
            RecoveryConnection connection,
            IsolationLevel isolationLevel
        )
        {
            _connection = connection;
            IsolationLevel = isolationLevel;
        }

        public override IsolationLevel IsolationLevel { get; }

        public int CommitCount { get; private set; }

        public int RollbackCount { get; private set; }

        public int DisposeCount { get; private set; }

        protected override System.Data.Common.DbConnection DbConnection => _connection;

        public override void Commit() => CommitCount++;

        public override void Rollback() => RollbackCount++;

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    DisposeCount++;
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }
    }

    private sealed class RecoveryCommand : System.Data.Common.DbCommand
    {
        private readonly RecoveryConnection _connection;
        private string _commandText = string.Empty;

        public RecoveryCommand(RecoveryConnection connection)
        {
            _connection = connection;
            DbConnection = connection;
        }

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText
        {
            get => _commandText;
            set => _commandText = value ?? string.Empty;
        }

        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override System.Data.Common.DbConnection? DbConnection { get; set; }

        protected override System.Data.Common.DbTransaction? DbTransaction { get; set; }

        protected override System.Data.Common.DbParameterCollection DbParameterCollection
            => throw new NotSupportedException();

        public override void Cancel() { }

        public override int ExecuteNonQuery() => ExecuteCleanup(CancellationToken.None);

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
            => Task.FromResult(ExecuteCleanup(cancellationToken));

        public override object? ExecuteScalar() => throw new NotSupportedException();

        public override void Prepare() { }

        protected override System.Data.Common.DbParameter CreateDbParameter() => throw new NotSupportedException();

        protected override System.Data.Common.DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            => throw new NotSupportedException();

        private int ExecuteCleanup(CancellationToken cancellationToken)
        {
            _connection.Commands.Add(new CleanupTrace(CommandText, CommandTimeout, DbTransaction, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();

            if (_connection.FailCleanup)
            {
                throw new InvalidOperationException("cleanup cannot recover this session");
            }

            return 0;
        }
    }

    private sealed record CleanupTrace(
        string Sql,
        int Timeout,
        System.Data.Common.DbTransaction? Transaction,
        CancellationToken Token
    );
}
