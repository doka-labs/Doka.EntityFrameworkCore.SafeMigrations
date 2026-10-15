namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Qualifies native UUID contracts against an isolated database on the shared MariaDB engine.</summary>
public sealed class MySqlNativeUuidIntegrationTests : MySqlIntegrationTestBase
{
    private static readonly Guid s_identifier = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid s_replacement = Guid.Parse("ffeeddcc-bbaa-4988-8766-554433221100");

    /// <summary>Retains per-test database ownership while sharing only the engine lifecycle.</summary>
    /// <param name="fixture">The lazily activated assembly-owned engine.</param>
    public MySqlNativeUuidIntegrationTests(MySqlEngineContainerFixture fixture) : base(fixture) { }

    /// <summary>Live and projected UUID columns support complete, non-prefixed physical keys.</summary>
    /// <param name="kind">The independently authored primary, unique or ordinary index contract.</param>
    /// <param name="projected">Whether an earlier operation creates the UUID column.</param>
    [NativeUuidTheory]
    [InlineData("primary", false)]
    [InlineData("primary", true)]
    [InlineData("unique", false)]
    [InlineData("unique", true)]
    [InlineData("index", false)]
    [InlineData("index", true)]
    public async Task NativeUuidPhysicalKeysApplyAndReplay(string kind, bool projected)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, projected
            ? "CREATE TABLE `uuid_keys` (`sentinel` int NOT NULL);"
            : "CREATE TABLE `uuid_keys` (`sentinel` int NOT NULL, `id` uuid NOT NULL);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (projected)
        {
            AddNativeUuid(builder, "uuid_keys", "id", nullable: false);
        }

