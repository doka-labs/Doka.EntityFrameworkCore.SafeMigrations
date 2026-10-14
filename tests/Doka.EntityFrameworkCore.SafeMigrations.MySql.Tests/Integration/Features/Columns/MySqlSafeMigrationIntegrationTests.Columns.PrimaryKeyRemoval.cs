namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the ColumnsPrimaryKeyRemoval migration contracts in an independent class collection.</summary>
public sealed partial class MySqlColumnsPrimaryKeyRemovalIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlColumnsPrimaryKeyRemovalIntegrationTests(MySqlEngineContainerFixture fixture) : base(fixture) { }

    /// <summary>Ordered native key removal preserves an independently proven nullable column transition.</summary>
    /// <param name="supportingIndexExists">Whether the independent FK index already exists.</param>
    /// <param name="useEnsure">Whether the target has no authored old definition.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PrimaryKeyRemovalPreservesNullableColumnProof(bool supportingIndexExists, bool useEnsure)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, PrimaryKeyRemovalFixture(supportingIndexExists));
        await using var context = CreateContext(connectionString);
        var operations = PrimaryKeyRemovalOperations(useEnsure);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("primary-removal"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, operations,
            new SafeMigrationRunOptions("primary-removal-final"), CancellationToken.None);

        var rerun = await runner.AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("primary-removal-rerun"), CancellationToken.None);

        rerun.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, operations, CancellationToken.None);
        var nullable = await ScalarStringAsync(connectionString,
            "SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'key_items' AND COLUMN_NAME = 'user_id';");
        var retained = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `key_items` WHERE `question_id` = "
            + "'00000000-0000-0000-0000-000000000001' AND `user_id` = "
            + "'00000000-0000-0000-0000-000000000002' AND `payload` = 'retained';");
        var remainingForeignKey = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'key_items' "
            + "AND CONSTRAINT_NAME = 'fk_key_items_question';");

        await ExecuteSqlAsync(connectionString,
            "INSERT INTO `key_items` VALUES ('00000000-0000-0000-0000-000000000001', NULL, 'nullable');");
        var nullRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `key_items` WHERE `user_id` IS NULL;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[0].Action);
        Assert.Equal(supportingIndexExists ? SafeMigrationAction.NoOp : SafeMigrationAction.Apply,
            preflight.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[2].Action);
        Assert.Equal(SafeMigrationAction.Repair, preflight.Assessments[3].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, rerun.Status);
        Assert.Equal(SafeMigrationAction.NoOp, rerun.Assessments[3].Action);
        Assert.Equal("YES", nullable);
        Assert.Equal(1, retained);
        Assert.Equal(1, remainingForeignKey);
        Assert.Equal(1, nullRows);
    }

    /// <summary>An interrupted accepted removal prefix retains forward recovery without rewriting the source.</summary>
    [Fact]
    public async Task PrimaryKeyRemovalInterruptedPrefixCanResumeNullableAlter()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, PrimaryKeyRemovalFixture(supportingIndexExists: true));
        await using var context = CreateContext(connectionString);
        var operations = PrimaryKeyRemovalOperations();
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        await ExecuteOperationsAsync(context, operations.Take(3).ToArray(), CancellationToken.None);
        var resumed = await runner.AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("primary-removal-resume"), CancellationToken.None);

        resumed.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, operations, CancellationToken.None);
        var final = await runner.VerifyAsync(context, operations,
            new SafeMigrationRunOptions("primary-removal-resumed-final"), CancellationToken.None);

        var retained = await PrimaryKeyRemovalSentinelCountAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, resumed.Status);
        Assert.Equal(SafeMigrationAction.NoOp, resumed.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.NoOp, resumed.Assessments[2].Action);
        Assert.Equal(SafeMigrationAction.Repair, resumed.Assessments[3].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, final.Status);
        Assert.Equal(1, retained);
    }

    /// <summary>The same source and ordered changes are valid native DDL without any projection assumptions.</summary>
    [Fact]
    public async Task PrimaryKeyRemovalNativeControlPreservesColumnValues()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, PrimaryKeyRemovalFixture(supportingIndexExists: true));

        // Act
        await ExecuteSqlAsync(connectionString,
            "ALTER TABLE `key_items` DROP FOREIGN KEY `fk_key_items_user`; "
            + "ALTER TABLE `key_items` DROP PRIMARY KEY; "
            + "ALTER TABLE `key_items` MODIFY COLUMN `user_id` char(36) NULL;");
        var retained = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `key_items` WHERE `user_id` = "
            + "'00000000-0000-0000-0000-000000000002' AND `payload` = 'retained';");
        var nullable = await ScalarStringAsync(connectionString,
            "SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'key_items' AND COLUMN_NAME = 'user_id';");

        // Assert
        Assert.Equal(1, retained);
        Assert.Equal("YES", nullable);
    }
}

