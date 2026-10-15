namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the NativeOwnerCase migration contracts in an independent class collection.</summary>
public sealed partial class MySqlNativeOwnerCaseIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlNativeOwnerCaseIntegrationTests(MySqlEngineContainerFixture fixture) : base(fixture) { }

    /// <summary>Strict table contracts retain standalone declarations for one case-insensitive native owner.</summary>
    /// <param name="kind">Whether the standalone declaration is a unique index or constraint.</param>
    /// <param name="analyze">Whether to exercise live analysis rather than connection-free SQL generation.</param>
    /// <param name="mixedOwner">Whether the standalone operation uses another native-equivalent owner spelling.</param>
    [Theory]
    [InlineData("unique-index", true, true)]
    [InlineData("unique-index", false, true)]
    [InlineData("unique-constraint", true, true)]
    [InlineData("unique-constraint", false, true)]
    [InlineData("unique-index", true, false)]
    [InlineData("unique-index", false, false)]
    [InlineData("unique-constraint", true, false)]
    [InlineData("unique-constraint", false, false)]
    public async Task NativeCaseInsensitiveStrictTableRetainsStandaloneContract(
        string kind,
        bool analyze,
        bool mixedOwner
    )
    {
        // Arrange
        using var dedicated = new MySqlEngineContainerFixture(lowerCaseTableNames: 1);
        await dedicated.InitializeAsync();
        try
        {
            var connectionString = await dedicated.CreateDatabaseAsync(CancellationToken.None);
            await ExecuteSqlAsync(connectionString,
                "CREATE TABLE `mixed_owner` (`id` int NOT NULL PRIMARY KEY, `value` int NULL, "
                + "UNIQUE KEY `uq_slot` (`value`)) ENGINE=InnoDB;");

            await using var context = new SafeMigrationDbContext(connectionString, dedicated.ServerVersion);
            var operations = NativeMixedOwnerContract(context, kind, mixedOwner);
            var runner = context.GetService<ISafeMigrationRunner>();

            // WHY: The live unique object was authored by the standalone
            // operation. A model-only fallback would hide the split owner.
            // Act
            var report = analyze
                ? await runner.AnalyzeAsync(context, operations,
                    new SafeMigrationRunOptions("native-mode-one-contract"), CancellationToken.None)
                : null;

            var failure = analyze
                ? null
                : await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, operations,
                    CancellationToken.None));

            // Assert
            if (analyze)
            {
                var actual = Assert.IsType<SafeMigrationRunReport>(report);
                Assert.Equal(SafeMigrationReportStatus.Ready, actual.Status);
                Assert.All(actual.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp,
                    assessment.Action));
            }
            else
            {
                Assert.Null(failure);
                Assert.Equal(1, await ScalarIntAsync(connectionString,
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA=DATABASE() "
                    + "AND TABLE_NAME='mixed_owner' AND INDEX_NAME='uq_slot';"));
            }

        }
        finally
        {
            await dedicated.DisposeAsync();
        }
    }

    /// <summary>A mode-zero strict owner cannot borrow a standalone declaration from a case-distinct table.</summary>
    /// <param name="kind">Whether the foreign declaration is a unique index or constraint.</param>
    [Theory]
    [InlineData("unique-index")]
    [InlineData("unique-constraint")]
    public async Task NativeCaseSensitiveStrictTableDoesNotBorrowDistinctOwnerContract(string kind)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `Mixed_Owner` (`id` int NOT NULL PRIMARY KEY, `value` int NULL, "
            + "UNIQUE KEY `uq_unmodeled` (`value`)) ENGINE=InnoDB; "
            + "CREATE TABLE `MIXED_OWNER` (`id` int NOT NULL PRIMARY KEY, `value` int NULL, "
            + "UNIQUE KEY `uq_slot` (`value`)) ENGINE=InnoDB;");

        await using var context = CreateContext(connectionString);
        var operations = NativeMixedOwnerContract(context, kind);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("native-mode-zero-distinct-contract"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, operations,
            CancellationToken.None));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[0].Action);
        Assert.Contains("doka_sm_different", AssertOwnedGuardFailure(failure).Message,
            StringComparison.Ordinal);
        Assert.Equal(2, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA=DATABASE() "
            + "AND BINARY TABLE_NAME IN (BINARY 'Mixed_Owner', BINARY 'MIXED_OWNER');"));
    }

    /// <summary>A future unique declaration forbids a lossy default backfill across native owner spelling.</summary>
    /// <param name="analyze">Whether to exercise live analysis rather than direct runtime execution.</param>
    /// <param name="mixedOwner">Whether the future declaration uses another native-equivalent owner spelling.</param>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task NativeCaseInsensitiveFutureUniqueBackfillPreservesOriginalNullRows(
        bool analyze,
        bool mixedOwner
    )
    {
        // Arrange
        using var dedicated = new MySqlEngineContainerFixture(lowerCaseTableNames: 1);
        await dedicated.InitializeAsync();
        try
        {
            var connectionString = await dedicated.CreateDatabaseAsync(CancellationToken.None);
            await ExecuteSqlAsync(connectionString,
                "CREATE TABLE `backfill_owner` (`id` int NOT NULL PRIMARY KEY, `value` varchar(10) NULL) "
                + "ENGINE=InnoDB; INSERT INTO `backfill_owner` VALUES (1,NULL),(2,NULL);");

            await using var context = new SafeMigrationDbContext(connectionString, dedicated.ServerVersion);
            var builder = new MigrationBuilder(context.Database.ProviderName!);
            builder.Operations.Add(new SafeMigrationOperation(new AlterColumnIntent("backfill_owner",
                    new ExpectedColumnDefinition("value", typeof(string), false, "varchar(20)",
                        defaultValue: SafeMigrationDefaultValue.Literal("x")),
                    new ExpectedColumnDefinition("value", typeof(string), true, "varchar(10)")),
                SafeMigrationPolicy.RepairIfSafe));

            builder.CreateIndexIfNotExists("uq_future", mixedOwner ? "BACKFILL_OWNER" : "backfill_owner",
                ["value"], unique: true);

            var runner = context.GetService<ISafeMigrationRunner>();

            // Act
            var report = analyze
                ? await runner.AnalyzeAsync(context, builder.Operations,
                    new SafeMigrationRunOptions("native-mode-one-backfill"), CancellationToken.None)
                : null;

            var failure = analyze
                ? null
                : await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations,
                    CancellationToken.None));

            var nullRows = await ScalarIntAsync(connectionString,
                "SELECT COUNT(*) FROM `backfill_owner` WHERE `value` IS NULL;");

            // Assert
            Assert.Equal(2, nullRows);
            if (analyze)
            {
                var actual = Assert.IsType<SafeMigrationRunReport>(report);
                Assert.Equal(SafeMigrationReportStatus.Blocked, actual.Status);
                Assert.Equal(SafeMigrationAction.RejectDataBlocked, actual.Assessments[0].Action);
            }
            else
            {
                Assert.Contains("doka_sm_data_blocked", AssertOwnedGuardFailure(failure).Message,
                    StringComparison.Ordinal);
            }

        }
        finally
        {
            await dedicated.DisposeAsync();
        }
    }

    /// <summary>Cold application and replay preserve one native owner across standalone declarations.</summary>
    /// <param name="kind">The standalone unique index or unique constraint contract.</param>
    [Theory]
    [InlineData("unique-index")]
    [InlineData("unique-constraint")]
    public async Task NativeCaseInsensitiveColdStrictOwnerAppliesVerifiesAndReplays(string kind)
    {
        // Arrange
        using var dedicated = new MySqlEngineContainerFixture(lowerCaseTableNames: 1);
        await dedicated.InitializeAsync();
        try
        {
            var connectionString = await dedicated.CreateDatabaseAsync(CancellationToken.None);
            await using var context = new SafeMigrationDbContext(connectionString, dedicated.ServerVersion);
            var operations = NativeMixedOwnerContract(context, kind);
            var runner = context.GetService<ISafeMigrationRunner>();

            // Act
            var initial = await runner.AnalyzeAsync(context, operations,
                new SafeMigrationRunOptions("native-mode-one-cold"), CancellationToken.None);

            initial.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, operations, CancellationToken.None);
            var final = await runner.VerifyAsync(context, operations,
                new SafeMigrationRunOptions("native-mode-one-cold-final"), CancellationToken.None);

            var replay = await runner.AnalyzeAsync(context, operations,
                new SafeMigrationRunOptions("native-mode-one-cold-replay"), CancellationToken.None);

            // Assert
            Assert.Equal(SafeMigrationReportStatus.Ready, initial.Status);
            Assert.All(initial.Assessments, static assessment => Assert.Equal(SafeMigrationAction.Apply,
                assessment.Action));
            Assert.Equal(SafeMigrationReportStatus.Ready, final.Status);
            Assert.All(final.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
            Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
            Assert.All(replay.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp,
                assessment.Action));
        }
        finally
        {
            await dedicated.DisposeAsync();
        }
    }

    /// <summary>Constant catalog predicates do not borrow a differently spelled native mode-zero owner.</summary>
    /// <param name="create">Whether to exercise table creation or a missing-column drop.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCaseZeroMissingOwnerDoesNotBorrowCatalogPresence(bool create)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `Rows` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB;");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (create)
        {
            builder.EnsureTable(new ExpectedTableDefinition("rows",
                    [new ExpectedColumnDefinition("id", typeof(int), false, "int")]),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }
        else
        {
            builder.DropColumnIfExists("id", "rows");
        }

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-mode-zero-missing-owner"), CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var requested = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA=DATABASE() "
            + "AND BINARY TABLE_NAME=BINARY 'rows';");

        var originalColumn = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() "
            + "AND BINARY TABLE_NAME=BINARY 'Rows' AND COLUMN_NAME='id';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, report.Assessments[0].ObservedState);
        Assert.Equal(create ? SafeMigrationAction.Apply : SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(create ? 1 : 0, requested);
        Assert.Equal(1, originalColumn);
    }

    /// <summary>
    /// Mode-zero runtime does not apply a folded future constraint to another owner's declared backfill.
    /// </summary>
    [Fact]
    public async Task NativeCaseZeroBackfillDoesNotBorrowForeignOwnerHazard()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `backfill_owner` (`id` int NOT NULL PRIMARY KEY, `value` varchar(10) NULL) "
            + "ENGINE=InnoDB; INSERT INTO `backfill_owner` VALUES (1,NULL),(2,NULL); "
            + "CREATE TABLE `BACKFILL_OWNER` (`id` int NOT NULL PRIMARY KEY, `value` varchar(20) NULL) ENGINE=InnoDB;");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Operations.Add(new SafeMigrationOperation(new AlterColumnIntent("backfill_owner",
                new ExpectedColumnDefinition("value", typeof(string), false, "varchar(20)",
                    defaultValue: SafeMigrationDefaultValue.Literal("x")),
                new ExpectedColumnDefinition("value", typeof(string), true, "varchar(10)")),
            SafeMigrationPolicy.RepairIfSafe));

        builder.CreateIndexIfNotExists("uq_future", "BACKFILL_OWNER", ["value"], unique: true);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var replacedRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `backfill_owner` WHERE `value`='x';");

        var nativeIndex = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA=DATABASE() "
            + "AND BINARY TABLE_NAME=BINARY 'BACKFILL_OWNER' AND INDEX_NAME='uq_future';");

        // Assert
        Assert.Equal(2, replacedRows);
        Assert.Equal(1, nativeIndex);
    }

    /// <summary>Only the actual folded mode rejects an incoherent stream, before its first table mutation.</summary>
    /// <param name="fold">Whether the dedicated native server folds table owners.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCaseSelectionRejectsOnlyInvalidFoldedStream(bool fold)
    {
        // Arrange
        using var dedicated = new MySqlEngineContainerFixture(lowerCaseTableNames: fold ? 1 : 0);
        await dedicated.InitializeAsync();
        try
        {
            var connectionString = await dedicated.CreateDatabaseAsync(CancellationToken.None);
            await using var context = new SafeMigrationDbContext(connectionString, dedicated.ServerVersion);
            var builder = new MigrationBuilder(context.Database.ProviderName!);
            foreach (var table in new[] { "rows", "ROWS" })
            {
                builder.EnsureTable(new ExpectedTableDefinition(table,
                        [new ExpectedColumnDefinition("id", typeof(int), false, "int"),
                            new ExpectedColumnDefinition("value", typeof(int), true, "int")]),
                    SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
            }

            builder.CreateIndexIfNotExists("ix_value", "ROWS", ["value"]);
            builder.DropColumnIfExists("value", "rows");

            // Act
            var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations,
                CancellationToken.None));

            var tables = await ScalarIntAsync(connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA=DATABASE();");

            // Assert
            if (fold)
            {
                Assert.Contains("doka_sm_different", AssertOwnedGuardFailure(failure).Message,
                    StringComparison.Ordinal);
                Assert.Equal(0, tables);
            }
            else
            {
                Assert.Null(failure);
                Assert.Equal(2, tables);
                Assert.Equal(0, await ScalarIntAsync(connectionString,
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() "
                    + "AND BINARY TABLE_NAME=BINARY 'rows' AND COLUMN_NAME='value';"));
            }

        }
        finally
        {
            await dedicated.DisposeAsync();
        }
    }

    /// <summary>Builds an explicit strict owner and its standalone unique declaration.</summary>
    /// <param name="context">The verified native provider context.</param>
    /// <param name="kind">The standalone unique declaration.</param>
    /// <param name="mixedOwner">Whether the declaration intentionally uses another owner spelling.</param>
    /// <returns>The explicit strict table and its ordered unique operation.</returns>
    private static List<MigrationOperation> NativeMixedOwnerContract(
        DbContext context,
        string kind,
        bool mixedOwner = true
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("Mixed_Owner",
                [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int"),
                    new ExpectedColumnDefinition("value", typeof(int), isNullable: true, storeType: "int")],
                primaryKey: new ExpectedPrimaryKeyDefinition("PRIMARY", "Mixed_Owner", ["id"])),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        var table = mixedOwner ? "MIXED_OWNER" : "Mixed_Owner";
        if (kind == "unique-index")
        {
            builder.CreateIndexIfNotExists("uq_slot", table, ["value"], unique: true);
        }
        else
        {
            builder.AddUniqueConstraintIfNotExists("uq_slot", table, ["value"]);
        }

        return builder.Operations;
    }
}
