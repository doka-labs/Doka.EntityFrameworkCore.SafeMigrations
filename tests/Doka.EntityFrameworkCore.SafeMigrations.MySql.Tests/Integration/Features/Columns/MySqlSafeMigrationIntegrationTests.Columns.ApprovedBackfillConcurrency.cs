namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    private const string ApprovedBackfillGuidValue = "00000000-0000-0000-0000-000000000017";
    private const string ApprovedBackfillPreservedGuid = "00000000-0000-0000-0000-000000000023";

    /// <summary>A declared replacement does not authorize a permissive conversion after its separate UPDATE.</summary>
    /// <param name="storageContract">The GUID, integer or widening VARCHAR contract.</param>
    [Theory]
    [InlineData("guid")]
    [InlineData("integer")]
    [InlineData("varchar-transition")]
    public async Task ApprovedBackfillConcurrencyRejectsPermissiveSessionBeforeUpdate(string storageContract)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ApprovedBackfillFixtureSql(storageContract));
        var operation = ApprovedBackfillOperation(storageContract);
        var observer = new ApprovedBackfillCheckpointInterceptor(injectCheckpoint: false);
        await using var context = CreateRuntimeContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await AtomicRequiredAddSessionSqlAsync(context, "SET SESSION sql_mode = '';");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("approved-backfill-permissive-session"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));
        var originalNulls = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `approved_backfill_items` WHERE `key` = 1 AND `required_value` IS NULL;");

        var preserved = await ScalarStringAsync(connectionString,
            "SELECT `required_value` FROM `approved_backfill_items` WHERE `key` = 2;");

        var nullable = await ApprovedBackfillNullableCountAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, Assert.Single(report.Assessments).Action);
        Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(failure).Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(observer.BodyWasObserved);
        Assert.Equal(1, originalNulls);
        Assert.Equal(ApprovedBackfillPreservedValue(storageContract), preserved);
        Assert.Equal(1, nullable);
    }

    /// <summary>A strict session preserves the authored replacement and all unrelated existing values.</summary>
    /// <param name="storageContract">The GUID, integer or widening VARCHAR contract.</param>
    [Theory]
    [InlineData("guid")]
    [InlineData("integer")]
    [InlineData("varchar-transition")]
    public async Task ApprovedBackfillConcurrencyStrictSessionAppliesDeclaredValues(string storageContract)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ApprovedBackfillFixtureSql(storageContract));
        var operation = ApprovedBackfillOperation(storageContract);
        var observer = new ApprovedBackfillCheckpointInterceptor(injectCheckpoint: false);
        await using var context = CreateRuntimeContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await AtomicRequiredAddSessionSqlAsync(context, "SET SESSION sql_mode = 'STRICT_TRANS_TABLES';");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("approved-backfill-strict-session"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, [operation],
            new SafeMigrationRunOptions("approved-backfill-strict-final"), CancellationToken.None);

        var approved = await ScalarStringAsync(connectionString,
            "SELECT `required_value` FROM `approved_backfill_items` WHERE `key` = 1;");

        var preserved = await ScalarStringAsync(connectionString,
            "SELECT `required_value` FROM `approved_backfill_items` WHERE `key` = 2;");

        var nullable = await ApprovedBackfillNullableCountAsync(connectionString);
        var targetWidth = await ApprovedBackfillCharacterWidthAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Repair, Assert.Single(preflight.Assessments).Action);
        Assert.True(observer.BodyWasObserved);
        Assert.Equal("repair", observer.ObservedAction);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(ApprovedBackfillDeclaredValue(storageContract), approved);
        Assert.Equal(ApprovedBackfillPreservedValue(storageContract), preserved);
        Assert.Equal(0, nullable);
        Assert.Equal(storageContract == "guid" ? 36 : storageContract == "integer" ? 0 : 32, targetWidth);
    }

    /// <summary>
    /// Strict finalization rejects a late NULL rather than substituting an implicit zero or empty value.
    /// </summary>
    /// <param name="storageContract">The GUID, integer or widening VARCHAR contract.</param>
    [Theory]
    [InlineData("guid")]
    [InlineData("integer")]
    [InlineData("varchar-transition")]
    public async Task ApprovedBackfillConcurrencyStrictSessionPreservesLateNull(string storageContract)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ApprovedBackfillFixtureSql(storageContract) + ApprovedBackfillGateSql);
        var operation = ApprovedBackfillOperation(storageContract);
        var checkpoint = new ApprovedBackfillCheckpointInterceptor(injectCheckpoint: true);
        await using var context = CreateRuntimeContext(connectionString, checkpoint);
        context.Database.SetCommandTimeout(60);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await AtomicRequiredAddSessionSqlAsync(context, "SET SESSION sql_mode = 'STRICT_TRANS_TABLES';");
        await using var writer = new MySqlConnection(connectionString);
        await writer.OpenAsync(CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var gate = await AcquireAtomicAddWriterGateAsync(writer, deadline.Token);
        var gateReleased = false;

        // Act
        var execution = Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], executionCancellation.Token));
        Exception? failure;
        try
        {
            await WaitForApprovedBackfillCheckpointAsync(writer, gate, checkpoint, deadline.Token);
            await ExecuteAtomicAddWriterSqlAsync(writer, gate,
                "INSERT INTO `approved_backfill_items` (`key`, `required_value`) VALUES (3, NULL);", deadline.Token);
            await gate.CommitAsync(deadline.Token);
            gateReleased = true;
            failure = await execution.WaitAsync(deadline.Token);
        }
        finally
        {
            await FinishAtomicAddConcurrentAttemptAsync(execution, executionCancellation, gate, gateReleased);
        }

        var approved = await ScalarStringAsync(connectionString,
            "SELECT `required_value` FROM `approved_backfill_items` WHERE `key` = 1;");

        var preserved = await ScalarStringAsync(connectionString,
            "SELECT `required_value` FROM `approved_backfill_items` WHERE `key` = 2;");

        var lateNulls = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `approved_backfill_items` WHERE `key` = 3 AND `required_value` IS NULL;");

        var nullable = await ApprovedBackfillNullableCountAsync(connectionString);
        var sourceWidth = await ApprovedBackfillCharacterWidthAsync(connectionString);

        // Assert
        Assert.True(checkpoint.BodyWasObserved);
        Assert.True(checkpoint.WasInjected);
        Assert.True(checkpoint.CheckpointWasReached);
        Assert.Equal("repair", checkpoint.ObservedAction);
        Assert.IsType<MySqlException>(failure);
        Assert.Equal(ApprovedBackfillDeclaredValue(storageContract), approved);
        Assert.Equal(ApprovedBackfillPreservedValue(storageContract), preserved);
        Assert.Equal(1, lateNulls);
        Assert.Equal(1, nullable);
        Assert.Equal(storageContract == "guid" ? 36 : storageContract == "integer" ? 0 : 16, sourceWidth);
    }

    /// <summary>
    /// An already matching target needs no conversion and remains a runtime no-op in a permissive session.
    /// </summary>
    /// <param name="storageContract">The GUID, integer or widening VARCHAR contract.</param>
    [Theory]
    [InlineData("guid")]
    [InlineData("integer")]
    [InlineData("varchar-transition")]
    public async Task ApprovedBackfillConcurrencyMatchingTargetDoesNotRequireStrictRepair(string storageContract)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ApprovedBackfillFixtureSql(storageContract, alreadyMatching: true));
        var operation = ApprovedBackfillOperation(storageContract);
        var observer = new ApprovedBackfillCheckpointInterceptor(injectCheckpoint: false);
        await using var context = CreateRuntimeContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await AtomicRequiredAddSessionSqlAsync(context, "SET SESSION sql_mode = '';");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("approved-backfill-matching-permissive-session"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var approved = await ScalarStringAsync(connectionString,
            "SELECT `required_value` FROM `approved_backfill_items` WHERE `key` = 1;");

        var preserved = await ScalarStringAsync(connectionString,
            "SELECT `required_value` FROM `approved_backfill_items` WHERE `key` = 2;");

        var nullable = await ApprovedBackfillNullableCountAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(report.Assessments).Action);
        Assert.True(observer.BodyWasObserved);
        Assert.Equal("no_op", observer.ObservedAction);
        Assert.Equal(ApprovedBackfillDeclaredValue(storageContract), approved);
        Assert.Equal(ApprovedBackfillPreservedValue(storageContract), preserved);
        Assert.Equal(0, nullable);
    }

    // WHY: The existing gate helpers use these separate test-owned tables.
    // Neither participates in the target's data, metadata, or advisory lock.
    private const string ApprovedBackfillGateSql =
        "CREATE TABLE `atomic_add_checkpoint` "
        + "(`key` int NOT NULL PRIMARY KEY, `reached` int NOT NULL) ENGINE=InnoDB; "
        + "INSERT INTO `atomic_add_checkpoint` VALUES (1, 0); "
        + "CREATE TABLE `atomic_add_gate` (`key` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
        + "INSERT INTO `atomic_add_gate` VALUES (1);";

    /// <summary>Captures one caller-approved nullable-to-required transition with a concrete replacement.</summary>
    /// <param name="storageContract">The finite test-owned storage contract.</param>
    /// <returns>The immutable explicitly approved alteration.</returns>
    private static SafeMigrationOperation ApprovedBackfillOperation(string storageContract)
    {
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        if (storageContract == "guid")
        {
            builder.AlterColumnIfDifferentFromModel(
                operationFactory: static operations => operations.AlterColumn<Guid>(
                        name: "required_value", table: "approved_backfill_items", type: "char(36)",
                        fixedLength: true, maxLength: 36, nullable: false,
                        defaultValue: Guid.Parse(ApprovedBackfillGuidValue), oldClrType: typeof(Guid),
                        oldType: "char(36)", oldFixedLength: true, oldMaxLength: 36, oldNullable: true)
                    .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                    .Annotation("Doka:MySql:RequiresExplicitBackfill", true)
                    .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None)
                    .OldAnnotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                    .OldAnnotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None),
                policy: SafeMigrationPolicy.RepairIfSafe);
        }
        else if (storageContract == "integer")
        {
            builder.AlterColumnIfDifferentFromModel(
                operationFactory: static operations => operations.AlterColumn<int>(
                    name: "required_value", table: "approved_backfill_items", type: "int", nullable: false,
                    defaultValue: 7, oldClrType: typeof(int), oldType: "int", oldNullable: true),
                policy: SafeMigrationPolicy.RepairIfSafe);
        }
        else if (storageContract == "varchar-transition")
        {
            builder.AlterColumnIfDifferentFromModel(
                operationFactory: static operations => operations.AlterColumn<string>(
                    name: "required_value", table: "approved_backfill_items", type: "varchar(32)",
                    maxLength: 32, nullable: false, defaultValue: "approved", oldClrType: typeof(string),
                    oldType: "varchar(16)", oldMaxLength: 16, oldNullable: true),
                policy: SafeMigrationPolicy.RepairIfSafe);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(storageContract));
        }

        return Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
    }

    /// <summary>Creates either the original nullable schema or the exact final matching contract.</summary>
    /// <param name="storageContract">The finite test-owned storage contract.</param>
    /// <param name="alreadyMatching">Whether the target default and nullability are already satisfied.</param>
    /// <returns>The anonymous schema, one backfill candidate and one preserved sentinel.</returns>
    private static string ApprovedBackfillFixtureSql(string storageContract, bool alreadyMatching = false)
    {
        var storeType = storageContract switch
        {
            "guid" => "char(36)",
            "integer" => "int",
            "varchar-transition" => alreadyMatching ? "varchar(32)" : "varchar(16)",
            _ => throw new ArgumentOutOfRangeException(nameof(storageContract)),
        };

        var declaredValue = ApprovedBackfillValueSql(storageContract, declared: true);
        var preservedValue = ApprovedBackfillValueSql(storageContract, declared: false);
        // WHY: CONVERT(hex USING utf8mb4) is an expression default, not the
        // provider's authored literal default. Matching must use the same facet.
        var defaultLiteral = storageContract == "integer"
            ? "7"
            : "'" + ApprovedBackfillDeclaredValue(storageContract).Replace("'", "''", StringComparison.Ordinal) + "'";

        return "CREATE TABLE `approved_backfill_items` (`key` int NOT NULL PRIMARY KEY, `required_value` "
            + storeType + (alreadyMatching ? " NOT NULL DEFAULT " + defaultLiteral : " NULL")
            + ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `approved_backfill_items` (`key`, `required_value`) VALUES (1, "
            + (alreadyMatching ? declaredValue : "NULL") + "), (2, " + preservedValue + ");";
    }

    /// <summary>Returns the portable SQL value for a bounded, explicitly declared or preserved test sentinel.</summary>
    /// <param name="storageContract">The finite test-owned storage contract.</param>
    /// <param name="declared">Whether the value is the reviewed replacement.</param>
    /// <returns>The fixture literal, with no production data or external input.</returns>
    private static string ApprovedBackfillValueSql(string storageContract, bool declared)
        => storageContract == "integer"
            ? declared ? "7" : "11"
            : AtomicRequiredAddLiteral(declared
                ? ApprovedBackfillDeclaredValue(storageContract)
                : ApprovedBackfillPreservedValue(storageContract));

    /// <summary>Gets the exact declared replacement for portable scalar comparison.</summary>
    /// <param name="storageContract">The finite test-owned storage contract.</param>
    /// <returns>The string representation of the approved immutable value.</returns>
    private static string ApprovedBackfillDeclaredValue(string storageContract) => storageContract switch
    {
        "guid" => ApprovedBackfillGuidValue,
        "integer" => "7",
        "varchar-transition" => "approved",
        _ => throw new ArgumentOutOfRangeException(nameof(storageContract)),
    };

    /// <summary>Gets the non-NULL sentinel which the backfill must never alter.</summary>
    /// <param name="storageContract">The finite test-owned storage contract.</param>
    /// <returns>The string representation of the original value.</returns>
    private static string ApprovedBackfillPreservedValue(string storageContract) => storageContract switch
    {
        "guid" => ApprovedBackfillPreservedGuid,
        "integer" => "11",
        "varchar-transition" => "preserved",
        _ => throw new ArgumentOutOfRangeException(nameof(storageContract)),
    };

    /// <summary>Reads current physical nullability without using cached operation state.</summary>
    /// <param name="connectionString">The isolated test database.</param>
    /// <returns>One while the source still permits NULL, otherwise zero.</returns>
    private static Task<int> ApprovedBackfillNullableCountAsync(string connectionString)
        => ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'approved_backfill_items' AND COLUMN_NAME = 'required_value' AND IS_NULLABLE = 'YES';");

    /// <summary>Reads current character width so failed widening cannot masquerade as completed DDL.</summary>
    /// <param name="connectionString">The isolated test database.</param>
    /// <returns>The physical character width, or zero for integer storage.</returns>
    private static Task<int> ApprovedBackfillCharacterWidthAsync(string connectionString)
        => ScalarIntAsync(connectionString,
            "SELECT COALESCE(CHARACTER_MAXIMUM_LENGTH, 0) FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'approved_backfill_items' "
            + "AND COLUMN_NAME = 'required_value';");

    /// <summary>Waits for a committed marker after the real UPDATE and before the real ALTER.</summary>
    /// <param name="writer">The independent writer session.</param>
    /// <param name="transaction">The transaction retaining the existing test-owned gate.</param>
    /// <param name="checkpoint">The scoped approved-backfill body observer.</param>
    /// <param name="cancellationToken">The bounded test deadline.</param>
    private static async Task WaitForApprovedBackfillCheckpointAsync(
        MySqlConnection writer,
        MySqlTransaction transaction,
        ApprovedBackfillCheckpointInterceptor checkpoint,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            await using var command = writer.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT `reached` FROM `atomic_add_checkpoint` WHERE `key` = 1;";
            var reached = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);

            if (reached == 1)
            {
                checkpoint.RecordObservedCheckpoint();

                return;
            }

            // WHY: Polling merely yields until a real committed marker is
            // visible. Elapsed time never determines the writer's interleaving.
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    /// <summary>Observes or checkpoints only the real approved UPDATE-plus-MODIFY logical body.</summary>
    private sealed class ApprovedBackfillCheckpointInterceptor : DbCommandInterceptor
    {
        private const string BodyPrefix = "SET @doka_sm_sql = CASE WHEN @doka_sm_action = 'apply' THEN";
        private const string StageEnd = "DEALLOCATE PREPARE doka_sm_statement;";
        private const string CheckpointSql = "UPDATE `atomic_add_checkpoint` SET `reached` = 1 WHERE `key` = 1; "
            + "SELECT `key` FROM `atomic_add_gate` WHERE `key` = 1 FOR UPDATE; ";
        private static readonly string s_updateHex = Convert.ToHexString(
            Encoding.UTF8.GetBytes("UPDATE `approved_backfill_items`"));

        private static readonly string s_alterHex = Convert.ToHexString(
            Encoding.UTF8.GetBytes("ALTER TABLE `approved_backfill_items`"));

        private readonly bool _injectCheckpoint;
        private int _observed;
        private int _injected;
        private int _checkpointReached;
        private string? _observedAction;

        /// <summary>Chooses an observation-only control or a deterministic late-writer checkpoint.</summary>
        /// <param name="injectCheckpoint">Whether to block after UPDATE and before MODIFY.</param>
        public ApprovedBackfillCheckpointInterceptor(bool injectCheckpoint) => _injectCheckpoint = injectCheckpoint;

        /// <summary>Gets whether the exact expected logical body was actually dispatched.</summary>
        public bool BodyWasObserved => Volatile.Read(ref _observed) != 0;

        /// <summary>Gets whether the real body was checkpointed exactly once.</summary>
        public bool WasInjected => Volatile.Read(ref _injected) != 0;

        /// <summary>Gets whether the independent writer observed the database's committed marker.</summary>
        public bool CheckpointWasReached => Volatile.Read(ref _checkpointReached) != 0;

        /// <summary>Gets the runtime-selected action captured before executing the guarded body.</summary>
        public string? ObservedAction => Volatile.Read(ref _observedAction);

        /// <summary>Records non-vacuity only after observing the live committed marker.</summary>
        public void RecordObservedCheckpoint() => Volatile.Write(ref _checkpointReached, 1);

        /// <inheritdoc />
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            var sql = command.CommandText;
            if (!sql.StartsWith(BodyPrefix, StringComparison.Ordinal)
                || !sql.Contains(s_updateHex, StringComparison.OrdinalIgnoreCase)
                || !sql.Contains(s_alterHex, StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            var firstEnd = sql.IndexOf(StageEnd, StringComparison.Ordinal);
            var secondEnd = firstEnd < 0 ? -1 : sql.IndexOf(StageEnd, firstEnd + StageEnd.Length,
                StringComparison.Ordinal);

            if (firstEnd < 0 || secondEnd < 0
                || sql.IndexOf(StageEnd, secondEnd + StageEnd.Length, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException("Approved backfill requires its two owned native command groups.");
            }

            await using var probe = (command.Connection
                ?? throw new InvalidOperationException("The approved-backfill body has no active connection."))
                .CreateCommand();

            // WHY: The internal action is read only on this already acquired
            // session. Seeing payload text alone cannot prove a matching NoOp.
            probe.Transaction = command.Transaction;
            probe.CommandText = "SELECT @doka_sm_action;";
            var action = Convert.ToString(await probe.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);

            Volatile.Write(ref _observedAction, action);
            Volatile.Write(ref _observed, 1);
            if (_injectCheckpoint && Interlocked.CompareExchange(ref _injected, 1, 0) == 0)
            {
                command.CommandText = sql.Insert(firstEnd + StageEnd.Length, CheckpointSql);
            }

            return result;
        }
    }
}
