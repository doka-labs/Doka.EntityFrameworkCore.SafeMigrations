namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the ColumnsRequiredDefaults migration contracts in an independent class collection.</summary>
public sealed partial class MySqlColumnsRequiredDefaultsIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlColumnsRequiredDefaultsIntegrationTests(MySqlEngineContainerFixture fixture) : base(fixture) { }

    /// <summary>
    /// Explicit no-default ENUM and SET contracts remain eligible for a guarded empty-table addition.
    /// </summary>
    /// <param name="useSetStorage">Whether the explicit store type is SET rather than ENUM.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredDefaultEnumAndSetAddPreservesAbsentDefaultAndMatchingRerun(bool useSetStorage)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `required_default_items` (`key` int NOT NULL PRIMARY KEY) ENGINE=InnoDB;");
        await using var context = CreateContext(connectionString);
        var storeType = useSetStorage ? "set('first','second')" : "enum('first','second')";
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent("required_default_items",
                new ExpectedColumnDefinition("value", typeof(string), false, storeType)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("required-enum-set-add"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var rerun = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("required-enum-set-add-rerun"), CancellationToken.None);

        rerun.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var exactContract = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'required_default_items' AND COLUMN_NAME = 'value' "
            + "AND IS_NULLABLE = 'NO' AND COLUMN_DEFAULT IS NULL AND LOWER(COLUMN_TYPE) = "
            + AtomicRequiredAddLiteral(storeType) + ";");
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `required_default_items`;");

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(preflight.Assessments).Action);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(rerun.Assessments).Action);
        Assert.Equal(1, exactContract);
        Assert.Equal(0, rows);
        Assert.Equal(SafeMigrationDefaultValueKind.None,
            Assert.IsType<EnsureColumnIntent>(operation.Intent).Definition.DefaultValue.Kind);
    }

    /// <summary>
    /// A literal NULL default on a required column is rejected at definition construction, before provider work.
    /// </summary>
    [Fact]
    public void RequiredDefaultLiteralNullIsRejectedBeforeProviderWork()
    {
        // Arrange
        var nullDefault = SafeMigrationDefaultValue.Literal(null);

        // Act
        var exception = Record.Exception(() => new ExpectedColumnDefinition(
            "value", typeof(int), false, "int", defaultValue: nullDefault));

        // Assert
        var invalidDefinition = Assert.IsType<ArgumentException>(exception);

        Assert.Equal("defaultValue", invalidDefinition.ParamName);
        Assert.Contains("literal NULL default requires a nullable column", invalidDefinition.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Structured DEFAULT (NULL) uses the engine's representability contract without inventing row values.
    /// </summary>
    [Fact]
    public async Task RequiredDefaultStructuredSqlNullPreservesEngineContractAndAuthoredMetadata()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `required_default_items` (`key` int NOT NULL PRIMARY KEY, `value` char(36) NULL) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `required_default_items` VALUES (1, '00000000-0000-0000-0000-000000000001');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferentFromModel(
            operationFactory: static operations => operations.AlterColumn<Guid>(
                    name: "value", table: "required_default_items", type: "char(36)",
                    fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "NULL",
                    oldClrType: typeof(Guid), oldType: "char(36)", oldFixedLength: true,
                    oldMaxLength: 36, oldNullable: true)
                .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                .Annotation("Doka:MySql:RequiresExplicitBackfill", true)
                .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None)
                .OldAnnotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                .OldAnnotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None),
            policy: SafeMigrationPolicy.RepairIfSafe);

        var operation = Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
        var originalFingerprint = SafeMigrationContractFingerprint.Create([operation]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("required-structured-null-default"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var required = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'required_default_items' AND COLUMN_NAME = 'value' AND IS_NULLABLE = 'NO';");
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `required_default_items` WHERE `key` = 1 "
            + "AND `value` = '00000000-0000-0000-0000-000000000001';");

        // Assert
        Assert.Equal(Fixture.IsMariaDb ? SafeMigrationAction.RejectUnsupported : SafeMigrationAction.Repair,
            Assert.Single(report.Assessments).Action);
        if (Fixture.IsMariaDb)
        {
            Assert.Contains("doka_sm_unsupported", AssertOwnedGuardFailure(exception).Message,
                StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(exception);
        }

        Assert.Equal(Fixture.IsMariaDb ? 0 : 1, required);
        Assert.Equal(1, preserved);
        Assert.Equal(originalFingerprint, SafeMigrationContractFingerprint.Create([operation]));
        var definition = Assert.IsType<AlterColumnIntent>(operation.Intent).Definition;

        Assert.Equal(SafeMigrationDefaultValueKind.Sql, definition.DefaultValue.Kind);
        Assert.NotNull(definition.DefaultValue.StructuredExpression);
        Assert.True(Assert.IsType<bool>(Assert.Single(definition.ProviderAnnotations,
            annotation => annotation.Name == "Doka:MySql:RequiresExplicitBackfill").CreateValueSnapshot()));
    }

    /// <summary>
    /// Required TIMESTAMP repairs cannot rely on a session that silently creates temporal values for NULL.
    /// </summary>
    /// <param name="ensure">Whether the target uses Ensure rather than an explicit Alter.</param>
    /// <param name="explicitDefaults">
    /// Whether explicit_defaults_for_timestamp is enabled on the migration session.
    /// </param>
    /// <param name="approvedDefault">Whether the caller supplies a concrete non-NULL temporal replacement.</param>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task RequiredDefaultTimestampRepairRequiresExplicitSessionSemantics(
        bool ensure,
        bool explicitDefaults,
        bool approvedDefault
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `required_default_items` (`key` int NOT NULL PRIMARY KEY, `value` timestamp(6) NULL) "
            + "ENGINE=InnoDB; INSERT INTO `required_default_items` VALUES (1, "
            + (approvedDefault && !ensure ? "NULL" : "'2000-01-02 03:04:05'") + ");");

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await AtomicRequiredAddSessionSqlAsync(context, "SET SESSION sql_mode = 'STRICT_ALL_TABLES'; "
            + "SET SESSION explicit_defaults_for_timestamp = " + (explicitDefaults ? "ON" : "OFF") + ";");
        var operation = RequiredDefaultsTimestampOperation(ensure, approvedDefault);
        var originalFingerprint = SafeMigrationContractFingerprint.Create([operation]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("required-timestamp-session"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var required = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'required_default_items' AND COLUMN_NAME = 'value' AND IS_NULLABLE = 'NO';");
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `required_default_items` WHERE `key` = 1 AND `value` "
            + (approvedDefault && !ensure && !explicitDefaults ? "IS NULL" : "= '"
                + (approvedDefault && !ensure ? "2001-01-02 03:04:05" : "2000-01-02 03:04:05") + "'") + ";");

        // Assert
        Assert.Equal(explicitDefaults ? SafeMigrationAction.Repair : SafeMigrationAction.RejectDifferent,
            Assert.Single(report.Assessments).Action);
        if (explicitDefaults)
        {
            Assert.Null(exception);
        }
        else
        {
            Assert.Contains("doka_sm_different", AssertOwnedGuardFailure(exception).Message,
                StringComparison.Ordinal);
        }

        Assert.Equal(explicitDefaults ? 1 : 0, required);
        Assert.Equal(1, preserved);
        Assert.Equal(originalFingerprint, SafeMigrationContractFingerprint.Create([operation]));
    }

    /// <summary>
    /// Already matching required TIMESTAMP columns remain no-ops even when the session mode is permissive.
    /// </summary>
    /// <param name="ensure">Whether the target uses Ensure rather than an explicit Alter.</param>
    /// <param name="approvedDefault">Whether the existing target includes a concrete caller-owned default.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RequiredDefaultTimestampMatchingTargetRemainsNoOpWithExplicitDefaultsDisabled(
        bool ensure,
        bool approvedDefault
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        // WHY: The no-default fixture must be authored under explicit temporal
        // semantics; creating it with OFF would already introduce a different contract.
        await AtomicRequiredAddSessionSqlAsync(context,
            "SET SESSION explicit_defaults_for_timestamp = ON; "
            + "CREATE TABLE `required_default_items` (`key` int NOT NULL PRIMARY KEY, `value` timestamp(6) NOT NULL"
            + (approvedDefault ? " DEFAULT '2001-01-02 03:04:05'" : "") + ") ENGINE=InnoDB; "
            + "INSERT INTO `required_default_items` VALUES (1, '2000-01-02 03:04:05'); "
            + "SET SESSION explicit_defaults_for_timestamp = OFF;");
        var operation = RequiredDefaultsTimestampOperation(ensure, approvedDefault);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("matching-required-timestamp-session"), CancellationToken.None);

        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `required_default_items` WHERE `key` = 1 AND `value` = '2000-01-02 03:04:05';");

        // Assert
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(report.Assessments).Action);
        Assert.Equal(1, preserved);
    }

    /// <summary>
    /// Fresh projected tables must consume the captured TIMESTAMP session guard before required repair.
    /// </summary>
    /// <param name="explicitDefaults">Whether the captured session preserves explicit TIMESTAMP semantics.</param>
    /// <param name="approvedDefault">Whether the target carries a caller-owned temporal replacement.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RequiredDefaultProjectedTimestampRepairUsesActualCapturedSessionGuard(
        bool explicitDefaults,
        bool approvedDefault
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await AtomicRequiredAddSessionSqlAsync(context, "SET SESSION sql_mode = 'STRICT_ALL_TABLES'; "
            + "SET SESSION explicit_defaults_for_timestamp = " + (explicitDefaults ? "ON" : "OFF") + ";");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            new ExpectedTableDefinition("required_default_items",
                [new ExpectedColumnDefinition("key", typeof(int), false, "int"),
                    new ExpectedColumnDefinition("value", typeof(DateTime), true, "timestamp(6)")],
                primaryKey: new ExpectedPrimaryKeyDefinition("PRIMARY", "required_default_items", ["key"])),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.Operations.Add(RequiredDefaultsTimestampOperation(ensure: false, approvedDefault));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("projected-required-timestamp-session"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        var required = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'required_default_items' AND COLUMN_NAME = 'value' AND IS_NULLABLE = 'NO';");
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `required_default_items`;");

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(explicitDefaults ? SafeMigrationAction.Repair : SafeMigrationAction.RejectDifferent,
            report.Assessments[1].Action);
        if (explicitDefaults)
        {
            Assert.Null(exception);
        }
        else
        {
            Assert.Contains("doka_sm_different", AssertOwnedGuardFailure(exception).Message,
                StringComparison.Ordinal);
        }

        Assert.Equal(explicitDefaults ? 1 : 0, required);
        Assert.Equal(0, rows);
    }

    /// <summary>Rejected owners skip required-add facts and snapshots before catalog or row classification.</summary>
    /// <param name="ownerState">
    /// A foreign qualifier, missing owner, or eligible positive instrumentation control.
    /// </param>
    [Theory]
    [InlineData("foreign-database")]
    [InlineData("missing-table")]
    [InlineData("eligible")]
    public async Task RequiredDefaultAddStatusTransportRespectsIdentityAndPrerequisitePrecedence(string ownerState)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, AtomicRequiredAddOriginalTableSql());
        await using var observer = new RequiredAddCatalogTransportObserver(connectionString);
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>()
            .UseMySql(observer, Fixture.ServerVersion)
            .UseMySqlSafeMigrations<SafeMigrationDbContext>()
            .Options;

        await using var context = new SafeMigrationDbContext(options);
        var operation = ownerState switch
        {
            "foreign-database" => AtomicRequiredAddOperation(schema: "foreign_required_add_database"),
            "missing-table" => AtomicRequiredAddOperation(table: "missing_required_add_owner"),
            _ => AtomicRequiredAddOperation(),
        };

        // WHY: Reach the snapshot threshold with real eligible plans in the
        // positive control; rejected batches must still create no snapshot.
        var operations = Enumerable.Repeat(operation, SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
            .Cast<MigrationOperation>().ToArray();

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("required-add-transport-precedence"), CancellationToken.None);

        // Assert
        Assert.True(observer.Commands > 0, "Raw-command instrumentation must observe real analysis.");
        Assert.True(observer.NativeBatches > 0, "Instrumentation must preserve and observe native batch execution.");
        if (ownerState == "eligible")
        {
            Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
            Assert.True(observer.RequiredAddFacts > 0, "The positive control must observe required-add fact queries.");
            Assert.Equal(Fixture.IsMariaDb, observer.Snapshots > 0);
        }
        else
        {
            Assert.All(report.Assessments, assessment => Assert.Equal(ownerState == "foreign-database"
                ? SafeMigrationAction.RejectUnsupported
                : SafeMigrationAction.RejectPrerequisiteMissing, assessment.Action));
            Assert.Equal(0, observer.RequiredAddFacts);
            Assert.Equal(0, observer.Snapshots);
        }
    }

    /// <summary>Creates matching-shape required TIMESTAMP targets with an optional caller-owned replacement.</summary>
    /// <param name="ensure">Whether the operation uses an Ensure intent.</param>
    /// <param name="approvedDefault">Whether a concrete temporal replacement belongs to the target contract.</param>
    /// <returns>The immutable repair request without implicit CURRENT_TIMESTAMP ownership.</returns>
    private static SafeMigrationOperation RequiredDefaultsTimestampOperation(bool ensure, bool approvedDefault)
    {
        // WHY: Doka requires caller-owned SQL for temporal backfills;
        // a CLR TIMESTAMP literal does not declare session-zone semantics.
        var defaultValue = approvedDefault
            ? SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal("2001-01-02 03:04:05"))
            : SafeMigrationDefaultValue.None;

        var target = new ExpectedColumnDefinition("value", typeof(DateTime), false, "timestamp(6)",
            defaultValue: defaultValue);

        return new SafeMigrationOperation(ensure
            ? new EnsureColumnIntent("required_default_items", target)
            : new AlterColumnIntent("required_default_items", target,
                new ExpectedColumnDefinition("value", typeof(DateTime), true, "timestamp(6)")),
            SafeMigrationPolicy.RepairIfSafe);
    }
}