public abstract partial class MySqlSafeMigrationIntegrationTests
{

    /// <summary>Declares the physical removal prefix and exact metadata-preserving nullable alteration.</summary>
    /// <param name="useEnsure">Whether to capture native target evidence without an authored old source.</param>
    /// <returns>The neutral ordered operation stream, without fabricated table convergence.</returns>
    protected static List<MigrationOperation> PrimaryKeyRemovalOperations(bool useEnsure = false)
    {
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        builder.DropForeignKeyIfExists("fk_key_items_user", "key_items");
        builder.CreateIndexIfNotExistsFromModel("ix_key_items_question", "key_items", "question_id");
        builder.DropPrimaryKeyIfExists("pk_key_items", "key_items");
        if (useEnsure)
        {
            builder.AddColumnIfNotExistsFromModel(
                operationFactory: static operations => operations.AddColumn<Guid>(
                        name: "user_id", table: "key_items", type: "char(36)",
                        fixedLength: true, maxLength: 36, nullable: true)
                    .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                    .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None),
                policy: SafeMigrationPolicy.RepairIfSafe);
        }
        else
        {
            builder.AlterColumnIfDifferentFromModel(
                operationFactory: static operations => operations.AlterColumn<Guid>(
                        name: "user_id", table: "key_items", type: "char(36)",
                        fixedLength: true, maxLength: 36, nullable: true,
                        oldClrType: typeof(Guid), oldType: "char(36)", oldFixedLength: true, oldMaxLength: 36)
                    .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                    .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None)
                    .OldAnnotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                    .OldAnnotation("Doka:MySql:ValueGenerationStrategy",
                        MySqlValueGenerationStrategy.None));
        }

        return builder.Operations;
    }

    /// <summary>
    /// Builds a populated composite-key schema with two independently supported outgoing foreign keys.
    /// </summary>
    /// <param name="supportingIndexExists">Whether to create the independent question index explicitly.</param>
    /// <returns>The isolated schema and deterministic sentinel values.</returns>
    protected static string PrimaryKeyRemovalFixture(bool supportingIndexExists)
        => "CREATE TABLE `key_questions` (`id` char(36) NOT NULL PRIMARY KEY) ENGINE=InnoDB "
            + "DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "CREATE TABLE `key_users` (`id` char(36) NOT NULL PRIMARY KEY) ENGINE=InnoDB "
            + "DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "CREATE TABLE `key_items` (`question_id` char(36) NOT NULL, `user_id` char(36) NOT NULL, "
            + "`payload` longtext NOT NULL, PRIMARY KEY (`question_id`, `user_id`), "
            + (supportingIndexExists ? "KEY `ix_key_items_question` (`question_id`), " : string.Empty)
            + "KEY `ix_key_items_user` (`user_id`), "
            + "CONSTRAINT `fk_key_items_question` FOREIGN KEY (`question_id`) "
            + "REFERENCES `key_questions` (`id`) ON DELETE CASCADE, "
            + "CONSTRAINT `fk_key_items_user` FOREIGN KEY (`user_id`) "
            + "REFERENCES `key_users` (`id`) ON DELETE CASCADE) ENGINE=InnoDB "
            + "DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `key_questions` VALUES ('00000000-0000-0000-0000-000000000001'); "
            + "INSERT INTO `key_users` VALUES ('00000000-0000-0000-0000-000000000002'); "
            + "INSERT INTO `key_items` VALUES ('00000000-0000-0000-0000-000000000001', "
            + "'00000000-0000-0000-0000-000000000002', 'retained');";
}
