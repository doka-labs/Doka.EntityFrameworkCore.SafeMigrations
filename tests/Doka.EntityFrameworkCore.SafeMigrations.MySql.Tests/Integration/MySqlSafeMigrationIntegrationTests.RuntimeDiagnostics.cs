namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Rejects a populated required addition with exact owner, reason and native cause after cleanup.</summary>
    /// <param name="synchronous">Whether to exercise the synchronous rather than asynchronous native scope.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeDiagnosticsRequiredAddIdentifiesExactSecondOperationAfterCleanup(bool synchronous)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `diagnostic_items` (`id` int NOT NULL PRIMARY KEY);"
            + "INSERT INTO `diagnostic_items` VALUES (1);");
        var observer = new MySqlRuntimeCommandInterceptor();
        await using var context = CreateRuntimeContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("first", "diagnostic_items", type: "int", nullable: true);
        builder.AddColumnIfNotExists<bool>("active", "diagnostic_items", type: "tinyint(1)", nullable: false);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteDiagnosticCommandsAsync(context, commands, synchronous));
        var cleaned = await ContextScalarIntAsync(context, "SELECT @doka_sm_state IS NULL "
            + "AND @doka_sm_action IS NULL AND @doka_sm_sql IS NULL AND @doka_sm_diag IS NULL;");
        var preserved = await ContextScalarIntAsync(context, "SELECT COUNT(*) FROM `diagnostic_items` WHERE `id` = 1;");

        // Assert
        var diagnostic = Assert.IsType<SafeMigrationExecutionException>(failure);
        Assert.Equal("doka_sm_data_blocked", diagnostic.GuardCode);
        Assert.Equal("required_column_backfill_missing", diagnostic.ReasonCode);
        Assert.Equal("diagnostic_items", diagnostic.Identity.Table);
        Assert.Equal("active", diagnostic.Identity.ObjectName);
        Assert.Equal(1, diagnostic.OperationOrdinal);
        Assert.Equal(SafeMigrationPolicy.ThrowIfDifferent, diagnostic.Policy);
        Assert.StartsWith("doka_sm_data_blocked", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("diagnostic_items", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("active", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("backfill", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        var native = Assert.IsType<MySqlException>(diagnostic.InnerException);
        Assert.Equal(1062, native.Number);
        Assert.Equal("23000", native.SqlState);
        Assert.Equal(1, cleaned);
        Assert.Equal(1, preserved);
        Assert.Equal(1, observer.GuardedBodyCount);
    }

    /// <summary>A durable default supplies existing rows, while an empty owner needs no fabricated replacement.</summary>
    /// <param name="populated">Whether the original owner contains data.</param>
    /// <param name="hasDefault">Whether the authored target supplies an explicit boolean replacement.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RuntimeDiagnosticsSuccessfulAddAndMatchingReplayPreserveRows(bool populated, bool hasDefault)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `diagnostic_items` (`id` int NOT NULL PRIMARY KEY);"
            + (populated ? "INSERT INTO `diagnostic_items` VALUES (1);" : string.Empty));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<bool>("active", "diagnostic_items", type: "tinyint(1)", nullable: false,
            defaultValue: hasDefault ? true : null);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);

        // Act
        await ExecuteDiagnosticCommandsAsync(context, commands, synchronous: false);
        await ExecuteDiagnosticCommandsAsync(context, commands, synchronous: false);
        var values = await ContextScalarIntAsync(context, "SELECT COUNT(*) FROM `diagnostic_items` WHERE `active` = 1;");
        var cleaned = await ContextScalarIntAsync(context, "SELECT @doka_sm_diag IS NULL;");

        // Assert
        Assert.Equal(populated ? 1 : 0, values);
        Assert.Equal(1, cleaned);
    }

    /// <summary>Wrapping metadata does not add dispatches, connection ownership changes or successful diagnostic rows.</summary>
    /// <param name="requiredWithDefault">Whether the accepted column has an explicit replacement.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeDiagnosticsDelegationHasExactlyNativeDispatchCount(bool requiredWithDefault)
    {
        // Arrange
        var wrappedDatabase = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var nativeDatabase = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        const string setup = "CREATE TABLE `diagnostic_items` (`id` int NOT NULL PRIMARY KEY);"
            + "INSERT INTO `diagnostic_items` VALUES (1);";
        await ExecuteSqlAsync(wrappedDatabase, setup);
        await ExecuteSqlAsync(nativeDatabase, setup);
        var wrappedObserver = new MySqlRuntimeCommandInterceptor();
        var nativeObserver = new MySqlRuntimeCommandInterceptor();
        await using var wrappedContext = CreateRuntimeContext(wrappedDatabase, wrappedObserver);
        await using var nativeContext = CreateRuntimeContext(nativeDatabase, nativeObserver);
        var builder = new MigrationBuilder(wrappedContext.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("value", "diagnostic_items", type: "int", nullable: !requiredWithDefault,
            defaultValue: requiredWithDefault ? 7 : null);
        var wrapped = Assert.IsType<MySqlSafeMigrationDiagnosticCommand>(Assert.Single(
            wrappedContext.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, wrappedContext.Model)));
        var native = Assert.IsType<MySqlSafeMigrationDiagnosticCommand>(Assert.Single(
            nativeContext.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, nativeContext.Model)));

        // Act
        await wrapped.ExecuteNonQueryAsync(wrappedContext.GetService<IRelationalConnection>());
        await native.OriginalCommand.ExecuteNonQueryAsync(nativeContext.GetService<IRelationalConnection>());

        // Assert
        Assert.True(wrappedObserver.CommandCount > 0);
        Assert.Equal(nativeObserver.CommandCount, wrappedObserver.CommandCount);
        Assert.Equal(nativeObserver.GuardedBodyCount, wrappedObserver.GuardedBodyCount);
        Assert.Same(wrapped.OriginalCommand.CommandLogger, wrapped.CommandLogger);
        Assert.Same(wrapped.OriginalCommand.CommandText, wrapped.CommandText);
        Assert.Equal(wrapped.OriginalCommand.TransactionSuppressed, wrapped.TransactionSuppressed);
        Assert.DoesNotContain(" AS documentKind", wrapped.CommandText, StringComparison.Ordinal);
    }

    /// <summary>A rejected generated command can recover on the same borrowed session with an explicitly revised contract.</summary>
    [Fact]
    public async Task RuntimeDiagnosticsOwnedRejectionRetainsSessionForReviewedRetry()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `diagnostic_items` (`id` int NOT NULL PRIMARY KEY);"
            + "INSERT INTO `diagnostic_items` VALUES (1);");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var session = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");
        var rejected = new MigrationBuilder(context.Database.ProviderName!);
        rejected.AddColumnIfNotExists<bool>("active", "diagnostic_items", type: "tinyint(1)", nullable: false);
        var approved = new MigrationBuilder(context.Database.ProviderName!);
        approved.AddColumnIfNotExists<bool>("active", "diagnostic_items", type: "tinyint(1)", nullable: false,
            defaultValue: true);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, rejected.Operations));
        await ExecuteOperationsAsync(context, approved.Operations);
        var actualSession = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");
        var filled = await ContextScalarIntAsync(context, "SELECT COUNT(*) FROM `diagnostic_items` WHERE `active` = 1;");
        var cleaned = await ContextScalarIntAsync(context, "SELECT @doka_sm_diag IS NULL;");

        // Assert
        Assert.IsType<SafeMigrationExecutionException>(failure);
        Assert.Equal(session, actualSession);
        Assert.Equal(1, filled);
        Assert.Equal(1, cleaned);
    }

    /// <summary>An application constraint resembling an owned guard remains the exact ordinary native failure.</summary>
    /// <param name="name">The exact or similarly prefixed application-owned unique key.</param>
    [Theory]
    [InlineData("doka_sm_data_blocked")]
    [InlineData("doka_sm_data_blocked_external")]
    public async Task RuntimeDiagnosticsDoNotWrapApplicationDuplicatesInsideOwnedScope(string name)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `diagnostic_items` (`id` int NOT NULL PRIMARY KEY);"
            + "CREATE TABLE `diagnostic_duplicates` (`value` int NOT NULL, CONSTRAINT `" + name
            + "` UNIQUE (`value`)); INSERT INTO `diagnostic_duplicates` VALUES (1);");
        var observer = new MySqlRuntimeCommandInterceptor();
        observer.InjectFirstSetup("INSERT INTO `diagnostic_duplicates` VALUES (1);");
        await using var context = CreateRuntimeContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("value", "diagnostic_items", type: "int", nullable: true);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var cleaned = await ContextScalarIntAsync(context, "SELECT @doka_sm_state IS NULL "
            + "AND @doka_sm_action IS NULL AND @doka_sm_sql IS NULL AND @doka_sm_diag IS NULL;");

        // Assert
        var native = Assert.IsType<MySqlException>(failure);
        Assert.Equal(1062, native.Number);
        Assert.Equal("23000", native.SqlState);
        Assert.Contains(name, native.Message, StringComparison.Ordinal);
        Assert.Equal(1, cleaned);
        Assert.True(observer.SetupWasInjected);
    }

    /// <summary>Qualification and owner prerequisites have distinct actionable explanations without inferred row defects.</summary>
    /// <param name="foreignQualifier">Whether the authored qualifier mismatches the active database.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeDiagnosticsPreserveIdentityFirstAndMissingOwnerReasons(bool foreignQualifier)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("value", "diagnostic_missing", type: "int", nullable: true,
            schema: foreignQualifier ? "foreign_database" : null);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        var diagnostic = Assert.IsType<SafeMigrationExecutionException>(failure);
        Assert.Equal(foreignQualifier ? "doka_sm_unsupported" : "doka_sm_prerequisite_missing", diagnostic.GuardCode);
        Assert.Equal("diagnostic_missing", diagnostic.Identity.Table);
        Assert.Equal(foreignQualifier ? "foreign_database" : null, diagnostic.Identity.Schema);
        if (foreignQualifier)
        {
            Assert.Equal("database_qualifier_mismatch", diagnostic.ReasonCode);
        }

        Assert.IsType<MySqlException>(diagnostic.InnerException);
    }

    /// <summary>A direct generated script returns its full diagnostic row before the unchanged native error arrives.</summary>
    [Fact]
    public async Task RuntimeDiagnosticsRawScriptPublishesFailureContextBeforeNativeError()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `diagnostic_items` (`id` int NOT NULL PRIMARY KEY);"
            + "INSERT INTO `diagnostic_items` VALUES (1);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<bool>("active", "diagnostic_items", type: "tinyint(1)", nullable: false);
        var sql = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations,
            context.Model, MigrationsSqlGenerationOptions.Script)).CommandText;

        // Act
        var result = await ReadDiagnosticScriptFailureAsync(connectionString, sql);

        // Assert
        Assert.Equal("safe_migration_runtime_diagnostic", result.DocumentKind);
        Assert.Equal("doka_sm_data_blocked", result.GuardCode);
        Assert.Equal("required_column_backfill_missing", result.ReasonCode);
        Assert.Contains("diagnostic_items", result.Message, StringComparison.Ordinal);
        Assert.Contains("active", result.Message, StringComparison.Ordinal);
        Assert.Contains("backfill", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(result.Message!.Length, 1, 2048);
        Assert.Equal(1062, Assert.IsType<MySqlException>(result.Failure).Number);
    }

    /// <summary>FK diagnostics identify both ends and distinguish rejected data from structural drift.</summary>
    /// <param name="orphan">Whether the child contains an orphan rather than a different existing FK contract.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeDiagnosticsForeignKeyIdentifiesBothOwnersWithoutLeakingRows(bool orphan)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `diagnostic_parents` (`id` int NOT NULL PRIMARY KEY);"
            + "CREATE TABLE `diagnostic_children` (`id` int NOT NULL PRIMARY KEY, `parent_id` int NULL, "
            + "`private_value` varchar(40) NULL); INSERT INTO `diagnostic_parents` VALUES (1);"
            + "INSERT INTO `diagnostic_children` VALUES (7, " + (orphan ? "2" : "1")
            + ", 'private-row-never-for-diagnostics');"
            + (orphan ? string.Empty : "ALTER TABLE `diagnostic_children` ADD CONSTRAINT `fk_diagnostic_parent` "
                + "FOREIGN KEY (`parent_id`) REFERENCES `diagnostic_parents` (`id`) ON DELETE RESTRICT;"));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists("fk_diagnostic_parent", "diagnostic_children", ["parent_id"],
            "diagnostic_parents", ["id"], onDelete: ReferentialAction.Cascade);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        var diagnostic = Assert.IsType<SafeMigrationExecutionException>(failure);
        Assert.Equal(orphan ? "doka_sm_data_blocked" : "doka_sm_different", diagnostic.GuardCode);
        Assert.Equal("diagnostic_children", diagnostic.Identity.Table);
        Assert.Equal("diagnostic_parents", diagnostic.Identity.PrincipalTable);
        Assert.Equal(["parent_id"], diagnostic.Identity.Columns);
        Assert.Equal(["id"], diagnostic.Identity.PrincipalColumns);
        Assert.Contains("diagnostic_children", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("diagnostic_parents", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-row-never-for-diagnostics", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(1062, Assert.IsType<MySqlException>(diagnostic.InnerException).Number);
    }

    /// <summary>Final verification retains native cleanup and labels a postcondition, not a preflight rejection.</summary>
    [Fact]
    public async Task RuntimeDiagnosticsPostconditionFailureRetainsFreshOwnerAndCleanup()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `diagnostic_items` (`id` int NOT NULL PRIMARY KEY);");
        var interceptor = new DiagnosticPostconditionFailureInterceptor();
        await using var context = CreateRuntimeContext(connectionString, interceptor);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("value", "diagnostic_items", type: "int", nullable: true);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var cleaned = await ContextScalarIntAsync(context, "SELECT @doka_sm_state IS NULL "
            + "AND @doka_sm_sql IS NULL AND @doka_sm_post_ok IS NULL AND @doka_sm_diag IS NULL;");

        // Assert
        var diagnostic = Assert.IsType<SafeMigrationExecutionException>(failure);
        Assert.Equal("doka_sm_postcondition", diagnostic.GuardCode);
        Assert.Equal("postcondition_failed", diagnostic.ReasonCode);
        Assert.Equal("diagnostic_items", diagnostic.Identity.Table);
        Assert.Equal("value", diagnostic.Identity.ObjectName);
        Assert.True(interceptor.Injected);
        Assert.Equal(1, cleaned);
        Assert.Equal(1062, Assert.IsType<MySqlException>(diagnostic.InnerException).Number);
    }

    /// <summary>Uncertain cleanup retains the native provider recovery exception even when the main error is an owned guard.</summary>
    [Fact]
    public async Task RuntimeDiagnosticsNeverReplaceNativeRecoveryFailure()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `diagnostic_items` (`id` int NOT NULL PRIMARY KEY);"
            + "INSERT INTO `diagnostic_items` VALUES (1);");
        var interceptor = new RuntimeCleanupFailureInterceptor();
        await using var context = CreateRuntimeContext(connectionString, interceptor);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<bool>("active", "diagnostic_items", type: "tinyint(1)", nullable: false);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        var recovery = Assert.IsType<InvalidOperationException>(failure, exactMatch: false);
        Assert.Equal("Doka.EntityFrameworkCore.MySql.MySqlMigrationSessionCleanupException", recovery.GetType().FullName);
        Assert.False(recovery is SafeMigrationExecutionException);
        var causes = Assert.IsType<AggregateException>(recovery.InnerException).InnerExceptions;
        Assert.Equal(2, causes.Count);
        Assert.Equal(1062, Assert.IsType<MySqlException>(causes[0]).Number);
        Assert.Equal(1146, Assert.IsType<MySqlException>(causes[1]).Number);
        Assert.True(interceptor.Injected);
        Assert.NotNull(interceptor.Cleanup);
        Assert.Equal(1, interceptor.Cleanup.VariablesCleared);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    /// <summary>A newly inserted row after the empty-state probe is rejected by the exact owned atomic barrier.</summary>
    [Fact]
    public async Task RuntimeDiagnosticsAtomicRequiredAddCheckRetainsNativeNumberAndDataReason()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `diagnostic_items` (`id` int NOT NULL PRIMARY KEY);");
        var interceptor = new DiagnosticRequiredAddRaceInterceptor();
        await using var context = CreateRuntimeContext(connectionString, interceptor);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<bool>("active", "diagnostic_items", type: "tinyint(1)", nullable: false);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var preserved = await ContextScalarIntAsync(context, "SELECT COUNT(*) FROM `diagnostic_items` WHERE `id` = 9;");
        var cleaned = await ContextScalarIntAsync(context, "SELECT @doka_sm_diag IS NULL;");

        // Assert
        var diagnostic = Assert.IsType<SafeMigrationExecutionException>(failure);
        Assert.Equal("doka_sm_data_blocked", diagnostic.GuardCode);
        Assert.Equal("column_atomic_add_check_failed", diagnostic.ReasonCode);
        Assert.Equal("diagnostic_items", diagnostic.Identity.Table);
        Assert.Equal("active", diagnostic.Identity.ObjectName);
        Assert.Equal(Fixture.IsMariaDb ? 4025 : 3819, Assert.IsType<MySqlException>(diagnostic.InnerException).Number);
        Assert.True(interceptor.Injected);
        Assert.Equal(1, preserved);
        Assert.Equal(1, cleaned);
    }

    /// <summary>Executes generated command ownership unchanged on either supported managed path.</summary>
    private static async Task ExecuteDiagnosticCommandsAsync(DbContext context, IReadOnlyList<MigrationCommand> commands,
        bool synchronous)
    {
        var connection = context.GetService<IRelationalConnection>();
        foreach (var command in commands)
        {
            if (synchronous)
            {
                _ = command.ExecuteNonQuery(connection);
            }
            else
            {
                _ = await command.ExecuteNonQueryAsync(connection);
            }
        }
    }

    /// <summary>Consumes the actual script result stream instead of treating SQL comments as diagnostics.</summary>
    private static async Task<DiagnosticScriptResult> ReadDiagnosticScriptFailureAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        string? documentKind = null;
        string? guard = null;
        string? reason = null;
        string? message = null;
        Exception? failure = null;
        try
        {
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            do
            {
                if (reader.FieldCount == 5 && await reader.ReadAsync(CancellationToken.None))
                {
                    documentKind = reader.GetString("documentKind");
                    guard = reader.GetString("guardCode");
                    reason = reader.GetString("reasonCode");
                    message = reader.GetString("message");
                }
            }
            while (await reader.NextResultAsync(CancellationToken.None));
        }
        catch (MySqlException exception)
        {
            failure = exception;
        }

        return new DiagnosticScriptResult(documentKind, guard, reason, message, failure);
    }

    /// <summary>Retains only test-owned context read before the final script error.</summary>
    private sealed record DiagnosticScriptResult(string? DocumentKind, string? GuardCode, string? ReasonCode,
        string? Message, Exception? Failure);

    /// <summary>Forces the owned postcondition branch after real DDL without disrupting earlier setup or cleanup.</summary>
    private sealed class DiagnosticPostconditionFailureInterceptor : DbCommandInterceptor
    {
        /// <summary>Gets whether the real guarded body, not an unrelated command, received the failure probe.</summary>
        internal bool Injected { get; private set; }

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Injected && command.CommandText.StartsWith("EXECUTE doka_sm_statement;", StringComparison.Ordinal)
                && command.CommandText.Contains("SET @doka_sm_post_ok", StringComparison.Ordinal))
            {
                var assertion = command.CommandText.IndexOf("INSERT INTO `__doka_sm_assert`", StringComparison.Ordinal);
                command.CommandText = command.CommandText.Insert(assertion, "SET @doka_sm_post_ok = FALSE;");
                Injected = true;
            }

            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Inserts one test-owned row immediately before the actual admitted required-column body.</summary>
    private sealed class DiagnosticRequiredAddRaceInterceptor : DbCommandInterceptor
    {
        /// <summary>Gets whether the real two-stage body received the post-classification row.</summary>
        internal bool Injected { get; private set; }

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Injected && command.CommandText.StartsWith("SET @doka_sm_sql = CASE WHEN @doka_sm_action = 'apply' "
                    + "AND NOT @doka_sm_required_add_resume", StringComparison.Ordinal)
                && command.CommandText.Contains("SET @doka_sm_post_ok", StringComparison.Ordinal))
            {
                command.CommandText = "INSERT INTO `diagnostic_items` (`id`) VALUES (9);" + command.CommandText;
                Injected = true;
            }

            return ValueTask.FromResult(result);
        }
    }
}