public abstract partial class MySqlSafeMigrationIntegrationTests
{

    /// <summary>
    /// Counts raw provider statements while retaining native batching and storing no SQL or parameter payloads.
    /// </summary>
    /// <remarks>
    /// WHY: Catalog analysis uses raw ADO, bypassing EF interceptors. The locked
    /// MySqlConnector 2.5.0 MySqlBatch also passes a null Activity to execution.
    /// A delegating observer must therefore retain its native DbBatch rather
    /// than mismeasure tracing gaps or change the transport to sequential SQL.
    /// </remarks>
    protected sealed class RequiredAddCatalogTransportObserver : DbConnection
    {
        private readonly MySqlConnection _inner;
        private string _connectionString;
        private long _commands;
        private long _requiredAddFacts;
        private long _snapshots;
        private long _nativeBatches;

        /// <summary>Wraps only this isolated fixture's connection without changing SQL or server behavior.</summary>
        /// <param name="connectionString">The isolated fixture database connection.</param>
        public RequiredAddCatalogTransportObserver(string connectionString)
        {
            _inner = new MySqlConnection(connectionString);
            _connectionString = connectionString;
        }

        /// <summary>Gets the number of submitted actual raw SQL statements.</summary>
        public long Commands => Interlocked.Read(ref _commands);

