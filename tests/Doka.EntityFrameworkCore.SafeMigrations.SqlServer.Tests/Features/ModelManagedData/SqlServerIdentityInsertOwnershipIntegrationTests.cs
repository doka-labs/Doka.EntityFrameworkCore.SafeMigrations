namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies owned identity recovery against real retained SQL Server sessions.</summary>
public sealed class SqlServerIdentityInsertOwnershipIntegrationTests : SqlServerIntegrationTestBase
{
    /// <summary>Creates the suite with an isolated database for each test.</summary>
    /// <param name="fixture">The shared native SQL Server container.</param>
    public SqlServerIdentityInsertOwnershipIntegrationTests(SqlServerContainerFixture fixture) : base(fixture) { }

    /// <summary>
    /// Preserves caller-owned ON when the seed guard rejects before acquisition, including restricted callers.
    /// </summary>
    /// <param name="withoutAlterPermission">Whether the seed caller is denied ALTER on the target.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedSeedBeforeOwnOn_PreservesInheritedIdentityAndDoesNotQuarantine(
        bool withoutAlterPermission
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, TablesSql
            + "SET IDENTITY_INSERT dbo.identity_roles ON; "
            + "INSERT INTO dbo.identity_roles (Id, Caption) VALUES (7, N'Actual'); "
            + "SET IDENTITY_INSERT dbo.identity_roles OFF; "
            + "CREATE USER identity_reader WITHOUT LOGIN; "
            + "GRANT VIEW DEFINITION TO identity_reader; "
            + "GRANT SELECT, INSERT ON dbo.identity_roles TO identity_reader; "
            + "DENY ALTER ON dbo.identity_roles TO identity_reader;");
        await using var session = new SqlConnection(connectionString);
        await session.OpenAsync();
        var originalConnectionId = session.ClientConnectionId;
        await using var context = CreateRetainedContext(session);
        var seed = CreateSeedCommand(context);
        var connection = context.GetService<IRelationalConnection>();
        await RunSessionSqlAsync(session, "SET IDENTITY_INSERT dbo.identity_roles ON; "
            + (withoutAlterPermission ? "EXECUTE AS USER = N'identity_reader';" : string.Empty));

        // Act
        var rejected = await Record.ExceptionAsync(() => seed.ExecuteNonQueryAsync(connection));
        var inheritedInsert = await Record.ExceptionAsync(() => RunSessionSqlAsync(session,
            "INSERT INTO dbo.identity_roles (Id, Caption) VALUES (8, N'Caller-owned identity');"));
        var ownershipReleased = await ReadMarkerIsNullAsync(session);
        var quarantine = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate([], context.Model));

        if (withoutAlterPermission)
        {
            await RunSessionSqlAsync(session, "REVERT;");
        }

        var otherOn = await Record.ExceptionAsync(() => RunSessionSqlAsync(session,
            "SET IDENTITY_INSERT dbo.identity_probe ON;"));
        await RunSessionSqlAsync(session, "SET IDENTITY_INSERT dbo.identity_roles OFF;");

        // Assert
        Assert.Equal(51001, Assert.IsType<SqlException>(rejected).Number);
        Assert.Null(inheritedInsert);
        Assert.True(ownershipReleased);
        Assert.Null(quarantine);
        Assert.Equal(8107, Assert.IsType<SqlException>(otherOn).Number);
        Assert.Equal(ConnectionState.Open, session.State);
        Assert.Equal(originalConnectionId, session.ClientConnectionId);
        Assert.Null(rejected.Data[SqlServerSafeMigrationIdentityInsertCommand.RecoveryFailureDataKey]);
    }

    /// <summary>Clears only its ready marker when native ON itself fails for insufficient ALTER permission.</summary>
    [SqlServerLiveFact]
    public async Task NativeOnPermissionFailure_DoesNotClaimInheritedIdentityOrQuarantine()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, TablesSql
            + "CREATE USER identity_reader WITHOUT LOGIN; "
            + "GRANT VIEW DEFINITION TO identity_reader; "
            + "GRANT SELECT, INSERT ON dbo.identity_roles TO identity_reader; "
            + "DENY ALTER ON dbo.identity_roles TO identity_reader;");
        await using var session = new SqlConnection(connectionString);
        await session.OpenAsync();
        await using var context = CreateRetainedContext(session);
        var seed = CreateSeedCommand(context);
        var connection = context.GetService<IRelationalConnection>();
        await RunSessionSqlAsync(session,
            "SET IDENTITY_INSERT dbo.identity_roles ON; EXECUTE AS USER = N'identity_reader';");

        // Act
        var rejected = await Record.ExceptionAsync(() => seed.ExecuteNonQueryAsync(connection));
        var inheritedInsert = await Record.ExceptionAsync(() => RunSessionSqlAsync(session,
            "INSERT INTO dbo.identity_roles (Id, Caption) VALUES (8, N'Caller-owned identity');"));
        var ownershipReleased = await ReadMarkerIsNullAsync(session);
        var quarantine = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate([], context.Model));
        await RunSessionSqlAsync(session, "REVERT; SET IDENTITY_INSERT dbo.identity_roles OFF;");

        // Assert
        var providerFailure = Assert.IsType<SqlException>(rejected);

        Assert.True(providerFailure.Number is 229 or 1088);
        Assert.Null(inheritedInsert);
        Assert.True(ownershipReleased);
        Assert.Null(quarantine);
        Assert.Equal(ConnectionState.Open, session.State);
        Assert.Null(providerFailure.Data[SqlServerSafeMigrationIdentityInsertCommand.RecoveryFailureDataKey]);
    }

    /// <summary>Rejects canceled dispatch before touching a native caller-owned marker or identity setting.</summary>
    [SqlServerLiveFact]
    public async Task AlreadyCanceledSeed_DoesNotClearNativeCallerState()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, TablesSql);
        await using var session = new SqlConnection(connectionString);
        await session.OpenAsync();
        await using var context = CreateRetainedContext(session);
        var seed = CreateSeedCommand(context);
        var connection = context.GetService<IRelationalConnection>();
        await RunSessionSqlAsync(session, "SET IDENTITY_INSERT dbo.identity_roles ON; "
            + $"EXEC sys.sp_set_session_context @key = N'{SqlServerSafeMigrationIdentityInsertScope.ContextKey}', "
            + "@value = N'caller-owned marker', @read_only = 1;");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        var canceled = await Record.ExceptionAsync(() => seed.ExecuteNonQueryAsync(
            connection, cancellationToken: cancellation.Token));
        var inheritedInsert = await Record.ExceptionAsync(() => RunSessionSqlAsync(session,
            "INSERT INTO dbo.identity_roles (Id, Caption) VALUES (8, N'Caller-owned identity');"));
        await using var observation = session.CreateCommand();
        observation.CommandText = $"SELECT SESSION_CONTEXT(N'{SqlServerSafeMigrationIdentityInsertScope.ContextKey}');";
        var marker = await observation.ExecuteScalarAsync();
        await RunSessionSqlAsync(session, "SET IDENTITY_INSERT dbo.identity_roles OFF;");

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(canceled);
        Assert.Null(inheritedInsert);
        Assert.Equal("caller-owned marker", marker);
        Assert.Equal(ConnectionState.Open, session.State);
        Assert.Null(canceled.Data[SqlServerSafeMigrationIdentityInsertCommand.RecoveryFailureDataKey]);
    }

    /// <summary>Preserves the caller's different identity target when its own ON never succeeds.</summary>
    [SqlServerLiveFact]
    public async Task OtherTableAlreadyOn_FailedAcquisitionPreservesCallerState()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, TablesSql);
        await using var session = new SqlConnection(connectionString);
        await session.OpenAsync();
        await using var context = CreateRetainedContext(session);
        var seed = CreateSeedCommand(context);
        var connection = context.GetService<IRelationalConnection>();
        await RunSessionSqlAsync(session, "SET IDENTITY_INSERT dbo.identity_probe ON;");

        // Act
        var rejected = await Record.ExceptionAsync(() => seed.ExecuteNonQueryAsync(connection));
        var inheritedInsert = await Record.ExceptionAsync(() => RunSessionSqlAsync(session,
            "INSERT INTO dbo.identity_probe (Id) VALUES (8);"));
        var markerReleased = await ReadMarkerIsNullAsync(session);
        var quarantine = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate([], context.Model));
        await RunSessionSqlAsync(session, "SET IDENTITY_INSERT dbo.identity_probe OFF;");

        // Assert
        Assert.Equal(8107, Assert.IsType<SqlException>(rejected).Number);
        Assert.Null(inheritedInsert);
        Assert.True(markerReleased);
        Assert.Null(quarantine);
        Assert.Equal(ConnectionState.Open, session.State);
    }

    /// <summary>
    /// Rejects a stale or caller-read-only marker without replacing it or changing inherited identity state.
    /// </summary>
    /// <param name="readOnly">Whether the existing context value cannot be changed on this logical connection.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingMarker_CannotAuthorizeOrBeClearedByNewInvocation(bool readOnly)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, TablesSql);
        await using var session = new SqlConnection(connectionString);
        await session.OpenAsync();
        await using var context = CreateRetainedContext(session);
        var seed = CreateSeedCommand(context);
        var connection = context.GetService<IRelationalConnection>();
        await RunSessionSqlAsync(session, "SET IDENTITY_INSERT dbo.identity_roles ON; "
            + $"EXEC sys.sp_set_session_context @key = N'{SqlServerSafeMigrationIdentityInsertScope.ContextKey}', "
            + "@value = N'caller-owned marker', @read_only = " + (readOnly ? "1;" : "0;"));

        // Act
        var rejected = await Record.ExceptionAsync(() => seed.ExecuteNonQueryAsync(connection));
        var inheritedInsert = await Record.ExceptionAsync(() => RunSessionSqlAsync(session,
            "INSERT INTO dbo.identity_roles (Id, Caption) VALUES (8, N'Caller-owned identity');"));
        await using var observation = session.CreateCommand();
        observation.CommandText = $"SELECT SESSION_CONTEXT(N'{SqlServerSafeMigrationIdentityInsertScope.ContextKey}');";
        var marker = await observation.ExecuteScalarAsync();
        var quarantine = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate([], context.Model));
        await RunSessionSqlAsync(session, "SET IDENTITY_INSERT dbo.identity_roles OFF;");

        // Assert
        Assert.Equal(51007, Assert.IsType<SqlException>(rejected).Number);
        Assert.Null(inheritedInsert);
        Assert.Equal("caller-owned marker", marker);
        Assert.Null(quarantine);
        Assert.Equal(ConnectionState.Open, session.State);
        Assert.Null(rejected.Data[SqlServerSafeMigrationIdentityInsertCommand.RecoveryFailureDataKey]);
    }

    /// <summary>
    /// Reuses the same generated command with fresh invocation tokens and no retained identity marker.
    /// </summary>
    [SqlServerLiveFact]
    public async Task SameCommand_ReplayReleasesOwnershipAndPreservesLaterCallerOn()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, TablesSql);
        await using var session = new SqlConnection(connectionString);
        await session.OpenAsync();
        await using var context = CreateRetainedContext(session);
        var seed = CreateSeedCommand(context);
        var connection = context.GetService<IRelationalConnection>();

        // Act
        await seed.ExecuteNonQueryAsync(connection);
        var firstReleased = await ReadMarkerIsNullAsync(session);
        await RunSessionSqlAsync(session, "SET IDENTITY_INSERT dbo.identity_roles ON;");
        await seed.ExecuteNonQueryAsync(connection);
        var replayReleased = await ReadMarkerIsNullAsync(session);
        var inheritedInsert = await Record.ExceptionAsync(() => RunSessionSqlAsync(session,
            "INSERT INTO dbo.identity_roles (Id, Caption) VALUES (8, N'Caller-owned identity');"));
        await RunSessionSqlAsync(session, "SET IDENTITY_INSERT dbo.identity_roles OFF;");

        // Assert
        Assert.True(firstReleased);
        Assert.True(replayReleased);
        Assert.Null(inheritedInsert);
        Assert.Equal(ConnectionState.Open, session.State);
    }

    /// <summary>
    /// Preserves caller marker values in SQL-only mode, including a protocol-shaped ready marker.
    /// </summary>
    /// <param name="matching">Whether exact seed replay is NoOp instead of requiring an identity insert.</param>
    /// <param name="readOnly">Whether the existing marker is immutable on the retained logical connection.</param>
    [SqlServerLiveTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ScriptForeignReadyMarker_IsNotConsumedOrCleared(bool matching, bool readOnly)
    {
        // Arrange
        const string markerHex = "000123456789ABCDEF0123456789ABCDEF";
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, TablesSql + (matching
            ? "SET IDENTITY_INSERT dbo.identity_roles ON; "
                + "INSERT INTO dbo.identity_roles (Id, Caption) VALUES (7, N'Expected'); "
                + "SET IDENTITY_INSERT dbo.identity_roles OFF;"
            : string.Empty));
        await using var session = new SqlConnection(connectionString);
        await session.OpenAsync();
        await using var context = CreateRetainedContext(session);
        var script = CreateSeedScript(context);
        await RunSessionSqlAsync(session, "SET IDENTITY_INSERT dbo.identity_roles ON; "
            + $"EXEC sys.sp_set_session_context @key = N'{SqlServerSafeMigrationIdentityInsertScope.ContextKey}', "
            + "@value = 0x" + markerHex + ", @read_only = " + (readOnly ? "1;" : "0;"));

        // Act
        var execution = await Record.ExceptionAsync(() => context.Database.ExecuteSqlRawAsync(script));
        var inheritedInsert = await Record.ExceptionAsync(() => RunSessionSqlAsync(session,
            "INSERT INTO dbo.identity_roles (Id, Caption) VALUES (8, N'Caller-owned identity');"));
        await using var observation = session.CreateCommand();
        observation.CommandText = $"SELECT SESSION_CONTEXT(N'{SqlServerSafeMigrationIdentityInsertScope.ContextKey}');";
        var marker = await observation.ExecuteScalarAsync();
        await RunSessionSqlAsync(session, "SET IDENTITY_INSERT dbo.identity_roles OFF;");

        // Assert
        if (matching)
        {
            Assert.Null(execution);
        }
        else
        {
            Assert.Equal(51007, Assert.IsType<SqlException>(execution).Number);
        }

        Assert.Null(inheritedInsert);
        Assert.Equal(Convert.FromHexString(markerHex), Assert.IsType<byte[]>(marker));
        Assert.Equal(ConnectionState.Open, session.State);
    }

    /// <summary>Applies and replays fresh SQL-only seed execution without retaining an ownership marker.</summary>
    [SqlServerLiveFact]
    public async Task FreshScript_AppliesAndReplaysWithNoRetainedMarker()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, TablesSql);
        await using var session = new SqlConnection(connectionString);
        await session.OpenAsync();
        await using var context = CreateRetainedContext(session);
        var script = CreateSeedScript(context);

        // Act
        await context.Database.ExecuteSqlRawAsync(script);
        await context.Database.ExecuteSqlRawAsync(script);
        var markerReleased = await ReadMarkerIsNullAsync(session);
        await RunSessionSqlAsync(session, "SET IDENTITY_INSERT dbo.identity_probe ON; "
            + "INSERT INTO dbo.identity_probe (Id) VALUES (8); SET IDENTITY_INSERT dbo.identity_probe OFF;");
        var count = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.identity_roles WHERE Id = 7 AND Caption = N'Expected';");

        // Assert
        Assert.True(markerReleased);
        Assert.Equal(1, count);
        Assert.Equal(ConnectionState.Open, session.State);
    }

    /// <summary>
    /// Quarantines an attention between native ON and ownership confirmation instead of issuing unproven OFF.
    /// </summary>
    [SqlServerLiveFact]
    public async Task AttentionInAcquisitionGap_QuarantinesInsteadOfGuessingIdentityOwnership()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, TablesSql);
        await using var session = new SqlConnection(connectionString);
        await session.OpenAsync();
        var originalConnectionId = session.ClientConnectionId;
        await using var observer = new SqlConnection(connectionString);
        await observer.OpenAsync();
        var pause = new SqlServerIdentityInsertPauseInterceptor("identity_gap_" + Guid.NewGuid().ToString("N"),
            beforeConfirmation: true);

        var options = new DbContextOptionsBuilder().UseSqlServer(session).UseSqlServerSafeMigrations()
            .AddInterceptors(pause).Options;

        await using var context = new DbContext(options);
        context.Database.SetCommandTimeout(120);
        var seed = CreateSeedCommand(context);
        var connection = context.GetService<IRelationalConnection>();
        using var cancellation = new CancellationTokenSource();
        using var readinessDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var execution = seed.ExecuteNonQueryAsync(connection, cancellationToken: cancellation.Token);
        await pause.WaitUntilReadyAsync(observer, execution, readinessDeadline.Token);
        await cancellation.CancelAsync();
        var attention = await Record.ExceptionAsync(() => execution.WaitAsync(TimeSpan.FromSeconds(45)));
        var quarantine = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate([], context.Model));
        await using var newLease = new SqlConnection(connectionString);
        await newLease.OpenAsync();
        var newLeaseMarkerIsNull = await ReadMarkerIsNullAsync(newLease);

        // Assert
        Assert.NotNull(attention);
        Assert.True(attention is OperationCanceledException or SqlException);
        Assert.Equal("session_closed",
            attention.Data[SqlServerSafeMigrationIdentityInsertCommand.RecoveryFailureDataKey]);
        Assert.Equal(ConnectionState.Closed, session.State);
        Assert.IsType<InvalidOperationException>(quarantine);
        Assert.NotEqual(originalConnectionId, newLease.ClientConnectionId);
        Assert.True(newLeaseMarkerIsNull);
    }

    private const string TablesSql = "CREATE TABLE dbo.identity_roles (Id int IDENTITY(1,1) NOT NULL "
        + "CONSTRAINT PK_identity_roles PRIMARY KEY, Caption nvarchar(80) NOT NULL); "
        + "CREATE TABLE dbo.identity_probe (Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_identity_probe PRIMARY KEY);";

    /// <summary>
    /// Retains the caller's open physical connection rather than opening a replacement cleanup session.
    /// </summary>
    private static DbContext CreateRetainedContext(SqlConnection session)
        => new(new DbContextOptionsBuilder().UseSqlServer(session).UseSqlServerSafeMigrations().Options);

    /// <summary>Generates the actual managed seed wrapper, not the raw SQL-only execution path.</summary>
    private static SqlServerSafeMigrationIdentityInsertCommand CreateSeedCommand(DbContext context)
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel("identity_roles", ["Id"], ["int"], ["Id", "Caption"],
            ["int", "nvarchar(80)"], new object?[,] { { 7, "Expected" } });

        return context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model)
            .OfType<SqlServerSafeMigrationIdentityInsertCommand>().Single();
    }

    /// <summary>
    /// Generates the explicit Script mode used by SQL-only clients instead of managed migration execution.
    /// </summary>
    private static string CreateSeedScript(DbContext context)
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel("identity_roles", ["Id"], ["int"], ["Id", "Caption"],
            ["int", "nvarchar(80)"], new object?[,] { { 7, "Expected" } });
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model,
            MigrationsSqlGenerationOptions.Script);

        return Assert.Single(commands, static command =>
            command.CommandText.Contains("DECLARE @doka_identity_insert", StringComparison.Ordinal)).CommandText;
    }

    /// <summary>Executes a caller-state probe directly so provider recovery cannot hide the assertion.</summary>
    private static async Task RunSessionSqlAsync(SqlConnection session, string sql)
    {
        await using var command = session.CreateCommand();
        command.CommandText = sql;

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Reads only the ownership marker, without modifying it or testing unrelated session settings.</summary>
    private static async Task<bool> ReadMarkerIsNullAsync(SqlConnection session)
    {
        await using var command = session.CreateCommand();
        command.CommandText = "SELECT CASE WHEN SESSION_CONTEXT("
            + $"N'{SqlServerSafeMigrationIdentityInsertScope.ContextKey}') "
            + "IS NULL THEN 1 ELSE 0 END;";

        return (int)(await command.ExecuteScalarAsync())! == 1;
    }
}
