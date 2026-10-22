namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Qualifies replacement keys without discarding independent surviving-column evidence.</summary>
public sealed class MySqlIndependentColumnDropIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Shares the engine while keeping each synthetic schema independently owned.</summary>
    /// <param name="fixture">The assembly-owned native engine.</param>
    public MySqlIndependentColumnDropIntegrationTests(MySqlEngineContainerFixture fixture) : base(fixture) { }

    /// <summary>The isolated schema permits supported replacement shapes under native guarded DDL.</summary>
    /// <param name="newIdentifier">Whether the replacement uses a newly populated native-expression member.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeIndependentDropControlSupportsReplacement(bool newIdentifier)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExistingKeyFixture("(1, 'a', 10), (2, 'b', 20)"));
        var replacement = newIdentifier
            ? $"ALTER TABLE `replacement_items` ADD COLUMN `id` {IdentifierStorage} "
                + $"NOT NULL DEFAULT ({IdentifierDefault}); "
                + "ALTER TABLE `replacement_items` ADD PRIMARY KEY (`id`);"
            : "ALTER TABLE `replacement_items` MODIFY COLUMN `tag_id` int NOT NULL; "
                + "ALTER TABLE `replacement_items` ADD PRIMARY KEY (`event_id`, `tag_id`);";

        // Act
        await ExecuteSqlAsync(connectionString,
            "ALTER TABLE `replacement_items` DROP PRIMARY KEY; "
            + "ALTER TABLE `replacement_items` DROP COLUMN `legacy_value`; " + replacement);

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `replacement_items`;");
        var primary = await ScalarStringAsync(connectionString,
            "SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION) "
            + "FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'replacement_items' AND CONSTRAINT_NAME = 'PRIMARY';");

        var identifierDefault = newIdentifier
            ? await ScalarStringAsync(connectionString,
                "SELECT COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
                + "AND TABLE_NAME = 'replacement_items' AND COLUMN_NAME = 'id';")
            : null;

        // Assert
        Assert.Equal(2, rows);
        Assert.Equal(newIdentifier ? "id" : "event_id,tag_id", primary);
        if (newIdentifier && !Fixture.IsMariaDb)
        {
            Assert.Equal(IdentifierDefault, identifierDefault);
        }
    }

    /// <summary>Separates native UUID-default replication refusal from SafeMigrations projection decisions.</summary>
    [Fact]
    public async Task NativeUuidDefaultAdditionRetainsEngineReplicationBoundary()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExistingKeyFixture("(1, 'a', 10), (2, 'b', 20)"));

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString,
            "ALTER TABLE `replacement_items` ADD COLUMN `id` char(36) NOT NULL DEFAULT (UUID());"));

        var identifierColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'replacement_items' AND COLUMN_NAME = 'id';");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `replacement_items`;");

        // Assert
        Assert.Equal(2, rows);
        Assert.Equal(Fixture.IsMariaDb ? 1 : 0, identifierColumns);
        if (Fixture.IsMariaDb)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal(1674, Assert.IsType<MySqlException>(failure).Number);
        }
    }

    /// <summary>An unrelated member removal does not invalidate a null-free replacement-key transition.</summary>
    /// <param name="script">Whether execution uses the emitted script instead of EF commands.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentDropPreservesExistingReplacementKeyProof(bool script)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExistingKeyFixture("(1, 'a', 10), (2, 'b', 20)"));
        await using var context = CreateContext(connectionString);
        var operations = ReplacementOperations(newIdentifier: false).Take(4).ToArray();
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("independent-drop-existing-key"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteIndependentDropAsync(context, connectionString, operations, script);
        var postflight = await runner.VerifyAsync(context, operations,
            new SafeMigrationRunOptions("independent-drop-existing-final"), CancellationToken.None);

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `replacement_items` WHERE (`event_id` = 1 AND `tag_id` = 10) "
            + "OR (`event_id` = 2 AND `tag_id` = 20);");

        var primary = await ScalarStringAsync(connectionString,
            "SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION) "
            + "FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'replacement_items' AND CONSTRAINT_NAME = 'PRIMARY';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Repair, preflight.Assessments[2].Action);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[3].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(2, rows);
        Assert.Equal("event_id,tag_id", primary);
    }

    /// <summary>
    /// Generated identifiers require truthful runtime uniqueness validation, not speculative refusal.
    /// </summary>
    /// <param name="script">Whether execution uses the actual generated migration script.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentDropAndExpressionDefaultAllowGuardedReplacementAndRerun(bool script)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExistingKeyFixture("(1, 'a', 10), (2, 'b', 10)"));
        await using var context = CreateContext(connectionString);
        var operations = ReplacementOperations(newIdentifier: true);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("independent-drop-expression-key"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteIndependentDropAsync(context, connectionString, operations, script);
        var postflight = await runner.VerifyAsync(context, operations,
            new SafeMigrationRunOptions("independent-drop-expression-final"), CancellationToken.None);

        var rerun = await runner.AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("independent-drop-expression-rerun"), CancellationToken.None);

        rerun.ThrowIfBlocked();
        await ExecuteIndependentDropAsync(context, connectionString, operations, script);
        var identifiers = await ScalarIntAsync(connectionString,
            "SELECT COUNT(DISTINCT `id`) FROM `replacement_items` WHERE `id` IS NOT NULL "
            + (Fixture.IsMariaDb ? "AND CHAR_LENGTH(`id`) = 36;" : "AND `id` = `event_id`;"));

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `replacement_items`;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, preflight.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[3].Action);
        Assert.Equal(2, preflight.Assessments[3].DeferredOrigin?.OperationOrdinal);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(2, identifiers);
        Assert.Equal(2, rows);
        Assert.DoesNotContain(rerun.Assessments,
            static assessment => assessment.Action is { } action && action.RejectsExecution());
    }

    /// <summary>Old composite uniqueness does not prove replacement uniqueness or NULL freedom.</summary>
    /// <param name="rows">Rows valid under the old key but invalid under the requested replacement.</param>
    [Theory]
    [InlineData("(1, 'a', 10), (1, 'b', 10)")]
    [InlineData("(1, 'a', NULL), (2, 'b', 20)")]
    public async Task InvalidReplacementRowsBlockBeforeDestructiveColumnDrop(string rows)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExistingKeyFixture(rows));
        await using var context = CreateContext(connectionString);
        var operations = ReplacementOperations(newIdentifier: false);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("independent-drop-invalid-rows"), CancellationToken.None);

        var legacy = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'replacement_items' AND COLUMN_NAME = 'legacy_value';");

        var retained = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `replacement_items`;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Contains(report.Assessments,
            static assessment => assessment.Action == SafeMigrationAction.RejectDataBlocked);
        Assert.Equal(1, legacy);
        Assert.Equal(2, retained);
    }

    /// <summary>An expression default does not certify duplicate IDs already stored in an existing member.</summary>
    [Fact]
    public async Task ExistingExpressionDefaultCannotHideDuplicateReplacementIdentifiers()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExistingKeyFixture("(1, 'a', 10), (2, 'b', 20)")
            + $"ALTER TABLE `replacement_items` ADD COLUMN `id` {IdentifierStorage} "
            + $"NOT NULL DEFAULT ({IdentifierDefault}); "
            + $"UPDATE `replacement_items` SET `id` = {DuplicateIdentifierLiteral};");

        await using var context = CreateContext(connectionString);
        var operations = ReplacementOperations(newIdentifier: true);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("independent-drop-existing-default-duplicates"), CancellationToken.None);

        var legacy = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'replacement_items' AND COLUMN_NAME = 'legacy_value';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, report.Assessments[3].Action);
        Assert.Equal(1, legacy);
    }

    /// <summary>
    /// A native index transformed by DROP is rechecked instead of trusting its old semantic identity.
    /// </summary>
    /// <param name="script">Whether execution uses the emitted migration script.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentDropRevalidatesAnIndexThatBecomesASemanticAlias(bool script)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `replacement_items` (`id` int NOT NULL PRIMARY KEY, `legacy_value` int NULL, "
            + "`tag_id` int NULL, KEY `ix_transformed` (`legacy_value`, `tag_id`)) ENGINE=InnoDB; "
            + "INSERT INTO `replacement_items` VALUES (1, 2, 3);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("legacy_value", "replacement_items");
        builder.CreateIndexIfNotExistsFromModel("ix_requested", "replacement_items", "tag_id");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("independent-drop-index-identity"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteIndependentDropAsync(context, connectionString, builder.Operations, script);
        var physical = await ScalarStringAsync(connectionString,
            "SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX) FROM INFORMATION_SCHEMA.STATISTICS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'replacement_items' "
            + "AND INDEX_NAME = 'ix_transformed';");

        var redundant = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'replacement_items' AND INDEX_NAME = 'ix_requested';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[1].Action);
        Assert.Equal(0, report.Assessments[1].DeferredOrigin?.OperationOrdinal);
        Assert.Equal("tag_id", physical);
        Assert.Equal(0, redundant);
    }

    /// <summary>A completed prefix can resume without manufacturing a second set of identifier values.</summary>
    /// <param name="appliedCount">The number of already completed prefix operations.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task IndependentDropExpressionReplacementResumesAnAppliedPrefix(int appliedCount)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExistingKeyFixture("(1, 'a', 10), (2, 'b', 20)"));
        await using var context = CreateContext(connectionString);
        var operations = ReplacementOperations(newIdentifier: true);
        await ExecuteOperationsAsync(context, operations.Take(appliedCount).ToArray(), CancellationToken.None);
        var before = appliedCount == 3
            ? await ScalarStringAsync(connectionString,
                "SELECT GROUP_CONCAT(`id` ORDER BY `event_id`) FROM `replacement_items`;")
            : null;

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("independent-drop-resume"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, operations, CancellationToken.None);
        var after = await ScalarStringAsync(connectionString,
            "SELECT GROUP_CONCAT(`id` ORDER BY `event_id`) FROM `replacement_items`;");

        // Assert
        Assert.Equal(appliedCount == 3 ? SafeMigrationReportStatus.Ready
            : SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(appliedCount >= 2 ? SafeMigrationAction.NoOp : SafeMigrationAction.Apply,
            report.Assessments[1].Action);
        Assert.Equal(appliedCount >= 3 ? SafeMigrationAction.NoOp : SafeMigrationAction.Apply,
            report.Assessments[2].Action);
        Assert.NotNull(after);
        Assert.NotEmpty(after);
        if (before is not null)
        {
            Assert.Equal(before, after);
        }
    }

    /// <summary>A sibling removal cannot erase an outgoing SET NULL action on the surviving target.</summary>
    /// <param name="removeForeignKey">Whether the exact conflicting FK was removed before tightening.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentDropRetainsSetNullDependencyUnlessExplicitlyRemoved(bool removeForeignKey)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `dependency_owners` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `dependency_items` (`id` int NOT NULL PRIMARY KEY, `legacy_value` int NULL, "
            + "`owner_id` int NULL, CONSTRAINT `fk_dependency_owner` FOREIGN KEY (`owner_id`) "
            + "REFERENCES `dependency_owners` (`id`) ON DELETE SET NULL) ENGINE=InnoDB; "
            + "INSERT INTO `dependency_owners` VALUES (1); INSERT INTO `dependency_items` VALUES (1, 2, 1);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (removeForeignKey)
        {
            builder.DropForeignKeyIfExists("fk_dependency_owner", "dependency_items");
        }

        builder.DropColumnIfExists("legacy_value", "dependency_items");
        builder.AlterColumnIfDifferentFromModel(
            operationFactory: static operations => operations.AlterColumn<int>(
                name: "owner_id", table: "dependency_items", type: "int", nullable: false,
                oldClrType: typeof(int), oldType: "int", oldNullable: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("independent-drop-set-null-dependency"), CancellationToken.None);

        if (removeForeignKey)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        }

        var nullable = await ScalarStringAsync(connectionString,
            "SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'dependency_items' AND COLUMN_NAME = 'owner_id';");

        // Assert
        Assert.Equal(removeForeignKey ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            report.Status);
        Assert.Equal(removeForeignKey ? "NO" : "YES", nullable);
        if (!removeForeignKey)
        {
            Assert.Equal("column_set_null_dependency", report.Assessments[^1].AnalysisCode);
        }
    }

    /// <summary>An independently surviving PRIMARY still forbids making its retained member nullable.</summary>
    [Fact]
    public async Task IndependentDropRetainsSurvivingPrimaryMembership()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `dependency_items` (`id` int NOT NULL PRIMARY KEY, `legacy_value` int NULL) "
            + "ENGINE=InnoDB; INSERT INTO `dependency_items` VALUES (1, 2);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("legacy_value", "dependency_items");
        builder.AlterColumnIfDifferentFromModel(
            operationFactory: static operations => operations.AlterColumn<int>(
                name: "id", table: "dependency_items", type: "int", nullable: true,
                oldClrType: typeof(int), oldType: "int"));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("independent-drop-primary-membership"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal("column_primary_key_dependency", report.Assessments[^1].AnalysisCode);
    }

    /// <summary>An initial generated-member addition keeps its real zero ordinal through later key removal.</summary>
    [Fact]
    public async Task DefaultedColumnAddedBeforeTheRemovalPrefixRetainsItsOwnRuntimeOrigin()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExistingKeyFixture("(1, 'a', 10), (2, 'b', 20)"));
        await using var context = CreateContext(connectionString);
        var operations = ReplacementOperations(newIdentifier: true);
        var addition = operations[2];
        operations.RemoveAt(2);
        operations.Insert(0, addition);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("independent-drop-add-first"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, operations, CancellationToken.None);
        var identifiers = await ScalarIntAsync(connectionString,
            "SELECT COUNT(DISTINCT `id`) FROM `replacement_items`;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[3].Action);
        Assert.Equal(0, report.Assessments[3].DeferredOrigin?.OperationOrdinal);
        Assert.Equal(2, identifiers);
    }

    /// <summary>Deferred classification never waives a fresh runtime duplicate check after the prefix.</summary>
    /// <param name="script">Whether the actual generated script enforces the late row conflict.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeferredExpressionReplacementRejectsDuplicatesIntroducedAfterPreflight(bool script)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExistingKeyFixture("(1, 'a', 10), (2, 'b', 20)"));
        await using var context = CreateContext(connectionString);
        var operations = ReplacementOperations(newIdentifier: true);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("independent-drop-late-duplicates"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, operations.Take(3).ToArray(), CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            $"UPDATE `replacement_items` SET `id` = {DuplicateIdentifierLiteral};");

        var failure = await Record.ExceptionAsync(() => ExecuteIndependentDropAsync(context, connectionString,
            operations.Skip(3).ToArray(), script));

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `replacement_items`;");
        var primary = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'replacement_items' AND CONSTRAINT_TYPE = 'PRIMARY KEY';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        if (script)
        {
            var native = Assert.IsType<MySqlException>(failure);
            Assert.Equal(1062, native.Number);
            Assert.Contains("doka_sm_data_blocked", native.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal("doka_sm_data_blocked", Assert.IsType<SafeMigrationExecutionException>(failure).GuardCode);
        }

        Assert.Equal(2, rows);
        Assert.Equal(0, primary);
    }

    /// <summary>Builds neutral populated rows valid under the original two-member primary key.</summary>
    private static string ExistingKeyFixture(string rows)
        => "CREATE TABLE `replacement_items` (`event_id` int NOT NULL, `legacy_value` varchar(20) NOT NULL, "
            + "`tag_id` int NULL, PRIMARY KEY (`event_id`, `legacy_value`)) ENGINE=InnoDB "
            + "DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `replacement_items` (`event_id`, `legacy_value`, `tag_id`) VALUES " + rows + ";";

    /// <summary>Declares the exact removal, surviving-column or generated-value transition, and new key.</summary>
    private List<MigrationOperation> ReplacementOperations(bool newIdentifier)
    {
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        builder.DropPrimaryKeyIfExists("pk_replacement_items", "replacement_items");
        builder.DropColumnIfExists("legacy_value", "replacement_items");
        if (newIdentifier)
        {
            if (Fixture.IsMariaDb)
            {
                builder.AddColumnIfNotExistsFromModel(
                    operationFactory: operations => operations.AddColumn<Guid>(
                        name: "id", table: "replacement_items", type: "char(36)",
                        fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: IdentifierDefault)
                        .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                        .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None));
            }
            else
            {
                builder.AddColumnIfNotExistsFromModel(
                    operationFactory: operations => operations.AddColumn<int>(
                        name: "id", table: "replacement_items", type: "int", nullable: false,
                        defaultValueSql: IdentifierDefault));
            }
        }
        else
        {
            builder.AlterColumnIfDifferentFromModel(
                operationFactory: static operations => operations.AlterColumn<int>(
                    name: "tag_id", table: "replacement_items", type: "int", nullable: false,
                    oldClrType: typeof(int), oldType: "int", oldNullable: true));
        }

        builder.AddPrimaryKeyIfNotExists(
            "pk_replacement_items",
            "replacement_items",
            newIdentifier ? ["id"] : ["event_id", "tag_id"]);

        builder.CreateIndexIfNotExistsFromModel("ix_replacement_event", "replacement_items", "event_id");

        return builder.Operations;
    }

    /// <summary>Uses the native UUID contract only where its DDL is safe under the unchanged fixture binlog.</summary>
    private string IdentifierDefault => Fixture.IsMariaDb
        ? "UUID()"
        // WHY: MySQL rejects non-deterministic expression-default ALTER under
        // ROW/MIXED binlogging. A deterministic per-row expression exercises
        // the same prospective-value proof without changing session settings.
        : "`event_id`";

    /// <summary>Keeps each native expression's declared CLR and physical value domain consistent.</summary>
    private string IdentifierStorage => Fixture.IsMariaDb ? "char(36)" : "int";

    /// <summary>Introduces the same duplicate conflict within either supported target domain.</summary>
    private string DuplicateIdentifierLiteral => Fixture.IsMariaDb
        ? "'00000000-0000-0000-0000-000000000001'"
        : "1";

    /// <summary>Executes the same guarded SQL through either supported emission path.</summary>
    private static Task ExecuteIndependentDropAsync(DbContext context, string connectionString,
        IReadOnlyList<MigrationOperation> operations, bool script)
    {
        if (!script)
        {
            return ExecuteOperationsAsync(context, operations, CancellationToken.None);
        }

        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(operations, context.Model,
            MigrationsSqlGenerationOptions.Script);

        return ExecuteMigrationScriptAsync(connectionString,
            string.Join(Environment.NewLine, commands.Select(static command => command.CommandText)));
    }
}