        /// <summary>Gets the number of executed required-add closed-status fact queries.</summary>
        public long RequiredAddFacts => Interlocked.Read(ref _requiredAddFacts);

        /// <summary>Gets the number of created owned catalog-snapshot relations.</summary>
        public long Snapshots => Interlocked.Read(ref _snapshots);

        /// <summary>
        /// Gets the number of submitted native batches, proving no sequential fallback was introduced.
        /// </summary>
        public long NativeBatches => Interlocked.Read(ref _nativeBatches);

        /// <inheritdoc />
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString
        {
            // WHY: EF clones an unknown DbConnection from this property. The native
            // connector hides its password after opening, so forwarding that getter
            // would make the observer change authentication rather than just count SQL.
            get => _connectionString;
            set
            {
                _inner.ConnectionString = value;
                _connectionString = value ?? string.Empty;
            }
        }

        /// <inheritdoc />
        public override string Database => _inner.Database;

        /// <inheritdoc />
        public override string DataSource => _inner.DataSource;

        /// <inheritdoc />
        public override string ServerVersion => _inner.ServerVersion;

        /// <inheritdoc />
        public override ConnectionState State => _inner.State;

        /// <inheritdoc />
        public override bool CanCreateBatch => _inner.CanCreateBatch;

        /// <inheritdoc />
        public override void Open() => _inner.Open();

