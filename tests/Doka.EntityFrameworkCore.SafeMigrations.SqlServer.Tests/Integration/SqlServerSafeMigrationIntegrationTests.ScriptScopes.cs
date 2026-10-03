namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Concatenated guard batches preserve caller variables and safely bind earlier-created objects.</summary>
    [SqlServerLiveTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GuardedBatch_CombinedGenerationPreservesPrivateScopesAndCallerTransaction(
        bool separateGeneration,
        bool callerTransaction
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
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = callerTransaction ? connection.BeginTransaction() : null;

        // Act
        var commands = separateGeneration
            ? builder.Operations.SelectMany(operation => generator.Generate([operation], context.Model)).ToArray()
            : generator.Generate(builder.Operations, context.Model).ToArray();

        await using var batch = connection.CreateCommand();
        batch.Transaction = transaction;
        batch.CommandText = "DECLARE @doka_state nvarchar(32) = N'caller';\n"
            + "DECLARE @doka_action nvarchar(32) = N'caller';\n"
            + "DECLARE @doka_repair_ok int = 41;\n"
            + "DECLARE @doka_postcondition int = 42;\n"
            + "DECLARE @doka_default_supported int = 43;\n"
            + string.Join("\n", commands.Select(static command => command.CommandText))
            + "\nIF @doka_state <> N'caller' OR @doka_action <> N'caller' "
            + "OR @doka_repair_ok <> 41 OR @doka_postcondition <> 42 OR @doka_default_supported <> 43 "
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

    /// <summary>Executes generated fixture scripts using GO boundaries on one session, like an EF script consumer.</summary>
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
