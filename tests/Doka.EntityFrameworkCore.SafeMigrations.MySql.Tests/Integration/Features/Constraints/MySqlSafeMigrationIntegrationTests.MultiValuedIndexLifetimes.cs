namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>
    /// Accepted source metadata cannot override independent session or remaining SET NULL restrictions.
    /// </summary>
    /// <param name="remainingSetNull">
    /// Whether a second native SET NULL dependency remains after the declared drop.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureColumnIntrinsicLayoutCannotOverrideNativeRepairPrerequisites(bool remainingSetNull)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `parent_id` int NULL, "
            + "CONSTRAINT `fk_dropped` FOREIGN KEY (`parent_id`) REFERENCES `live_parent` (`id`) "
            + "ON DELETE CASCADE"
            + (remainingSetNull ? ", CONSTRAINT `fk_retained` FOREIGN KEY (`parent_id`) "
                + "REFERENCES `live_parent` (`id`) ON UPDATE SET NULL" : string.Empty)
            + ") ENGINE=InnoDB; INSERT INTO `live_parent` VALUES (7); INSERT INTO `live_child` VALUES (1,7);");

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        if (!remainingSetNull)
        {
            // WHY: Analysis and execution must see the same deliberately
            // permissive session, not an unrelated short-lived connection.
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SET SESSION sql_mode='';";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
            "parent_id", "live_child", type: "int", nullable: true));

        builder.DropForeignKeyIfExists("fk_dropped", "live_child");
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
            "parent_id", "live_child", type: "int", nullable: false), SafeMigrationPolicy.RepairIfSafe);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-ensure-layout-independent-prerequisites"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            builder.Operations, CancellationToken.None));

        var nullable = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() "
            + "AND TABLE_NAME='live_child' AND COLUMN_NAME='parent_id' AND IS_NULLABLE='YES';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationAction.NoOp, preflight.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectDifferent, preflight.Assessments[^1].Action);
        Assert.Contains("doka_sm_different", AssertOwnedGuardFailure(failure).Message, StringComparison.Ordinal);
        Assert.Equal(1, nullable);
    }

    /// <summary>
    /// A safe live repair does not invent a complete accepted source layout for subsequent constraints.
    /// </summary>
    /// <param name="makeNullable">Whether the requested nullability relaxes instead of tightening.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeyEnsureColumnRepairWithoutAcceptedSourceRemainsUnproven(bool makeNullable)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `parent_id` int "
            + (makeNullable ? "NOT NULL" : "NULL") + ", CONSTRAINT `fk_native` FOREIGN KEY (`parent_id`) "
            + "REFERENCES `live_parent` (`id`) ON DELETE CASCADE) ENGINE=InnoDB; "
            + "INSERT INTO `live_parent` VALUES (7); INSERT INTO `live_child` VALUES (1,7);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_native", "live_child");
        builder.AddColumnIfNotExistsFromModel(operation => operation.AddColumn<int>(
            "parent_id", "live_child", type: "int", nullable: makeNullable), SafeMigrationPolicy.RepairIfSafe);

        builder.AddForeignKeyIfNotExists("fk_native", "live_child", ["parent_id"], "live_parent", ["id"],
            onDelete: makeNullable ? ReferentialAction.SetNull : ReferentialAction.NoAction);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-ensure-key-layout-source-unproven"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, preflight.Assessments[^1].Action);
    }

    /// <summary>Source-certified column repairs and explicit alters preserve the same retained FK key layout.</summary>
    /// <param name="ensureRepair">
    /// Whether the transition uses an existence-based repair instead of an explicit alter.
    /// </param>
    /// <param name="makeNullable">Whether the current nonnull fixture is relaxed rather than tightened.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ForeignKeyEnsureColumnRepairPreservesCertifiedSupportingLayout(
        bool ensureRepair,
        bool makeNullable
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `parent_id` int "
            + (makeNullable ? "NOT NULL" : "NULL") + ", CONSTRAINT `fk_native` FOREIGN KEY (`parent_id`) "
            + "REFERENCES `live_parent` (`id`) ON DELETE CASCADE) ENGINE=InnoDB; "
            + "INSERT INTO `live_parent` VALUES (7); INSERT INTO `live_child` VALUES (1,7);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operation => operation.AddColumn<int>(
            "parent_id", "live_child", type: "int", nullable: !makeNullable));

        builder.DropForeignKeyIfExists("fk_native", "live_child");
        if (ensureRepair)
        {
            builder.AddColumnIfNotExistsFromModel(operation => operation.AddColumn<int>(
                "parent_id", "live_child", type: "int", nullable: makeNullable),
                SafeMigrationPolicy.RepairIfSafe);
        }
        else
        {
            builder.AlterColumnIfDifferentFromModel(operation => operation.AlterColumn<int>(
                "parent_id", "live_child", type: "int", nullable: makeNullable, oldClrType: typeof(int),
                oldType: "int", oldNullable: !makeNullable), SafeMigrationPolicy.RepairIfSafe);
        }

        builder.AddForeignKeyIfNotExists("fk_native", "live_child", ["parent_id"], "live_parent", ["id"],
            onDelete: makeNullable ? ReferentialAction.SetNull : ReferentialAction.NoAction);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-certified-ensure-key-layout"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var final = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-certified-ensure-key-layout-final"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Repair, preflight.Assessments[2].Action);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[^1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, final.Status);
    }

    /// <summary>
    /// Native case aliases are valid, but an accented principal column is a different physical endpoint.
    /// </summary>
    /// <param name="accentedPrincipal">
    /// Whether only the native accented endpoint, not the requested case alias, exists.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SparseForeignKeyNativeColumnCertificateDoesNotBorrowAccentedPrincipal(bool accentedPrincipal)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var principalColumn = accentedPrincipal ? "\u00e9" : "e";
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`" + principalColumn + "` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "INSERT INTO `live_child` VALUES (1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
            "parent_id", "live_child", type: "int", nullable: true));

        builder.CreateIndexIfNotExistsFromModel("ix_parent", "live_child", "parent_id");
        builder.AddForeignKeyIfNotExists("fk_native", "live_child", ["parent_id"], "live_parent", ["E"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-column-identity-certificate"), CancellationToken.None);

        if (!accentedPrincipal)
        {
            preflight.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        }

        // Assert
        Assert.Equal(accentedPrincipal ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready,
            preflight.Status);
        Assert.Equal(accentedPrincipal ? SafeMigrationAction.RejectPrerequisiteMissing : SafeMigrationAction.Apply,
            preflight.Assessments[^1].Action);
    }

    /// <summary>
    /// A namespace snapshot cannot overwrite a subsequently accepted FK's current definition or alias.
    /// </summary>
    /// <param name="differentAction">
    /// Whether the second declaration deliberately changes the accepted FK action.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeyNamespaceSnapshotRetainsCurrentAcceptedConstraint(bool differentAction)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `parent_id` int NULL) ENGINE=InnoDB; "
            + "INSERT INTO `live_child` VALUES (1,NULL);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExistsFromModel("ix_parent", "live_child", "parent_id");
        builder.AddForeignKeyIfNotExists("fk_first", "live_child", ["parent_id"], "live_parent", ["id"],
            onDelete: ReferentialAction.Cascade);

        builder.AddForeignKeyIfNotExists(differentAction ? "fk_first" : "fk_alias", "live_child", ["parent_id"],
            "live_parent", ["id"], onDelete: differentAction ? ReferentialAction.NoAction : ReferentialAction.Cascade);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-current-fk-namespace-snapshot"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            builder.Operations, CancellationToken.None));

        // Assert
        Assert.Equal(differentAction ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready,
            preflight.Status);
        Assert.Equal(differentAction ? SafeMigrationAction.RejectDifferent : SafeMigrationAction.NoOp,
            preflight.Assessments[^1].Action);
        if (differentAction)
        {
            Assert.Contains("doka_sm_different", AssertOwnedGuardFailure(failure).Message,
                StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(failure);
        }
    }

    /// <summary>Accepted parent declarations cannot hide a new child's different inherited native collation.</summary>
    /// <param name="matchingDefault">
    /// Whether the new owner's inherited native collation matches the live parent.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeyNewOwnerCannotBorrowRawNullCollationFromMatchingParentDeclarations(bool matchingDefault)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        await ExecuteSqlAsync(connectionString,
            "ALTER DATABASE `" + database + "` CHARACTER SET utf8mb4 COLLATE "
            + (matchingDefault ? "utf8mb4_unicode_ci" : "utf8mb4_general_ci") + "; "
            + "CREATE TABLE `live_parent` (`id` char(36) NOT NULL PRIMARY KEY) ENGINE=InnoDB "
            + "DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<string>(
            "id", "live_parent", type: "char(36)", nullable: false, fixedLength: true, maxLength: 36));

        builder.AddPrimaryKeyIfNotExists("PRIMARY", "live_parent", ["id"]);
        builder.EnsureTable(new ExpectedTableDefinition("live_child",
            [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int"),
                new ExpectedColumnDefinition("parent_id", typeof(string), isNullable: true,
                    storeType: "char(36)", isFixedLength: true, maxLength: 36)],
            primaryKey: new ExpectedPrimaryKeyDefinition("PRIMARY", "live_child", ["id"])),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        builder.AddForeignKeyIfNotExists("fk_native", "live_child", ["parent_id"], "live_parent", ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-new-owner-inherited-parent-collation"), CancellationToken.None);

        if (matchingDefault)
        {
            preflight.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        }

        // Assert
        Assert.Equal(matchingDefault ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            preflight.Status);
        Assert.Equal(SafeMigrationAction.NoOp, preflight.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.NoOp, preflight.Assessments[1].Action);
        Assert.Equal(matchingDefault ? SafeMigrationAction.Apply : SafeMigrationAction.RejectPrerequisiteMissing,
            preflight.Assessments[^1].Action);
    }

    /// <summary>A newly accepted safe FK lifetime must not inherit its predecessor's retired MVI rejection.</summary>
    /// <param name="reuseOperation">
    /// Whether the identical authored operation object is replayed in the same stream.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeyFreshSafeReplacementRepeatsWithoutOriginalUnsafeLifetime(bool reuseOperation)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `parent_id` int NULL, "
            + "`values_json` JSON NOT NULL) ENGINE=InnoDB;");

        await ExecuteSqlAsync(connectionString, Fixture.IsMariaDb
            ? "ALTER TABLE `live_child` ADD `derived_id` int GENERATED ALWAYS AS (ABS(`id`)) STORED; "
                + "CREATE INDEX `ix_multi` ON `live_child` (`parent_id`, `derived_id`);"
            : "CREATE INDEX `ix_multi` ON `live_child` (`parent_id`, "
                + "(CAST(`values_json` AS UNSIGNED ARRAY)));");

        await ExecuteSqlAsync(connectionString,
            "ALTER TABLE `live_child` ADD CONSTRAINT `fk_native` FOREIGN KEY (`parent_id`) "
            + "REFERENCES `live_parent` (`id`);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_native", "live_child");
        builder.DropIndexIfExists("ix_multi", "live_child");
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
            "empty_marker", "live_child", type: "int", nullable: false));

        builder.AddForeignKeyIfNotExists("fk_native", "live_child", ["parent_id"], "live_parent", ["id"]);
        if (reuseOperation)
        {
            builder.Operations.Add(builder.Operations[^1]);
        }
        else
        {
            builder.AddForeignKeyIfNotExists("fk_native", "live_child", ["parent_id"], "live_parent", ["id"]);
        }

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-fresh-fk-repeat"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var final = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-fresh-fk-repeat-final"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[^2].Action);
        Assert.Equal(SafeMigrationAction.NoOp, preflight.Assessments[^1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, final.Status);
    }

    /// <summary>
    /// A retired semantic match or initially missing FK cannot borrow a subsequently removed child prefix.
    /// </summary>
    /// <param name="semanticMatch">
    /// Whether the original physical FK is a semantic alias of the requested endpoint.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeyNamespaceAfterRemovalQualifiesInitiallyMissingOrSemanticMatching(bool semanticMatch)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `parent_id` int NULL, `other_id` int NULL, "
            + "INDEX `fk_target` (`other_id`), INDEX `ix_cover` (`parent_id`)) ENGINE=InnoDB; "
            + "ALTER TABLE `live_child` ADD CONSTRAINT `fk_old` FOREIGN KEY (`"
            + (semanticMatch ? "parent_id" : "other_id") + "`) REFERENCES `live_parent` (`id`); "
            + "INSERT INTO `live_child` VALUES (1,NULL,NULL);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_old", "live_child");
        builder.DropIndexIfExists("ix_cover", "live_child");
        builder.AddForeignKeyIfNotExists("fk_target", "live_child", ["parent_id"], "live_parent", ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-retired-alias-index-namespace"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            builder.Operations, CancellationToken.None));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, preflight.Assessments[^1].Action);
        Assert.Contains("doka_sm_different", AssertOwnedGuardFailure(failure).Message,
            StringComparison.Ordinal);
    }

    /// <summary>A preceding index creation can occupy a previously free automatic FK index symbol.</summary>
    [Fact]
    public async Task ForeignKeyInitiallyMissingCannotBorrowPrecedingWrongIndexCreation()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `parent_id` int NULL, `other_id` int NULL) "
            + "ENGINE=InnoDB; INSERT INTO `live_child` VALUES (1,NULL,NULL);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExistsFromModel("fk_target", "live_child", "other_id");
        builder.AddForeignKeyIfNotExists("fk_target", "live_child", ["parent_id"], "live_parent", ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-created-wrong-index-namespace"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            builder.Operations, CancellationToken.None));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, preflight.Assessments[^1].Action);
        Assert.Contains("doka_sm_different", AssertOwnedGuardFailure(failure).Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Native removal of ordinary support beside an MVI must not silently lose reference enforcement.
    /// </summary>
    /// <param name="cascade">Whether parent deletion requests CASCADE instead of NO ACTION.</param>
    /// <param name="targetsFkPrefix">Whether the removed ordinary index actually covers the FK endpoint.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MultiValuedIndexOrdinarySupportingDropPreservesNativeForeignKeyEnforcement(
        bool cascade,
        bool targetsFkPrefix
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `parent_id` int NULL, "
            + "`other_id` int NULL, `values_json` JSON NOT NULL, INDEX `ix_ordinary` (`parent_id`), "
            + "INDEX `ix_other` (`other_id`)) ENGINE=InnoDB;");

        await ExecuteSqlAsync(connectionString, Fixture.IsMariaDb
            ? "ALTER TABLE `live_child` ADD `derived_id` int GENERATED ALWAYS AS (ABS(`id`)) STORED; "
                + "CREATE INDEX `ix_multi` ON `live_child` (`parent_id`, `derived_id`);"
            : "CREATE INDEX `ix_multi` ON `live_child` (`parent_id`, "
                + "(CAST(`values_json` AS UNSIGNED ARRAY)));");

        await ExecuteSqlAsync(connectionString,
            "ALTER TABLE `live_child` ADD CONSTRAINT `fk_native` FOREIGN KEY (`parent_id`) "
            + "REFERENCES `live_parent` (`id`)" + (cascade ? " ON DELETE CASCADE" : string.Empty)
            + "; INSERT INTO `live_parent` VALUES (1); INSERT INTO `live_child` (`id`,`parent_id`,`values_json`) "
            + "VALUES (1,1,'[]');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists(targetsFkPrefix ? "ix_ordinary" : "ix_other", "live_child");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-ordinary-support-drop-integrity"), CancellationToken.None);

        var dropFailure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            builder.Operations, CancellationToken.None));

        var deleteFailure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString,
            "DELETE FROM `live_parent` WHERE `id`=1;"));

        var remainingParents = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `live_parent`;");
        // WHY: A lookup on the broken prefix could itself omit an empty-array
        // row. The primary-key scan independently counts the physical fixture.
        var remainingChildren = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `live_child` FORCE INDEX (`PRIMARY`);");

        // Assert
        if (!Fixture.IsMariaDb && targetsFkPrefix)
        {
            Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
            Assert.Equal(SafeMigrationAction.RejectUnsupported, preflight.Assessments[0].Action);
            Assert.Contains("doka_sm_unsupported", AssertOwnedGuardFailure(dropFailure).Message,
                StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
            Assert.Null(dropFailure);
        }

        if (cascade)
        {
            Assert.Null(deleteFailure);
        }
        else
        {
            Assert.IsType<MySqlException>(deleteFailure);
        }

        Assert.Equal(cascade ? 0 : 1, remainingChildren);
        Assert.Equal(cascade ? 0 : 1, remainingParents);
    }

    /// <summary>A metadata-only dependency-first stream needs no subsequent ADD or FK to retire the index.</summary>
    /// <param name="ordinarySupport">Whether the retired target is an ordinary prefix beside a native MVI.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultiValuedIndexDependencyFirstRemovalAppliesAndReplaysWithoutNewColumns(bool ordinarySupport)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `parent_id` int NULL, "
            + "`values_json` JSON NOT NULL"
            + (ordinarySupport ? ", INDEX `ix_ordinary` (`parent_id`)" : string.Empty) + ") ENGINE=InnoDB;");

        await ExecuteSqlAsync(connectionString, Fixture.IsMariaDb
            ? "ALTER TABLE `live_child` ADD `derived_id` int GENERATED ALWAYS AS (ABS(`id`)) STORED; "
                + "CREATE INDEX `ix_multi` ON `live_child` (`parent_id`, `derived_id`);"
            : "CREATE INDEX `ix_multi` ON `live_child` (`parent_id`, "
                + "(CAST(`values_json` AS UNSIGNED ARRAY)));");

        await ExecuteSqlAsync(connectionString,
            "ALTER TABLE `live_child` ADD CONSTRAINT `fk_native` FOREIGN KEY (`parent_id`) "
            + "REFERENCES `live_parent` (`id`);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_native", "live_child");
        builder.DropIndexIfExists(ordinarySupport ? "ix_ordinary" : "ix_multi", "live_child");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-dependency-first-drop"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var replay = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-dependency-first-replay"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments, static item => Assert.Equal(SafeMigrationAction.Apply, item.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, static item => Assert.Equal(SafeMigrationAction.NoOp, item.Action));
    }

    /// <summary>A wrong or absent authored FK removal cannot retire another actual physical dependency.</summary>
    /// <param name="wrongName">Whether the attempted removal uses another plausible semantic alias.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultiValuedIndexDependencyRemovalCannotBorrowNoOpOrWrongPhysicalAlias(bool wrongName)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `parent_id` int NULL, "
            + "`values_json` JSON NOT NULL) ENGINE=InnoDB;");

        await ExecuteSqlAsync(connectionString, Fixture.IsMariaDb
            ? "ALTER TABLE `live_child` ADD `derived_id` int GENERATED ALWAYS AS (ABS(`id`)) STORED; "
                + "CREATE INDEX `ix_multi` ON `live_child` (`parent_id`, `derived_id`);"
            : "CREATE INDEX `ix_multi` ON `live_child` (`parent_id`, "
                + "(CAST(`values_json` AS UNSIGNED ARRAY)));");

        await ExecuteSqlAsync(connectionString,
            "ALTER TABLE `live_child` ADD CONSTRAINT `fk_native` FOREIGN KEY (`parent_id`) "
            + "REFERENCES `live_parent` (`id`); CREATE INDEX `ix_ordinary` ON `live_child` (`parent_id`);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists(wrongName ? "fk_semantic_alias" : "fk_absent", "live_child");
        builder.DropIndexIfExists("ix_multi", "live_child");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-unowned-dependency-drop"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            builder.Operations, CancellationToken.None));

        // Assert
        Assert.Equal(SafeMigrationAction.NoOp, preflight.Assessments[0].Action);
        Assert.Equal(Fixture.IsMariaDb ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            preflight.Status);
        if (Fixture.IsMariaDb)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal(SafeMigrationAction.RejectUnsupported, preflight.Assessments[1].Action);
            Assert.Contains("doka_sm_unsupported", AssertOwnedGuardFailure(failure).Message,
                StringComparison.Ordinal);
        }

        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA=DATABASE() AND TABLE_NAME='live_child' AND CONSTRAINT_NAME='fk_native';"));
    }

    /// <summary>
    /// An existing alternate endpoint still needs a native-feasible child index after exact FK removal.
    /// </summary>
    /// <param name="createSupportingIndex">Whether the accepted stream supplies an explicit new child prefix.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeyOwnedExistingEndpointReplacementCannotBypassRetainedIndexCollision(
        bool createSupportingIndex
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `old_id` int NULL, "
            + "`parent_id` int NULL, CONSTRAINT `fk_native` FOREIGN KEY (`old_id`) "
            + "REFERENCES `live_parent` (`id`)) ENGINE=InnoDB; INSERT INTO `live_child` VALUES (1, NULL, NULL);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_native", "live_child");
        if (createSupportingIndex)
        {
            builder.CreateIndexIfNotExistsFromModel("ix_new", "live_child", "parent_id");
        }

        builder.AddForeignKeyIfNotExists("fk_native", "live_child", ["parent_id"], "live_parent", ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-existing-endpoint-index-collision"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            builder.Operations, CancellationToken.None));

        // Assert
        Assert.Equal(createSupportingIndex ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            preflight.Status);
        Assert.Equal(createSupportingIndex ? SafeMigrationAction.Apply : SafeMigrationAction.RejectDifferent,
            preflight.Assessments[^1].Action);
        if (createSupportingIndex)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Contains("doka_sm_different", AssertOwnedGuardFailure(failure).Message,
                StringComparison.Ordinal);
        }
    }

    /// <summary>Only a surviving accepted covering index can discharge a retained-symbol collision.</summary>
    /// <param name="shape">The future, rejected, existence-only or removed supporting-index lifetime.</param>
    [Theory]
    [InlineData("future")]
    [InlineData("rejected")]
    [InlineData("existence_only")]
    [InlineData("dropped")]
    public async Task ForeignKeyOwnedEndpointReplacementCannotBorrowUnacceptedSupportingIndex(string shape)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `old_id` int NULL, "
            + "`parent_id` int NULL, CONSTRAINT `fk_native` FOREIGN KEY (`old_id`) "
            + "REFERENCES `live_parent` (`id`)) ENGINE=InnoDB; INSERT INTO `live_child` VALUES (1, NULL, NULL);"
            + (shape is "rejected" or "existence_only"
                ? "CREATE INDEX `ix_new` ON `live_child` (`id`);"
                : string.Empty));

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_native", "live_child");
        if (shape != "future")
        {
            builder.CreateIndexIfNotExists("ix_new", "live_child", ["parent_id"],
                policy: shape == "existence_only"
                    ? SafeMigrationPolicy.ExistenceOnly
                    : SafeMigrationPolicy.ThrowIfDifferent);

            if (shape == "dropped")
            {
                builder.DropIndexIfExists("ix_new", "live_child");
            }

        }

        var foreignKeyOrdinal = builder.Operations.Count;
        builder.AddForeignKeyIfNotExists("fk_native", "live_child", ["parent_id"], "live_parent", ["id"]);
        if (shape == "future")
        {
            builder.CreateIndexIfNotExistsFromModel("ix_new", "live_child", "parent_id");
        }

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-unaccepted-supporting-lifetime"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, preflight.Assessments[foreignKeyOrdinal].Action);
        Assert.Equal("projected_foreign_key_supporting_index_name_collision",
            preflight.Assessments[foreignKeyOrdinal].AnalysisCode);
    }

    /// <summary>
    /// Dropping a column remains a complete-table structure boundary, not a new implicit recovery contract.
    /// </summary>
    [Fact]
    public async Task SparseForeignKeyRetirementDoesNotBypassDroppedColumnStructureBoundary()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `parent_id` int NULL, "
            + "INDEX `ix_parent` (`parent_id`), CONSTRAINT `fk_native` FOREIGN KEY (`parent_id`) "
            + "REFERENCES `live_parent` (`id`)) ENGINE=InnoDB;");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_native", "live_child");
        builder.DropColumnIfExists("parent_id", "live_child");
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
            "parent_id", "live_child", type: "int", nullable: true));

        builder.CreateIndexIfNotExistsFromModel("ix_parent", "live_child", "parent_id");
        builder.AddForeignKeyIfNotExists("fk_native", "live_child", ["parent_id"], "live_parent", ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-dropped-column-boundary"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, preflight.Assessments[3].Action);
        Assert.Equal("projected_structure_state_unknown", preflight.Assessments[3].AnalysisCode);
    }

    /// <summary>Composite inventory requires every ordered FK part, not a coincidental first-column prefix.</summary>
    /// <param name="shape">The exact, reversed or partially different native supporting-index shape.</param>
    /// <param name="multipleUnsafeAliases">
    /// Whether an ordinary target has two independently matching MVI siblings.
    /// </param>
    [Theory]
    [InlineData("exact", false)]
    [InlineData("exact", true)]
    [InlineData("reversed", false)]
    [InlineData("partial", false)]
    public async Task MultiValuedIndexBoundForeignKeyInventoryRequiresCompleteOrderedPrefix(
        string shape,
        bool multipleUnsafeAliases
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`first_id` int NOT NULL, `second_id` int NOT NULL, "
            + "PRIMARY KEY (`first_id`, `second_id`)) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `first_id` int NULL, "
            + "`second_id` int NULL, `other_id` int NULL, `values_json` JSON NOT NULL, "
            + "INDEX `ix_ordinary` (`first_id`, `second_id`)) ENGINE=InnoDB; "
            + "ALTER TABLE `live_child` ADD CONSTRAINT `fk_native` FOREIGN KEY (`first_id`, `second_id`) "
            + "REFERENCES `live_parent` (`first_id`, `second_id`);");

        var leading = shape switch
        {
            "exact" => "`first_id`, `second_id`",
            "reversed" => "`second_id`, `first_id`",
            "partial" => "`first_id`, `other_id`",
            _ => throw new UnreachableException(),
        };

        await ExecuteSqlAsync(connectionString, Fixture.IsMariaDb
            ? "ALTER TABLE `live_child` ADD `derived_id` int GENERATED ALWAYS AS (ABS(`id`)) STORED; "
                + "CREATE INDEX `ix_multi` ON `live_child` (" + leading + ", `derived_id`);"
            : "CREATE INDEX `ix_multi` ON `live_child` (" + leading
                + ", (CAST(`values_json` AS UNSIGNED ARRAY)));");

        if (multipleUnsafeAliases && !Fixture.IsMariaDb)
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE INDEX `ix_multi_second` ON `live_child` (`first_id`,`second_id`, "
                + "(CAST(`values_json` AS SIGNED ARRAY)));");
        }

        var query = MySqlSafeMigrationCatalogSqlBuilder.BuildMultiValuedIndexBoundForeignKeysQuery(
            "'live_child'", multipleUnsafeAliases ? "'ix_ordinary'" : "'ix_multi'", Fixture.IsMariaDb);

        // Act
        var count = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM (" + query + ") actual;");

        // Assert
        Assert.Equal(!Fixture.IsMariaDb && shape == "exact" ? 1 : 0, count);
    }

    /// <summary>
    /// Native column and index identifiers distinguish accents even though their catalog collation may not.
    /// </summary>
    [Fact]
    public async Task ForeignKeySupportingIndexNativeIdentifiersDoNotBorrowAccentedColumnsOrAliases()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        const string accented = "\u00e9";
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `e` int NULL, `" + accented
            + "` int NULL, INDEX `fk_native` (`id`), INDEX `ix_e` (`e`), INDEX `ix_" + accented
            + "` (`" + accented + "`)) ENGINE=InnoDB;");

        var query = MySqlSafeMigrationCatalogSqlBuilder.BuildForeignKeyChildCoveringIndexQuery(
            "'live_child'", ["'e'"], Fixture.IsMariaDb);

        var wrongColumnQuery = MySqlSafeMigrationCatalogSqlBuilder.BuildForeignKeyChildCoveringIndexQuery(
            "'live_child'", ["'" + accented + "'"], Fixture.IsMariaDb);

        // Act
        var ordinaryCount = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM (" + query + ") actual;");
        var accentedCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM (" + wrongColumnQuery + ") actual;");

        // Assert
        Assert.Equal(1, ordinaryCount);
        Assert.Equal(1, accentedCount);
    }

    /// <summary>A case-distinct foreign database cannot supply the absent principal of a mode-zero owner.</summary>
    [Fact]
    public async Task SparseForeignKeyLiveMetadataCannotBorrowCaseDistinctDatabase()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var localDatabase = new MySqlConnectionStringBuilder(connectionString).Database;
        var foreignDatabase = localDatabase.ToUpperInvariant();
        await ExecuteSqlAsync(connectionString, "CREATE DATABASE `" + foreignDatabase + "`;");
        try
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
                + "CREATE TABLE `" + foreignDatabase + "`.`live_parent` "
                + "(`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; INSERT INTO `live_child` VALUES (1);");

            await using var context = CreateContext(connectionString);
            var builder = new MigrationBuilder(context.Database.ProviderName!);
            builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
                "parent_id", "live_child", type: "int", nullable: true));

            builder.CreateIndexIfNotExistsFromModel("ix_parent", "live_child", "parent_id");
            builder.AddForeignKeyIfNotExists("fk_native", "live_child", ["parent_id"], "live_parent", ["id"]);
            var runner = context.GetService<ISafeMigrationRunner>();

            // Act
            var preflight = await runner.AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("native-case-distinct-database"), CancellationToken.None);

            // Assert
            Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
            Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, preflight.Assessments[2].Action);
        }
        finally
        {
            // This exact generated database is owned solely by this fixture;
            // the primary fixture separately cleans the local database.
            await ExecuteSqlAsync(connectionString, "DROP DATABASE `" + foreignDatabase + "`;");
        }
    }

    /// <summary>
    /// Accepted dependency removals retain one native owner across case-insensitive server spellings.
    /// </summary>
    [Fact]
    public async Task ForeignKeySparseLiveLifetimeUsesVerifiedCaseInsensitiveOwnerIdentity()
    {
        // Arrange
        using var dedicated = new MySqlEngineContainerFixture(lowerCaseTableNames: 1);
        await dedicated.InitializeAsync();
        try
        {
            var connectionString = await dedicated.CreateDatabaseAsync(CancellationToken.None);
            await ExecuteSqlAsync(connectionString,
                "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
                + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `old_id` int NULL, "
                + "`values_json` JSON NOT NULL) ENGINE=InnoDB;");

            await ExecuteSqlAsync(connectionString, dedicated.IsMariaDb
                ? "ALTER TABLE `live_child` ADD `derived_id` int GENERATED ALWAYS AS (ABS(`id`)) STORED; "
                    + "CREATE INDEX `ix_multi` ON `live_child` (`old_id`, `derived_id`);"
                : "CREATE INDEX `ix_multi` ON `live_child` (`old_id`, "
                    + "(CAST(`values_json` AS UNSIGNED ARRAY)));");

            await ExecuteSqlAsync(connectionString,
                "ALTER TABLE `live_child` ADD CONSTRAINT `fk_native` FOREIGN KEY (`old_id`) "
                + "REFERENCES `live_parent` (`id`); INSERT INTO `live_child` VALUES "
                + (dedicated.IsMariaDb ? "(1, NULL, '[]', DEFAULT);" : "(1, NULL, '[]');"));

            await using var context = new SafeMigrationDbContext(connectionString, dedicated.ServerVersion);
            var builder = new MigrationBuilder(context.Database.ProviderName!);
            builder.DropForeignKeyIfExists("fk_native", "Live_Child");
            builder.DropIndexIfExists("ix_multi", "live_CHILD");
            builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
                "parent_id", "LIVE_CHILD", type: "int", nullable: true));

            builder.CreateIndexIfNotExistsFromModel("ix_new", "Live_Child", "parent_id");
            builder.AddForeignKeyIfNotExists("fk_native", "live_CHILD", ["parent_id"], "LIVE_PARENT", ["id"]);
            var runner = context.GetService<ISafeMigrationRunner>();

            // Act
            var preflight = await runner.AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("native-case-insensitive-lifetime"), CancellationToken.None);

            preflight.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
            var final = await runner.VerifyAsync(context, builder.Operations,
                new SafeMigrationRunOptions("native-case-insensitive-lifetime-final"), CancellationToken.None);

            // Assert
            Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
            Assert.Equal(SafeMigrationReportStatus.Ready, final.Status);
            Assert.Equal(1, await ScalarIntAsync(connectionString,
                "SELECT COUNT(*) FROM `live_child` WHERE `parent_id` IS NULL;"));
        }
        finally
        {
            await dedicated.DisposeAsync();
        }
    }
}