        /// <inheritdoc />
        public override Task OpenAsync(CancellationToken cancellationToken) => _inner.OpenAsync(cancellationToken);

        /// <inheritdoc />
        public override void Close() => _inner.Close();

        /// <inheritdoc />
        public override Task CloseAsync() => _inner.CloseAsync();

        /// <inheritdoc />
        public override void ChangeDatabase(string databaseName)
        {
            _inner.ChangeDatabase(databaseName);
            _connectionString = new MySqlConnectionStringBuilder(_connectionString)
            {
                Database = databaseName,
            }.ConnectionString;
        }

        /// <inheritdoc />
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            => _inner.BeginTransaction(isolationLevel);

        /// <inheritdoc />
        protected override DbCommand CreateDbCommand() => new RequiredAddObservedCommand(this, _inner.CreateCommand());

        /// <inheritdoc />
        protected override DbBatch CreateDbBatch() => new RequiredAddObservedBatch(this, _inner.CreateBatch());

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            await base.DisposeAsync();
            GC.SuppressFinalize(this);
        }

        /// <summary>Records a submission through the connection rather than byref access from another object.</summary>
        private void ObserveBatch() => Interlocked.Increment(ref _nativeBatches);

        /// <summary>Classifies transient submitted SQL while storing only bounded numeric counters.</summary>
        /// <param name="sql">The actual unmodified statement about to be submitted.</param>
        private void Observe(string sql)
        {
            Interlocked.Increment(ref _commands);
            if (sql.Contains("'complete'", StringComparison.Ordinal)
                && sql.Contains("'fresh'", StringComparison.Ordinal)
                && sql.Contains("'staged'", StringComparison.Ordinal)
                && sql.Contains("'ordinary'", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _requiredAddFacts);
            }

            if (sql.Contains("CREATE TEMPORARY TABLE `__doka_sm_cat_", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _snapshots);
            }
        }

