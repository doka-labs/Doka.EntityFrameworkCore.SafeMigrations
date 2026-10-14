namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the ColumnsForeignKeyNullability migration contracts in an independent class collection.</summary>
public sealed partial class MySqlColumnsForeignKeyNullabilityIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlColumnsForeignKeyNullabilityIntegrationTests(MySqlEngineContainerFixture fixture) : base(fixture) { }

    /// <summary>Each unchanged sibling retains its own proof after several FK removals and relaxations.</summary>
    /// <param name="matchingSibling">Whether the second target already permits NULL.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeyDropPreservesMultipleSameOwnerNullableColumnProofs(bool matchingSibling)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ForeignKeyNullabilityFixture(alreadyNullable: false)
            + " ALTER TABLE `proof_child` ADD `second_owner_id` char(36) "
            + (matchingSibling ? "NULL;" : "NOT NULL;")
            + " UPDATE `proof_child` SET `second_owner_id` = `owner_id`;"
            + " ALTER TABLE `proof_child` ADD CONSTRAINT `fk_proof_second_parent` "
            + "FOREIGN KEY (`second_owner_id`) REFERENCES `proof_parent` (`id`) ON DELETE CASCADE;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_proof_child_parent", "proof_child");
        builder.DropForeignKeyIfExists("fk_proof_second_parent", "proof_child");
        builder.Operations.Add(ForeignKeyNullabilityOperations()[1]);
        builder.AlterColumnIfDifferentFromModel(
            operationFactory: static operations => operations.AlterColumn<Guid>(
                    name: "second_owner_id", table: "proof_child", type: "char(36)",
                    fixedLength: true, maxLength: 36, nullable: true,
                    oldClrType: typeof(Guid), oldType: "char(36)", oldFixedLength: true, oldMaxLength: 36)
                .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None)
                .OldAnnotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                .OldAnnotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None));

        builder.AddForeignKeyIfNotExists("fk_proof_child_parent", "proof_child", ["owner_id"],
            "proof_parent", ["id"], onDelete: ReferentialAction.SetNull);
        builder.AddForeignKeyIfNotExists("fk_proof_second_parent", "proof_child", ["second_owner_id"],
            "proof_parent", ["id"], onDelete: ReferentialAction.SetNull);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("multiple-fk-nullability"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("multiple-fk-nullability-final"), CancellationToken.None);

        await ExecuteSqlAsync(connectionString, "DELETE FROM `proof_parent`;");
        var retained = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `proof_child` WHERE `owner_id` IS NULL AND `second_owner_id` IS NULL;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Repair, preflight.Assessments[2].Action);
        Assert.Equal(matchingSibling ? SafeMigrationAction.NoOp : SafeMigrationAction.Repair,
            preflight.Assessments[3].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(1, retained);
    }

    /// <summary>An FK removal must preserve an exact same-layout nullable transition and its rerun.</summary>
    /// <param name="alreadyNullable">Whether the live target nullability is already satisfied.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeyDropPreservesNullableColumnProofAndReplacement(bool alreadyNullable)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ForeignKeyNullabilityFixture(alreadyNullable));
        await using var context = CreateContext(connectionString);
        var operations = ForeignKeyNullabilityOperations();
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("fk-nullability"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, operations,
            new SafeMigrationRunOptions("fk-nullability-final"), CancellationToken.None);

        var rerun = await runner.AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("fk-nullability-rerun"), CancellationToken.None);

        rerun.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, operations, CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "DELETE FROM `proof_parent` WHERE `id` = "
            + "'00000000-0000-0000-0000-000000000001';");
        var retained = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `proof_child` WHERE `id` = 1 AND `owner_id` IS NULL;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(alreadyNullable ? SafeMigrationAction.NoOp : SafeMigrationAction.Repair,
            preflight.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[2].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, rerun.Status);
        Assert.Equal(SafeMigrationAction.NoOp, rerun.Assessments[1].Action);
        Assert.Equal(1, retained);
    }

    /// <summary>Replacing only a referential action must not require invented table definitions.</summary>
    [Fact]
    public async Task ForeignKeyReplacementOnExistingNullableColumnUsesFreshProof()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ForeignKeyNullabilityFixture(alreadyNullable: true));
        await using var context = CreateContext(connectionString);
        var operations = ForeignKeyNullabilityOperations().Where(
            static operation => operation is not SafeMigrationOperation { Intent: AlterColumnIntent }).ToArray();

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("fk-action-replacement"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[1].Action);
    }

    /// <summary>A known backfill marker cannot populate a required GUID column for existing rows.</summary>
    [Fact]
    public async Task ExplicitBackfillRequiredGuidAddRejectsPopulatedTable()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `backfill_items` (`value` int NOT NULL); INSERT INTO `backfill_items` VALUES (7);");
        await using var context = CreateContext(connectionString);
        var operation = ExplicitBackfillGuidAdd();

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("required-guid-backfill"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, report.Assessments[0].ObservedState);
        Assert.NotEqual("provider_column_annotation", report.Assessments[0].AnalysisCode);
    }

    /// <summary>
    /// Fresh empty-table guards allow annotated required GUID additions without a fabricated default.
    /// </summary>
    [Fact]
    public async Task ExplicitBackfillRequiredGuidAddAllowsEmptyTableAndMatchingRerun()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `backfill_items` (`value` int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var operation = ExplicitBackfillGuidAdd();
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("empty-guid-backfill"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var rerun = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("existing-guid-backfill"), CancellationToken.None);

        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var count = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'backfill_items' AND COLUMN_NAME = 'id' AND IS_NULLABLE = 'NO';");

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.NoOp, rerun.Assessments[0].Action);
        Assert.Equal(1, count);
    }

    /// <summary>Creates one annotated required addition through the public scaffolding API.</summary>
    /// <returns>The immutable captured operation with no backfill default.</returns>
    private static SafeMigrationOperation ExplicitBackfillGuidAdd()
    {
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        builder.AddColumnIfNotExistsFromModel(
            operationFactory: static operations => operations.AddColumn<Guid>(
                    name: "id", table: "backfill_items", type: "char(36)",
                    fixedLength: true, maxLength: 36, nullable: false)
                .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                .Annotation("Doka:MySql:RequiresExplicitBackfill", true)
                .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ClientGuid));

        return Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
    }

    /// <summary>Declares only the real FK and same-layout column operations, without table convergence.</summary>
    /// <returns>The ordered removal, nullable alteration and action replacement.</returns>
    private static List<MigrationOperation> ForeignKeyNullabilityOperations()
    {
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        builder.DropForeignKeyIfExists("fk_proof_child_parent", "proof_child");
        builder.AlterColumnIfDifferentFromModel(
            operationFactory: static operations => operations.AlterColumn<Guid>(
                    name: "owner_id", table: "proof_child", type: "char(36)",
                    fixedLength: true, maxLength: 36, nullable: true,
                    oldClrType: typeof(Guid), oldType: "char(36)", oldFixedLength: true, oldMaxLength: 36)
                .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None)
                .OldAnnotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                .OldAnnotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None));

        builder.AddForeignKeyIfNotExists("fk_proof_child_parent", "proof_child", ["owner_id"],
            "proof_parent", ["id"], onDelete: ReferentialAction.SetNull);

        return builder.Operations;
    }
}

public abstract partial class MySqlSafeMigrationIntegrationTests
{

    /// <summary>Creates a populated brownfield FK fixture with explicit compatible character storage.</summary>
    /// <param name="alreadyNullable">Whether the initial child column already permits NULL.</param>
    /// <returns>The isolated test schema and sentinel rows.</returns>
    protected static string ForeignKeyNullabilityFixture(bool alreadyNullable)
        => "CREATE TABLE `proof_parent` (`id` char(36) NOT NULL PRIMARY KEY) ENGINE=InnoDB "
            + "DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "CREATE TABLE `proof_child` (`id` int NOT NULL PRIMARY KEY, `owner_id` char(36) "
            + (alreadyNullable ? "NULL" : "NOT NULL")
            + ", CONSTRAINT `fk_proof_child_parent` FOREIGN KEY (`owner_id`) "
            + "REFERENCES `proof_parent` (`id`) ON DELETE CASCADE) ENGINE=InnoDB "
            + "DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `proof_parent` VALUES ('00000000-0000-0000-0000-000000000001'); "
            + "INSERT INTO `proof_child` VALUES (1, '00000000-0000-0000-0000-000000000001');";
}
