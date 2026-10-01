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
        if (!executeMigrationCommand)
        {
            Assert.Equal(8107, Assert.IsType<SqlException>(leakedSessionFailure).Number);
        }
    }
}