        /// <summary>Forwards every command member while observing only actual execution attempts.</summary>
        private sealed class RequiredAddObservedCommand : DbCommand
        {
            private readonly RequiredAddCatalogTransportObserver _owner;
            private readonly DbCommand _inner;

            /// <summary>Binds one real provider command to the isolated observer.</summary>
            /// <param name="owner">The owning observer connection.</param>
            /// <param name="inner">The unmodified native provider command.</param>
            public RequiredAddObservedCommand(RequiredAddCatalogTransportObserver owner, DbCommand inner)
            {
                _owner = owner;
                _inner = inner;
            }

            /// <inheritdoc />
            [System.Diagnostics.CodeAnalysis.AllowNull]
            public override string CommandText
            {
                get => _inner.CommandText;
                set => _inner.CommandText = value;
            }

            /// <inheritdoc />
            public override int CommandTimeout { get => _inner.CommandTimeout; set => _inner.CommandTimeout = value; }

            /// <inheritdoc />
            public override CommandType CommandType { get => _inner.CommandType; set => _inner.CommandType = value; }

            /// <inheritdoc />
            public override bool DesignTimeVisible
            {
                get => _inner.DesignTimeVisible;
                set => _inner.DesignTimeVisible = value;
            }

            /// <inheritdoc />
            public override UpdateRowSource UpdatedRowSource
            {
                get => _inner.UpdatedRowSource;
                set => _inner.UpdatedRowSource = value;
            }

            /// <inheritdoc />
            protected override DbConnection? DbConnection
            {
                get => _owner;
                set => _inner.Connection = value is RequiredAddCatalogTransportObserver observed
                    ? observed._inner
                    : value;
            }

            /// <inheritdoc />
            protected override DbTransaction? DbTransaction
            {
                get => _inner.Transaction;
                set => _inner.Transaction = value;
            }

            /// <inheritdoc />
            protected override DbParameterCollection DbParameterCollection => _inner.Parameters;

            /// <inheritdoc />
            public override void Cancel() => _inner.Cancel();

            /// <inheritdoc />
            public override void Prepare() => _inner.Prepare();

            /// <inheritdoc />
            public override Task PrepareAsync(CancellationToken cancellationToken = default)
                => _inner.PrepareAsync(cancellationToken);

            /// <inheritdoc />
            protected override DbParameter CreateDbParameter() => _inner.CreateParameter();

            /// <inheritdoc />
            public override int ExecuteNonQuery()
            {
                _owner.Observe(_inner.CommandText);

                return _inner.ExecuteNonQuery();
            }

            /// <inheritdoc />
            public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
            {
                _owner.Observe(_inner.CommandText);

                return _inner.ExecuteNonQueryAsync(cancellationToken);
            }

