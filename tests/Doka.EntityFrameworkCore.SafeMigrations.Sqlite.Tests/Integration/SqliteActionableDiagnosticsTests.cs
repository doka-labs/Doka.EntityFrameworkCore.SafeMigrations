namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Checks readable SQLite diagnostics at their real guarded execution boundaries.</summary>
public sealed class SqliteActionableDiagnosticsTests : SqliteIntegrationTestBase,
    IClassFixture<SqliteConstraintShapeServiceFixture>
{
    private readonly IServiceProvider _internalServices;

    /// <summary>Owns services for the new migrator model without adding process-wide provider-cache entries.</summary>
    /// <param name="services">The class-lifetime scoped SQLite services.</param>
    public SqliteActionableDiagnosticsTests(SqliteConstraintShapeServiceFixture services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _internalServices = services.Provider;
    }

    /// <summary>Rejections retain the correct owner and ordinal after a preceding provider segment.</summary>
    /// <param name="scenario">The rejected data, definition, prerequisite, or qualifier contract.</param>
    /// <param name="asynchronous">Whether to use EF's asynchronous command entry point.</param>
    [Theory]
    [InlineData("data", false)]
    [InlineData("data", true)]
    [InlineData("different", false)]
    [InlineData("different", true)]
    [InlineData("prerequisite", false)]
    [InlineData("prerequisite", true)]
    [InlineData("unsupported", false)]
    [InlineData("unsupported", true)]
    public async Task GuardRejectsWithOwnerAndOriginalOrdinal(string scenario, bool asynchronous)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE diagnostic_left (Id INTEGER NOT NULL); "
            + "CREATE TABLE diagnostic_right (Value TEXT NULL); "
            + "INSERT INTO diagnostic_right VALUES ('private-row-value');");
        if (scenario == "different")
        {
            await ExecuteSqlAsync(connection, "ALTER TABLE diagnostic_right ADD Id TEXT NULL;");
        }

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.Sql("SELECT 1;");
        builder.AddColumnIfNotExists<int>("Id", "diagnostic_left", type: "INTEGER");
        var rejectedOwner = scenario == "prerequisite" ? "diagnostic_absent" : "diagnostic_right";
        builder.AddColumnIfNotExists<int>("Id", rejectedOwner, type: "INTEGER",
            schema: scenario == "unsupported" ? "attached" : null);

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteDiagnosticOperationsAsync(
            context, builder.Operations, asynchronous));

        // Assert
        var rejection = Assert.IsType<InvalidOperationException>(exception);
        var token = scenario switch
        {
            "data" => "doka_sm_data_blocked",
            "different" => "doka_sm_different",
            "prerequisite" => "doka_sm_prerequisite_missing",
            _ => "doka_sm_unsupported",
        };

        Assert.StartsWith(token, rejection.Message, StringComparison.Ordinal);
        Assert.Contains(rejectedOwner, rejection.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic_left", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("Id", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("operation 2", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("reason=", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("next=", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("analysis=", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("decision=", rejection.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-row-value", rejection.Message, StringComparison.Ordinal);
        Assert.InRange(rejection.Message.Length, 1, 2048);
        Assert.Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM diagnostic_right;"));
        Assert.Null(context.GetService<IRelationalConnection>().CurrentTransaction);
    }

    /// <summary>Non-batched schema operations carry their own source ordinal and capability reason.</summary>
    /// <param name="asynchronous">Whether to execute through the asynchronous command entry point.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndividualGuardIncludesCapabilityAndOrdinal(bool asynchronous)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.Sql("SELECT 1;");
        builder.DropSchemaIfExists("main");

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteDiagnosticOperationsAsync(
            context, builder.Operations, asynchronous));

        // Assert
        var rejection = Assert.IsType<InvalidOperationException>(exception);
        Assert.StartsWith("doka_sm_unsupported", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("main", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("operation 1", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("schema_operations", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("next=", rejection.Message, StringComparison.Ordinal);
        Assert.Null(context.GetService<IRelationalConnection>().CurrentTransaction);
    }

    /// <summary>Native provider DDL failures retain their exception type and native error number.</summary>
    /// <param name="asynchronous">Whether to execute through the asynchronous command entry point.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeDdlErrorIsNotRewrittenAsSafeGuard(bool asynchronous)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE diagnostic_collision (Id INTEGER NOT NULL);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.CreateTable("diagnostic_collision", table => new
        {
            Id = table.Column<int>(type: "INTEGER", nullable: false),
        });

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteDiagnosticOperationsAsync(
            context, builder.Operations, asynchronous));

        // Assert
        var native = Assert.IsType<SqliteException>(exception);
        Assert.Equal(1, native.SqliteErrorCode);
        Assert.Contains("diagnostic_collision", native.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("doka_sm_", native.Message, StringComparison.Ordinal);
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'diagnostic_collision';"));
    }

    /// <summary>Owned postcondition failures include identity and roll back the incorrect baseline DDL.</summary>
    /// <param name="batched">Whether to exercise structural batching or the individual command boundary.</param>
    /// <param name="asynchronous">Whether to use the asynchronous command entry point.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PostconditionFailureIsReadableAndRollsBack(bool batched, bool asynchronous)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE diagnostic_postcondition (Id INTEGER NOT NULL);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<string>("Payload", "diagnostic_postcondition", type: "TEXT", nullable: true);
        var operation = Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
        var command = CreateIncorrectBaselineCommand(context, operation, batched);
        var relationalConnection = context.GetService<IRelationalConnection>();

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteDiagnosticCommandAsync(
            command, relationalConnection, asynchronous));

        // Assert
        var rejection = Assert.IsType<InvalidOperationException>(exception);
        Assert.StartsWith("doka_sm_postcondition:", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("diagnostic_postcondition", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("Payload", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("operation 7", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("next=", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("analysis=classified_missing", rejection.Message, StringComparison.Ordinal);
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('diagnostic_postcondition') WHERE name = 'Wrong';"));
        Assert.Null(relationalConnection.CurrentTransaction);
    }

    /// <summary>Integrity diagnostics name the affected relationship without reading or printing row keys.</summary>
    /// <param name="asynchronous">Whether to use the asynchronous command entry point.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeyValidationOmitsPrivateValuesAndRestoresEnforcement(bool asynchronous)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_parents PRIMARY KEY (Id)); "
            + "CREATE TABLE constraint_children (Id INTEGER NOT NULL, ParentId INTEGER NULL, "
            + "CONSTRAINT pk_constraint_children PRIMARY KEY (Id), "
            + "CONSTRAINT fk_constraint_children_parent FOREIGN KEY (ParentId) "
            + "REFERENCES constraint_parents (Id) ON DELETE CASCADE); "
            + "PRAGMA foreign_keys = OFF; "
            + "INSERT INTO constraint_children (Id, ParentId) VALUES (987654321, 876543219); "
            + "PRAGMA foreign_keys = ON;");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("ck_constraint_parents_positive", "constraint_parents", "Id > 0");

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteDiagnosticOperationsAsync(
            context, builder.Operations, asynchronous));

        // Assert
        var rejection = Assert.IsType<InvalidOperationException>(exception);
        Assert.StartsWith("doka_sm_foreign_key_validation_failed", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("constraint_children", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("constraint_parents", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("fk_constraint_children_parent", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("ParentId", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("foreignKeyOrdinal=0", rejection.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("987654321", rejection.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("876543219", rejection.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await ScalarIntAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM constraint_children;"));
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'constraint_parents' "
            + "AND sql LIKE '%ck_constraint_parents_positive%';"));
        Assert.Null(context.GetService<IRelationalConnection>().CurrentTransaction);
    }

    /// <summary>Cancellation leaves the unexecuted guarded batch, catalog and foreign-key setting unchanged.</summary>
    [Fact]
    public async Task PreCancelledGuardPreservesCancellationAndCatalog()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE diagnostic_cancellation (Id INTEGER NOT NULL);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<string>("Payload", "diagnostic_cancellation", type: "TEXT", nullable: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context, builder.Operations, cancellation.Token));

        // Assert
        var cancelled = Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        Assert.DoesNotContain("doka_sm_", cancelled.Message, StringComparison.Ordinal);
        Assert.Equal(1, await ScalarIntAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('diagnostic_cancellation') WHERE name = 'Payload';"));
        Assert.Null(context.GetService<IRelationalConnection>().CurrentTransaction);
    }

    /// <summary>Guarded script generation still fails before producing partial provider SQL.</summary>
    [Fact]
    public async Task UnsupportedScriptExplainsCapabilityAndFirstSafeOperation()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.Sql("CREATE TABLE script_native (Id INTEGER);");
        builder.AddColumnIfNotExists<string>("Payload", "script_owner", type: "TEXT", nullable: true);
        var generator = context.GetService<IMigrationsSqlGenerator>();

        // Act
        var exception = Record.Exception(() => generator.Generate(
            builder.Operations, context.Model, MigrationsSqlGenerationOptions.Script));

        // Assert
        var unsupported = Assert.IsType<NotSupportedException>(exception);
        Assert.StartsWith("doka_sm_script_unsupported", unsupported.Message, StringComparison.Ordinal);
        Assert.Contains("script_owner", unsupported.Message, StringComparison.Ordinal);
        Assert.Contains("operation 1", unsupported.Message, StringComparison.Ordinal);
        Assert.Contains("bundle", unsupported.Message, StringComparison.Ordinal);
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'script_native';"));
    }

    /// <summary>Missing computed-column mappings report owner rather than only an ambiguous column name.</summary>
    [Fact]
    public async Task UnmappedGeneratedColumnNamesOwnerWithoutSqlPayload()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("diagnostic_generated", new ExpectedColumnDefinition(
            "Computed", typeof(UnmappedValue), isNullable: true,
            computedExpression: SafeMigrationSql.Literal("private-expression-payload")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var generator = context.GetService<IMigrationsSqlGenerator>();

        // Act
        var exception = Record.Exception(() => generator.Generate(builder.Operations, context.Model));

        // Assert
        var unsupported = Assert.IsType<NotSupportedException>(exception);
        Assert.StartsWith("doka_sm_unsupported", unsupported.Message, StringComparison.Ordinal);
        Assert.Contains("diagnostic_generated", unsupported.Message, StringComparison.Ordinal);
        Assert.Contains("Computed", unsupported.Message, StringComparison.Ordinal);
        Assert.Contains("column_type_mapping", unsupported.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-expression-payload", unsupported.Message, StringComparison.Ordinal);
        Assert.Contains("next=", unsupported.Message, StringComparison.Ordinal);
    }

    /// <summary>Integrity identities remain bounded ASCII when catalog names contain hostile control text.</summary>
    /// <param name="includeMetadata">Whether the captured catalog has a bound named composite relationship.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignKeyValidationBoundsAndEscapesStructuralIdentifiers(bool includeMetadata)
    {
        // Arrange
        var owner = "private\nowner\u20ac" + new string('x', 4096);
        var principal = "principal\rname" + new string('y', 4096);
        var foreignKey = includeMetadata
            ? new SqliteForeignKeySnapshot(7, owner, principal,
                Enumerable.Repeat(owner, 16).ToArray(), Enumerable.Repeat(principal, 16).ToArray(),
                ReferentialAction.NoAction, ReferentialAction.NoAction, "NONE")
            : null;

        // Act
        var message = SqliteSafeMigrationDiagnosticMessages.FormatForeignKeyValidation(owner, principal, 7, foreignKey);

        // Assert
        Assert.StartsWith("doka_sm_foreign_key_validation_failed", message, StringComparison.Ordinal);
        Assert.All(message, character => Assert.InRange(character, ' ', '~'));
        Assert.InRange(message.Length, 1, 2048);
        Assert.Contains("...", message, StringComparison.Ordinal);
        Assert.Contains("foreignKeyOrdinal=7", message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 129), message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('y', 129), message, StringComparison.Ordinal);
    }

    /// <summary>A successful apply and matching retry do not acquire new rejection behavior.</summary>
    /// <param name="asynchronous">Whether to use the asynchronous command entry point.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingRetryPreservesSuccessfulExecution(bool asynchronous)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE diagnostic_matching (Id INTEGER NOT NULL);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<string>("Payload", "diagnostic_matching", type: "TEXT", nullable: true);

        // Act
        await ExecuteDiagnosticOperationsAsync(context, builder.Operations, asynchronous);
        await ExecuteDiagnosticOperationsAsync(context, builder.Operations, asynchronous);

        // Assert
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('diagnostic_matching') WHERE name = 'Payload';"));
        Assert.Null(context.GetService<IRelationalConnection>().CurrentTransaction);
    }

    /// <summary>The actual EF migrator provides readable rejection without requiring a preflight call.</summary>
    /// <param name="asynchronous">Whether to invoke MigrateAsync or Migrate.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigratorWithoutPreflightReportsGuardAndDoesNotWriteHistory(bool asynchronous)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE diagnostic_migration (Id INTEGER NOT NULL); "
            + "INSERT INTO diagnostic_migration VALUES (123456789);");
        await using var context = new SqliteDiagnosticMigrationContext(connection, _internalServices);

        // Act
        var exception = await Record.ExceptionAsync(async () =>
        {
            if (asynchronous)
            {
                await context.Database.MigrateAsync(CancellationToken.None);
            }
            else
            {
                context.Database.Migrate();
            }
        });

        // Assert
        var rejection = Assert.IsType<InvalidOperationException>(exception);
        Assert.StartsWith("doka_sm_data_blocked", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("diagnostic_migration", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("RequiredValue", rejection.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("123456789", rejection.Message, StringComparison.Ordinal);
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('diagnostic_migration') WHERE name = 'RequiredValue';"));
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId = '"
            + SqliteDiagnosticGuardMigration.MigrationIdentifier + "';"));
    }

    /// <summary>Routes both command entry points through the same isolated execution fixture.</summary>
    private static async Task ExecuteDiagnosticOperationsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        bool asynchronous)
    {
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(operations, context.Model);
        var connection = context.GetService<IRelationalConnection>();
        foreach (var command in commands)
        {
            await ExecuteDiagnosticCommandAsync(command, connection, asynchronous);
        }
    }

    /// <summary>Preserves synchronous exceptions while adapting the fixture to an asynchronous test.</summary>
    private static async Task ExecuteDiagnosticCommandAsync(
        MigrationCommand command,
        IRelationalConnection connection,
        bool asynchronous)
    {
        if (asynchronous)
        {
            _ = await command.ExecuteNonQueryAsync(connection);
        }
        else
        {
            _ = command.ExecuteNonQuery(connection);
        }
    }

    /// <summary>Injects wrong baseline DDL to prove the real postcondition and rollback guards are reached.</summary>
    private static MigrationCommand CreateIncorrectBaselineCommand(
        DbContext context,
        SafeMigrationOperation operation,
        bool batched)
    {
        var dependencies = context.GetService<MigrationsSqlGeneratorDependencies>();
        var nativeCommand = new MigrationCommand(
            dependencies.CommandBuilderFactory.Create()
                .Append("ALTER TABLE diagnostic_postcondition ADD Wrong TEXT NULL;").Build(),
            context, dependencies.Logger, transactionSuppressed: false);

        var analyzer = Assert.IsType<SqliteSafeMigrationProviderAnalyzer>(
            context.GetService<ISafeMigrationProviderAnalyzer>());

        var state = context.GetService<SqliteSafeMigrationExecutionState>();
        var artifacts = SqliteRebuildArtifactContract.FromOperations([operation]);
        var placeholder = dependencies.CommandBuilderFactory.Create().Append("SELECT 0;").Build();

        return batched
            ? new SqliteSafeMigrationBatchCommand(
                [operation], new IncorrectBaselineGenerator(nativeCommand), analyzer, state,
                new SqliteSafeMigrationSqlExpressionRenderer(
                    dependencies.TypeMappingSource, dependencies.SqlGenerationHelper),
                artifacts, context.Model, MigrationsSqlGenerationOptions.Default, placeholder,
                context, dependencies.Logger, firstOperationOrdinal: 7)
            : new SqliteSafeMigrationCommand(operation, [nativeCommand], analyzer, state, artifacts,
                placeholder, context, dependencies.Logger, operationOrdinal: 7);
    }

    /// <summary>Returns executable but deliberately incorrect DDL for non-vacuous postcondition regression.</summary>
    private sealed class IncorrectBaselineGenerator(MigrationCommand command) : ISqliteSafeMigrationsBaselineGenerator
    {
        /// <inheritdoc />
        public IReadOnlyList<MigrationCommand> Generate(
            IReadOnlyList<MigrationOperation> operations,
            IModel? model = null,
            MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default) => [command];
    }

    /// <summary>Has no provider type mapping and supplies no data-bearing object members.</summary>
    private sealed class UnmappedValue { }
}
