namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    private const string RuntimeProbeColumnLengthSql = "SELECT CHARACTER_MAXIMUM_LENGTH "
        + "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
        + "AND TABLE_NAME = 'runtime_probe_rows' AND COLUMN_NAME = 'Code';";

    private const string RuntimeProbeColumnNullableSql = "SELECT IS_NULLABLE = 'YES' "
        + "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
        + "AND TABLE_NAME = 'runtime_probe_rows' AND COLUMN_NAME = 'Code';";

    // WHY: Binary equality and byte length also detect changes hidden by case-insensitive or padded comparisons.
    private const string RuntimeProbePreservedRowsSql = "SELECT COUNT(*) FROM `runtime_probe_rows` "
        + "WHERE (`Id` = 1 AND BINARY `Code` = BINARY 'preserved') OR (`Id` = 2 AND `Code` IS NULL) "
        + "OR (`Id` = 3 AND OCTET_LENGTH(`Code`) = 0);";

    /// <summary>
    /// Verifies an interrupted fused DataProbe cannot mutate rows or bypass independent session cleanup.
    /// </summary>
    /// <param name="failureMode">The error, caller cancellation, or command timeout injected after PREPARE.</param>
    [Theory]
    [InlineData("error")]
    [InlineData("cancellation")]
    [InlineData("timeout")]
    public async Task RuntimeDataProbeSetupFailure_PreservesRowsAndRecoversSameSession(string failureMode)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `runtime_probe_rows` (`Id` int NOT NULL, `Code` varchar(20) NULL, "
            + "PRIMARY KEY (`Id`)) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + "INSERT INTO `runtime_probe_rows` VALUES (1, 'preserved'), (2, NULL), (3, '');");

        await using var blocker = failureMode == "error"
            ? null
            : await AcquireRuntimeSetupLockAsync(connectionString);

        using var cancellation = new CancellationTokenSource();
        var observer = new MySqlRuntimeCommandInterceptor();
        // WHY: A real held row lock makes interruption observable on both engines;
        // an interrupted SLEEP may return normally and would not prove first-error behavior.
        var probe = failureMode == "error"
            ? "SELECT * FROM `runtime_missing_data_probe`;"
            : "SELECT `Id` FROM `runtime_setup_lock` WHERE `Id` = 1 FOR UPDATE;";

        observer.InjectFirstCompactedDataProbeGroup(
            "SET @runtime_data_probe_prepared = 1; " + probe + " SET @runtime_data_probe_sentinel = 1;",
            failureMode == "cancellation" ? () => cancellation.CancelAfter(TimeSpan.FromMilliseconds(500)) : null);

        await using var context = CreateRuntimeContext(connectionString, observer);
        context.Database.SetCommandTimeout(failureMode == "timeout" ? 1 : 60);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        // WHY: Nullable narrowing requires the dedicated length DataProbe, not only
        // the nullability guard reached by the other prepared-setup recovery tests.
        builder.EnsureColumn(
            "runtime_probe_rows",
            new ExpectedColumnDefinition(
                "Code",
                typeof(string),
                isNullable: true,
                storeType: "varchar(10)",
                maxLength: 10),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var result = await ExecuteRuntimeDataProbeRecoveryAsync(
            context, observer, builder.Operations, cancellation.Token);

        // Assert
        if (failureMode == "cancellation")
        {
            Assert.IsAssignableFrom<OperationCanceledException>(result.Exception);
        }
        else
        {
            var exception = Assert.IsType<MySqlException>(result.Exception);
            if (failureMode == "timeout")
            {
                Assert.Equal(MySqlErrorCode.CommandTimeoutExpired, exception.ErrorCode);
                Assert.Equal(1, observer.InjectedCommandTimeout);
            }
            else
            {
                Assert.Equal(1146, exception.Number);
            }
        }

        Assert.True(observer.SetupWasInjected);
        Assert.True(observer.CompactedPrepareGroupWasInjected);
        Assert.True(observer.DataProbePrepareGroupWasInjected);
        Assert.True(observer.OriginalCompactedPrepareGroupPayloadBytes > 256);
        Assert.Equal(1, result.PreparedMarker);
        Assert.Equal(0, result.SentinelExecuted);
        Assert.Equal(0, result.BodiesBeforeRecovery);
        Assert.Equal(1, result.Cleanup.VariablesCleared);
        Assert.Equal(1146, result.Cleanup.TemporaryTableError);
        Assert.Equal(1243, result.Cleanup.PreparedStatementError);
        Assert.Equal(20, result.ColumnLengthBeforeRecovery);
        Assert.Equal(1, result.ColumnNullableBeforeRecovery);
        Assert.Equal(3, result.RowCountBeforeRecovery);
        Assert.Equal(3, result.PreservedRowsBeforeRecovery);
        Assert.Equal(result.SessionBefore, result.SessionAfter);
        Assert.Equal(10, result.ColumnLengthAfterRecovery);
        Assert.Equal(1, result.ColumnNullableAfterRecovery);
        Assert.Equal(3, result.RowCountAfterRecovery);
        Assert.Equal(3, result.PreservedRowsAfterRecovery);
        Assert.Equal(1, result.BodiesAfterRecovery);
    }

    /// <summary>Captures the interrupted runtime state before an uncancelled retry can hide leaked resources.</summary>
    /// <param name="context">The context retaining one open provider session throughout failure and recovery.</param>
    /// <param name="observer">The observer injecting exactly one DataProbe setup failure.</param>
    /// <param name="operations">The generated guarded nullable-narrowing operation.</param>
    /// <param name="cancellationToken">The token used only for the initial attempt.</param>
    /// <returns>The captured error, cleanup, schema, row, and same-session recovery evidence.</returns>
    private static async Task<RuntimeDataProbeRecovery> ExecuteRuntimeDataProbeRecoveryAsync(
        DbContext context,
        MySqlRuntimeCommandInterceptor observer,
        IReadOnlyList<MigrationOperation> operations,
        CancellationToken cancellationToken
    )
    {
        var sessionBefore = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");
        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, operations, cancellationToken));

        var bodiesBefore = observer.GuardedBodyCount;
        var cleanup = await ReadRuntimeCleanupProofAsync(context.Database.GetDbConnection());
        var preparedMarker = await ContextScalarIntAsync(
            context,
            "SELECT COALESCE(@runtime_data_probe_prepared = 1, FALSE);");

        var sentinel = await ContextScalarIntAsync(context, "SELECT @runtime_data_probe_sentinel IS NOT NULL;");
        var lengthBefore = await ContextScalarIntAsync(context, RuntimeProbeColumnLengthSql);
        var nullableBefore = await ContextScalarIntAsync(context, RuntimeProbeColumnNullableSql);
        var rowCountBefore = await ContextScalarIntAsync(context, "SELECT COUNT(*) FROM `runtime_probe_rows`;");
        var preservedBefore = await ContextScalarIntAsync(context, RuntimeProbePreservedRowsSql);

        context.Database.SetCommandTimeout(60);
        await ExecuteOperationsAsync(context, operations, CancellationToken.None);
        var sessionAfter = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");
        var lengthAfter = await ContextScalarIntAsync(context, RuntimeProbeColumnLengthSql);
        var nullableAfter = await ContextScalarIntAsync(context, RuntimeProbeColumnNullableSql);
        var rowCountAfter = await ContextScalarIntAsync(context, "SELECT COUNT(*) FROM `runtime_probe_rows`;");
        var preservedAfter = await ContextScalarIntAsync(context, RuntimeProbePreservedRowsSql);

        return new RuntimeDataProbeRecovery(
            exception,
            bodiesBefore,
            cleanup,
            preparedMarker,
            sentinel,
            sessionBefore,
            lengthBefore,
            nullableBefore,
            rowCountBefore,
            preservedBefore,
            sessionAfter,
            lengthAfter,
            nullableAfter,
            rowCountAfter,
            preservedAfter,
            observer.GuardedBodyCount);
    }

    /// <summary>
    /// Carries immutable before-retry proofs and successful same-session DataProbe recovery evidence.
    /// </summary>
    private sealed record RuntimeDataProbeRecovery(
        Exception? Exception,
        long BodiesBeforeRecovery,
        RuntimeCleanupProof Cleanup,
        int PreparedMarker,
        int SentinelExecuted,
        int SessionBefore,
        int ColumnLengthBeforeRecovery,
        int ColumnNullableBeforeRecovery,
        int RowCountBeforeRecovery,
        int PreservedRowsBeforeRecovery,
        int SessionAfter,
        int ColumnLengthAfterRecovery,
        int ColumnNullableAfterRecovery,
        int RowCountAfterRecovery,
        int PreservedRowsAfterRecovery,
        long BodiesAfterRecovery
    );
}
