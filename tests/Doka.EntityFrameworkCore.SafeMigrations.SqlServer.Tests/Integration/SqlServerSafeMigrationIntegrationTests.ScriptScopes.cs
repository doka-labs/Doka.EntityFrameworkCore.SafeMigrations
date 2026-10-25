namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies script scopes SQL Server contracts against isolated databases.</summary>
public sealed class SqlServerSafeMigrationScriptScopesIntegrationTests : SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Creates the feature suite with the assembly-owned SQL Server fixture.</summary>
    /// <param name="fixture">The shared fixture providing a database per test.</param>
    public SqlServerSafeMigrationScriptScopesIntegrationTests(
        SqlServerContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Compiler, provenance and analysis paths preserve continuation-shaped identities.</summary>
    [SqlServerLiveFact]
    public async Task ContinuationNamesPreserveCompilerProvenanceAnalysisAndReplay()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();

        // Act
        var actual = await ObserveContinuationContractsAsync(connectionString);

        // Assert
        Assert.True(actual.Matching);
        Assert.Equal(SafeMigrationObservedState.Different, actual.Drift);
        Assert.Equal(2, actual.Columns);
        Assert.Equal(5, actual.Stamps);
    }

    /// <summary>Newline-bearing column, index and table renames retain identities and individually replay.</summary>
    [SqlServerLiveFact]
    public async Task ContinuationRenamesPreserveIdentitiesAndReplay()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();

        // Act
        var actual = await ObserveContinuationRenamesAsync(connectionString);

        // Assert
        Assert.Equal(3, actual);
    }

    /// <summary>Runs all three guarded rename forms plus schema-only transfer against one owned database.</summary>
    /// <param name="connectionString">The initially empty disposable database.</param>
    /// <returns>The independent physical table, column and index witness count.</returns>
    internal static async Task<int> ObserveContinuationRenamesAsync(string connectionString)
    {
        const string schema = "rename\\\nsource";
        const string targetSchema = "rename\\\rtarget";
        const string table = "old\\\ntable";
        const string newTable = "new\\\r\ntable";
        const string column = "old\\\ncolumn";
        const string newColumn = "new\\\rcolumn";
        const string index = "old\\\nindex";
        const string newIndex = "new\\\r\nindex";
        await using var context = CreateContext(connectionString);
        var setup = new MigrationBuilder(context.Database.ProviderName!);
        setup.EnsureSchemaExists(schema);
        setup.EnsureSchemaExists(targetSchema);
        setup.Operations.Add(new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition(table,
        [
            new ExpectedColumnDefinition("Id", typeof(int), false, "int"),
            new ExpectedColumnDefinition(column, typeof(int), true, "int"),
        ], schema), SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent));
        setup.CreateIndexIfNotExists(index, table, ["Id"], schema: schema);
        await ExecuteOperationsAsync(context, setup.Operations);
        var renames = new MigrationBuilder(context.Database.ProviderName!);
        renames.RenameColumnIfExists(column, table, newColumn, schema);
        renames.RenameIndexIfExists(index, table, newIndex, schema);
        renames.RenameTableIfExists(table, newTable, schema, targetSchema);
        renames.RenameTableIfExists(newTable, newTable, targetSchema, schema);
        foreach (var operation in renames.Operations)
        {
            await ExecuteOperationsAsync(context, [operation]);
            await ExecuteOperationsAsync(context, [operation]);
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = SafeMigrationDbContext.CommandTimeoutSeconds;
        command.CommandText = "SELECT (SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(@table)), "
            + "(SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(@table) AND name=@column), "
            + "(SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(@table) AND name=@index);";
        command.Parameters.Add("@table", SqlDbType.NVarChar, 512).Value =
            context.GetService<ISqlGenerationHelper>().DelimitIdentifier(newTable, schema);
        command.Parameters.Add("@column", SqlDbType.NVarChar, 128).Value = newColumn;
        command.Parameters.Add("@index", SqlDbType.NVarChar, 128).Value = newIndex;
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return reader.GetInt32(0) + reader.GetInt32(1) + reader.GetInt32(2);
    }

    /// <summary>Runs the same provider contracts on one independently owned native diagnostic database.</summary>
    /// <param name="connectionString">The explicit connection to a disposable, initially empty database.</param>
    /// <returns>Fresh classification and independent parameterized physical metadata observations.</returns>
    internal static async Task<(bool Matching, SafeMigrationObservedState? Drift, int Columns, int Stamps)>
        ObserveContinuationContractsAsync(string connectionString)
    {
        const string schema = "schema\\\nline";
        const string table = "table\\\r\nline";
        const string first = "constant\\\nfirst";
        const string second = "constant\\\r\nsecond";
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureSchemaExists(schema);
        builder.Operations.Add(new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition(table,
        [
            new ExpectedColumnDefinition("Id", typeof(int), false, "int"),
            new ExpectedColumnDefinition("Value", typeof(int), true, "int",
                defaultValue: SafeMigrationDefaultValue.Sql("1")),
            new ExpectedColumnDefinition(first, typeof(int), false, "int", computedColumnSql: "1", isStored: false),
            new ExpectedColumnDefinition(second, typeof(int), false, "int", computedColumnSql: "2", isStored: false),
        ], schema), SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent));
        builder.AddCheckConstraintIfNotExists("check\\\nfirst", table, "[Id] >= 0", schema: schema);
        builder.AddCheckConstraintIfNotExists("check\\\r\nsecond", table, "[Id] <= 100", schema: schema);
        builder.CreateIndexIfNotExists("index\\\nfirst", table, ["Id"], schema: schema, filter: "[Id] >= 0");
        builder.CreateIndexIfNotExists("index\\\r\nsecond", table, [first], schema: schema, filter: "[Id] >= 0");
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var matching = await analyzer.AnalyzeAsync(context,
            builder.Operations.OfType<SafeMigrationOperation>().ToArray());
        var drift = await analyzer.AnalyzeAsync(context,
        [
            new SafeMigrationOperation(new EnsureColumnIntent(table,
                new ExpectedColumnDefinition("Value", typeof(int), true, "int",
                    defaultValue: SafeMigrationDefaultValue.Sql("2")), schema), SafeMigrationPolicy.ThrowIfDifferent),
        ]);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = SafeMigrationDbContext.CommandTimeoutSeconds;
        command.CommandText = "SELECT (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(@table) "
            + "AND name IN (@first,@second)), (SELECT COUNT(*) FROM sys.extended_properties ep "
            + "WHERE ep.name=N'Doka:SafeMigrations:Contract' AND (ep.major_id=OBJECT_ID(@table) "
            + "OR ep.major_id IN (SELECT object_id FROM sys.objects WHERE parent_object_id=OBJECT_ID(@table))));";
        command.Parameters.Add("@table", SqlDbType.NVarChar, 512).Value =
            context.GetService<ISqlGenerationHelper>().DelimitIdentifier(table, schema);
        command.Parameters.Add("@first", SqlDbType.NVarChar, 128).Value = first;
        command.Parameters.Add("@second", SqlDbType.NVarChar, 128).Value = second;
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return (matching.All(static result => result.ObservedState == SafeMigrationObservedState.Matching),
            drift[0].ObservedState, reader.GetInt32(0), reader.GetInt32(1));
    }

    /// <summary>Real scripts preserve long multiline defaults across migrations and idempotent replay.</summary>
    /// <param name="idempotent">Whether to qualify script replay as well as initial application.</param>
    /// <param name="noTransactions">Whether the client requests the supported transaction-free script form.</param>
    [SqlServerLiveTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HistoryScriptsPreserveMultilineDefaultsAndIdempotentReplay(
        bool idempotent,
        bool noTransactions
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = new SqlServerMultilineScriptContext(connectionString);
        var options = (idempotent ? MigrationsSqlGenerationOptions.Idempotent : MigrationsSqlGenerationOptions.Default)
            | (noTransactions ? MigrationsSqlGenerationOptions.NoTransactions : MigrationsSqlGenerationOptions.Default);

        var script = context.GetService<IMigrator>().GenerateScript(
            Migration.InitialDatabase, SqlServerMultilineUpgradeMigration.MigrationIdentifier, options);

        // Act
        await ExecuteGeneratedScriptAsync(connectionString, script);
        if (idempotent)
        {
            await ExecuteGeneratedScriptAsync(connectionString, script);
        }

        await ExecuteSqlAsync(connectionString, "INSERT dbo.multiline_payload_items (Id) VALUES (7);");
        var actual = await ReadMultilineScriptStateAsync(connectionString);

        // Assert
        Assert.Equal(SqlServerMultilineInitialMigration.Payload, actual.Initial);
        Assert.Equal(SqlServerMultilineUpgradeMigration.Payload, actual.Later);
        Assert.Equal(2, actual.History);
    }

    /// <summary>The actual EF runtime migrator and replay preserve the same authored defaults and identifier.</summary>
    [SqlServerLiveFact]
    public async Task RuntimeMigrationPreservesMultilineDefaultsAndReplay()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = new SqlServerMultilineScriptContext(connectionString);
        var migrator = context.GetService<IMigrator>();

        // Act
        await migrator.MigrateAsync(SqlServerMultilineUpgradeMigration.MigrationIdentifier);
        await migrator.MigrateAsync(SqlServerMultilineUpgradeMigration.MigrationIdentifier);
        await ExecuteSqlAsync(connectionString, "INSERT dbo.multiline_payload_items (Id) VALUES (7);");
        var actual = await ReadMultilineScriptStateAsync(connectionString);

        // Assert
        Assert.Equal(SqlServerMultilineInitialMigration.Payload, actual.Initial);
        Assert.Equal(SqlServerMultilineUpgradeMigration.Payload, actual.Later);
        Assert.Equal(2, actual.History);
    }

    /// <summary>Lossless framing cannot turn an actual raw-default difference into an accepted match.</summary>
    [SqlServerLiveFact]
    public async Task IdempotentHistoryScriptRetainsDefaultDriftRejection()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = new SqlServerMultilineScriptContext(connectionString);
        var initial = context.GetService<IMigrator>().GenerateScript(
            Migration.InitialDatabase, SqlServerMultilineInitialMigration.MigrationIdentifier,
            MigrationsSqlGenerationOptions.Idempotent);
        await ExecuteGeneratedScriptAsync(connectionString, initial);
        await ExecuteSqlAsync(connectionString,
            "ALTER TABLE dbo.multiline_payload_items ADD [" + SqlServerMultilineUpgradeMigration.ColumnName
            + "] nvarchar(80) NULL DEFAULT 'wrong';");
        var script = context.GetService<IMigrator>().GenerateScript(
            SqlServerMultilineInitialMigration.MigrationIdentifier,
            SqlServerMultilineUpgradeMigration.MigrationIdentifier,
            MigrationsSqlGenerationOptions.Idempotent);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteGeneratedScriptAsync(connectionString, script));

        // Assert
        var actual = Assert.IsType<SqlException>(failure);

        Assert.Equal(51001, actual.Number);
        Assert.Contains("doka_sm_different", actual.Message, StringComparison.Ordinal);
    }

    /// <summary>Observes exact authored values and both actual migration identities from one synthetic row.</summary>
    /// <param name="connectionString">The independently owned fixture database.</param>
    /// <returns>The persisted default results and matching migration-history count.</returns>
    private static async Task<(string? Initial, string? Later, int History)> ReadMultilineScriptStateAsync(
        string connectionString
    )
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = SafeMigrationDbContext.CommandTimeoutSeconds;
        command.CommandText = "SELECT Payload, [" + SqlServerMultilineUpgradeMigration.ColumnName
            + "], (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId IN (N'"
            + SqlServerMultilineInitialMigration.MigrationIdentifier + "', N'"
            + SqlServerMultilineUpgradeMigration.MigrationIdentifier + "')) "
            + "FROM dbo.multiline_payload_items WHERE Id = 7;";
        await using var reader = await command.ExecuteReaderAsync();

        return await reader.ReadAsync() ? (reader.GetString(0), reader.GetString(1), reader.GetInt32(2)) : default;
    }

    /// <summary>Concatenated guard batches preserve caller variables and safely bind earlier-created objects.</summary>
    /// <param name="separateGeneration">Whether each operation comes from an independent generator invocation.</param>
    /// <param name="callerTransaction">Whether the caller owns the transaction rolled back after observation.</param>
    /// <param name="idempotent">Whether complete commands use the private single-line script transport.</param>
    [SqlServerLiveTheory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task GuardedBatch_CombinedGenerationPreservesPrivateScopesAndCallerTransaction(
        bool separateGeneration,
        bool callerTransaction,
        bool idempotent
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("scope'items",
            table => new { Id = table.Column<int>(type: "int", nullable: false) },
            constraints: table => table.PrimaryKey("PK_scope'items", row => row.Id));
        builder.AddColumnIfNotExists<string>("Caption", "scope'items", type: "varchar(80)", maxLength: 80,
            nullable: false, defaultValue: "O'Brien",
            collation: new SafeMigrationCollationIdentifier("Latin1_General_100_CI_AS"));

        builder.EnsureModelManagedDataFromModel("scope'items", ["Id"], ["int"], ["Id", "Caption"],
            ["int", "varchar(80)"], new object?[,] { { 7, "O'Brien" } });
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var options = idempotent
            ? MigrationsSqlGenerationOptions.Script | MigrationsSqlGenerationOptions.Idempotent
            : MigrationsSqlGenerationOptions.Default;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = callerTransaction ? connection.BeginTransaction() : null;

        // Act
        var commands = separateGeneration
            ? builder.Operations.SelectMany(operation => generator.Generate([operation], context.Model, options))
                .ToArray()
            : generator.Generate(builder.Operations, context.Model, options).ToArray();

        await using var batch = connection.CreateCommand();
        batch.Transaction = transaction;
        batch.CommandText = "DECLARE @doka_state nvarchar(32) = N'caller';\n"
            + "DECLARE @doka_action nvarchar(32) = N'caller';\n"
            + "DECLARE @doka_repair_ok int = 41;\n"
            + "DECLARE @doka_postcondition int = 42;\n"
            + "DECLARE @doka_default_supported int = 43;\n"
            + "DECLARE @doka_script_payload nvarchar(max) = N'caller';\n"
            + string.Join("\n", commands.Select(static command => command.CommandText))
            + "\nIF @doka_state <> N'caller' OR @doka_action <> N'caller' "
            + "OR @doka_repair_ok <> 41 OR @doka_postcondition <> 42 OR @doka_default_supported <> 43 "
            + "OR @doka_script_payload <> N'caller' "
            + "THROW 51011, N'Caller guard variables changed', 1;";

        await batch.ExecuteNonQueryAsync();
        await using var observation = connection.CreateCommand();
        observation.Transaction = transaction;
        observation.CommandText = "SELECT COUNT(*) FROM dbo.[scope'items] WHERE Id = 7 AND Caption = 'O''Brien';";
        var seedCount = Convert.ToInt32(await observation.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

        if (transaction is not null)
        {
            await transaction.RollbackAsync();
        }

        var remainingTables = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'[dbo].[scope''items]', N'U');");

        // Assert
        Assert.Equal(1, seedCount);
        Assert.Equal(callerTransaction ? 0 : 1, remainingTables);
        Assert.All(commands, static command => Assert.False(command.TransactionSuppressed));
    }

    /// <summary>Real EF normal and idempotent scripts apply all guards and record migration history once.</summary>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EfScript_NormalAndIdempotentCommandsApplyAndRetainHistory(
        bool idempotent
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        var options = idempotent ? MigrationsSqlGenerationOptions.Idempotent : MigrationsSqlGenerationOptions.Default;

        // Act
        var script = migrator.GenerateScript(Migration.InitialDatabase,
            SqlServerHistoryMigration.MigrationIdentifier, options);

        await ExecuteGeneratedScriptAsync(connectionString, script);
        if (idempotent)
        {
            await ExecuteGeneratedScriptAsync(connectionString, script);
        }

        var historyCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory "
            + $"WHERE MigrationId = N'{SqlServerHistoryMigration.MigrationIdentifier}';");

        var columnCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.safe_history_probe') "
            + "AND name = N'Caption' AND is_nullable = 0;");

        var checkCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.safe_history_probe') "
            + "AND name = N'CK_safe_history_probe_Id';");

        // Assert
        Assert.Equal(1, historyCount);
        Assert.Equal(1, columnCount);
        Assert.Equal(1, checkCount);
        Assert.Contains("EXEC sys.sp_executesql", script, StringComparison.Ordinal);
        Assert.Contains("DECLARE @doka_state", script, StringComparison.Ordinal);
    }

    /// <summary>A real scripted conflict retains the safe error and rolls back ordinary DDL and history.</summary>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EfScript_ConflictingGuardPreservesErrorAndUnappliedHistory(
        bool idempotent
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.safe_history_probe (Id bigint NOT NULL);");
        await using var context = CreateContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        var options = idempotent ? MigrationsSqlGenerationOptions.Idempotent : MigrationsSqlGenerationOptions.Default;
        var script = migrator.GenerateScript(Migration.InitialDatabase,
            SqlServerHistoryMigration.MigrationIdentifier, options);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteGeneratedScriptAsync(connectionString, script));
        var ordinaryTables = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.ordinary_pipeline_probe', N'U');");

        var historyCount = await ScalarIntAsync(connectionString,
            "IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL SELECT 0; "
            + "ELSE SELECT COUNT(*) FROM dbo.__EFMigrationsHistory "
            + $"WHERE MigrationId = N'{SqlServerHistoryMigration.MigrationIdentifier}';");

        // Assert
        var sqlFailure = Assert.IsType<SqlException>(failure);

        Assert.Equal(51001, sqlFailure.Number);
        Assert.Contains("doka_sm_different", sqlFailure.Message, StringComparison.Ordinal);
        Assert.Equal(0, ordinaryTables);
        Assert.Equal(0, historyCount);
    }

    /// <summary>Executes generated fixture scripts with GO batch boundaries on one client session.</summary>
    private static async Task ExecuteGeneratedScriptAsync(
        string connectionString,
        string script
    )
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var reader = new StringReader(script);
        var batch = new StringBuilder();

        // WHY: GO is a client-side EF script separator, not server SQL. These
        // fixed fixtures contain no GO lines inside authored SQL literals;
        // retaining one connection preserves the script's transaction/history.
        while (reader.ReadLine() is { } line)
        {
            if (!line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                batch.AppendLine(line);

                continue;
            }

            await ExecuteScriptBatchAsync(connection, batch);
        }

        await ExecuteScriptBatchAsync(connection, batch);
    }

    /// <summary>Executes a nonempty script batch and clears only the completed client-side buffer.</summary>
    private static async Task ExecuteScriptBatchAsync(
        SqlConnection connection,
        StringBuilder batch
    )
    {
        if (batch.Length == 0)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = batch.ToString();

        await command.ExecuteNonQueryAsync();
        batch.Clear();
    }
}
