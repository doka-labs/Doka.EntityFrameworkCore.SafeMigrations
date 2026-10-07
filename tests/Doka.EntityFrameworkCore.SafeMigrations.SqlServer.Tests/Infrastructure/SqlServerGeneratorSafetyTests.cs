namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies guarded T-SQL shape without requiring a live database.
/// </summary>
public sealed class SqlServerGeneratorSafetyTests
{
    private const string ConnectionString =
        "Server=127.0.0.1,1433;Database=generation;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>A safe rename preserves its explicit source schema unless a transfer is requested.</summary>
    [Theory]
    [InlineData(null, "application")]
    [InlineData("target", "target")]
    public void SafeRename_UsesTheSameTargetSchemaForCatalogAndBaseline(string? newSchema, string expectedSchema)
    {
        // Arrange
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseSqlServer(ConnectionString);
        ((DbContextOptionsBuilder)options)
            .UseSqlServerSafeMigrations<RecordingBaselineGenerator, SafeMigrationDbContext>();
        using var context = new SafeMigrationDbContext(options.Options);
        var baseline = context.GetService<RecordingBaselineGenerator>();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("orders", "renamed_orders", schema: "application", newSchema: newSchema);

        // Act
        context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);

        // Assert
        var rename = Assert.Single(baseline.Calls.SelectMany(static operations => operations)
            .OfType<RenameTableOperation>());