            /// <inheritdoc />
            public override object? ExecuteScalar()
            {
                _owner.Observe(_inner.CommandText);

                return _inner.ExecuteScalar();
            }

            /// <inheritdoc />
            public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
            {
                _owner.Observe(_inner.CommandText);

                return _inner.ExecuteScalarAsync(cancellationToken);
            }

            /// <inheritdoc />
            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            {
                _owner.Observe(_inner.CommandText);

                return _inner.ExecuteReader(behavior);
            }

            /// <inheritdoc />
            protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
                CommandBehavior behavior,
                CancellationToken cancellationToken
            )
            {
                _owner.Observe(_inner.CommandText);

                return _inner.ExecuteReaderAsync(behavior, cancellationToken);
            }

            /// <inheritdoc />
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }

            /// <inheritdoc />
            public override async ValueTask DisposeAsync()
            {
                await _inner.DisposeAsync();
                await base.DisposeAsync();
                GC.SuppressFinalize(this);
            }
        }

        /// <summary>
        /// Retains the native batch collection, parameters and reader rather than flattening transport.
        /// </summary>
        private sealed class RequiredAddObservedBatch : DbBatch
        {
            private readonly RequiredAddCatalogTransportObserver _owner;
            private readonly DbBatch _inner;

            /// <summary>Binds one real native batch to the isolated observer.</summary>
            /// <param name="owner">The owning observer connection.</param>
            /// <param name="inner">The unmodified native provider batch.</param>
            public RequiredAddObservedBatch(RequiredAddCatalogTransportObserver owner, DbBatch inner)
            {
                _owner = owner;
                _inner = inner;
            }

            /// <inheritdoc />
            public override int Timeout { get => _inner.Timeout; set => _inner.Timeout = value; }

            /// <inheritdoc />
            protected override DbBatchCommandCollection DbBatchCommands => _inner.BatchCommands;

            /// <inheritdoc />
            protected override DbConnection? DbConnection
            {
                get => _owner;
                set => _inner.Connection = value is RequiredAddCatalogTransportObserver observed
                    ? observed._inner
                    : value;
            }

            /// <inheritdoc />
            protected override DbTransaction? DbTransaction
            {
                get => _inner.Transaction;
                set => _inner.Transaction = value;
            }

            /// <inheritdoc />
            protected override DbBatchCommand CreateDbBatchCommand() => _inner.CreateBatchCommand();

            /// <inheritdoc />
            public override void Cancel() => _inner.Cancel();

            /// <inheritdoc />
            public override void Prepare() => _inner.Prepare();

            /// <inheritdoc />
            public override Task PrepareAsync(CancellationToken cancellationToken = default)
                => _inner.PrepareAsync(cancellationToken);

            /// <inheritdoc />
            public override int ExecuteNonQuery()
            {
                ObserveCommands();

                return _inner.ExecuteNonQuery();
            }

            /// <inheritdoc />
            public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default)
            {
                ObserveCommands();

                return _inner.ExecuteNonQueryAsync(cancellationToken);
            }

            /// <inheritdoc />
            public override object? ExecuteScalar()
            {
                ObserveCommands();

                return _inner.ExecuteScalar();
            }

            /// <inheritdoc />
            public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default)
            {
                ObserveCommands();

                return _inner.ExecuteScalarAsync(cancellationToken);
            }

            /// <inheritdoc />
            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            {
                ObserveCommands();

                return _inner.ExecuteReader(behavior);
            }

            /// <inheritdoc />
            protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
                CommandBehavior behavior,
                CancellationToken cancellationToken
            )
            {
                ObserveCommands();

                return _inner.ExecuteReaderAsync(behavior, cancellationToken);
            }

            /// <inheritdoc />
            public override void Dispose()
            {
                _inner.Dispose();
                base.Dispose();
                GC.SuppressFinalize(this);
            }

            /// <inheritdoc />
            public override async ValueTask DisposeAsync()
            {
                await _inner.DisposeAsync();
                await base.DisposeAsync();
                GC.SuppressFinalize(this);
            }

            /// <summary>Counts each submitted native statement without retaining its SQL or parameter values.</summary>
            private void ObserveCommands()
            {
                _owner.ObserveBatch();
                foreach (var command in _inner.BatchCommands)
                {
                    _owner.Observe(command.CommandText);
                }
            }
        }
    }
}
