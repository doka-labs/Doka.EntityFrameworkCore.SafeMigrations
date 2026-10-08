namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Exercises catalog-proof lifetime through the real analyzer and its analysis scope.</summary>
public sealed class SqlServerCatalogPreambleLifecycleTests
{
    /// <summary>Server permission evidence uses the documented null securable and class, not an invalid SERVER class.</summary>
    [Fact]
    public async Task EnvironmentProbe_UsesSupportedServerPermissionArguments()
    {
        // Arrange
        await using var connection = new PreambleConnection();
        await using var context = Context(connection);
        var analyzer = Analyzer(context);

        // Act
        await analyzer.AnalyzeAsync(context, AnalysisOperations());

        // Assert
        Assert.Contains("HAS_PERMS_BY_NAME(NULL,NULL,N'VIEW ANY DEFINITION')", connection.EnvironmentCommand,
            StringComparison.Ordinal);
        Assert.DoesNotContain("HAS_PERMS_BY_NAME(NULL,N'SERVER'", connection.EnvironmentCommand,
            StringComparison.Ordinal);
    }

    /// <summary>Successful analysis and inventory still recheck the current environment inside their active scope.</summary>
    /// <param name="borrowed">Whether the caller supplies the transaction.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveScope_InventoryRechecksCurrentEnvironment(bool borrowed)
    {
        // Arrange
        await using var connection = new PreambleConnection();
        await using var context = Context(connection);
        if (borrowed)
        {
            await context.Database.BeginTransactionAsync();
        }

        var analyzer = Analyzer(context);
        await using var scope = await analyzer.AcquireAnalysisScopeAsync(context);
        await analyzer.AnalyzeAsync(context, AnalysisOperations());

        // Act
        var inventory = await analyzer.FindUnexpectedObjectsAsync(context, InventoryOperations());

        // Assert
        Assert.Empty(inventory);
        Assert.Equal(2, connection.EnvironmentReads);
        Assert.Equal(1, connection.ClassificationReads);
    }

    /// <summary>A direct analysis never lends its session proofs to a separate inventory request.</summary>
    /// <param name="alreadyOpen">Whether the caller already opened the connection.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoAnalysisScope_InventoryRequiresFreshEnvironment(bool alreadyOpen)
    {
        // Arrange
        await using var connection = new PreambleConnection();
        await using var context = Context(connection);
        if (alreadyOpen)
        {
            await connection.OpenAsync();
        }

        var analyzer = Analyzer(context);
        await analyzer.AnalyzeAsync(context, AnalysisOperations());
        connection.MetadataVisible = false;

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            analyzer.FindUnexpectedObjectsAsync(context, InventoryOperations()));

