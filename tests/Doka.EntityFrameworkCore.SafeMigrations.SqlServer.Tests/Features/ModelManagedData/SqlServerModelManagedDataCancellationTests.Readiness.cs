namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

// WHY: Readiness and cancellation share the same class fixture rather than starting another server container.
public sealed partial class SqlServerModelManagedDataCancellationTests
{
    /// <summary>
    /// Observes available and separately held session markers through the exact SQL integer contract.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadinessScalar_AvailableAndHeldMarkersUseExactIntContract(
        bool markerHeld
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var resource = "doka_identity_ready_" + Guid.NewGuid().ToString("N");
        var interceptor = new SqlServerIdentityInsertPauseInterceptor(resource);
        await using var owner = new SqlConnection(connectionString);
        await owner.OpenAsync();
        await using var observer = new SqlConnection(connectionString);
        await observer.OpenAsync();
        if (markerHeld)
        {
            await using var acquire = owner.CreateCommand();
            acquire.CommandText = "DECLARE @result int; "
                + "EXEC @result = sys.sp_getapplock @DbPrincipal = N'public', @Resource = @resource, "
                + "@LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0; "
                + "IF @result < 0 THROW 51006, 'identity readiness lock failed', 1;";
            acquire.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = resource });

            await acquire.ExecuteNonQueryAsync();
        }

        await using var observation = interceptor.CreateMarkerObservationCommand(observer);
        await using var observerLock = observer.CreateCommand();
        observerLock.CommandText = "SELECT APPLOCK_MODE(N'public', @resource, N'Session');";
        observerLock.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = resource });

        // Act
        await using var reader = await observation.ExecuteReaderAsync();
        var rowPresent = await reader.ReadAsync();
        var sqlType = reader.GetDataTypeName(0);
        var clrType = reader.GetFieldType(0);
        var scalar = reader.GetValue(0);
        await reader.CloseAsync();
        var available = await interceptor.CanAcquireMarkerAsync(observer);
        var observerLockMode = await observerLock.ExecuteScalarAsync();

        // Assert
        Assert.True(rowPresent);
        Assert.Equal("int", sqlType);
        Assert.Equal(typeof(int), clrType);
        Assert.Equal(markerHeld ? 0 : 1, Assert.IsType<int>(scalar));
        Assert.Equal(!markerHeld, available);
        Assert.Equal("NoLock", Assert.IsType<string>(observerLockMode));
        Assert.Equal(ConnectionState.Open, owner.State);
        Assert.Equal(ConnectionState.Open, observer.State);
    }
}