        Assert.Equal("application", rename.Schema);
        Assert.Equal(expectedSchema, rename.NewSchema);
    }

    /// <summary>An unchanged identity retains its guards without emitting rename or transfer DDL.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("application")]
    public void SafeRename_UnchangedIdentityOnlyEmitsGuardedValidation(string? schema)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("orders", "orders", schema: schema, newSchema: schema);

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);
        var sql = string.Join("\n", commands.Select(static command => command.CommandText));

        // Assert
        Assert.NotEmpty(commands);
        Assert.Contains("HAS_PERMS_BY_NAME", sql, StringComparison.Ordinal);
        Assert.Contains("doka_sm_postcondition", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("sp_rename", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("TRANSFER", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// Requires metadata, default-schema, action, and postcondition guards before a safe baseline.
    /// </summary>
    [Fact]
    public void SafeOperation_EmitsGuardsAndEscapesQuotedIdentifierLiteral()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("O'Brien");

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);
        var sql = string.Join("\n", commands.Select(static command =>
            SqlServerGuardedSqlTestContract.DecodeScope(command.CommandText)));

        // Assert
        Assert.Contains("HAS_PERMS_BY_NAME", sql, StringComparison.Ordinal);
        Assert.Contains("SCHEMA_NAME()", sql, StringComparison.Ordinal);
        // WHY: The permission-qualified classifier is a nested SQL literal;
        // both quoting levels must retain the original apostrophe as data.
        Assert.Contains("N''O''''Brien''", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("N'O'Brien'", sql, StringComparison.Ordinal);
        Assert.Contains("THROW", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// Forwards included columns through SQL Server's provider-specific index annotation.
    /// </summary>
    [Fact]
    public void IncludedIndex_GeneratesIncludeClauseInsideGuard()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "IX_included_orders_Id",
                "included_orders",
                [new ExpectedIndexKeyDefinition("Id")],
                includedColumns: ["Caption"]),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);
        var sql = string.Join("\n", commands.Select(command => command.CommandText));

        // Assert
        Assert.Contains("INCLUDE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Caption", sql, StringComparison.Ordinal);
        Assert.Contains("sp_executesql", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects a provider-unsupported index method before rendering any command.
    /// </summary>
    [Fact]
    public void UnsupportedIndexMethod_DoesNotRenderBaselineDdl()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "IX_unsupported_orders_Id",
                "unsupported_orders",
                [new ExpectedIndexKeyDefinition("Id")],
                method: "hash"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var exception = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model));

        // Assert
        Assert.IsType<NotSupportedException>(exception);
    }

    /// <summary>
    /// Preflights all safe intents before delegating an earlier ordinary EF operation.
    /// </summary>
    [Fact]
    public void UnsupportedSafeIntentAfterOrdinaryDdl_FailsBeforeAnyProviderSql()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseSqlServer(ConnectionString);
        ((DbContextOptionsBuilder)options)
            .UseSqlServerSafeMigrations<RecordingBaselineGenerator, SafeMigrationDbContext>();
        using var context = new SafeMigrationDbContext(options.Options);
        var baseline = context.GetService<RecordingBaselineGenerator>();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Sql("CREATE TABLE dbo.ordinary_before_unsupported (Id int NOT NULL);");
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "IX_unsupported_orders_Id",
                "unsupported_orders",
                [new ExpectedIndexKeyDefinition("Id")],
                method: "hash"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var exception = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model));

        // Assert
        Assert.IsType<NotSupportedException>(exception);
        Assert.Empty(baseline.Calls);
    }

    /// <summary>
    /// Uses the analyzer's physical identifier contract before even preceding ordinary DDL.
    /// </summary>
    [Fact]
    public void IdentifierAliases_PrependCatalogCollationGuardBeforeOrdinaryDdl()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Sql("CREATE TABLE dbo.ordinary_before_aliases (Id int NOT NULL);");
        builder.DropTableIfExists("Roles");
        builder.DropTableIfExists("roles", schema: "dbo");

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);
        var firstCommand = commands[0].CommandText;

        // Assert
        Assert.Contains("COLLATE CATALOG_DEFAULT", firstCommand, StringComparison.Ordinal);
        Assert.Contains("Latin1_General_100_BIN2", firstCommand, StringComparison.Ordinal);
        Assert.Contains("DATALENGTH(name)", firstCommand, StringComparison.Ordinal);
        Assert.Contains("THROW 51002", firstCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("ordinary_before_aliases", firstCommand, StringComparison.Ordinal);
        Assert.Contains("ordinary_before_aliases", commands[1].CommandText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Leaves ordinary SQL Server generation untouched when SafeMigrations is not enabled.
    /// </summary>
    [Fact]
    public void DisabledSafeMigrations_PreservesOrdinaryEfGeneratorAndSql()
    {
        // Arrange
        var options = new DbContextOptionsBuilder().UseSqlServer(ConnectionString).Options;
        using var context = new DbContext(options);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var operation = new SqlOperation { Sql = "SELECT 7;" };

        // Act
        var commands = generator.Generate([operation]);

        // Assert
        Assert.IsNotType<SqlServerSafeMigrationsSqlGenerator>(generator);
        Assert.Contains("SELECT 7;", Assert.Single(commands).CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("doka_sm_", commands[0].CommandText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects a custom baseline that omits a guard or changes its transaction boundary.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CustomBaseline_InvalidGuardOutputIsRejected(
        bool identifierGuard,
        bool transactionSuppressed
    )
    {
        // Arrange
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseSqlServer(ConnectionString);
        ((DbContextOptionsBuilder)options)
            .UseSqlServerSafeMigrations<RecordingBaselineGenerator, SafeMigrationDbContext>();
        using var context = new SafeMigrationDbContext(options.Options);
        var baseline = context.GetService<RecordingBaselineGenerator>();
        IReadOnlyList<MigrationCommand> invalidCommands = transactionSuppressed
            ? [baseline.CreateCommand("SELECT 1;", true)]
            : [];

        if (identifierGuard)
        {
            baseline.IdentifierGuardCommands = invalidCommands;
        }
        else
        {
            baseline.GuardedSqlCommands = invalidCommands;
        }

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("Amount", "items", type: "int", nullable: true);

        // Act
        var exception = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model));

        // Assert
        if (transactionSuppressed)
        {
            Assert.IsType<NotSupportedException>(exception);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(exception);
        }
    }

    /// <summary>
    /// Rejects a transaction-suppressed safe baseline before a guard can be delegated.
    /// </summary>
    [Fact]
    public void TransactionSuppressedSafeBaseline_IsRejectedBeforeGuardGeneration()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseSqlServer(ConnectionString);
        ((DbContextOptionsBuilder)options)
            .UseSqlServerSafeMigrations<RecordingBaselineGenerator, SafeMigrationDbContext>();
        using var context = new SafeMigrationDbContext(options.Options);
        var baseline = context.GetService<RecordingBaselineGenerator>();
        baseline.ColumnCommands = [baseline.CreateCommand("ALTER TABLE [items] ADD [Amount] int NULL;", true)];
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("Amount", "items", type: "int", nullable: true);

        // Act
        var exception = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model));

        // Assert
        Assert.IsType<NotSupportedException>(exception);
        Assert.Single(baseline.Calls);
        Assert.IsType<AddColumnOperation>(Assert.Single(baseline.Calls[0]));
    }

    /// <summary>
    /// Preserves the complete temporal EF operation segment when no SafeMigrations boundary intervenes.
    /// </summary>
    [Fact]
    public void OrdinaryTemporalSegment_DelegatesUnchangedToEfBaseline()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseSqlServer(ConnectionString);
        ((DbContextOptionsBuilder)options)
            .UseSqlServerSafeMigrations<RecordingBaselineGenerator, SafeMigrationDbContext>();
        using var context = new SafeMigrationDbContext(options.Options);
        var baseline = context.GetService<RecordingBaselineGenerator>();
        var table = new CreateTableOperation { Name = "temporal_orders" };
        table["SqlServer:IsTemporal"] = true;
        var column = new AddColumnOperation
        {
            Name = "Subject",
            Table = "temporal_orders",
            ClrType = typeof(string),
            ColumnType = "nvarchar(80)",
            IsNullable = true,
        };

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([table, column], context.Model);

        // Assert
        Assert.Equal(2, commands.Count);
        Assert.Collection(baseline.Calls,
            batch => Assert.Collection(batch,
                operation => Assert.Same(table, operation),
                operation => Assert.Same(column, operation)));
    }

    /// <summary>
    /// Rejects a SafeMigrations boundary that would split EF's temporal rewrite state.
    /// </summary>
    [Fact]
    public void SafeBoundaryInsideTemporalStream_FailsBeforeAnyProviderSql()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseSqlServer(ConnectionString);
        ((DbContextOptionsBuilder)options)
            .UseSqlServerSafeMigrations<RecordingBaselineGenerator, SafeMigrationDbContext>();
        using var context = new SafeMigrationDbContext(options.Options);
        var baseline = context.GetService<RecordingBaselineGenerator>();
        var table = new CreateTableOperation { Name = "temporal_orders" };
        table["SqlServer:IsTemporal"] = true;
        var safeBuilder = new MigrationBuilder(context.Database.ProviderName!);
        safeBuilder.DropTableIfExists("obsolete_orders");
        var column = new AddColumnOperation
        {
            Name = "Subject",
            Table = "temporal_orders",
            ClrType = typeof(string),
            ColumnType = "nvarchar(80)",
            IsNullable = true,
        };

        // Act
        var exception = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate(
                [table, safeBuilder.Operations[0], column],
                context.Model));

        // Assert
        Assert.IsType<NotSupportedException>(exception);
        Assert.Empty(baseline.Calls);
    }

    /// <summary>
    /// Rejects both ordinary-only and guarded retries before the enabled generator invokes its baseline.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuarantinedContext_RejectsAllLaterGenerationBeforeBaseline(
        bool guarded
    )
    {
        // Arrange
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseSqlServer(ConnectionString);
        ((DbContextOptionsBuilder)options)
            .UseSqlServerSafeMigrations<RecordingBaselineGenerator, SafeMigrationDbContext>();
        using var context = new SafeMigrationDbContext(options.Options);
        var baseline = context.GetService<RecordingBaselineGenerator>();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)
            context.GetService<ISafeMigrationProviderAnalyzer>();

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (guarded)
        {
            builder.AddColumnIfNotExists<int>("Amount", "items", type: "int", nullable: true);
        }
        else
        {
            builder.Sql("SELECT 7;");
        }

        analyzer.QuarantineConnection();

        // Act
        var failure = Record.Exception(() => generator.Generate(builder.Operations));

        // Assert
        Assert.IsType<SqlServerSafeMigrationProviderAnalyzer>(analyzer);
        Assert.Contains("requires a new context", Assert.IsType<InvalidOperationException>(failure).Message,
            StringComparison.Ordinal);
        Assert.Empty(baseline.Calls);
    }

    /// <summary>
    /// Gates every command in a guarded stream, including its ordinary prefix and identifier preflight.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuarantinedContext_RejectsAlreadyGeneratedGuardedStream(
        bool asynchronous
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Sql("SELECT 7;");
        builder.AddColumnIfNotExists<int>("Amount", "items", type: "int", nullable: true);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations);
        var connection = context.GetService<IRelationalConnection>();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)
            context.GetService<ISafeMigrationProviderAnalyzer>();

        analyzer.QuarantineConnection();

        // Act
        var failures = new List<Exception?>();
        foreach (var command in commands)
        {
            failures.Add(asynchronous
                ? await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync(connection,
                    cancellationToken: cancellation.Token))
                : Record.Exception(() =>
                {
                    command.ExecuteNonQuery(connection);
                }));
        }

        // Assert
        Assert.IsType<SqlServerSafeMigrationProviderAnalyzer>(analyzer);
        Assert.True(commands.Count >= 3);
        Assert.Contains(commands, command => command.CommandText.Contains("SELECT 7;", StringComparison.Ordinal));
        Assert.All(commands, command => Assert.IsType<SqlServerSafeMigrationGuardedCommand>(command));
        Assert.All(failures, failure =>
            Assert.Contains("requires a new context", Assert.IsType<InvalidOperationException>(failure).Message,
                StringComparison.Ordinal));
        Assert.Equal(ConnectionState.Closed, connection.DbConnection.State);
    }

    /// <summary>
    /// Rejects cached ordinary-only commands created before another migration quarantines their context.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuarantinedContext_RejectsCachedOrdinaryOnlyCommands(
        bool asynchronous
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Sql("SELECT 7;");
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations);
        var command = commands.Single();
        var connection = context.GetService<IRelationalConnection>();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)
            context.GetService<ISafeMigrationProviderAnalyzer>();

        analyzer.QuarantineConnection();

        // Act
        var failure = asynchronous
            ? await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync(connection))
            : Record.Exception(() =>
            {
                command.ExecuteNonQuery(connection);
            });

        // Assert
        Assert.Single(commands);
        Assert.IsType<SqlServerSafeMigrationProviderAnalyzer>(analyzer);
        Assert.IsType<SqlServerSafeMigrationGuardedCommand>(command);
        Assert.Contains("SELECT 7;", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("requires a new context", Assert.IsType<InvalidOperationException>(failure).Message,
            StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, connection.DbConnection.State);
    }

    private sealed class RecordingBaselineGenerator : IMigrationsSqlGenerator
    {
        private readonly MigrationsSqlGeneratorDependencies _dependencies;

        public RecordingBaselineGenerator(
            MigrationsSqlGeneratorDependencies dependencies
        )
        {
            _dependencies = dependencies;
        }

        public IReadOnlyList<MigrationCommand> ColumnCommands { get; set; } = [];

        public IReadOnlyList<MigrationCommand>? GuardedSqlCommands { get; set; }

        public IReadOnlyList<MigrationCommand>? IdentifierGuardCommands { get; set; }

        public List<IReadOnlyList<MigrationOperation>> Calls { get; } = [];

        public MigrationCommand CreateCommand(
            string sql,
            bool suppressTransaction
        )
        {
            var relationalCommand = _dependencies.CommandBuilderFactory.Create().Append(sql).Build();

            return new MigrationCommand(
                relationalCommand,
                _dependencies.CurrentContext.Context,
                _dependencies.Logger,
                suppressTransaction);
        }

        public IReadOnlyList<MigrationCommand> Generate(
            IReadOnlyList<MigrationOperation> operations,
            IModel? model = null,
            MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default
        )
        {
            Calls.Add(operations.ToArray());

            if (operations.Count == 1 && operations[0] is SqlOperation sqlOperation)
            {
                var operationGuard = sqlOperation.Sql.Contains("DECLARE @doka_state", StringComparison.Ordinal);
                if (GuardedSqlCommands is { } guardedCommands
                    && operationGuard)
                {
                    return guardedCommands;
                }

                if (IdentifierGuardCommands is { } identifierCommands
                    && !operationGuard
                    && sqlOperation.Sql.StartsWith("EXEC sys.sp_executesql N'", StringComparison.Ordinal))
                {
                    return identifierCommands;
                }
            }

            if (ColumnCommands.Count > 0
                && operations.Any(static operation => operation is AddColumnOperation))
            {
                return ColumnCommands;
            }

            return operations
                .Select(operation => operation is SqlOperation sql
                    ? CreateCommand(sql.Sql, sql.SuppressTransaction)
                    : CreateCommand("SELECT 1;", false))
                .ToArray();
        }
    }
}
