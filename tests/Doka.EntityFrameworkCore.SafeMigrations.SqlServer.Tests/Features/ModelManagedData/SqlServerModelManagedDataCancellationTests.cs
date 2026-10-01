namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Exercises deterministic SQL Server client attention and retained-session recovery.
/// </summary>
public sealed partial class SqlServerModelManagedDataCancellationTests : SqlServerIntegrationTestBase
{
    /// <summary>
    /// Creates the cancellation suite with an isolated SQL Server fixture.
    /// </summary>
    public SqlServerModelManagedDataCancellationTests(
        SqlServerContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>
    /// Clears inherited identity state for managed commands while raw dynamic batches restore caller state.
    /// </summary>
    /// <param name="executeMigrationCommand">Whether to execute the managed recovery boundary.</param>
    /// <param name="callerTransaction">Whether the retained caller session has an explicit transaction.</param>
    /// <param name="inheritedIdentityInsert">Whether the caller enables identity insertion before execution.</param>
    [SqlServerLiveTheory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public async Task AttentionAfterIdentityInsertOn_RecoversSameCallerOwnedSession(
        bool executeMigrationCommand,
        bool callerTransaction,
        bool inheritedIdentityInsert
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
        var readinessResource = "doka_identity_ready_" + Guid.NewGuid().ToString("N");
        var pauseInterceptor = new SqlServerIdentityInsertPauseInterceptor(readinessResource);
        await using var observer = new SqlConnection(connectionString);
        await observer.OpenAsync();

        var options = new DbContextOptionsBuilder()
            .UseSqlServer(session)
            .UseSqlServerSafeMigrations()
            .AddInterceptors(pauseInterceptor)
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

        Exception? inheritedIdentityFailure = null;

        if (inheritedIdentityInsert)
        {
            // WHY: An unparameterized caller batch retains ON outside the generated sp_executesql scope.
            // Bypass the pause interceptor, which injects SQL quoted for the generator's dynamic batch.
            await using var enableIdentity = session.CreateCommand();
            enableIdentity.Transaction = (SqlTransaction?)transaction?.GetDbTransaction();
            enableIdentity.CommandText = "SET IDENTITY_INSERT dbo.identity_roles ON;";

            await enableIdentity.ExecuteNonQueryAsync(CancellationToken.None);
            inheritedIdentityFailure = await Record.ExceptionAsync(() => context.Database.ExecuteSqlRawAsync(
                "SET IDENTITY_INSERT dbo.identity_probe ON;",
                CancellationToken.None));
        }

        var requiresCallerRecovery = inheritedIdentityInsert && !executeMigrationCommand;
        using var cancellation = new CancellationTokenSource();
        using var readinessDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var markerInitiallyAvailable = await pauseInterceptor.CanAcquireMarkerAsync(observer);
        var execution = executeMigrationCommand
            ? seedCommand.ExecuteNonQueryAsync(connection, cancellationToken: cancellation.Token)
            : context.Database.ExecuteSqlRawAsync(seedCommand.CommandText, cancellation.Token);

        // WHY: The lock is acquired only after ON, and a separate session observes it without an INFO token.
        // This proves the attention reaches a running identity-enabled batch, not work canceled before ON.
        var readinessFailure = await Record.ExceptionAsync(() => pauseInterceptor.WaitUntilReadyAsync(
            observer, execution, readinessDeadline.Token));

        var cancelRequestFailure = await Record.ExceptionAsync(() =>
            cancellation.CancelAsync().WaitAsync(TimeSpan.FromSeconds(10)));

        var cancellationFailure = await Record.ExceptionAsync(() => execution.WaitAsync(TimeSpan.FromSeconds(30)));

        if (!execution.IsCompleted)
        {
            throw new InvalidOperationException("Client attention did not complete within the recovery deadline.",
                cancellationFailure);
        }

        var markerHeldAfterAttention = !await pauseInterceptor.CanAcquireMarkerAsync(observer);
        Exception? explicitIdentityFailure = null;

        if (!requiresCallerRecovery)
        {
            // WHY: Another table accepting ON alone cannot prove that the original target is OFF.
            explicitIdentityFailure = await Record.ExceptionAsync(() => context.Database.ExecuteSqlRawAsync(
                "INSERT INTO dbo.identity_roles (Id, Caption) VALUES (7, N'Identity state probe');",
                CancellationToken.None));
        }

        var probeFailure = await Record.ExceptionAsync(() => context.Database.ExecuteSqlRawAsync(
            "SET IDENTITY_INSERT dbo.identity_probe ON; "
            + "INSERT INTO dbo.identity_probe (Id) VALUES (11); "
            + "SET IDENTITY_INSERT dbo.identity_probe OFF;",
            CancellationToken.None));

        if (requiresCallerRecovery)
        {
            await context.Database.ExecuteSqlRawAsync(
                "SET IDENTITY_INSERT dbo.identity_roles OFF;",
                CancellationToken.None);
            await context.Database.ExecuteSqlRawAsync(
                "SET IDENTITY_INSERT dbo.identity_probe ON; "
                + "INSERT INTO dbo.identity_probe (Id) VALUES (12); "
                + "SET IDENTITY_INSERT dbo.identity_probe OFF;",
                CancellationToken.None);
        }

        await context.Database.ExecuteSqlRawAsync(
            "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = N'Session';",
            [new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = readinessResource }],
            CancellationToken.None);
        var markerReleased = await pauseInterceptor.CanAcquireMarkerAsync(observer);
        var quarantineFailure = Record.Exception(() =>
        {
            context.GetService<IMigrationsSqlGenerator>().Generate([], context.Model);
        });

        if (transaction is not null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }

        var seedCount = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.identity_roles;");

        // Assert
        Assert.Single(commands.OfType<SqlServerSafeMigrationIdentityInsertCommand>());
        Assert.True(markerInitiallyAvailable);
        Assert.Null(readinessFailure);
        Assert.Null(cancelRequestFailure);
        Assert.NotNull(cancellationFailure);
        Assert.True(cancellationFailure is OperationCanceledException or SqlException);

        if (cancellationFailure is SqlException sqlFailure)
        {
            Assert.DoesNotContain(sqlFailure.Errors.Cast<SqlError>(), error => error.Number == -2);
        }

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(markerHeldAfterAttention);
        Assert.True(markerReleased);
        Assert.Null(quarantineFailure);
        Assert.Null(cancellationFailure.Data[SqlServerSafeMigrationIdentityInsertCommand.RecoveryFailureDataKey]);
        Assert.Equal(ConnectionState.Open, session.State);
        Assert.Equal(originalConnectionId, session.ClientConnectionId);
        Assert.Equal(0, seedCount);

        if (inheritedIdentityInsert)
        {
            Assert.Equal(8107, Assert.IsType<SqlException>(inheritedIdentityFailure).Number);
        }

        if (requiresCallerRecovery)
        {
            Assert.Equal(8107, Assert.IsType<SqlException>(probeFailure).Number);
        }
        else
        {
            Assert.Equal(544, Assert.IsType<SqlException>(explicitIdentityFailure).Number);
            Assert.Null(probeFailure);
        }
    }
}