        // Assert
        Assert.Contains("requires database metadata visibility", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, connection.EnvironmentReads);
    }

    /// <summary>Scope disposal, including a borrowed transaction, invalidates successful proofs.</summary>
    /// <param name="borrowed">Whether the caller supplies the transaction.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposedScope_InventoryRequiresFreshEnvironment(bool borrowed)
    {
        // Arrange
        await using var connection = new PreambleConnection();
        await using var context = Context(connection);
        if (borrowed)
        {
            await context.Database.BeginTransactionAsync();
        }

        var analyzer = Analyzer(context);
        var scope = await analyzer.AcquireAnalysisScopeAsync(context);
        await analyzer.AnalyzeAsync(context, AnalysisOperations());
        await scope.DisposeAsync();
        connection.MetadataVisible = false;

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            analyzer.FindUnexpectedObjectsAsync(context, InventoryOperations()));

        // Assert
        Assert.Contains("requires database metadata visibility", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, connection.EnvironmentReads);
        Assert.Equal(borrowed ? 1 : 0, connection.ScopeReleases);
    }

    /// <summary>Changing the physical session or database cannot retain earlier scope evidence.</summary>
    /// <param name="changeDatabase">Whether the database changes rather than the connection being reopened.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSession_InventoryRequiresFreshEnvironment(bool changeDatabase)
    {
        // Arrange
        await using var connection = new PreambleConnection();
        await using var context = Context(connection);
        var analyzer = Analyzer(context);
        await using var scope = await analyzer.AcquireAnalysisScopeAsync(context);
        await analyzer.AnalyzeAsync(context, AnalysisOperations());
        if (changeDatabase)
        {
            connection.ChangeDatabase("another_catalog");
        }
        else
        {
            connection.Close();
            await connection.OpenAsync();
        }

        connection.MetadataVisible = false;

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            analyzer.FindUnexpectedObjectsAsync(context, InventoryOperations()));

        // Assert
        Assert.Contains("requires database metadata visibility", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, connection.EnvironmentReads);
    }

    /// <summary>A failed or cancelled classification cannot publish partially collected environment evidence.</summary>
    /// <param name="cancelled">Whether classification fails through cancellation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAnalysis_InventoryRequiresFreshEnvironment(bool cancelled)
    {
        // Arrange
        await using var connection = new PreambleConnection();
        await using var context = Context(connection);
        var analyzer = Analyzer(context);
        await using var scope = await analyzer.AcquireAnalysisScopeAsync(context);
        connection.ClassificationFailure = cancelled
            ? new OperationCanceledException("Injected classification cancellation.")
            : new InvalidOperationException("Injected classification failure.");

        var analysisFailure = await Record.ExceptionAsync(() => analyzer.AnalyzeAsync(context, AnalysisOperations()));
        connection.MetadataVisible = false;

        // Act
        var inventoryFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            analyzer.FindUnexpectedObjectsAsync(context, InventoryOperations()));

        // Assert
        Assert.Same(connection.ClassificationFailure, analysisFailure);
        Assert.Contains("requires database metadata visibility", inventoryFailure.Message, StringComparison.Ordinal);
        Assert.Equal(2, connection.EnvironmentReads);
    }

    /// <summary>A successful invariant rejection is suppressed only in the scope that produced the report.</summary>
    [Fact]
    public async Task RejectedAnalysis_AfterScopeDisposalDoesNotSuppressInventoryFailure()
    {
        // Arrange
        await using var connection = new PreambleConnection { MetadataVisible = false };
        await using var context = Context(connection);
        var analyzer = Analyzer(context);
        var scope = await analyzer.AcquireAnalysisScopeAsync(context);
        var analyses = await analyzer.AnalyzeAsync(context, AnalysisOperations());
        await scope.DisposeAsync();

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            analyzer.FindUnexpectedObjectsAsync(context, InventoryOperations()));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(analyses).ObservedState);
        Assert.Contains("requires database metadata visibility", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A completed scope-local rejection remains an Unsupported report, not an inventory exception.</summary>
    [Fact]
    public async Task ActiveScope_RejectedAnalysisPreservesUnsupportedReport()
    {
        // Arrange
        await using var connection = new PreambleConnection { MetadataVisible = false };
        await using var context = Context(connection);
        var analyzer = Analyzer(context);
        await using var scope = await analyzer.AcquireAnalysisScopeAsync(context);
        var analyses = await analyzer.AnalyzeAsync(context, AnalysisOperations());

        // Act
        var inventory = await analyzer.FindUnexpectedObjectsAsync(context, InventoryOperations());

        // Assert
        Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(analyses).ObservedState);
        Assert.Empty(inventory);
    }

    /// <summary>Replacing the transaction prevents cached proofs even on an unchanged connection.</summary>
    [Fact]
    public async Task ReplacedTransaction_InventoryRequiresFreshEnvironment()
    {
        // Arrange
        await using var connection = new PreambleConnection();
        await using var context = Context(connection);
        var analyzer = Analyzer(context);
        await using var scope = await analyzer.AcquireAnalysisScopeAsync(context);
        await analyzer.AnalyzeAsync(context, AnalysisOperations());
        await context.Database.CurrentTransaction!.DisposeAsync();
        await context.Database.BeginTransactionAsync();
        connection.MetadataVisible = false;

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            analyzer.FindUnexpectedObjectsAsync(context, InventoryOperations()));

        // Assert
        Assert.Contains("requires database metadata visibility", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, connection.EnvironmentReads);
    }

    /// <summary>Disposal through either owner releases a borrowed lock once and invalidates the proof.</summary>
    /// <param name="disposeAnalyzer">Whether disposal starts through the analyzer rather than the scope.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedDisposal_ReleasesBorrowedScopeExactlyOnce(bool disposeAnalyzer)
    {
        // Arrange
        await using var connection = new PreambleConnection();
        await using var context = Context(connection);
        await context.Database.BeginTransactionAsync();
        var analyzer = Analyzer(context);
        var scope = await analyzer.AcquireAnalysisScopeAsync(context);
        await analyzer.AnalyzeAsync(context, AnalysisOperations());

        // Act
        if (disposeAnalyzer)
        {
            await analyzer.DisposeAsync();
        }
        else
        {
            await scope.DisposeAsync();
        }

        await scope.DisposeAsync();
        await analyzer.DisposeAsync();
        connection.MetadataVisible = false;
        var failure = await Record.ExceptionAsync(() =>
            analyzer.FindUnexpectedObjectsAsync(context, InventoryOperations()));

        // Assert
        Assert.Contains("requires database metadata visibility",
            Assert.IsType<InvalidOperationException>(failure).Message, StringComparison.Ordinal);
        Assert.Equal(1, connection.ScopeReleases);
        Assert.Equal(2, connection.EnvironmentReads);
    }

    /// <summary>A rejected nested scope does not disturb the original lock or its completed proofs.</summary>
    [Fact]
    public async Task NestedScope_IsRejectedWithoutInvalidatingOriginalProofs()
    {
        // Arrange
        await using var connection = new PreambleConnection();
        await using var context = Context(connection);
        var analyzer = Analyzer(context);
        await using var scope = await analyzer.AcquireAnalysisScopeAsync(context);
        await analyzer.AnalyzeAsync(context, AnalysisOperations());

        // Act
        var failure = await Record.ExceptionAsync(() => analyzer.AcquireAnalysisScopeAsync(context));
        var inventory = await analyzer.FindUnexpectedObjectsAsync(context, InventoryOperations());

        // Assert
        Assert.Contains("already owns an active analysis scope",
            Assert.IsType<InvalidOperationException>(failure).Message, StringComparison.Ordinal);
        Assert.Empty(inventory);
        Assert.Equal(2, connection.EnvironmentReads);
    }

    /// <summary>Synchronous EF service disposal releases an active owned or borrowed analysis scope.</summary>
    /// <param name="borrowed">Whether the caller supplies the transaction.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronousAnalyzerDisposal_ReleasesScopeAndInvalidatesProof(bool borrowed)
    {
        // Arrange
        await using var connection = new PreambleConnection();
        await using var context = Context(connection);
        if (borrowed)
        {
            await context.Database.BeginTransactionAsync();
        }

        var analyzer = Analyzer(context);
        var scope = await analyzer.AcquireAnalysisScopeAsync(context);
        await analyzer.AnalyzeAsync(context, AnalysisOperations());

        // Act
        analyzer.Dispose();
        await scope.DisposeAsync();
        connection.MetadataVisible = false;
        var failure = await Record.ExceptionAsync(() =>
            analyzer.FindUnexpectedObjectsAsync(context, InventoryOperations()));

        // Assert
        Assert.Contains("requires database metadata visibility",
            Assert.IsType<InvalidOperationException>(failure).Message, StringComparison.Ordinal);
        Assert.Equal(borrowed ? 1 : 0, connection.ScopeReleases);
        Assert.Equal(2, connection.EnvironmentReads);
    }

    /// <summary>Only an unchanged, resolved environment can reuse the completed identifier verdict.</summary>
    /// <param name="change">The execution identity or collation changed after classification.</param>
    [Theory]
    [InlineData("unchanged")]
    [InlineData("principal")]
    [InlineData("login")]
    [InlineData("collation")]
    [InlineData("unproven")]
    [InlineData("dml_triggers")]
    public async Task FreshEnvironment_GatesActualIdentifierProbeReuse(string change)
    {
        // Arrange
        await using var connection = new PreambleConnection();
        await using var context = Context(connection);
        var analyzer = Analyzer(context);
        await using var scope = await analyzer.AcquireAnalysisScopeAsync(context);
        var operations = InventoryOperations();
        await analyzer.AnalyzeAsync(context, operations.Cast<SafeMigrationOperation>().ToArray());
        switch (change)
        {
            case "principal":
                connection.PrincipalId = 2;
                break;
            case "login":
                connection.LoginSid = "02";
                break;
            case "collation":
                connection.Collation = "Latin1_General_100_CS_AS";
                break;
            case "unproven":
                connection.LoginSid = null;
                break;
            case "dml_triggers":
                connection.HasEnabledDmlTriggers = true;
                break;
        }

        connection.IdentifierSafe = false;

        // Act
        var failure = await Record.ExceptionAsync(() => analyzer.FindUnexpectedObjectsAsync(context, operations));

        // Assert
        Assert.Equal(2, connection.EnvironmentReads);
        Assert.Equal(change == "unchanged" ? 1 : 2, connection.IdentifierReads);
        if (change == "unchanged")
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Contains("could not prove object identity",
                Assert.IsType<InvalidOperationException>(failure).Message, StringComparison.Ordinal);
        }
    }

    /// <summary>A new principal must not inherit another principal's negative inventory-suppression marker.</summary>
    [Fact]
    public async Task ChangedPrincipal_DoesNotReusePreviousInvariantRejection()
    {
        // Arrange
        await using var connection = new PreambleConnection { MetadataVisible = false };
        await using var context = Context(connection);
        var analyzer = Analyzer(context);
        await using var scope = await analyzer.AcquireAnalysisScopeAsync(context);
        await analyzer.AnalyzeAsync(context, AnalysisOperations());
        connection.PrincipalId = 2;

        // Act
        var failure = await Record.ExceptionAsync(() =>
            analyzer.FindUnexpectedObjectsAsync(context, InventoryOperations()));

        // Assert
        Assert.Contains("requires database metadata visibility",
            Assert.IsType<InvalidOperationException>(failure).Message, StringComparison.Ordinal);
        Assert.Equal(2, connection.EnvironmentReads);
    }

    /// <summary>An already ended caller transaction releases its application lock without another command.</summary>
    /// <param name="asynchronous">Whether scope disposal uses the asynchronous path.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndedBorrowedTransaction_DoesNotExecuteAnotherLockRelease(bool asynchronous)
    {
        // Arrange
        await using var connection = new PreambleConnection();
        await using var context = Context(connection);
        await context.Database.BeginTransactionAsync();
        var analyzer = Analyzer(context);
        var scope = await analyzer.AcquireAnalysisScopeAsync(context);
        await context.Database.CurrentTransaction!.DisposeAsync();

        // Act
        if (asynchronous)
        {
            await scope.DisposeAsync();
        }
        else
        {
            analyzer.Dispose();
        }

        // Assert
        Assert.Equal(0, connection.ScopeReleases);
    }

    /// <summary>Actual EF service-scope teardown can safely precede disposal of the returned analysis scope.</summary>
    /// <param name="borrowed">Whether the context owns a transaction before analysis acquires its scope.</param>
    /// <param name="asynchronous">Whether EF disposes its service scope asynchronously.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RegisteredContextDisposal_ReleasesOutstandingScopeSafely(
        bool borrowed,
        bool asynchronous
    )
    {
        // Arrange
        await using var connection = new PreambleConnection();
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>()
            .UseSqlServer(connection)
            .UseSqlServerSafeMigrations<SafeMigrationDbContext>()
            .Options;

        var context = new SafeMigrationDbContext(options);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        if (borrowed)
        {
            await context.Database.BeginTransactionAsync();
        }

        var scope = await analyzer.AcquireAnalysisScopeAsync(context);
        await analyzer.AnalyzeAsync(context, AnalysisOperations());

        // Act
        var failure = asynchronous
            ? await Record.ExceptionAsync(async () => await context.DisposeAsync())
            : Record.Exception(context.Dispose);

        var repeatedScopeFailure = await Record.ExceptionAsync(async () => await scope.DisposeAsync());

        // Assert
        Assert.Null(failure);
        Assert.Null(repeatedScopeFailure);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.InRange(connection.ScopeReleases, 0, borrowed ? 1 : 0);
    }

    private static SafeMigrationDbContext Context(PreambleConnection connection)
        => new(new DbContextOptionsBuilder<SafeMigrationDbContext>().UseSqlServer(connection).Options);

    private static SqlServerSafeMigrationProviderAnalyzer Analyzer(DbContext context)
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    private static SafeMigrationOperation[] AnalysisOperations()
    {
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        builder.EnsureSchemaExists("application");

        return builder.Operations.Cast<SafeMigrationOperation>().ToArray();
    }

    private static MigrationOperation[] InventoryOperations()
    {
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        builder.EnsureTable(
            new ExpectedTableDefinition("items", [new ExpectedColumnDefinition("Id", typeof(int), true, "int")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        return builder.Operations.ToArray();
    }

    /// <summary>Returns only the protocol rows needed by the real scope, schema classifier and inventory.</summary>
    private sealed class PreambleConnection : System.Data.Common.DbConnection
    {
        private ConnectionState _state;
        private string _database = "preamble";

        [AllowNull]
        public override string ConnectionString { get; set; } = "Server=localhost;Database=preamble;Integrated Security=true;";
        public override string Database => _database;
        public override string DataSource => "preamble";
        public override string ServerVersion => "16.0.0";
        public override ConnectionState State => _state;
        public bool MetadataVisible { get; set; } = true;
        public int? PrincipalId { get; set; } = 1;
        public string? LoginSid { get; set; } = "01";
        /// <summary>Gets or sets the invocation-local DML-trigger presence returned by the environment probe.</summary>
        public bool HasEnabledDmlTriggers { get; set; }
        public string Collation { get; set; } = "Latin1_General_100_CI_AS";
        public string DefaultSchema { get; set; } = "dbo";
        public bool IdentifierSafe { get; set; } = true;
        public int IdentifierReads { get; set; }
        public Exception? ClassificationFailure { get; set; }
        public int EnvironmentReads { get; set; }
        public string EnvironmentCommand { get; set; } = string.Empty;
        public int ClassificationReads { get; set; }
        public int ScopeReleases { get; set; }

        public override void Open() => ChangeState(ConnectionState.Open);
        public override void Close() => ChangeState(ConnectionState.Closed);
        public override void ChangeDatabase(string databaseName) => _database = databaseName;
        protected override System.Data.Common.DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            => new PreambleTransaction(this);
        protected override System.Data.Common.DbCommand CreateDbCommand() => new PreambleCommand(this);

        private void ChangeState(ConnectionState state)
        {
            var original = _state;
            _state = state;
            OnStateChange(new StateChangeEventArgs(original, state));
        }
    }

    private sealed class PreambleTransaction(PreambleConnection connection) : System.Data.Common.DbTransaction
    {
        private bool _disposed;

        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override System.Data.Common.DbConnection? DbConnection => _disposed ? null : connection;
        public override void Commit() { }
        public override void Rollback() { }

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class PreambleCommand(PreambleConnection connection) : System.Data.Common.DbCommand
    {
        private readonly SqlCommand _parameterHost = new();
        private DataTable? _result;

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override System.Data.Common.DbConnection? DbConnection { get; set; } = connection;
        protected override System.Data.Common.DbTransaction? DbTransaction { get; set; }
        protected override System.Data.Common.DbParameterCollection DbParameterCollection => _parameterHost.Parameters;
        public override void Cancel() => throw new NotSupportedException();
        public override void Prepare() => throw new NotSupportedException();
        protected override System.Data.Common.DbParameter CreateDbParameter() => new SqlParameter();
        public override int ExecuteNonQuery() => 0;

        public override object ExecuteScalar()
        {
            if (CommandText.Contains("sp_releaseapplock", StringComparison.Ordinal))
            {
                connection.ScopeReleases++;
            }

            if (CommandText.Contains("SELECT @doka_identity_safe", StringComparison.Ordinal))
            {
                connection.IdentifierReads++;

                return connection.IdentifierSafe ? 1 : 0;
            }

            return CommandText.StartsWith("SELECT OBJECT_ID", StringComparison.Ordinal) ? DBNull.Value : 1;
        }

        protected override System.Data.Common.DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            _result = new DataTable { Locale = CultureInfo.InvariantCulture };
            if (CommandText.StartsWith("SELECT SCHEMA_NAME()", StringComparison.Ordinal))
            {
                connection.EnvironmentReads++;
                connection.EnvironmentCommand = CommandText;
                _result.Columns.Add("schema", typeof(string));
                _result.Columns.Add("collation", typeof(string));
                _result.Columns.Add("metadata_visible", typeof(int));
                _result.Columns.Add("default_is_dbo", typeof(int));
                _result.Columns.Add("principal", typeof(int));
                _result.Columns.Add("login", typeof(string));
                _result.Columns.Add("ddl_row_effects_unproven", typeof(int));
                _result.Columns.Add("expression_dependencies_readable", typeof(int));
                _result.Columns.Add("enabled_dml_triggers", typeof(int));
                _result.Rows.Add(connection.DefaultSchema, connection.Collation, connection.MetadataVisible ? 1 : 0,
                    connection.DefaultSchema == "dbo" ? 1 : 0,
                    connection.PrincipalId is { } principal ? principal : DBNull.Value,
                    connection.LoginSid is { } sid ? sid : DBNull.Value, 0, 1,
                    connection.HasEnabledDmlTriggers ? 1 : 0);
            }
            else if (CommandText.StartsWith("SELECT requested.ordinal,", StringComparison.Ordinal))
            {
                _result.Columns.Add("ordinal", typeof(int));
                _result.Columns.Add("absent", typeof(int));
                _result.Rows.Add(0, 1);
            }
            else if (CommandText.StartsWith("SELECT 0, (", StringComparison.Ordinal)
                     || CommandText.StartsWith("EXEC sys.sp_executesql", StringComparison.Ordinal))
            {
                connection.ClassificationReads++;
                if (connection.ClassificationFailure is { } failure)
                {
                    throw failure;
                }

                for (var index = 0; index < 9; index++)
                {
                    _result.Columns.Add("value" + index, index is 0 or 2 or 3 ? typeof(int) : typeof(string));
                }

                _result.Rows.Add(0, "matching", 1, 0, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value);
            }
            else
            {
                var fieldCount = CommandText.StartsWith("SELECT 0,", StringComparison.Ordinal) ? 3 : 5;
                for (var index = 0; index < fieldCount; index++)
                {
                    _result.Columns.Add("value" + index);
                }
            }

            return _result.CreateDataReader();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _result?.Dispose();
                _parameterHost.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
