namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies local temporary-table observations and cleanup across simultaneously open native sessions.
/// </summary>
public sealed class SqlServerTemporaryTableIsolationTests : SqlServerIntegrationTestBase
{
    private const string CallerTable = "#doka_sm_identifiers";

    /// <summary>Creates the isolation suite with the assembly-owned native SQL Server fixture.</summary>
    /// <param name="fixture">The shared fixture providing an owned database per test.</param>
    public SqlServerTemporaryTableIsolationTests(
        SqlServerContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Same-name peer DDL cannot supply a missing object or replace an existing caller identity.</summary>
    /// <param name="ownerState">Zero means absent, one retained, and two explicitly dropped in the owner.</param>
    [SqlServerLiveTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SessionLookup_SeparatesIdenticalNamesDuringPeerDdl(
        int ownerState
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(cancellationToken: CancellationToken.None);
        await using var owner = new SqlConnection(connectionString);
        await using var peer = new SqlConnection(connectionString);
        await Task.WhenAll(owner.OpenAsync(), peer.OpenAsync());
        await using var peerTransaction = (SqlTransaction)await peer.BeginTransactionAsync();

        // WHY: Retain uncommitted peer DDL while the owner's lookup runs. A
        // server-wide tempdb scan is neither isolated nor required to observe
        // this session's exact object; no elapsed-time assertion is needed.
        await ExecuteSessionSqlAsync(
            peer,
            "CREATE TABLE #doka_sm_identifiers (Sentinel int NOT NULL); "
            + "INSERT INTO #doka_sm_identifiers VALUES (91);",
            cancellationToken: CancellationToken.None,
            peerTransaction);

        if (ownerState != 0)
        {
            await ExecuteSessionSqlAsync(
                owner,
                "CREATE TABLE #doka_sm_identifiers (Sentinel int NOT NULL); "
                + "INSERT INTO #doka_sm_identifiers VALUES (73);",
                cancellationToken: CancellationToken.None);
        }

        var originalObjectId = await SqlServerTemporaryTableProbe.ReadObjectIdAsync(
            owner,
            CallerTable,
            cancellationToken: CancellationToken.None);

        if (ownerState == 2)
        {
            await ExecuteSessionSqlAsync(
                owner,
                "DROP TABLE #doka_sm_identifiers;",
                cancellationToken: CancellationToken.None);
        }

        int? ownerSentinel = null;

        // Act
        var objectIds = await Task.WhenAll(
            SqlServerTemporaryTableProbe.ReadObjectIdAsync(
                owner,
                CallerTable,
                cancellationToken: CancellationToken.None),
            SqlServerTemporaryTableProbe.ReadObjectIdAsync(
                peer,
                CallerTable,
                cancellationToken: CancellationToken.None,
                peerTransaction));

        var ownerReadFailure = await Record.ExceptionAsync(async () =>
            ownerSentinel = await ReadSessionIntAsync(
                owner,
                "SELECT Sentinel FROM #doka_sm_identifiers;",
                cancellationToken: CancellationToken.None));

        var peerSentinel = await ReadSessionIntAsync(
            peer,
            "SELECT Sentinel FROM #doka_sm_identifiers;",
            cancellationToken: CancellationToken.None,
            peerTransaction);

        // Assert
        Assert.NotEqual(owner.ClientConnectionId, peer.ClientConnectionId);
        Assert.NotNull(objectIds[1]);
        Assert.Equal(91, peerSentinel);
        if (ownerState == 1)
        {
            Assert.NotNull(originalObjectId);
            Assert.Equal(originalObjectId, objectIds[0]);
            Assert.NotEqual(objectIds[0], objectIds[1]);
            Assert.Null(ownerReadFailure);
            Assert.Equal(73, ownerSentinel);
        }
        else
        {
            Assert.Equal(ownerState == 0, originalObjectId is null);
            Assert.Null(objectIds[0]);
            Assert.Null(ownerSentinel);
            Assert.Equal(208,
                Assert.IsType<SqlException>(ownerReadFailure)
                    .Number);
        }
    }

    /// <summary>Actual provider cleanup removes only its exact local scope despite same-name peer DDL.</summary>
    /// <param name="interrupted">Whether work stops after CREATE rather than completing its classifier.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentifierCleanup_PreservesCallerAndSameNamePeerScopes(
        bool interrupted
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(cancellationToken: CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(cancellationToken: CancellationToken.None);
        var owner = (SqlConnection)context.Database.GetDbConnection();
        await using var peer = new SqlConnection(connectionString);
        await peer.OpenAsync();
        await using var peerTransaction = (SqlTransaction)await peer.BeginTransactionAsync();
        SqlServerIdentifierReference[] references =
        [
            new(SqlServerIdentifierScope.Column, "dbo", "identifier_items", "Id"),
            new(SqlServerIdentifierScope.Column, "dbo", "identifier_items", "Caption"),
        ];

        var statements = SqlServerIdentifierContract.BuildCollisionGuardCommands(
            references,
            throwOnCollision: false,
            out var temporaryTable);

        var ownedTable = temporaryTable
            ?? throw new InvalidOperationException("The repeated identifier scope did not allocate a temporary name.");

        await ExecuteSessionSqlAsync(
            owner,
            "CREATE TABLE #doka_sm_identifiers (Sentinel int NOT NULL); "
            + "INSERT INTO #doka_sm_identifiers VALUES (73);",
            cancellationToken: CancellationToken.None);

        await ExecuteSessionSqlAsync(
            peer,
            "CREATE TABLE ["
            + ownedTable
            + "] (Sentinel int NOT NULL); "
            + "INSERT INTO ["
            + ownedTable
            + "] VALUES (91);",
            cancellationToken: CancellationToken.None,
            peerTransaction);

        var callerObjectId = await SqlServerTemporaryTableProbe.ReadObjectIdAsync(
            owner,
            CallerTable,
            cancellationToken: CancellationToken.None);

        var peerObjectId = await SqlServerTemporaryTableProbe.ReadObjectIdAsync(
            peer,
            ownedTable,
            cancellationToken: CancellationToken.None,
            peerTransaction);

        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        Exception? originalFailure = interrupted
            ? new InvalidOperationException("Injected identifier interruption.")
            : null;

        int? classification = null;

        // Act
        await ExecuteSessionSqlAsync(owner, statements[0], cancellationToken: CancellationToken.None);
        var createdObjectId = await SqlServerTemporaryTableProbe.ReadObjectIdAsync(
            owner,
            ownedTable,
            cancellationToken: CancellationToken.None);

        if (!interrupted)
        {
            for (var index = 1; index + 1 < statements.Count; index++)
            {
                await ExecuteSessionSqlAsync(owner, statements[index], cancellationToken: CancellationToken.None);
            }

            classification = await ReadSessionIntAsync(
                owner,
                statements[^1],
                cancellationToken: CancellationToken.None);
        }

        await analyzer.CleanupTemporaryScopeAsync(owner, null, ownedTable, null, originalFailure);
        var remainingObjectIds = await Task.WhenAll(
            SqlServerTemporaryTableProbe.ReadObjectIdAsync(
                owner,
                ownedTable,
                cancellationToken: CancellationToken.None),
            SqlServerTemporaryTableProbe.ReadObjectIdAsync(
                peer,
                ownedTable,
                cancellationToken: CancellationToken.None,
                peerTransaction));

        var retainedCallerObjectId = await SqlServerTemporaryTableProbe.ReadObjectIdAsync(
            owner,
            CallerTable,
            cancellationToken: CancellationToken.None);

        var callerSentinel = await ReadSessionIntAsync(
            owner,
            "SELECT Sentinel FROM #doka_sm_identifiers;",
            cancellationToken: CancellationToken.None);

        var peerSentinel = await ReadSessionIntAsync(
            peer,
            "SELECT Sentinel FROM [" + ownedTable + "];",
            cancellationToken: CancellationToken.None,
            peerTransaction);

        // Assert
        Assert.NotEqual(owner.ClientConnectionId, peer.ClientConnectionId);
        Assert.NotNull(createdObjectId);
        Assert.NotNull(peerObjectId);
        Assert.NotEqual(createdObjectId, peerObjectId);
        Assert.Equal(
            interrupted
                ? null
                : 1,
            classification);

        Assert.Null(remainingObjectIds[0]);
        Assert.Equal(peerObjectId, remainingObjectIds[1]);
        Assert.NotNull(callerObjectId);
        Assert.Equal(callerObjectId, retainedCallerObjectId);
        Assert.Equal(73, callerSentinel);
        Assert.Equal(91, peerSentinel);
        Assert.True(
            originalFailure is null
            || !originalFailure.Data.Contains("Doka.SafeMigrations.SqlServer.RecoveryStatus"));
    }

    /// <summary>Executes authored setup or actual provider statements on one retained native session.</summary>
    /// <param name="cancellationToken">The caller's cancellation for this helper operation.</param>
    private static async Task ExecuteSessionSqlAsync(
        SqlConnection connection,
        string sql,
        CancellationToken cancellationToken,
        SqlTransaction? transaction = null
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Reads a scalar without opening another session or enumerating unrelated metadata.</summary>
    /// <param name="cancellationToken">The caller's cancellation for this helper operation.</param>
    private static async Task<int> ReadSessionIntAsync(
        SqlConnection connection,
        string sql,
        CancellationToken cancellationToken,
        SqlTransaction? transaction = null
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }
}
