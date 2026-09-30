namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Exercises deterministic SQL Server client attention and retained-session recovery.
/// </summary>
public sealed class SqlServerModelManagedDataCancellationTests : SqlServerIntegrationTestBase
{
    /// <summary>
    /// Creates the cancellation suite with an isolated SQL Server fixture.
    /// </summary>
    public SqlServerModelManagedDataCancellationTests(SqlServerContainerFixture fixture) : base(fixture) { }

    /// <summary>
    /// Recovers generated commands automatically and requires explicit recovery for raw scripts.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task AttentionAfterIdentityInsertOn_RecoversSameCallerOwnedSession(
        bool executeMigrationCommand,
        bool callerTransaction
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.identity_roles (Id int IDENTITY(1,1) NOT NULL "
            + "CONSTRAINT PK_identity_roles PRIMARY KEY, Caption nvarchar(80) NOT NULL); "
            + "CREATE TABLE dbo.identity_probe (Id int IDENTITY(1,1) NOT NULL "
            + "CONSTRAINT PK_identity_probe PRIMARY KEY);");
        await using var session = new SqlConnection(connectionString);
        await session.OpenAsync();
        var originalConnectionId = session.ClientConnectionId;
        var identityEnabled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.InfoMessage += (_, message) =>
        {
            foreach (SqlError error in message.Errors)
            {
                if (error.Message == IdentityInsertPauseInterceptor.ReadyMessage)
                {
                    identityEnabled.TrySetResult(true);
                }
            }
        };
        var options = new DbContextOptionsBuilder()
            .UseSqlServer(session)
            .UseSqlServerSafeMigrations()
            .AddInterceptors(new IdentityInsertPauseInterceptor())
            .Options;

        await using var context = new DbContext(options);
        context.Database.SetCommandTimeout(120);
        await using var transaction = callerTransaction
            ? await context.Database.BeginTransactionAsync()
            : null;

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel(
            "identity_roles", ["Id"], ["int"], ["Id", "Caption"], ["int", "nvarchar(80)"],
            new object?[,] { { 7, "Administrator" } });
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);
        var seedCommand = commands.OfType<SqlServerSafeMigrationIdentityInsertCommand>().Single();
        var connection = context.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalConnection>();
        foreach (var command in commands.Where(command => !ReferenceEquals(command, seedCommand)))
        {
            await command.ExecuteNonQueryAsync(connection);
        }

        using var cancellation = new CancellationTokenSource();

        // Act
        var execution = executeMigrationCommand
            ? seedCommand.ExecuteNonQueryAsync(connection, cancellationToken: cancellation.Token)
            : context.Database.ExecuteSqlRawAsync(seedCommand.CommandText, cancellation.Token);

        // WHY: A server NOWAIT message proves ON has executed before the
        // attention is sent. A timer-only cancellation could run before ON
        // and would falsely certify session cleanup that never took place.
        var readinessFailure = await Record.ExceptionAsync(() =>
            identityEnabled.Task.WaitAsync(TimeSpan.FromSeconds(30)));

        cancellation.Cancel();
        var cancellationFailure = await Record.ExceptionAsync(() => execution);

        Exception? leakedSessionFailure = null;
        if (!executeMigrationCommand)
        {
            leakedSessionFailure = await Record.ExceptionAsync(() => context.Database.ExecuteSqlRawAsync(
                "SET IDENTITY_INSERT dbo.identity_probe ON;",
                CancellationToken.None));
            await context.Database.ExecuteSqlRawAsync(
                "SET IDENTITY_INSERT dbo.identity_roles OFF;",
                CancellationToken.None);
        }

        await context.Database.ExecuteSqlRawAsync(
            "SET IDENTITY_INSERT dbo.identity_probe ON; "
            + "INSERT INTO dbo.identity_probe (Id) VALUES (11); "
            + "SET IDENTITY_INSERT dbo.identity_probe OFF;",
            CancellationToken.None);
        if (transaction is not null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }

        var seedCount = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.identity_roles;");

        // Assert
        Assert.Single(commands.OfType<SqlServerSafeMigrationIdentityInsertCommand>());
        Assert.Null(readinessFailure);
        Assert.True(cancellationFailure is OperationCanceledException or SqlException);
        Assert.Equal(ConnectionState.Open, session.State);
        Assert.Equal(originalConnectionId, session.ClientConnectionId);
        Assert.Equal(0, seedCount);
        if (!executeMigrationCommand)
        {
            Assert.Equal(8107, Assert.IsType<SqlException>(leakedSessionFailure).Number);
        }
    }

    private sealed class IdentityInsertPauseInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public const string ReadyMessage = "doka_sm_identity_insert_enabled";

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>>
            NonQueryExecutingAsync(
                System.Data.Common.DbCommand command,
                Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
                Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
                CancellationToken cancellationToken = default
            )
        {
            const string identityOn = "SET IDENTITY_INSERT [dbo].[identity_roles] ON;";

            // WHY: This point is inside the generator's dynamic SQL literal.
            // Double quotes for that literal, not for a separate SQL batch.
            command.CommandText = command.CommandText.Replace(
                identityOn,
                identityOn + " RAISERROR (N''" + ReadyMessage + "'', 10, 1) WITH NOWAIT; "
                    + "WAITFOR DELAY ''00:05:00'';",
                StringComparison.Ordinal);

            return new ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>>(result);
        }
    }
}
