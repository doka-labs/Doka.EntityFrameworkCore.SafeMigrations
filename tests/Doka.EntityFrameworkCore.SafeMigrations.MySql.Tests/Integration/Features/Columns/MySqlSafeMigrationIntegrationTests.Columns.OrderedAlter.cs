namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Both new and populated tables retain physical provenance through multiple alterations.</summary>
    /// <param name="existing">Whether convergence observes an existing populated table.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrderedRenameAlterTransitionsUseAcceptedTargetWidths(bool existing)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        if (existing)
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE TABLE `ordered_source` (`id` char(36) NOT NULL, `value` longtext NOT NULL, "
                + "`other_value` longtext NOT NULL) ENGINE=InnoDB ROW_FORMAT=DYNAMIC "
                + "DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
                + "INSERT INTO `ordered_source` VALUES ('id', 'preserved', 'second');");
        }

        await using var context = CreateContext(connectionString);
        var operations = OrderedAlterOperations();

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, operations, new SafeMigrationRunOptions("ordered-alter-preflight"), CancellationToken.None);

        await ExecuteOperationsAsync(context, operations, CancellationToken.None);
        var postflight = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, operations, new SafeMigrationRunOptions("ordered-alter-postflight"), CancellationToken.None);

        var rowCount = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `ordered_target`;");
        var varcharCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'ordered_target' AND DATA_TYPE = 'varchar' "
            + "AND CHARACTER_MAXIMUM_LENGTH = 500;");

        var indexPrefixLength = await ScalarIntAsync(connectionString,
            "SELECT SUB_PART FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'ordered_target' AND INDEX_NAME = 'ix_ordered_target_value';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.Repair, preflight.Assessments[2].Action);
        Assert.Equal(SafeMigrationAction.Repair, preflight.Assessments[3].Action);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[4].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(existing ? 1 : 0, rowCount);
        Assert.Equal(2, varcharCount);
        Assert.Equal(200, indexPrefixLength);
    }

    /// <summary>Authored convergence columns cannot authorize drift, truncation or rename conflicts.</summary>
    /// <param name="failure">The adversarial live-state condition.</param>
    [Theory]
    [InlineData("old_definition")]
    [InlineData("overlength")]
    [InlineData("rename_collision")]
    [InlineData("source_row_format")]
    public async Task OrderedRenameAlterRetainsSourceBlockers(string failure)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var sourceType = failure == "old_definition" ? "text" : "longtext";
        var rowFormat = failure == "source_row_format" ? "COMPACT" : "DYNAMIC";
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE `ordered_source` (`id` char(36) NOT NULL, `value` {sourceType} NOT NULL, "
            + $"`other_value` longtext NOT NULL) ENGINE=InnoDB ROW_FORMAT={rowFormat} "
            + "DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + "INSERT INTO `ordered_source` VALUES ('id', "
            + (failure == "overlength" ? "REPEAT('x', 501)" : "'preserved'") + ", 'second');");
        if (failure == "rename_collision")
        {
            await ExecuteSqlAsync(connectionString, "CREATE TABLE `ordered_target` (`id` int NOT NULL);");
        }

        await using var context = CreateContext(connectionString);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, OrderedAlterOperations(), new SafeMigrationRunOptions("ordered-alter-blocker"),
            CancellationToken.None);

        var rowCount = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `ordered_source`;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        var blockedOrdinal = failure == "rename_collision" ? 1 : failure == "source_row_format" ? 4 : 2;
        Assert.Contains(report.Assessments[blockedOrdinal].Action, new SafeMigrationAction?[]
        {
            SafeMigrationAction.RejectDifferent,
            SafeMigrationAction.RejectDataBlocked,
            SafeMigrationAction.RejectUnsupported,
            SafeMigrationAction.RejectPrerequisiteMissing,
        });
        Assert.Equal(1, rowCount);
    }

    /// <summary>Typed data changes cannot reuse row proofs captured before a rename.</summary>
    [Fact]
    public async Task OrderedRenameAlterRejectsStaleDataProof()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `ordered_source` (`id` char(36) NOT NULL, `value` longtext NOT NULL, "
            + "`other_value` longtext NOT NULL) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 "
            + "COLLATE utf8mb4_unicode_ci;");
        await using var context = CreateContext(connectionString);
        var operations = OrderedAlterOperations();
        operations.Insert(2, new InsertDataOperation
        {
            Table = "ordered_target",
            Columns = ["id", "value", "other_value"],
            Values = new object?[,] { { "id", new string('x', 501), "second" } },
        });

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, operations, new SafeMigrationRunOptions("ordered-alter-stale"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[3].Action);
        Assert.Equal("prerequisite_missing", report.Assessments[3].Code);
    }

    private static List<MigrationOperation> OrderedAlterOperations()
    {
        var id = new ExpectedColumnDefinition("id", typeof(string), isNullable: false, storeType: "char(36)",
            maxLength: 36, isFixedLength: true);
        var source = new ExpectedColumnDefinition("value", typeof(string), isNullable: false, storeType: "longtext");
        var otherSource = new ExpectedColumnDefinition("other_value", typeof(string), isNullable: false,
            storeType: "longtext");
        var target = new ExpectedColumnDefinition("value", typeof(string), isNullable: false,
            storeType: "varchar(500)", maxLength: 500);
        var otherTarget = new ExpectedColumnDefinition("other_value", typeof(string), isNullable: false,
            storeType: "varchar(500)", maxLength: 500);

        return
        [
            new SafeMigrationOperation(
                new EnsureTableIntent(new ExpectedTableDefinition("ordered_source", [id, source, otherSource]),
                    SafeMigrationTableMode.ConvergenceContainer), SafeMigrationPolicy.ThrowIfDifferent),
            new SafeMigrationOperation(new RenameTableIntent("ordered_source", "ordered_target"),
                SafeMigrationPolicy.ThrowIfDifferent),
            new SafeMigrationOperation(new AlterColumnIntent("ordered_target", target, source),
                SafeMigrationPolicy.RepairIfSafe),
            new SafeMigrationOperation(new AlterColumnIntent("ordered_target", otherTarget, otherSource),
                SafeMigrationPolicy.RepairIfSafe),
            new SafeMigrationOperation(new EnsureIndexIntent(new ExpectedIndexDefinition(
                "ix_ordered_target_value", "ordered_target",
                [new ExpectedIndexKeyDefinition(column: "value", prefixLength: 200)])),
                SafeMigrationPolicy.ThrowIfDifferent),
        ];
    }

    /// <summary>A missing live source says nothing about the rename destination's absence.</summary>
    /// <param name="isView">Whether a view rather than a base table occupies the destination namespace.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewProjectedTableCannotRenameOverAnExistingDestination(bool isView)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, isView
            ? "CREATE VIEW `ordered_target` AS SELECT 1 AS `id`;"
            : "CREATE TABLE `ordered_target` (`id` int NOT NULL);");
        await using var context = CreateContext(connectionString);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, OrderedAlterOperations(), new SafeMigrationRunOptions("ordered-new-rename-conflict"),
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
    }

    /// <summary>An accepted FK stays associated with its physical source after a table rename.</summary>
    [Fact]
    public async Task AcceptedForeignKeyBeforeRenameBlocksProjectedAlter()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `fk_source` (`value` varchar(100) NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `fk_dependent` (`value` varchar(100) NOT NULL) ENGINE=InnoDB;");
        await using var context = CreateContext(connectionString);
        var source = new ExpectedColumnDefinition("value", typeof(string), false, "varchar(100)", maxLength: 100);
        var target = new ExpectedColumnDefinition("value", typeof(string), false, "varchar(200)", maxLength: 200);
        var operations = new List<MigrationOperation>
        {
            new SafeMigrationOperation(new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
                "fk_accepted", "fk_dependent", ["value"], "fk_source", ["value"])),
                SafeMigrationPolicy.ThrowIfDifferent),
            new SafeMigrationOperation(new RenameTableIntent("fk_source", "fk_target"),
                SafeMigrationPolicy.ThrowIfDifferent),
            new SafeMigrationOperation(new AlterColumnIntent("fk_target", target, source),
                SafeMigrationPolicy.RepairIfSafe),
        };

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, operations, new SafeMigrationRunOptions("ordered-fk-alter"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[2].Action);
    }

    /// <summary>Empty tables still obey provider foreign-key transition restrictions.</summary>
    [Fact]
    public async Task NewEmptyForeignKeyTableRejectsProjectedTypeTransition()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var source = new ExpectedColumnDefinition("value", typeof(string), false, "varchar(100)", maxLength: 100);
        var target = new ExpectedColumnDefinition("value", typeof(string), false, "varchar(200)", maxLength: 200);
        var table = new ExpectedTableDefinition("self_referencing", [source],
            primaryKey: new ExpectedPrimaryKeyDefinition("PRIMARY", "self_referencing", ["value"]),
            foreignKeys: [new ExpectedForeignKeyDefinition(
                "fk_self", "self_referencing", ["value"], "self_referencing", ["value"])]);
        var operations = new List<MigrationOperation>
        {
            new SafeMigrationOperation(new EnsureTableIntent(table, SafeMigrationTableMode.ConvergenceContainer),
                SafeMigrationPolicy.ThrowIfDifferent),
            new SafeMigrationOperation(new AlterColumnIntent("self_referencing", target, source),
                SafeMigrationPolicy.RepairIfSafe),
        };

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, operations, new SafeMigrationRunOptions("ordered-empty-fk"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[1].Action);
    }

    /// <summary>Raw SQL takes precedence over captured candidate source analyses.</summary>
    [Fact]
    public async Task OpaqueSqlAfterRenameDefersAlterAndDoesNotApplyItsShape()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var operations = OrderedAlterOperations();
        operations.Insert(2, new SqlOperation { Sql = "SELECT 1;" });

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, operations, new SafeMigrationRunOptions("ordered-opaque-alter"), CancellationToken.None);

        // Assert
        Assert.All(report.Assessments.Skip(3), static assessment =>
            Assert.Equal(SafeMigrationAction.ValidateAtRuntime, assessment.Action));
    }

    /// <summary>A rejected alteration cannot supply the target width to an index.</summary>
    [Fact]
    public async Task RejectedOrderedAlterDoesNotAuthorizeTheTargetIndexPrefix()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var source = new ExpectedColumnDefinition("value", typeof(string), false, "varchar(100)", maxLength: 100);
        var target = new ExpectedColumnDefinition("value", typeof(string), false, "varchar(500)", maxLength: 500);
        var operations = new List<MigrationOperation>
        {
            new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition("rejected_alter", [source]),
                SafeMigrationTableMode.ConvergenceContainer), SafeMigrationPolicy.ThrowIfDifferent),
            new SafeMigrationOperation(new AlterColumnIntent("rejected_alter", target, source),
                SafeMigrationPolicy.ThrowIfDifferent),
            new SafeMigrationOperation(new EnsureIndexIntent(new ExpectedIndexDefinition(
                "ix_rejected", "rejected_alter", [new ExpectedIndexKeyDefinition("value", prefixLength: 200)])),
                SafeMigrationPolicy.ThrowIfDifferent),
        };

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, operations, new SafeMigrationRunOptions("ordered-rejected-alter"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[2].Action);
        Assert.Equal("index_prefix_exceeds_target_column", report.Assessments[2].Code);
    }

    /// <summary>Accepted physical indexes remain constraints after the first column alteration.</summary>
    /// <param name="providerAlter">Whether an ordinary provider Alter replaces the first safe Alter.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrderedAlterRetainsAcceptedCompositeIndexBudget(bool providerAlter)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `composite_alter` (`left_value` varchar(100) NOT NULL, "
            + "`right_value` varchar(100) NOT NULL) ENGINE=InnoDB ROW_FORMAT=DYNAMIC "
            + "DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;");
        await using var context = CreateContext(connectionString);
        var left = new ExpectedColumnDefinition("left_value", typeof(string), false, "varchar(100)", maxLength: 100);
        var right = new ExpectedColumnDefinition("right_value", typeof(string), false, "varchar(100)", maxLength: 100);
        var operations = new List<MigrationOperation>
        {
            new SafeMigrationOperation(new EnsureIndexIntent(new ExpectedIndexDefinition(
                "ix_composite", "composite_alter",
                [new ExpectedIndexKeyDefinition("left_value"), new ExpectedIndexKeyDefinition("right_value")])),
                SafeMigrationPolicy.ThrowIfDifferent),
            new SafeMigrationOperation(new AlterColumnIntent("composite_alter",
                new ExpectedColumnDefinition("left_value", typeof(string), false, "varchar(500)", maxLength: 500),
                left),
                SafeMigrationPolicy.RepairIfSafe),
            new SafeMigrationOperation(new AlterColumnIntent("composite_alter",
                new ExpectedColumnDefinition("right_value", typeof(string), false, "varchar(500)", maxLength: 500),
                right),
                SafeMigrationPolicy.RepairIfSafe),
        };

        if (providerAlter)
        {
            operations[1] = new AlterColumnOperation
            {
                Table = "composite_alter",
                Name = "left_value",
                ClrType = typeof(string),
                ColumnType = "varchar(500)",
                MaxLength = 500,
                IsNullable = false,
                OldColumn = new AddColumnOperation
                {
                    ClrType = typeof(string),
                    ColumnType = "varchar(100)",
                    MaxLength = 100,
                    IsNullable = false,
                },
            };
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, operations, new SafeMigrationRunOptions("ordered-composite-budget"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Contains(report.Assessments[2].Action, new SafeMigrationAction?[]
        {
            SafeMigrationAction.RejectDifferent,
            SafeMigrationAction.RejectPrerequisiteMissing,
        });
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
    }

    /// <summary>Repeated references share source capture without sharing consumed target state.</summary>
    [Fact]
    public async Task RepeatedOrderedAlterReferenceCapturesSourceOnceAndProjectsReplay()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var operations = OrderedAlterOperations();
        operations.Insert(3, operations[2]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, operations, new SafeMigrationRunOptions("ordered-repeated-alter"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, report.Assessments[2].Action);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[3].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
    }

    /// <summary>Accepted indexes preserve logical ownership and aliases across table renames.</summary>
    /// <param name="existing">Whether the source table already exists.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrderedRenameRebindsAcceptedIndexIdentity(bool existing)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        if (existing)
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE TABLE `ordered_source` (`id` char(36) NOT NULL, `value` longtext NOT NULL, "
                + "`other_value` longtext NOT NULL) ENGINE=InnoDB ROW_FORMAT=DYNAMIC "
                + "DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;");
        }

        await using var context = CreateContext(connectionString);
        var operations = OrderedAlterOperations();
        operations.Insert(1, new SafeMigrationOperation(new EnsureIndexIntent(new ExpectedIndexDefinition(
            "ix_ordered_target_value", "ordered_source", [new ExpectedIndexKeyDefinition("value", prefixLength: 200)])),
            SafeMigrationPolicy.ThrowIfDifferent));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, operations, new SafeMigrationRunOptions("ordered-index-identity"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[5].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
    }

    /// <summary>Earlier accepted convergence repairs remain provenance, not immutable live source drift.</summary>
    /// <param name="originalLength">The immutable live source's bounded string domain.</param>
    /// <param name="mutateRows">Whether an intervening row change invalidates source row-domain evidence.</param>
    /// <param name="acceptedIndex">Whether a preceding accepted prefix index must survive convergence.</param>
    [Theory]
    [InlineData(200, false, false)]
    [InlineData(500, false, false)]
    [InlineData(500, true, false)]
    [InlineData(200, false, true)]
    public async Task OrderedAlterAfterAcceptedConvergenceRepairUsesCurrentSource(
        int originalLength,
        bool mutateRows,
        bool acceptedIndex
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE `converged_source` (`value` varchar({originalLength}) NOT NULL) "
            + "ENGINE=InnoDB ROW_FORMAT=DYNAMIC "
            + "DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + "INSERT INTO `converged_source` VALUES ('preserved');");
        await using var context = CreateContext(connectionString);
        var source = new ExpectedColumnDefinition("value", typeof(string), false, "longtext");
        var target = new ExpectedColumnDefinition("value", typeof(string), false, "varchar(500)", maxLength: 500);
        var operations = new List<MigrationOperation>
        {
            new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition("converged_source", [source]),
                SafeMigrationTableMode.ConvergenceContainer), SafeMigrationPolicy.ThrowIfDifferent),
        };

        if (acceptedIndex)
        {
            operations.Add(new SafeMigrationOperation(new EnsureIndexIntent(new ExpectedIndexDefinition(
                "ix_value", "converged_source", [new ExpectedIndexKeyDefinition("value", prefixLength: 100)])),
                SafeMigrationPolicy.ThrowIfDifferent));
        }

        var repairOrdinal = operations.Count;
        operations.Add(new SafeMigrationOperation(new EnsureColumnIntent("converged_source", source),
            SafeMigrationPolicy.RepairIfSafe));
        operations.Add(new SafeMigrationOperation(new RenameTableIntent("converged_source", "converged_target"),
            SafeMigrationPolicy.ThrowIfDifferent));
        if (mutateRows)
        {
            operations.Add(new InsertDataOperation
            {
                Table = "converged_target",
                Columns = ["value"],
                Values = new object?[,] { { new string('x', 501) } },
            });
        }

        operations.Add(new SafeMigrationOperation(new AlterColumnIntent("converged_target", target, source),
            SafeMigrationPolicy.RepairIfSafe));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, operations, new SafeMigrationRunOptions("ordered-converged-source"), CancellationToken.None);

        int? preservedRowCount = null;
        int? indexPrefixLength = null;
        if (!mutateRows)
        {
            await ExecuteOperationsAsync(context, operations, CancellationToken.None);
            preservedRowCount = await ScalarIntAsync(connectionString,
                "SELECT COUNT(*) FROM `converged_target` WHERE `value` = 'preserved';");

            if (acceptedIndex)
            {
                indexPrefixLength = await ScalarIntAsync(connectionString,
                    "SELECT SUB_PART FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() "
                    + "AND TABLE_NAME = 'converged_target' AND INDEX_NAME = 'ix_value';");
            }
        }

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, report.Assessments[repairOrdinal].Action);
        if (mutateRows)
        {
            Assert.Contains(report.Assessments[^1].Action, new SafeMigrationAction?[]
            {
                SafeMigrationAction.RejectDifferent,
                SafeMigrationAction.RejectPrerequisiteMissing,
            });
        }
        else
        {
            Assert.Equal(SafeMigrationAction.Repair, report.Assessments[^1].Action);
            Assert.Equal(1, preservedRowCount);
            if (acceptedIndex)
            {
                Assert.Equal(100, indexPrefixLength);
            }
        }
    }

    /// <summary>An ordinary EF backfill invalidates row proofs for tables changed by its triggers.</summary>
    [Fact]
    public async Task OrdinaryAlterBackfillTriggerInvalidatesLaterNarrowingProof()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `trigger_target` (`value` longtext NOT NULL) ENGINE=InnoDB; "
            + "INSERT INTO `trigger_target` VALUES ('short'); "
            + "CREATE TABLE `backfill_source` (`value` int NULL) ENGINE=InnoDB; "
            + "INSERT INTO `backfill_source` VALUES (NULL); "
            + "CREATE TRIGGER `backfill_rows` BEFORE UPDATE ON `backfill_source` FOR EACH ROW "
            + "UPDATE `trigger_target` SET `value` = REPEAT('x', 501);");
        await using var context = CreateContext(connectionString);
        var backfill = new AlterColumnOperation
        {
            Table = "backfill_source",
            Name = "value",
            ClrType = typeof(int),
            ColumnType = "int",
            IsNullable = false,
            DefaultValue = 1,
            OldColumn = new AddColumnOperation
            {
                ClrType = typeof(int),
                ColumnType = "int",
                IsNullable = true,
            },
        };

        var alter = new SafeMigrationOperation(new AlterColumnIntent("trigger_target",
            new ExpectedColumnDefinition("value", typeof(string), false, "varchar(500)", maxLength: 500),
            new ExpectedColumnDefinition("value", typeof(string), false, "longtext")),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, [backfill, alter], new SafeMigrationRunOptions("ordinary-backfill-trigger"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, [backfill], CancellationToken.None);
        var fresh = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, [alter], new SafeMigrationRunOptions("ordinary-backfill-after-trigger"), CancellationToken.None);

        var valueLength = await ScalarIntAsync(connectionString, "SELECT CHAR_LENGTH(`value`) FROM `trigger_target`;");

        // Assert
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, fresh.Assessments[0].Action);
        Assert.Equal(501, valueLength);
    }

    /// <summary>Model-managed DML triggers invalidate another table's retained original value domain.</summary>
    /// <param name="kind">The model-managed mutation that fires the cross-table trigger.</param>
    [Theory]
    [InlineData("INSERT")]
    [InlineData("UPDATE")]
    [InlineData("DELETE")]
    public async Task ModelManagedTriggerInvalidatesRetainedAlterValueDomain(string kind)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `managed_target` (`value` varchar(200) NOT NULL) ENGINE=InnoDB; "
            + "INSERT INTO `managed_target` VALUES ('short'); "
            + "CREATE TABLE `managed_source` (`id` int NOT NULL PRIMARY KEY, `value` int NOT NULL) ENGINE=InnoDB; "
            + (kind == "INSERT" ? string.Empty : "INSERT INTO `managed_source` VALUES (1, 1); ")
            + $"CREATE TRIGGER `managed_rows` AFTER {kind} ON `managed_source` FOR EACH ROW "
            + "UPDATE `managed_target` SET `value` = REPEAT('x', 501);");
        await using var context = CreateContext(connectionString);
        var source = new ExpectedColumnDefinition("value", typeof(string), false, "longtext");
        var ensure = new SafeMigrationOperation(new EnsureColumnIntent("managed_target", source),
            SafeMigrationPolicy.RepairIfSafe);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (kind == "INSERT")
        {
            _ = builder.EnsureModelManagedDataFromModel("managed_source", ["id"], ["int"],
                ["id", "value"], ["int", "int"], new object?[,] { { 1, 2 } });
        }
        else if (kind == "UPDATE")
        {
            _ = builder.UpdateModelManagedDataFromModel("managed_source", ["id"], ["int"],
                new object?[,] { { 1 } }, ["value"], ["int"], new object?[,] { { 1 } }, new object?[,] { { 2 } });
        }
        else
        {
            _ = builder.DeleteModelManagedDataFromModel("managed_source", ["id"], ["int"],
                new object?[,] { { 1 } }, ["id", "value"], ["int", "int"], new object?[,] { { 1, 1 } });
        }

        var mutation = builder.Operations[0];
        var alter = new SafeMigrationOperation(new AlterColumnIntent("managed_target",
            new ExpectedColumnDefinition("value", typeof(string), false, "varchar(500)", maxLength: 500), source),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [ensure, mutation, alter],
            new SafeMigrationRunOptions("managed-trigger-domain"), CancellationToken.None);

        await ExecuteOperationsAsync(context, [ensure, mutation], CancellationToken.None);
        var fresh = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [alter],
            new SafeMigrationRunOptions("managed-trigger-fresh"), CancellationToken.None);

        var valueLength = await ScalarIntAsync(connectionString, "SELECT CHAR_LENGTH(`value`) FROM `managed_target`;");

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Contains(report.Assessments[2].Action, new SafeMigrationAction?[]
        {
            SafeMigrationAction.RejectDifferent,
            SafeMigrationAction.RejectPrerequisiteMissing,
        });
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, fresh.Assessments[0].Action);
        Assert.Equal(501, valueLength);
    }
}
