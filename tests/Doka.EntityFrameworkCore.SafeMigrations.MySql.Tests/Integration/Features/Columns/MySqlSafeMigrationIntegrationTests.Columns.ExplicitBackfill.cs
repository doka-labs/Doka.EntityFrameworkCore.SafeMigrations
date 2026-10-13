namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>A nullable GUID column with no NULL rows needs no invented backfill value.</summary>
    [Fact]
    public async Task ExplicitBackfillGuidTighteningWithoutNullsConvergesAndRemainsIdempotent()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExplicitBackfillNullableGuidFixture(includeNull: false));
        await using var context = CreateContext(connectionString);
        var operation = ExplicitBackfillGuidTightening();
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("null-free-guid-tightening"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, [operation],
            new SafeMigrationRunOptions("null-free-guid-tightening-final"), CancellationToken.None);

        var rerun = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("null-free-guid-tightening-rerun"), CancellationToken.None);

        rerun.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `explicit_backfill_items` WHERE `owner_id` = "
            + "'00000000-0000-0000-0000-000000000002';");
        var required = await ExplicitBackfillRequiredColumnCountAsync(connectionString, "owner_id");

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, Assert.Single(preflight.Assessments).Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(rerun.Assessments).Action);
        Assert.Equal(1, preserved);
        Assert.Equal(1, required);
        Assert.Equal(SafeMigrationDefaultValueKind.None,
            Assert.IsType<AlterColumnIntent>(operation.Intent).Definition.DefaultValue.Kind);
    }

    /// <summary>The backfill marker cannot manufacture a GUID for a stored NULL value.</summary>
    [Fact]
    public async Task ExplicitBackfillGuidTighteningWithNullsBlocksBeforeChangingDataOrNullability()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExplicitBackfillNullableGuidFixture(includeNull: true));
        await using var context = CreateContext(connectionString);
        var operation = ExplicitBackfillGuidTightening();

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("null-guid-tightening"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var nullRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `explicit_backfill_items` WHERE `owner_id` IS NULL;");
        var required = await ExplicitBackfillRequiredColumnCountAsync(connectionString, "owner_id");

        // Assert
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, Assert.Single(report.Assessments).Action);
        Assert.Contains("doka_sm_data_blocked", Assert.IsType<MySqlException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(1, nullRows);
        Assert.Equal(0, required);
    }

    /// <summary>A preflight NULL-free observation cannot replace the fresh execution-time row proof.</summary>
    [Fact]
    public async Task ExplicitBackfillGuidTighteningRejectsNullInsertedAfterPreflight()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExplicitBackfillNullableGuidFixture(includeNull: false));
        await using var context = CreateContext(connectionString);
        var operation = ExplicitBackfillGuidTightening();

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("guid-tightening-race"), CancellationToken.None);

        await ExecuteSqlAsync(connectionString,
            "INSERT INTO `explicit_backfill_items` (`value`, `owner_id`) VALUES (2, NULL);");
        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var nullRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `explicit_backfill_items` WHERE `owner_id` IS NULL;");
        var required = await ExplicitBackfillRequiredColumnCountAsync(connectionString, "owner_id");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Contains("doka_sm_data_blocked", Assert.IsType<MySqlException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(1, nullRows);
        Assert.Equal(0, required);
    }

    /// <summary>An empty-table add proof must be rechecked if a row arrives before execution.</summary>
    [Fact]
    public async Task ExplicitBackfillGuidAddRejectsRowInsertedAfterPreflight()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `explicit_backfill_items` (`value` int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var operation = ExplicitBackfillRequiredIdAdd();

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("required-guid-add-race"), CancellationToken.None);

        await ExecuteSqlAsync(connectionString, "INSERT INTO `explicit_backfill_items` (`value`) VALUES (7);");
        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var idColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'explicit_backfill_items' AND COLUMN_NAME = 'id';");
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `explicit_backfill_items` WHERE `value` = 7;");

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(report.Assessments).Action);
        Assert.Contains("doka_sm_data_blocked", Assert.IsType<MySqlException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(0, idColumns);
        Assert.Equal(1, preserved);
    }

    /// <summary>A caller-declared non-NULL GUID default retains the provider's real backfill UPDATE.</summary>
    [Fact]
    public async Task ExplicitBackfillGuidTighteningPreservesDeclaredDefaultAndExistingValues()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExplicitBackfillNullableGuidFixture(includeNull: true));
        await using var context = CreateContext(connectionString);
        var replacement = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var operation = ExplicitBackfillGuidTightening(replacement);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("declared-guid-backfill"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, [operation],
            new SafeMigrationRunOptions("declared-guid-backfill-final"), CancellationToken.None);

        var rerun = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("declared-guid-backfill-rerun"), CancellationToken.None);

        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var expectedValues = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `explicit_backfill_items` WHERE "
            + "(`value` = 1 AND `owner_id` = '00000000-0000-0000-0000-000000000002') OR "
            + "(`value` = 2 AND `owner_id` = '00000000-0000-0000-0000-000000000001');");
        var required = await ExplicitBackfillRequiredColumnCountAsync(connectionString, "owner_id");

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, Assert.Single(preflight.Assessments).Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(rerun.Assessments).Action);
        Assert.Equal(2, expectedValues);
        Assert.Equal(1, required);
        Assert.Equal(replacement,
            Assert.IsType<AlterColumnIntent>(operation.Intent).Definition.DefaultValue.GetLiteralValue());
    }

    /// <summary>A guarded empty-table ID addition also proves a subsequent primary-key replacement.</summary>
    [Fact]
    public async Task ExplicitBackfillGuidAddOnEmptyTableAllowsPrimaryKeyReplacementAndRerun()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `explicit_backfill_items` (`value` int NOT NULL PRIMARY KEY) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Operations.Add(ExplicitBackfillRequiredIdAdd());
        builder.DropPrimaryKeyIfExists("pk_explicit_backfill_items_value", "explicit_backfill_items");
        builder.AddPrimaryKeyIfNotExists("pk_explicit_backfill_items_id", "explicit_backfill_items", ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("required-guid-key-replacement"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("required-guid-key-replacement-final"), CancellationToken.None);

        var rerun = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("required-guid-key-replacement-rerun"), CancellationToken.None);

        rerun.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var idPrimaryKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE WHERE CONSTRAINT_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'explicit_backfill_items' AND CONSTRAINT_NAME = 'PRIMARY' "
            + "AND COLUMN_NAME = 'id' AND ORDINAL_POSITION = 1;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[2].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, rerun.Status);
        Assert.Equal(SafeMigrationAction.NoOp, rerun.Assessments[0].Action);
        Assert.Equal(1, idPrimaryKeys);
    }

    /// <summary>A populated required GUID addition remains blocked without an explicit caller backfill.</summary>
    [Fact]
    public async Task ExplicitBackfillGuidAddOnPopulatedTableRejectsRuntimeWithoutFabricatingIds()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `explicit_backfill_items` (`value` int NOT NULL); "
            + "INSERT INTO `explicit_backfill_items` (`value`) VALUES (7);");
        await using var context = CreateContext(connectionString);
        var operation = ExplicitBackfillRequiredIdAdd();

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("populated-required-guid-add"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var idColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'explicit_backfill_items' AND COLUMN_NAME = 'id';");
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `explicit_backfill_items` WHERE `value` = 7;");

        // Assert
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, Assert.Single(report.Assessments).Action);
        Assert.Contains("doka_sm_data_blocked", Assert.IsType<MySqlException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(0, idColumns);
        Assert.Equal(1, preserved);
    }

    /// <summary>
    /// Explicit staged row values, rather than a safety marker, permit a populated required-ID contract.
    /// </summary>
    [Fact]
    public async Task ExplicitBackfillCallerSuppliedStagedGuidsAllowRequiredColumn()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `explicit_backfill_items` (`value` int NOT NULL); "
            + "INSERT INTO `explicit_backfill_items` (`value`) VALUES (1), (2);");
        await using var context = CreateContext(connectionString);
        var nullableBuilder = new MigrationBuilder(context.Database.ProviderName!);
        nullableBuilder.AddColumnIfNotExistsFromModel(
            operationFactory: static operations => operations.AddColumn<Guid>(
                    name: "owner_id", table: "explicit_backfill_items", type: "char(36)",
                    fixedLength: true, maxLength: 36, nullable: true)
                .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None));

        var requiredOperation = ExplicitBackfillGuidTightening();
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        await ExecuteOperationsAsync(context, nullableBuilder.Operations, CancellationToken.None);
        // WHY: Row ownership is the caller's decision. The subsequent proof
        // must use these explicit values instead of inventing a shared GUID.
        await ExecuteSqlAsync(connectionString,
            "UPDATE `explicit_backfill_items` SET `owner_id` = CASE `value` "
            + "WHEN 1 THEN '00000000-0000-0000-0000-000000000001' "
            + "WHEN 2 THEN '00000000-0000-0000-0000-000000000002' END;");
        var preflight = await runner.AnalyzeAsync(context, [requiredOperation],
            new SafeMigrationRunOptions("staged-guid-backfill"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, [requiredOperation], CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, [requiredOperation],
            new SafeMigrationRunOptions("staged-guid-backfill-final"), CancellationToken.None);

        var expectedValues = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `explicit_backfill_items` WHERE "
            + "(`value` = 1 AND `owner_id` = '00000000-0000-0000-0000-000000000001') OR "
            + "(`value` = 2 AND `owner_id` = '00000000-0000-0000-0000-000000000002');");
        var required = await ExplicitBackfillRequiredColumnCountAsync(connectionString, "owner_id");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(2, expectedValues);
        Assert.Equal(1, required);
    }

    /// <summary>A NULL-free column cannot become required while its live FK still requests SET NULL.</summary>
    /// <param name="onUpdate">Whether SET NULL belongs to UPDATE rather than DELETE.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitBackfillGuidTighteningRejectsStillLiveSetNullForeignKey(bool onUpdate)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExplicitBackfillGuidForeignKeyFixture(
            onUpdate ? ReferentialAction.NoAction : ReferentialAction.SetNull,
            onUpdate ? ReferentialAction.SetNull : ReferentialAction.NoAction));

        await using var context = CreateContext(connectionString);
        var operation = ExplicitBackfillGuidTightening();

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("retained-set-null-guid-tightening"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var required = await ExplicitBackfillRequiredColumnCountAsync(connectionString, "owner_id");
        var preserved = await ExplicitBackfillGuidSentinelCountAsync(connectionString);
        var foreignKeys = await ExplicitBackfillGuidForeignKeyCountAsync(connectionString);

        // Assert
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal("alter_not_approved", assessment.Code);
        Assert.Equal("column_set_null_dependency", assessment.AnalysisCode);
        Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(0, required);
        Assert.Equal(1, preserved);
        Assert.Equal(1, foreignKeys);
    }

    /// <summary>Dropping every exact SET NULL owner discharges the dependency without inventing row values.</summary>
    [Fact]
    public async Task ExplicitBackfillGuidTighteningAfterAllSetNullDropsConvergesAndReruns()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExplicitBackfillGuidForeignKeyFixture(
            ReferentialAction.SetNull, ReferentialAction.NoAction, secondDependency: true));

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_explicit_backfill_owner", "explicit_backfill_items");
        builder.DropForeignKeyIfExists("fk_explicit_backfill_owner_update", "explicit_backfill_items");
        builder.Operations.Add(ExplicitBackfillGuidTightening());
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("removed-set-null-guid-tightening"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("removed-set-null-guid-tightening-final"), CancellationToken.None);

        var rerun = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("removed-set-null-guid-tightening-rerun"), CancellationToken.None);

        rerun.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var required = await ExplicitBackfillRequiredColumnCountAsync(connectionString, "owner_id");
        var preserved = await ExplicitBackfillGuidSentinelCountAsync(connectionString);
        var foreignKeys = await ExplicitBackfillGuidForeignKeyCountAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Repair, preflight.Assessments[2].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, rerun.Status);
        Assert.Equal(SafeMigrationAction.NoOp, rerun.Assessments[2].Action);
        Assert.Equal(1, required);
        Assert.Equal(1, preserved);
        Assert.Equal(0, foreignKeys);
    }

    /// <summary>Removing one of two SET NULL owners cannot certify the remaining incompatible dependency.</summary>
    [Fact]
    public async Task ExplicitBackfillGuidTighteningAfterPartialSetNullDropRemainsBlocked()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExplicitBackfillGuidForeignKeyFixture(
            ReferentialAction.SetNull, ReferentialAction.NoAction, secondDependency: true));

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_explicit_backfill_owner", "explicit_backfill_items");
        builder.Operations.Add(ExplicitBackfillGuidTightening());

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("partial-set-null-guid-tightening"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        var required = await ExplicitBackfillRequiredColumnCountAsync(connectionString, "owner_id");
        var preserved = await ExplicitBackfillGuidSentinelCountAsync(connectionString);
        var foreignKeys = await ExplicitBackfillGuidForeignKeyCountAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[1].Action);
        Assert.Equal("alter_not_approved", report.Assessments[1].Code);
        Assert.Equal("column_set_null_dependency", report.Assessments[1].AnalysisCode);
        Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(0, required);
        Assert.Equal(1, preserved);
        Assert.Equal(1, foreignKeys);
    }

    /// <summary>A compatible retained CASCADE FK must not be confused with a SET NULL dependency.</summary>
    [Fact]
    public async Task ExplicitBackfillGuidTighteningPreservesCompatibleCascadeForeignKey()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExplicitBackfillGuidForeignKeyFixture(
            ReferentialAction.Cascade, ReferentialAction.NoAction));

        await using var context = CreateContext(connectionString);
        var operation = ExplicitBackfillGuidTightening();
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("cascade-guid-tightening"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, [operation],
            new SafeMigrationRunOptions("cascade-guid-tightening-final"), CancellationToken.None);

        var required = await ExplicitBackfillRequiredColumnCountAsync(connectionString, "owner_id");
        var preserved = await ExplicitBackfillGuidSentinelCountAsync(connectionString);
        var foreignKeys = await ExplicitBackfillGuidForeignKeyCountAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, Assert.Single(preflight.Assessments).Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(1, required);
        Assert.Equal(1, preserved);
        Assert.Equal(1, foreignKeys);
    }

    /// <summary>A newly accepted SET NULL FK invalidates any earlier removal certificate before tightening.</summary>
    /// <param name="reusePhysicalName">Whether the replacement reuses the removed physical constraint name.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitBackfillGuidTighteningRejectsSetNullForeignKeyRecreatedAfterDrop(bool reusePhysicalName)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExplicitBackfillGuidForeignKeyFixture(
            ReferentialAction.SetNull, ReferentialAction.NoAction));

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_explicit_backfill_owner", "explicit_backfill_items");
        builder.AddForeignKeyIfNotExists(
            reusePhysicalName ? "fk_explicit_backfill_owner" : "fk_explicit_backfill_owner_recreated",
            "explicit_backfill_items", ["owner_id"], "explicit_backfill_owners", ["id"],
            onDelete: ReferentialAction.SetNull);
        builder.Operations.Add(ExplicitBackfillGuidTightening());

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("recreated-set-null-guid-tightening"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        var required = await ExplicitBackfillRequiredColumnCountAsync(connectionString, "owner_id");
        var preserved = await ExplicitBackfillGuidSentinelCountAsync(connectionString);
        var foreignKeys = await ExplicitBackfillGuidForeignKeyCountAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[2].Action);
        Assert.Equal("alter_not_approved", report.Assessments[2].Code);
        Assert.Equal("column_set_null_dependency", report.Assessments[2].AnalysisCode);
        Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(0, required);
        Assert.Equal(1, preserved);
        Assert.Equal(1, foreignKeys);
    }

    /// <summary>Empty projected rows cannot make a newly accepted SET NULL FK compatible with NOT NULL.</summary>
    /// <param name="recreated">Whether an existing child is removed before its fresh creation.</param>
    /// <param name="inlineForeignKey">Whether the FK is declared inside the new table definition.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExplicitBackfillGuidTighteningRejectsFreshProjectedSetNullForeignKey(
        bool recreated,
        bool inlineForeignKey
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        if (recreated)
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE TABLE `explicit_backfill_items` (`value` int NOT NULL, `owner_id` char(36) NULL);");
        }

        await using var context = CreateContext(connectionString);
        var operations = ExplicitBackfillFreshSetNullOperations(recreated, inlineForeignKey);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("fresh-set-null-guid-tightening"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, operations, CancellationToken.None));

        var required = await ExplicitBackfillRequiredColumnCountAsync(connectionString, "owner_id");
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `explicit_backfill_items`;");
        var foreignKeys = await ExplicitBackfillGuidForeignKeyCountAsync(connectionString);

        // Assert
        var alteration = report.Assessments[^1];

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.All(report.Assessments.Take(report.Assessments.Count - 1),
            assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationAction.RejectDifferent, alteration.Action);
        Assert.Equal("alter_not_approved", alteration.Code);
        Assert.Equal("column_set_null_dependency", alteration.AnalysisCode);
        Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(0, required);
        Assert.Equal(0, rows);
        Assert.Equal(1, foreignKeys);
    }

    /// <summary>Exact dependency removal permits one explicit backfill without fabricating GUID ownership.</summary>
    [Fact]
    public async Task ExplicitBackfillGuidTighteningAfterSetNullDropsPreservesDeclaredRowBackfill()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExplicitBackfillGuidForeignKeyFixture(
            ReferentialAction.SetNull, ReferentialAction.NoAction, secondDependency: true)
            + " INSERT INTO `explicit_backfill_owners` (`id`) VALUES ('00000000-0000-0000-0000-000000000001');"
            + " INSERT INTO `explicit_backfill_items` (`value`, `owner_id`) VALUES (2, NULL);");

        await using var context = CreateContext(connectionString);
        var replacement = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_explicit_backfill_owner", "explicit_backfill_items");
        builder.DropForeignKeyIfExists("fk_explicit_backfill_owner_update", "explicit_backfill_items");
        builder.Operations.Add(ExplicitBackfillGuidTightening(replacement));
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("removed-set-null-guid-backfill"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("removed-set-null-guid-backfill-final"), CancellationToken.None);

        var rerun = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("removed-set-null-guid-backfill-rerun"), CancellationToken.None);

        rerun.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var expectedValues = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `explicit_backfill_items` WHERE "
            + "(`value` = 1 AND `owner_id` = '00000000-0000-0000-0000-000000000002') OR "
            + "(`value` = 2 AND `owner_id` = '00000000-0000-0000-0000-000000000001');");
        var required = await ExplicitBackfillRequiredColumnCountAsync(connectionString, "owner_id");
        var foreignKeys = await ExplicitBackfillGuidForeignKeyCountAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Repair, preflight.Assessments[2].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, rerun.Status);
        Assert.Equal(SafeMigrationAction.NoOp, rerun.Assessments[2].Action);
        Assert.Equal(2, expectedValues);
        Assert.Equal(1, required);
        Assert.Equal(0, foreignKeys);
    }

    /// <summary>
    /// Builds an empty projected child with a newly owned incompatible FK before its required alteration.
    /// </summary>
    /// <param name="recreated">Whether an accepted drop precedes child creation.</param>
    /// <param name="inlineForeignKey">Whether the child definition owns the FK inline.</param>
    /// <returns>The complete ordered stream ending in the unsupported required transition.</returns>
    private static List<MigrationOperation> ExplicitBackfillFreshSetNullOperations(
        bool recreated,
        bool inlineForeignKey
    )
    {
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        builder.CreateTableIfNotExists(
            name: "explicit_backfill_owners",
            columns: static table => new
            {
                id = table.Column<Guid>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false)
                    .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                    .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None),
            },
            constraints: static table => table.PrimaryKey("pk_explicit_backfill_owners", owner => owner.id));

        if (recreated)
        {
            builder.DropTableIfExists("explicit_backfill_items");
        }

        builder.CreateTableIfNotExists(
            name: "explicit_backfill_items",
            columns: static table => new
            {
                value = table.Column<int>(type: "int", nullable: false),
                owner_id = table.Column<Guid>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true)
                    .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                    .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None),
            },
            constraints: table =>
            {
                if (inlineForeignKey)
                {
                    table.ForeignKey("fk_explicit_backfill_owner", child => child.owner_id,
                        "explicit_backfill_owners", "id", onDelete: ReferentialAction.SetNull);
                }

            });

        if (!inlineForeignKey)
        {
            builder.AddForeignKeyIfNotExists("fk_explicit_backfill_owner", "explicit_backfill_items", ["owner_id"],
                "explicit_backfill_owners", ["id"], onDelete: ReferentialAction.SetNull);
        }

        builder.Operations.Add(ExplicitBackfillGuidTightening());

        return builder.Operations;
    }

    /// <summary>
    /// An Ensure repair cannot make a NULL-free column required while its FK still requests SET NULL.
    /// </summary>
    /// <param name="onUpdate">Whether SET NULL belongs to UPDATE rather than DELETE.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitBackfillGuidEnsureRejectsStillLiveSetNullForeignKey(bool onUpdate)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExplicitBackfillGuidForeignKeyFixture(
            onUpdate ? ReferentialAction.NoAction : ReferentialAction.SetNull,
            onUpdate ? ReferentialAction.SetNull : ReferentialAction.NoAction));

        await using var context = CreateContext(connectionString);
        var operation = ExplicitBackfillGuidEnsure();

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("retained-set-null-guid-ensure"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var required = await ExplicitBackfillRequiredColumnCountAsync(connectionString, "owner_id");
        var preserved = await ExplicitBackfillGuidSentinelCountAsync(connectionString);
        var foreignKeys = await ExplicitBackfillGuidForeignKeyCountAsync(connectionString);

        // Assert
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal("different_no_safe_repair", assessment.Code);
        Assert.Equal("column_set_null_dependency", assessment.AnalysisCode);
        Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(0, required);
        Assert.Equal(1, preserved);
        Assert.Equal(1, foreignKeys);
    }

    /// <summary>Exact removal of every SET NULL dependency permits a NULL-free Ensure repair.</summary>
    [Fact]
    public async Task ExplicitBackfillGuidEnsureAfterAllSetNullDropsConvergesAndReruns()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExplicitBackfillGuidForeignKeyFixture(
            ReferentialAction.SetNull, ReferentialAction.NoAction, secondDependency: true));

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_explicit_backfill_owner", "explicit_backfill_items");
        builder.DropForeignKeyIfExists("fk_explicit_backfill_owner_update", "explicit_backfill_items");
        builder.Operations.Add(ExplicitBackfillGuidEnsure());
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("removed-set-null-guid-ensure"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("removed-set-null-guid-ensure-final"), CancellationToken.None);

        var rerun = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("removed-set-null-guid-ensure-rerun"), CancellationToken.None);

        rerun.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var required = await ExplicitBackfillRequiredColumnCountAsync(connectionString, "owner_id");
        var preserved = await ExplicitBackfillGuidSentinelCountAsync(connectionString);
        var foreignKeys = await ExplicitBackfillGuidForeignKeyCountAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Repair, preflight.Assessments[2].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, rerun.Status);
        Assert.Equal(SafeMigrationAction.NoOp, rerun.Assessments[2].Action);
        Assert.Equal(1, required);
        Assert.Equal(1, preserved);
        Assert.Equal(0, foreignKeys);
    }

    /// <summary>A compatible retained CASCADE FK does not block a NULL-free Ensure repair.</summary>
    [Fact]
    public async Task ExplicitBackfillGuidEnsurePreservesCompatibleCascadeForeignKey()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExplicitBackfillGuidForeignKeyFixture(
            ReferentialAction.Cascade, ReferentialAction.NoAction));

        await using var context = CreateContext(connectionString);
        var operation = ExplicitBackfillGuidEnsure();
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("cascade-guid-ensure"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, [operation],
            new SafeMigrationRunOptions("cascade-guid-ensure-final"), CancellationToken.None);

        var required = await ExplicitBackfillRequiredColumnCountAsync(connectionString, "owner_id");
        var preserved = await ExplicitBackfillGuidSentinelCountAsync(connectionString);
        var foreignKeys = await ExplicitBackfillGuidForeignKeyCountAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, Assert.Single(preflight.Assessments).Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(1, required);
        Assert.Equal(1, preserved);
        Assert.Equal(1, foreignKeys);
    }

    /// <summary>An Ensure repair without a replacement value must continue rejecting actual NULL rows.</summary>
    [Fact]
    public async Task ExplicitBackfillGuidEnsureWithNullRowsRemainsDataBlocked()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExplicitBackfillNullableGuidFixture(includeNull: true));
        await using var context = CreateContext(connectionString);
        var operation = ExplicitBackfillGuidEnsure();

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("null-guid-ensure"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var nullRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `explicit_backfill_items` WHERE `owner_id` IS NULL;");
        var required = await ExplicitBackfillRequiredColumnCountAsync(connectionString, "owner_id");

        // Assert
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, Assert.Single(report.Assessments).Action);
        Assert.Contains("doka_sm_data_blocked", Assert.IsType<MySqlException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(1, nullRows);
        Assert.Equal(0, required);
    }

    /// <summary>A nullable Ensure target cannot relax a physical PRIMARY column until its key is removed.</summary>
    /// <param name="dropPrimaryKey">Whether an accepted exact primary-key removal precedes the repair.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitBackfillGuidEnsureNullableTargetRequiresPrimaryKeyRemoval(bool dropPrimaryKey)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `explicit_backfill_items` (`value` int NOT NULL, `owner_id` char(36) NOT NULL PRIMARY KEY) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `explicit_backfill_items` (`value`, `owner_id`) "
            + "VALUES (1, '00000000-0000-0000-0000-000000000002');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (dropPrimaryKey)
        {
            builder.DropPrimaryKeyIfExists("pk_explicit_backfill_items", "explicit_backfill_items");
        }

        builder.Operations.Add(ExplicitBackfillGuidEnsure(nullable: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("primary-guid-ensure"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        var required = await ExplicitBackfillRequiredColumnCountAsync(connectionString, "owner_id");
        var preserved = await ExplicitBackfillGuidSentinelCountAsync(connectionString);
        var primaryKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'explicit_backfill_items' AND CONSTRAINT_TYPE = 'PRIMARY KEY';");

        // Assert
        var column = report.Assessments[^1];

        Assert.Equal(dropPrimaryKey ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            report.Status);
        Assert.Equal(dropPrimaryKey ? SafeMigrationAction.Repair : SafeMigrationAction.RejectDifferent,
            column.Action);
        if (dropPrimaryKey)
        {
            Assert.Null(exception);
        }
        else
        {
            Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(exception).Message,
                StringComparison.Ordinal);
        }

        Assert.Equal(dropPrimaryKey ? 0 : 1, required);
        Assert.Equal(1, preserved);
        Assert.Equal(dropPrimaryKey ? 0 : 1, primaryKeys);
    }

    /// <summary>A NULL-free tightening requires strict conversion; an exact match needs no conversion.</summary>
    /// <param name="ensure">Whether the caller uses a complete Ensure repair instead of Alter.</param>
    /// <param name="alreadyMatching">Whether no physical alteration is needed.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExplicitBackfillGuidTighteningRequiresStrictModeOnlyForRepair(
        bool ensure,
        bool alreadyMatching
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ExplicitBackfillNullableGuidFixture(includeNull: false)
            + (alreadyMatching
                ? " ALTER TABLE `explicit_backfill_items` MODIFY `owner_id` char(36) NOT NULL;"
                : string.Empty));

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync("SET SESSION sql_mode = '';", CancellationToken.None);
        var operation = ensure ? ExplicitBackfillGuidEnsure() : ExplicitBackfillGuidTightening();

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("strict-null-free-guid-tightening"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var required = await ExplicitBackfillRequiredColumnCountAsync(connectionString, "owner_id");
        var preserved = await ExplicitBackfillGuidSentinelCountAsync(connectionString);

        // Assert
        Assert.Equal(alreadyMatching ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            report.Status);
        Assert.Equal(alreadyMatching ? SafeMigrationAction.NoOp : SafeMigrationAction.RejectDifferent,
            Assert.Single(report.Assessments).Action);
        if (alreadyMatching)
        {
            Assert.Null(exception);
        }
        else
        {
            Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(exception).Message,
                StringComparison.Ordinal);
        }

        Assert.Equal(alreadyMatching ? 1 : 0, required);
        Assert.Equal(1, preserved);
    }

    /// <summary>Captures a complete Ensure-column target rather than an explicit Alter operation.</summary>
    /// <param name="nullable">Whether the expected GUID contract allows NULL.</param>
    /// <returns>The immutable complete target under RepairIfSafe.</returns>
    private static SafeMigrationOperation ExplicitBackfillGuidEnsure(bool nullable = false)
    {
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        builder.AddColumnIfNotExistsFromModel(
            operationFactory: operations =>
            {
                var operation = operations.AddColumn<Guid>(
                        name: "owner_id", table: "explicit_backfill_items", type: "char(36)",
                        fixedLength: true, maxLength: 36, nullable: nullable)
                    .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                    .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None);

                if (!nullable)
                {
                    operation.Annotation("Doka:MySql:RequiresExplicitBackfill", true);
                }

            },
            policy: SafeMigrationPolicy.RepairIfSafe);

        return Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
    }

    /// <summary>Captures the exact provider safety marker on a same-layout GUID tightening.</summary>
    /// <param name="replacement">The optional caller-declared replacement for existing NULL values.</param>
    /// <returns>The immutable SafeMigrations alteration with its original nullable contract.</returns>
    private static SafeMigrationOperation ExplicitBackfillGuidTightening(Guid? replacement = null)
    {
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        builder.AlterColumnIfDifferentFromModel(
            operationFactory: operations => operations.AlterColumn<Guid>(
                    name: "owner_id", table: "explicit_backfill_items", type: "char(36)",
                    fixedLength: true, maxLength: 36, nullable: false, defaultValue: replacement,
                    oldClrType: typeof(Guid), oldType: "char(36)", oldFixedLength: true,
                    oldMaxLength: 36, oldNullable: true)
                .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                .Annotation("Doka:MySql:RequiresExplicitBackfill", true)
                .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None)
                .OldAnnotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                .OldAnnotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None),
            policy: SafeMigrationPolicy.RepairIfSafe);

        return Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
    }

    /// <summary>Captures a required GUID addition without a synthetic default or caller-owned row values.</summary>
    /// <returns>The immutable addition used behind a fresh empty-table guard.</returns>
    private static SafeMigrationOperation ExplicitBackfillRequiredIdAdd()
    {
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        builder.AddColumnIfNotExistsFromModel(
            operationFactory: static operations => operations.AddColumn<Guid>(
                    name: "id", table: "explicit_backfill_items", type: "char(36)",
                    fixedLength: true, maxLength: 36, nullable: false)
                .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                .Annotation("Doka:MySql:RequiresExplicitBackfill", true)
                .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ClientGuid));

        return Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
    }

    /// <summary>Creates a nullable GUID fixture with one sentinel row and an optional NULL row.</summary>
    /// <param name="includeNull">Whether the fixture requires a real row backfill.</param>
    /// <returns>The isolated schema and deterministic row values.</returns>
    private static string ExplicitBackfillNullableGuidFixture(bool includeNull)
        => "CREATE TABLE `explicit_backfill_items` (`value` int NOT NULL, `owner_id` char(36) NULL) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `explicit_backfill_items` (`value`, `owner_id`) "
            + "VALUES (1, '00000000-0000-0000-0000-000000000002');"
            + (includeNull ? " INSERT INTO `explicit_backfill_items` (`value`, `owner_id`) VALUES (2, NULL);" : "");

    /// <summary>Creates a populated NULL-free GUID fixture with explicit compatible FK storage.</summary>
    /// <param name="onDelete">The primary constraint's DELETE action.</param>
    /// <param name="onUpdate">The primary constraint's UPDATE action.</param>
    /// <param name="secondDependency">Whether another exact FK requests SET NULL on UPDATE.</param>
    /// <returns>The isolated parent and dependent schema with one known row.</returns>
    private static string ExplicitBackfillGuidForeignKeyFixture(
        ReferentialAction onDelete,
        ReferentialAction onUpdate,
        bool secondDependency = false
    ) => "CREATE TABLE `explicit_backfill_owners` (`id` char(36) NOT NULL PRIMARY KEY) "
        + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
        + "CREATE TABLE `explicit_backfill_items` (`value` int NOT NULL, `owner_id` char(36) NULL, "
        + "CONSTRAINT `fk_explicit_backfill_owner` FOREIGN KEY (`owner_id`) "
        + "REFERENCES `explicit_backfill_owners` (`id`) ON DELETE " + ExplicitBackfillReferentialActionSql(onDelete)
        + " ON UPDATE " + ExplicitBackfillReferentialActionSql(onUpdate)
        + (secondDependency
            ? ", CONSTRAINT `fk_explicit_backfill_owner_update` FOREIGN KEY (`owner_id`) "
                + "REFERENCES `explicit_backfill_owners` (`id`) ON DELETE NO ACTION ON UPDATE SET NULL"
            : "")
        + ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
        + "INSERT INTO `explicit_backfill_owners` (`id`) VALUES ('00000000-0000-0000-0000-000000000002'); "
        + "INSERT INTO `explicit_backfill_items` (`value`, `owner_id`) "
        + "VALUES (1, '00000000-0000-0000-0000-000000000002');";

    /// <summary>Renders only referential actions declared by these anonymous fixtures.</summary>
    /// <param name="action">The fixture's bounded referential action.</param>
    /// <returns>The canonical SQL action.</returns>
    private static string ExplicitBackfillReferentialActionSql(ReferentialAction action) => action switch
    {
        ReferentialAction.NoAction => "NO ACTION",
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET NULL",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>Counts the known NULL-free sentinel without exposing row payloads in test diagnostics.</summary>
    /// <param name="connectionString">The isolated fixture database connection.</param>
    /// <returns>One while the row's explicit GUID remains unchanged.</returns>
    private static Task<int> ExplicitBackfillGuidSentinelCountAsync(string connectionString)
        => ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `explicit_backfill_items` WHERE `value` = 1 "
            + "AND `owner_id` = '00000000-0000-0000-0000-000000000002';");

    /// <summary>Counts actual outgoing FK owners remaining on the fixture table.</summary>
    /// <param name="connectionString">The isolated fixture database connection.</param>
    /// <returns>The physical outgoing FK count.</returns>
    private static Task<int> ExplicitBackfillGuidForeignKeyCountAsync(string connectionString)
        => ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'explicit_backfill_items';");

    /// <summary>Reads only catalog nullability, without inspecting confidential row payloads.</summary>
    /// <param name="connectionString">The isolated fixture database connection.</param>
    /// <param name="column">The known test column to inspect.</param>
    /// <returns>One when the named column exists and is required; otherwise zero.</returns>
    private static Task<int> ExplicitBackfillRequiredColumnCountAsync(string connectionString, string column)
        => ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'explicit_backfill_items' AND COLUMN_NAME = '" + column + "' AND IS_NULLABLE = 'NO';");
}