        switch (kind)
        {
            case "primary":
                builder.AddPrimaryKeyIfNotExists("pk_uuid_keys", "uuid_keys", ["id"]);
                break;
            case "unique":
                builder.AddUniqueConstraintIfNotExists("uq_uuid_keys_id", "uuid_keys", ["id"]);
                break;
            case "index":
                builder.CreateIndexIfNotExists("ix_uuid_keys_id", "uuid_keys", ["id"]);
                break;
            default:
                throw new UnreachableException();
        }

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-physical-key"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-physical-key-postflight"), CancellationToken.None);

        var replay = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-physical-key-replay"), CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations);
        var keys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'uuid_keys' AND COLUMN_NAME = 'id' AND SUB_PART IS NULL;");

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[^1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static item => Assert.True(item.PostconditionSatisfied));
        Assert.All(replay.Assessments, static item => Assert.Equal(SafeMigrationAction.NoOp, item.Action));
        Assert.Equal(1, keys);
    }

    /// <summary>Sixteen-byte scalar storage must never authorize a textual or binary prefix.</summary>
    /// <param name="projected">Whether the UUID column is introduced in the same ordered stream.</param>
    [NativeUuidTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeUuidPrefixesAreRejectedBeforeIndexMutation(bool projected)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, projected
            ? "CREATE TABLE `uuid_prefix` (`sentinel` int NOT NULL);"
            : "CREATE TABLE `uuid_prefix` (`sentinel` int NOT NULL, `id` uuid NULL);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (projected)
        {
            AddNativeUuid(builder, "uuid_prefix", "id", nullable: true);
        }

        builder.EnsureIndex(new ExpectedIndexDefinition("ix_uuid_prefix_id", "uuid_prefix",
            [new ExpectedIndexKeyDefinition(column: "id", prefixLength: 8)]),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-prefix"), CancellationToken.None);

        // WHY: Runtime protection must reject the index even without a caller
        // running preflight; an earlier independently safe ADD may still apply.
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var indexes = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'uuid_prefix';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[^1].Action);
        AssertGuard(failure, "doka_sm_unsupported");
        Assert.Equal(0, indexes);
    }

    /// <summary>Composite keys account for exactly sixteen UUID bytes at the native 3,072-byte boundary.</summary>
    /// <param name="length">The target utf8mb4 character capacity adjacent to the UUID key.</param>
    /// <param name="accepted">Whether the complete ordered key remains representable.</param>
    [NativeUuidTheory]
    [InlineData(764, true)]
    [InlineData(765, false)]
    public async Task NativeUuidCompositeKeyBudgetRetainsExactBoundary(int length, bool accepted)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `uuid_key_budget` (`id` uuid NOT NULL, `value` varchar(700) NOT NULL) "
            + "ENGINE=InnoDB ROW_FORMAT=DYNAMIC DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("uuid_key_budget",
            new ExpectedColumnDefinition("value", typeof(string), false,
                "varchar(" + length.ToString(CultureInfo.InvariantCulture) + ")", maxLength: length),
            SafeMigrationPolicy.RepairIfSafe);

        builder.CreateIndexIfNotExists("ix_uuid_key_budget", "uuid_key_budget", ["id", "value"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-key-budget"), CancellationToken.None);

        // WHY: Bypassing preflight may apply the independently safe widening,
        // but an oversized composite key must still fail before index DDL.
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var indexes = await ScalarIntAsync(connectionString,
            "SELECT COUNT(DISTINCT INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'uuid_key_budget';");
        var actualLength = await ScalarIntAsync(connectionString,
            "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'uuid_key_budget' AND COLUMN_NAME = 'value';");

        // Assert
        Assert.Equal(accepted ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Repair, report.Assessments[0].Action);
        Assert.Equal(accepted ? SafeMigrationAction.Apply : SafeMigrationAction.RejectUnsupported,
            report.Assessments[1].Action);
        if (accepted)
        {
            Assert.Null(failure);
        }
        else
        {
            AssertGuard(failure, "doka_sm_unsupported");
        }

        Assert.Equal(accepted ? 1 : 0, indexes);
        Assert.Equal(length, actualLength);
    }

    /// <summary>Projected and live UUID endpoints retain ordinary FK ownership, data and replay guarantees.</summary>
    /// <param name="projected">Whether table creation precedes the key and FK declarations.</param>
    [NativeUuidTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeUuidForeignKeyAppliesToMatchingStorageAndReplays(bool projected)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (projected)
        {
            builder.CreateTableIfNotExists("uuid_parents", static table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false)
                        .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Uuid),
                },
                constraints: static table => table.PrimaryKey("pk_uuid_parents", row => row.id));

            builder.CreateTableIfNotExists("uuid_children", static table => new
            {
                row = table.Column<int>(type: "int", nullable: false),
                owner_id = table.Column<Guid>(type: "uuid", nullable: true)
                    .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Uuid),
            });
        }
        else
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE TABLE `uuid_parents` (`id` uuid NOT NULL, PRIMARY KEY (`id`)); "
                + "CREATE TABLE `uuid_children` (`row` int NOT NULL, `owner_id` uuid NULL);");
        }

        builder.CreateIndexIfNotExists("ix_uuid_children_owner", "uuid_children", ["owner_id"]);
        builder.AddForeignKeyIfNotExists("fk_uuid_children_owner", "uuid_children", ["owner_id"],
            "uuid_parents", ["id"], onDelete: ReferentialAction.NoAction);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-foreign-key"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteSqlAsync(connectionString,
            "INSERT INTO `uuid_parents` VALUES ('00112233-4455-6677-8899-aabbccddeeff'); "
            + "INSERT INTO `uuid_children` VALUES (1, '00112233-4455-6677-8899-aabbccddeeff'), (2, NULL);");
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-foreign-key-postflight"), CancellationToken.None);

        var replay = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-foreign-key-replay"), CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations);
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `uuid_children`;");

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[^1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(replay.Assessments, static item => Assert.Equal(SafeMigrationAction.NoOp, item.Action));
        Assert.Equal(2, rows);
    }

    /// <summary>Native UUID endpoints cannot bind an incompatible textual principal collation.</summary>
    [NativeUuidFact]
    public async Task NativeUuidForeignKeyRejectsTextualPrincipalCollation()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `uuid_parents` (`id` char(36) NOT NULL, PRIMARY KEY (`id`)) "
            + "DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "CREATE TABLE `uuid_children` (`owner_id` uuid NULL, KEY `ix_uuid_owner` (`owner_id`));");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists("fk_uuid_owner", "uuid_children", ["owner_id"],
            "uuid_parents", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-incompatible-foreign-key"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var foreignKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'uuid_children';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, Assert.Single(report.Assessments).Action);
        AssertGuard(failure, "doka_sm_prerequisite_missing");
        Assert.Equal(0, foreignKeys);
    }

    /// <summary>Native UUID can reference an unchanged canonical Binary16 principal accepted by MariaDB.</summary>
    [NativeUuidFact]
    public async Task NativeUuidForeignKeyRetainsCanonicalBinaryPrincipalCompatibility()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        // WHY: This is endpoint compatibility, not a column-format conversion.
        // The canonical bytes match Doka's Binary16 contract; the FK leaves
        // both original physical storage representations unchanged.
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `uuid_binary_parents` (`id` binary(16) NOT NULL, PRIMARY KEY (`id`)); "
            + "CREATE TABLE `uuid_binary_children` (`owner_id` uuid NULL, KEY `ix_uuid_owner` (`owner_id`)); "
            + "INSERT INTO `uuid_binary_parents` VALUES (0x00112233445566778899AABBCCDDEEFF);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists("fk_uuid_binary_owner", "uuid_binary_children", ["owner_id"],
            "uuid_binary_parents", ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-binary-principal"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteSqlAsync(connectionString,
            "INSERT INTO `uuid_binary_children` VALUES ('00112233-4455-6677-8899-aabbccddeeff');");
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-binary-principal-postflight"), CancellationToken.None);

        var replay = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-binary-principal-replay"), CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations);
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `uuid_binary_parents` p JOIN `uuid_binary_children` c ON c.`owner_id` = p.`id` "
            + "WHERE p.`id` = 0x00112233445566778899AABBCCDDEEFF;");
        var binaryColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'uuid_binary_parents' AND COLUMN_TYPE = 'binary(16)';");
        var nativeColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'uuid_binary_children' AND COLUMN_TYPE = 'uuid';");

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(preflight.Assessments).Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal(1, preserved);
        Assert.Equal(1, binaryColumns);
        Assert.Equal(1, nativeColumns);
    }

    /// <summary>Native UUID does not invent existing row identities when a required column has no default.</summary>
    /// <param name="populated">Whether a row needs a caller-supplied identity.</param>
    /// <param name="hasDefault">Whether the migration explicitly owns a non-NULL Guid default.</param>
    [NativeUuidTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RequiredNativeUuidAddRetainsDataAndDefaultGates(bool populated, bool hasDefault)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `uuid_add` (`value` int NOT NULL); "
            + (populated ? "INSERT INTO `uuid_add` VALUES (7);" : string.Empty));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AddNativeUuid(builder, "uuid_add", "id", nullable: false, hasDefault ? s_identifier : null);
        var runner = context.GetService<ISafeMigrationRunner>();
        var accepted = !populated || hasDefault;

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-required-add"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var columnCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'uuid_add' AND COLUMN_NAME = 'id' AND DATA_TYPE = 'uuid' AND IS_NULLABLE = 'NO';");
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `uuid_add`;");
        var defaultRows = accepted && populated
            ? await ScalarIntAsync(connectionString,
                "SELECT COUNT(*) FROM `uuid_add` WHERE `value` = 7 "
                + "AND `id` = '00112233-4455-6677-8899-aabbccddeeff';")
            : 0;

        // Assert
        Assert.Equal(accepted ? SafeMigrationAction.Apply : SafeMigrationAction.RejectDataBlocked,
            Assert.Single(preflight.Assessments).Action);
        if (accepted)
        {
            Assert.Null(failure);
        }
        else
        {
            AssertGuard(failure, "doka_sm_data_blocked");
        }

        Assert.Equal(accepted ? 1 : 0, columnCount);
        Assert.Equal(populated ? 1 : 0, rows);
        Assert.Equal(populated && hasDefault ? 1 : 0, defaultRows);
    }

    /// <summary>The required-column guard must be fresh even when native UUID preflight saw an empty table.</summary>
    [NativeUuidFact]
    public async Task NativeUuidRequiredAddRejectsRowsInsertedAfterPreflight()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `uuid_add_race` (`value` int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AddNativeUuid(builder, "uuid_add_race", "id", nullable: false);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-required-add-race"), CancellationToken.None);

        await ExecuteSqlAsync(connectionString, "INSERT INTO `uuid_add_race` VALUES (7);");
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'uuid_add_race' AND COLUMN_NAME = 'id';");
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `uuid_add_race` WHERE `value` = 7;");

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(preflight.Assessments).Action);
        AssertGuard(failure, "doka_sm_data_blocked");
        Assert.Equal(0, columns);
        Assert.Equal(1, rows);
    }

    /// <summary>Same-format UUID tightening preserves existing identities and uses only declared backfills.</summary>
    /// <param name="includeNull">Whether the source contains a missing identity.</param>
    /// <param name="hasDefault">Whether the caller declares a replacement for NULL.</param>
    [NativeUuidTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NativeUuidNullabilityRepairRetainsCallerOwnedBackfills(bool includeNull, bool hasDefault)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `uuid_tighten` (`value` int NOT NULL, `owner_id` uuid NULL); "
            + "INSERT INTO `uuid_tighten` VALUES (1, '00112233-4455-6677-8899-aabbccddeeff'); "
            + (includeNull ? "INSERT INTO `uuid_tighten` VALUES (2, NULL);" : string.Empty));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferentFromModel(operations => operations.AlterColumn<Guid>(
                "owner_id", "uuid_tighten", type: "uuid", nullable: false,
                defaultValue: hasDefault ? s_replacement : null,
                oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true)
            .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Uuid)
            .Annotation("Doka:MySql:RequiresExplicitBackfill", true)
            .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None)
            .OldAnnotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Uuid)
            .OldAnnotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None),
            SafeMigrationPolicy.RepairIfSafe);
        var runner = context.GetService<ISafeMigrationRunner>();
        var accepted = !includeNull || hasDefault;

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-tightening"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `uuid_tighten` WHERE `value` = 1 "
            + "AND `owner_id` = '00112233-4455-6677-8899-aabbccddeeff';");
        var nulls = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `uuid_tighten` WHERE `owner_id` IS NULL;");
        var required = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'uuid_tighten' AND COLUMN_NAME = 'owner_id' AND IS_NULLABLE = 'NO';");
        var replaced = includeNull && hasDefault
            ? await ScalarIntAsync(connectionString,
                "SELECT COUNT(*) FROM `uuid_tighten` WHERE `value` = 2 "
                + "AND `owner_id` = 'ffeeddcc-bbaa-4988-8766-554433221100';")
            : 0;

        // Assert
        Assert.Equal(accepted ? SafeMigrationAction.Repair : SafeMigrationAction.RejectDataBlocked,
            Assert.Single(preflight.Assessments).Action);
        if (accepted)
        {
            Assert.Null(failure);
        }
        else
        {
            AssertGuard(failure, "doka_sm_data_blocked");
        }

        Assert.Equal(1, preserved);
        Assert.Equal(includeNull && !hasDefault ? 1 : 0, nulls);
        Assert.Equal(accepted ? 1 : 0, required);
        Assert.Equal(includeNull && hasDefault ? 1 : 0, replaced);
    }

    /// <summary>Native UUID neighbors and keys must not block unrelated proven lossless column repairs.</summary>
    /// <param name="kind">The Ensure or explicit-alter VARCHAR, TEXT or Boolean repair family.</param>
    [NativeUuidTheory]
    [InlineData("varchar")]
    [InlineData("alter_varchar")]
    [InlineData("text")]
    [InlineData("boolean")]
    public async Task NativeUuidSiblingsPermitUnrelatedSafeRepairs(string kind)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `uuid_siblings` (`id` uuid NOT NULL, `value` varchar(10) NULL, "
            + "`flag` tinyint(1) NULL, PRIMARY KEY (`id`)"
            + (kind is "varchar" or "alter_varchar" ? ", INDEX `ix_uuid_siblings_value` (`value`, `id`)" : string.Empty)
            + ") ENGINE=InnoDB ROW_FORMAT=DYNAMIC DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `uuid_siblings` VALUES ('00112233-4455-6677-8899-aabbccddeeff', 'preserved', 1);");
        await using var context = CreateContext(connectionString);
        var target = kind switch
        {
            "varchar" or "alter_varchar" => new ExpectedColumnDefinition("value", typeof(string), true,
                "varchar(200)", maxLength: 200),
            "text" => new ExpectedColumnDefinition("value", typeof(string), true, "text"),
            "boolean" => new ExpectedColumnDefinition("flag", typeof(bool), false, "tinyint(1)"),
            _ => throw new UnreachableException(),
        };

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (kind == "alter_varchar")
        {
            builder.AlterColumnIfDifferent("uuid_siblings", target,
                new ExpectedColumnDefinition("value", typeof(string), true, "varchar(10)", maxLength: 10),
                SafeMigrationPolicy.RepairIfSafe);
        }
        else
        {
            builder.EnsureColumn("uuid_siblings", target, SafeMigrationPolicy.RepairIfSafe);
        }

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-unrelated-repair"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-unrelated-repair-postflight"), CancellationToken.None);

        var replay = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-unrelated-repair-replay"), CancellationToken.None);

        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `uuid_siblings` WHERE `id` = '00112233-4455-6677-8899-aabbccddeeff' "
            + "AND `value` = 'preserved' AND `flag` = 1;");

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, Assert.Single(preflight.Assessments).Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal(1, preserved);
    }

    /// <summary>RepairIfSafe never reinterprets existing GUID bytes or text as a different representation.</summary>
    /// <param name="source">The live native, text or binary GUID format.</param>
    /// <param name="target">The incompatible requested GUID format.</param>
    [NativeUuidTheory]
    [InlineData(DokaMySqlGuidFormat.Char36, DokaMySqlGuidFormat.Uuid)]
    [InlineData(DokaMySqlGuidFormat.Binary16, DokaMySqlGuidFormat.Uuid)]
    [InlineData(DokaMySqlGuidFormat.Uuid, DokaMySqlGuidFormat.Char36)]
    [InlineData(DokaMySqlGuidFormat.Uuid, DokaMySqlGuidFormat.Binary16)]
    public async Task NativeUuidRepresentationChangesRemainRejected(DokaMySqlGuidFormat source,
        DokaMySqlGuidFormat target)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var sourceType = GuidStoreType(source);
        var literal = source == DokaMySqlGuidFormat.Binary16
            ? "0x00112233445566778899AABBCCDDEEFF"
            : "'00112233-4455-6677-8899-aabbccddeeff'";

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `uuid_format` (`id` " + sourceType + " NOT NULL); "
            + "INSERT INTO `uuid_format` VALUES (" + literal + ");");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var targetColumn = new AddColumnOperation
        {
            Table = "uuid_format",
            Name = "id",
            ClrType = typeof(Guid),
            ColumnType = GuidStoreType(target),
            MaxLength = target == DokaMySqlGuidFormat.Uuid ? null : target == DokaMySqlGuidFormat.Char36 ? 36 : 16,
            IsFixedLength = target == DokaMySqlGuidFormat.Uuid ? null : true,
            IsNullable = false,
        };

        targetColumn["Doka:MySql:GuidFormat"] = target;
        var definition = SafeMigrationExpectedDefinitionFactory.From(targetColumn);
        builder.EnsureColumn("uuid_format", definition, SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-format-change"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var actualType = await ScalarStringAsync(connectionString,
            "SELECT COLUMN_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'uuid_format' AND COLUMN_NAME = 'id';");
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `uuid_format` WHERE `id` = " + literal + ";");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, Assert.Single(report.Assessments).Action);
        AssertGuard(failure, "doka_sm_different");
        Assert.Equal(sourceType, actualType);
        Assert.Equal(1, rows);
    }

    /// <summary>Native UUID model-managed keys work through parameterized probes, inline DML and typed reads.</summary>
    /// <param name="kind">The independently exercised model-managed lifecycle operation.</param>
    [NativeUuidTheory]
    [InlineData("ensure")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task NativeUuidModelManagedValuesApplyAndReplay(string kind)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `uuid_managed` (`id` uuid NOT NULL, `value` int NOT NULL, PRIMARY KEY (`id`)); "
            + (kind == "ensure" ? string.Empty
                : "INSERT INTO `uuid_managed` VALUES ('00112233-4455-6677-8899-aabbccddeeff', 7);"));

        await using var context = CreateNativeContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        switch (kind)
        {
            case "ensure":
                builder.EnsureModelManagedDataFromModel("uuid_managed", ["id"], ["uuid"],
                    ["id", "value"], ["uuid", "int"], new object?[,] { { s_identifier, 7 } });
                break;
            case "update":
                builder.UpdateModelManagedDataFromModel("uuid_managed", ["id"], ["uuid"],
                    new object?[,] { { s_identifier } }, ["value"], ["int"],
                    new object?[,] { { 7 } }, new object?[,] { { 11 } });
                break;
            case "delete":
                builder.DeleteModelManagedDataFromModel("uuid_managed", ["id"], ["uuid"],
                    new object?[,] { { s_identifier } }, ["id", "value"], ["uuid", "int"],
                    new object?[,] { { s_identifier, 7 } });
                break;
            default:
                throw new UnreachableException();
        }

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-model-managed"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-model-managed-postflight"), CancellationToken.None);

        var replay = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-model-managed-replay"), CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations);
        var stored = await context.Set<NativeUuidRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == s_identifier);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.True(Assert.Single(postflight.Assessments).PostconditionSatisfied);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        if (kind == "delete")
        {
            Assert.Null(stored);
        }
        else
        {
            Assert.NotNull(stored);
            Assert.Equal(s_identifier, stored.Id);
            Assert.Equal(kind == "update" ? 11 : 7, stored.Value);
        }

    }

    /// <summary>A matching UUID key cannot hide changed source-controlled values during compare-and-swap.</summary>
    [NativeUuidFact]
    public async Task NativeUuidModelManagedDriftRejectsWithoutOverwritingValues()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `uuid_managed` (`id` uuid NOT NULL, `value` int NOT NULL, PRIMARY KEY (`id`)); "
            + "INSERT INTO `uuid_managed` VALUES ('00112233-4455-6677-8899-aabbccddeeff', 9);");
        await using var context = CreateNativeContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.UpdateModelManagedDataFromModel("uuid_managed", ["id"], ["uuid"],
            new object?[,] { { s_identifier } }, ["value"], ["int"],
            new object?[,] { { 7 } }, new object?[,] { { 11 } });

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-uuid-model-managed-drift"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var stored = await context.Set<NativeUuidRow>().AsNoTracking().SingleAsync();

        // Assert
        Assert.Equal(SafeMigrationAction.RejectDifferent, Assert.Single(report.Assessments).Action);
        AssertGuard(failure, "doka_sm_different");
        Assert.Equal(s_identifier, stored.Id);
        Assert.Equal(9, stored.Value);
    }

    /// <summary>Builds a property-opted-in UUID model without changing the provider-wide Binary16 default.</summary>
    /// <param name="connectionString">The current test's isolated database.</param>
    /// <returns>A context using real native UUID value conversion and SafeMigrations services.</returns>
    private NativeUuidContext CreateNativeContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<NativeUuidContext>()
            .UseMySql(connectionString, Fixture.ServerVersion)
            .UseMySqlSafeMigrations<NativeUuidContext>()
            .Options;

        return new NativeUuidContext(options);
    }

    /// <summary>Maps the three explicitly supported GUID storage identities without equating their bytes.</summary>
    /// <param name="format">The provider-owned representation.</param>
    /// <returns>The exact physical store type.</returns>
    private static string GuidStoreType(DokaMySqlGuidFormat format) => format switch
    {
        DokaMySqlGuidFormat.Binary16 => "binary(16)",
        DokaMySqlGuidFormat.Char36 => "char(36)",
        DokaMySqlGuidFormat.Uuid => "uuid",
        _ => throw new UnreachableException(),
    };

    /// <summary>The native key and data payload used by the real EF materialization path.</summary>
    private sealed class NativeUuidRow
    {
        /// <summary>Gets or sets the Guid retained by the model while its store type is native UUID.</summary>
        public Guid Id { get; set; }

        /// <summary>Gets or sets the source-controlled scalar used to detect compare-and-swap drift.</summary>
        public int Value { get; set; }
    }

    /// <summary>Owns the opt-in model used for typed UUID parameters, reads and literal generation.</summary>
    private sealed class NativeUuidContext(DbContextOptions<NativeUuidContext> options) : DbContext(options)
    {
        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var entity = modelBuilder.Entity<NativeUuidRow>();
            entity.ToTable("uuid_managed");
            entity.HasKey(static row => row.Id);
            entity.Property(static row => row.Id).HasColumnName("id")
                .HasMySqlGuidFormat(DokaMySqlGuidFormat.Uuid).ValueGeneratedNever();
            entity.Property(static row => row.Value).HasColumnName("value");
        }
    }

    /// <summary>Captures native UUID metadata without copying textual length or fixed-length facets.</summary>
    /// <param name="builder">The ordered immutable operation stream.</param>
    /// <param name="table">The owned target table.</param>
    /// <param name="column">The target UUID column.</param>
    /// <param name="nullable">Whether old rows may retain NULL.</param>
    /// <param name="defaultValue">An optional explicitly authored identity for existing rows.</param>
    private static void AddNativeUuid(MigrationBuilder builder, string table, string column, bool nullable,
        Guid? defaultValue = null)
        => builder.AddColumnIfNotExistsFromModel(operations => operations.AddColumn<Guid>(column, table,
                type: "uuid", nullable: nullable, defaultValue: defaultValue)
            .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Uuid)
            .Annotation("Doka:MySql:RequiresExplicitBackfill", true)
            .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None));

    /// <summary>Requires the owned safety diagnostic rather than treating any provider failure as rejection.</summary>
    /// <param name="failure">The captured generated-command failure.</param>
    /// <param name="code">The precise safety guard that must have caused the failure.</param>
    private static void AssertGuard(Exception? failure, string code)
    {
        var diagnostic = Assert.IsType<SafeMigrationExecutionException>(failure);
        var native = Assert.IsType<MySqlException>(diagnostic.InnerException);

        Assert.Equal(code, diagnostic.GuardCode);
        Assert.Equal(1062, native.Number);
        Assert.Equal("23000", native.SqlState);
        Assert.Contains(code, native.Message, StringComparison.Ordinal);
        Assert.NotNull(diagnostic.OperationOrdinal);
    }

    /// <summary>Skips unsupported engines honestly during discovery, before their shared fixture starts.</summary>
    private sealed class NativeUuidFactAttribute : FactAttribute
    {
        /// <summary>Uses the engine selector and MariaDB default shared with the container fixture.</summary>
        public NativeUuidFactAttribute() => Skip = NativeUuidSkipReason();
    }

    /// <summary>Skips native UUID theory rows where the configured engine has no native UUID type.</summary>
    private sealed class NativeUuidTheoryAttribute : TheoryAttribute
    {
        /// <summary>Uses the same discovery-time capability boundary as native UUID facts.</summary>
        public NativeUuidTheoryAttribute() => Skip = NativeUuidSkipReason();
    }

    /// <summary>
    /// Respects the explicit test engine instead of turning unsupported execution into a silent pass.
    /// </summary>
    /// <returns>A stable skip explanation on MySQL or old MariaDB; otherwise null.</returns>
    private static string? NativeUuidSkipReason()
    {
        var engine = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_MYSQL_ENGINE")?.Trim();
        if (!string.IsNullOrEmpty(engine) && !engine.Equals("mariadb", StringComparison.OrdinalIgnoreCase))
        {
            return "Native UUID columns require MariaDB 10.7 or later; MySQL has no native UUID type.";
        }

        var version = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_MYSQL_VERSION")?.Trim();
        if (Version.TryParse(version, out var configured) && configured < new Version(10, 7))
        {
            return "Native UUID columns require MariaDB 10.7 or later.";
        }

        return null;
    }
}
