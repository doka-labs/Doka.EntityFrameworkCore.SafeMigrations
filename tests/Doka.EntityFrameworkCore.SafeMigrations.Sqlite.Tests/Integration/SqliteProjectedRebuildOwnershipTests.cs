namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Prevents accepted source additions from bypassing native SQLite rebuild ownership.</summary>
public sealed class SqliteProjectedRebuildOwnershipTests : SqliteIntegrationTestBase,
    IClassFixture<SqliteConstraintShapeServiceFixture>
{
    private readonly IServiceProvider _internalServices;

    /// <summary>Shares only provider services; every case owns its native database and model family.</summary>
    /// <param name="services">The class-owned SQLite services.</param>
    public SqliteProjectedRebuildOwnershipTests(SqliteConstraintShapeServiceFixture services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _internalServices = services.Provider;
    }

    /// <summary>
    /// Initial native absence cannot waive retained artifacts when projection later requires a rebuild.
    /// </summary>
    /// <param name="operation">The rebuilding operation after the accepted source addition.</param>
    /// <param name="artifact">The independently retained native artifact.</param>
    /// <param name="preflight">Whether the caller opts into the independent read-only preflight.</param>
    [Theory]
    [InlineData("drop", "trigger", true)]
    [InlineData("drop", "trigger", false)]
    [InlineData("drop", "index", true)]
    [InlineData("drop", "check", true)]
    [InlineData("drop", "foreign_key", true)]
    [InlineData("drop", "column", true)]
    [InlineData("alter", "trigger", true)]
    [InlineData("alter", "trigger", false)]
    public async Task ProjectedRebuildRetainsIndependentNativeOwnershipGuards(
        string operation,
        string artifact,
        bool preflight
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateNativeOwnerAsync(connection, artifact);
        await using var context = CreateTargetContext(connection, operation);
        var builder = CreateOperations(context, operation);
        var schemaBefore = await ReadSchemaAsync(connection);
        var artifactBefore = await CountArtifactsAsync(connection, artifact);

        // Act
        var report = preflight
            ? await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
                context, builder.Operations, new SafeMigrationRunOptions("sqlite-projected-rebuild-ownership"))
            : null;

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var schemaAfter = await ReadSchemaAsync(connection);
        var artifactAfter = await CountArtifactsAsync(connection, artifact);
        var retainedRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM rebuild_nodes WHERE id = 1 AND extra = 7 AND derived = 8;");

        // Assert
        var expected = ArtifactCode(artifact);
        if (report is not null)
        {
            Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
            Assert.Equal(expected, report.Assessments[^1].AnalysisCode);
            Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[^1].Action);
        }

        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, artifactBefore);
        Assert.Equal(1, artifactAfter);
        Assert.Equal(schemaBefore, schemaAfter);
        Assert.Equal(1, retainedRows);
    }

    /// <summary>A genuinely absent DROP remains a no-op even when rebuilding that owner would be forbidden.</summary>
    /// <param name="ownerExists">Whether the native owner exists with a retained trigger.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingDropDoesNotDemandUnusedRebuildOwnership(bool ownerExists)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        if (ownerExists)
        {
            await CreateNativeOwnerAsync(connection, "trigger");
        }

        await using var context = new DroppedSourceContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("source", "rebuild_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-native-missing-drop"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var owners = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM main.sqlite_schema WHERE type = 'table' AND name = 'rebuild_nodes';");

        var triggers = await CountArtifactsAsync(connection, "trigger");

        // Assert
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(report.Assessments).Action);
        Assert.Equal(ownerExists ? 1 : 0, owners);
        Assert.Equal(ownerExists ? 1 : 0, triggers);
    }

    /// <summary>Mapped owners still permit a real addition followed by an owned, explicitly authored rebuild.</summary>
    /// <param name="operation">The rebuilding operation after the source addition.</param>
    [Theory]
    [InlineData("drop")]
    [InlineData("alter")]
    public async Task MappedProjectedRebuildPreservesExistingRows(string operation)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateNativeOwnerAsync(connection, "none");
        await using var context = CreateTargetContext(connection, operation);
        var builder = CreateOperations(context, operation);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-owned-projected-rebuild"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var retainedRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM rebuild_nodes WHERE id = 1 AND extra = 7 AND derived = 8;");

        // Assert
        var diagnostic = Encoding.UTF8.GetString(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
        Assert.True(report.Status == SafeMigrationReportStatus.Ready, diagnostic);
        Assert.Null(exception);
        Assert.Equal(1, retainedRows);
    }

    /// <summary>Source presence alone still cannot certify an otherwise unproven native CHECK transition.</summary>
    /// <param name="preflight">Whether the caller opts into the independent read-only preflight.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnprovenProjectedCheckRemainsBlockedWithoutChangingTheOwner(bool preflight)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateNativeOwnerAsync(connection, "trigger");
        await using var context = new CheckedSourceContext(connection, _internalServices);
        var builder = CreateOperations(context, "check");
        var schemaBefore = await ReadSchemaAsync(connection);
        var triggersBefore = await CountArtifactsAsync(connection, "trigger");

        // Act
        var report = preflight
            ? await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
                context, builder.Operations, new SafeMigrationRunOptions("sqlite-unproven-projected-check"))
            : null;

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var schemaAfter = await ReadSchemaAsync(connection);
        var triggersAfter = await CountArtifactsAsync(connection, "trigger");
        var retainedRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM rebuild_nodes WHERE id = 1 AND extra = 7 AND derived = 8;");

        // Assert
        if (report is not null)
        {
            Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
            Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[^1].Action);
        }

        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("prerequisite_missing", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, triggersBefore);
        Assert.Equal(1, triggersAfter);
        Assert.Equal(schemaBefore, schemaAfter);
        Assert.Equal(1, retainedRows);
    }

    /// <summary>Fresh final ownership still validates a table that did not exist in the initial snapshot.</summary>
    /// <param name="removeParent">Whether the adversarial baseline also removes the initially present parent.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewlyCreatedValidationOwnerCannotEscapeFinalForeignKeyChecks(bool removeParent)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE parent_nodes (id INTEGER NOT NULL PRIMARY KEY); INSERT INTO parent_nodes VALUES (7);");

        await using var context = new DroppedSourceContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("new_children", table => new
        {
            id = table.Column<int>(type: "INTEGER", nullable: false),
            parent_id = table.Column<int>(type: "INTEGER", nullable: false),
        }, constraints: table =>
        {
            table.PrimaryKey("pk_new_children", value => value.id);
            table.ForeignKey("fk_new_children_parent", value => value.parent_id, "parent_nodes", "id");
        });

        builder.DropColumnIfExists("missing", "new_children");
        var mutation = removeParent
            ? "INSERT INTO new_children (id, parent_id) VALUES (1, 7); DROP TABLE parent_nodes;"
            : "INSERT INTO new_children (id, parent_id) VALUES (1, 999);";

        var baseline = new MutatingForeignKeyBaseline(
            context.GetService<ISqliteSafeMigrationsBaselineGenerator>(), mutation);

        var commands = CreateGenerator(context, baseline).Generate(builder.Operations, context.Model);

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteCommandsAsync(context, commands));
        var newOwners = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM main.sqlite_schema WHERE type = 'table' AND name = 'new_children';");

        var parentRows = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM parent_nodes WHERE id = 7;");
        var enforcement = await ScalarIntAsync(connection, "PRAGMA foreign_keys;");

        // Assert
        Assert.True(baseline.MutationGenerated);
        Assert.Contains("foreign_key_validation_failed", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);

        Assert.Equal(0, newOwners);
        Assert.Equal(1, parentRows);
        Assert.Equal(1, enforcement);
        Assert.Null(context.GetService<IRelationalConnection>().CurrentTransaction);
    }

    /// <summary>Filtering absent validation owners must retain checks for their surviving inbound children.</summary>
    [Fact]
    public async Task RemovedValidationPrincipalStillChecksSurvivingInboundChildren()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateNativeOwnerAsync(connection, "none");
        await ExecuteSqlAsync(connection,
            "CREATE TABLE inbound_children (id INTEGER NOT NULL PRIMARY KEY, parent_id INTEGER NOT NULL, "
            + "FOREIGN KEY (parent_id) REFERENCES parent_nodes (id)); "
            + "INSERT INTO inbound_children VALUES (1, 7);");

        await using var context = new DroppedSourceContext(connection, _internalServices);
        var builder = CreateOperations(context, "drop");
        builder.DropColumnIfExists("missing", "parent_nodes");
        var baseline = new MutatingForeignKeyBaseline(
            context.GetService<ISqliteSafeMigrationsBaselineGenerator>(), "DROP TABLE parent_nodes;");

        var commands = CreateGenerator(context, baseline).Generate(builder.Operations, context.Model);
        var schemaBefore = await ReadSchemaAsync(connection);

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteCommandsAsync(context, commands));
        var schemaAfter = await ReadSchemaAsync(connection);
        var parents = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM parent_nodes WHERE id = 7;");
        var children = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM inbound_children WHERE id = 1 AND parent_id = 7;");

        var ownerRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM rebuild_nodes WHERE id = 1 AND extra = 7 AND derived = 8;");

        // Assert
        Assert.True(baseline.MutationGenerated);
        var rejection = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("foreign_key_validation_failed", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("inbound_children", rejection.Message, StringComparison.Ordinal);
        Assert.Equal(schemaBefore, schemaAfter);
        Assert.Equal(1, parents);
        Assert.Equal(1, children);
        Assert.Equal(1, ownerRows);
        Assert.Null(context.GetService<IRelationalConnection>().CurrentTransaction);
    }

    /// <summary>A matching source proves presence, not the safety of rebuilding retained foreign-key rows.</summary>
    /// <param name="preflight">Whether the independent read-only preflight is requested.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MatchingSourceCannotEraseRetainedForeignKeyRowBlocker(bool preflight)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateForeignKeyOwnerAsync(connection, orphan: true);
        await using var context = new DroppedSourceContext(connection, _internalServices);
        var builder = CreateOperations(context, "drop");
        var schemaBefore = await ReadSchemaAsync(connection);
        var foreignKeysBefore = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_foreign_key_list('orphan_children');");

        // Act
        var report = preflight
            ? await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
                context, builder.Operations, new SafeMigrationRunOptions("sqlite-matching-source-retained-orphan"))
            : null;

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var schemaAfter = await ReadSchemaAsync(connection);
        var ownerRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM rebuild_nodes WHERE id = 1 AND extra = 7 AND derived = 8 AND source = 42;");

        var childRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM orphan_children WHERE id = 11 AND owner_id = 999;");

        var foreignKeysAfter = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_foreign_key_list('orphan_children');");

        var enforcement = await ScalarIntAsync(connection, "PRAGMA foreign_keys;");

        // Assert
        if (report is not null)
        {
            Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
            Assert.Equal(SafeMigrationObservedState.DataBlocked, report.Assessments[1].ObservedState);
            Assert.Equal("table_rebuild_foreign_key_violation", report.Assessments[1].AnalysisCode);
            Assert.Equal(SafeMigrationAction.RejectDataBlocked, report.Assessments[1].Action);
            Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        }

        Assert.Contains("foreign_key_validation_failed", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);

        Assert.Equal(schemaBefore, schemaAfter);
        Assert.Equal(1, ownerRows);
        Assert.Equal(1, childRows);
        Assert.Equal(1, foreignKeysBefore);
        Assert.Equal(1, foreignKeysAfter);
        Assert.Equal(1, enforcement);
        Assert.Null(context.GetService<IRelationalConnection>().CurrentTransaction);
    }

    /// <summary>Existing or newly added sources with valid references permit the owned rebuild and replay.</summary>
    /// <param name="sourceExists">Whether native source presence makes the first ensure a no-op.</param>
    /// <param name="safeBoundary">Whether a row-independent safe no-op starts a separate runtime segment.</param>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task MatchingSourceWithValidRetainedForeignKeyCanDropAndReplay(bool sourceExists, bool safeBoundary)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateForeignKeyOwnerAsync(connection, orphan: false, sourceExists: sourceExists);
        await using var context = new DroppedSourceContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("source", "rebuild_nodes", type: "INTEGER", nullable: true);
        if (safeBoundary)
        {
            builder.RenameIndexIfExists("absent_index", "rebuild_nodes", "absent_target");
        }

        builder.DropColumnIfExists("source", "rebuild_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-matching-source-valid-reference"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var ownerRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM rebuild_nodes WHERE id = 1 AND extra = 7 AND derived = 8;");

        var childRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM orphan_children WHERE id = 11 AND owner_id = 1;");

        var violations = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;");
        var sources = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('rebuild_nodes') WHERE name = 'source';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(sourceExists ? SafeMigrationAction.NoOp : SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^1].Action);
        Assert.Equal(1, ownerRows);
        Assert.Equal(1, childRows);
        Assert.Equal(0, violations);
        Assert.Equal(0, sources);
    }

    /// <summary>Safe runtime segment boundaries cannot erase an earlier potential source addition.</summary>
    [Fact]
    public async Task SafeIndexNoOpBoundaryCannotEraseConditionalForeignKeyRowProof()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateForeignKeyOwnerAsync(connection, orphan: true, sourceExists: false);
        await using var context = new DroppedSourceContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("source", "rebuild_nodes", type: "INTEGER", nullable: true);
        builder.RenameIndexIfExists("absent_index", "rebuild_nodes", "absent_target");
        builder.DropColumnIfExists("source", "rebuild_nodes");
        var schemaBefore = await ReadSchemaAsync(connection);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-safe-boundary-retained-orphan"));

        var schemaAfter = await ReadSchemaAsync(connection);
        var ownerRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM rebuild_nodes WHERE id = 1 AND extra = 7 AND derived = 8;");

        var childRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM orphan_children WHERE id = 11 AND owner_id = 999;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, report.Assessments[2].Action);
        Assert.Equal("table_rebuild_foreign_key_violation", report.Assessments[2].AnalysisCode);
        Assert.Equal(schemaBefore, schemaAfter);
        Assert.Equal(1, ownerRows);
        Assert.Equal(1, childRows);
    }

    /// <summary>Accepted source definitions retain independent row safety through an explicit column alias.</summary>
    /// <param name="sourceExists">Whether the first source ensure is matching or creates the nullable member.</param>
    /// <param name="orphan">Whether the independently retained incoming FK has an invalid native row.</param>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task AcceptedColumnAliasesRetainConditionalForeignKeyRowProof(bool sourceExists, bool orphan)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateForeignKeyOwnerAsync(connection, orphan, sourceExists);
        await using var context = new DroppedSourceContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("source", "rebuild_nodes", type: "INTEGER", nullable: true);
        builder.RenameColumnIfExists("source", "rebuild_nodes", "renamed_source");
        builder.DropColumnIfExists("renamed_source", "rebuild_nodes");
        var schemaBefore = await ReadSchemaAsync(connection);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-accepted-column-alias-row-proof"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var replayException = !orphan
            ? await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations))
            : null;

        var schemaAfter = await ReadSchemaAsync(connection);
        var ownerRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM rebuild_nodes WHERE id = 1 AND extra = 7 AND derived = 8;");

        var childRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM orphan_children WHERE id = 11 AND owner_id = " + (orphan ? "999" : "1") + ";");

        var sources = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('rebuild_nodes') WHERE name IN ('source', 'renamed_source');");

        var enforcement = await ScalarIntAsync(connection, "PRAGMA foreign_keys;");

        // Assert
        Assert.Equal(1, ownerRows);
        Assert.Equal(1, childRows);
        Assert.Equal(1, enforcement);
        Assert.Null(context.GetService<IRelationalConnection>().CurrentTransaction);
        if (orphan)
        {
            Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
            Assert.Equal(SafeMigrationAction.RejectDataBlocked, report.Assessments[^1].Action);
            Assert.Equal("table_rebuild_foreign_key_violation", report.Assessments[^1].AnalysisCode);
            Assert.Contains("foreign_key_validation_failed",
                Assert.IsType<InvalidOperationException>(exception).Message, StringComparison.Ordinal);

            Assert.Equal(schemaBefore, schemaAfter);
            Assert.Equal(sourceExists ? 1 : 0, sources);
        }
        else
        {
            Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
            Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^1].Action);
            Assert.Null(exception);
            Assert.Null(replayException);
            Assert.Equal(0, sources);
        }
    }

    /// <summary>Source-presence prefixes cannot waive independent row or still-unproven structure blockers.</summary>
    /// <param name="prefix">The safe, ordinary or mixed column-presence prefix.</param>
    /// <param name="expectedCode">The precise native row or existing fail-closed structural diagnosis.</param>
    [Theory]
    [InlineData("safe", "table_rebuild_foreign_key_violation")]
    [InlineData("ordinary", "table_rebuild_foreign_key_violation")]
    [InlineData("mixed", "table_rebuild_foreign_key_violation")]
    [InlineData("rename_owner", "projected_structure_state_unknown")]
    [InlineData("rename_column", "projected_structure_state_unknown")]
    public async Task SourcePresenceTransitionsPreserveIndependentRebuildBlockers(string prefix, string expectedCode)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateForeignKeyOwnerAsync(connection, orphan: true, sourceExists: prefix == "rename_column");
        await using DbContext context = prefix == "rename_owner"
            ? new RenamedOwnerContext(connection, _internalServices)
            : new DroppedSourceContext(connection, _internalServices);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (prefix == "rename_column")
        {
            builder.RenameColumnIfExists("source", "rebuild_nodes", "renamed_source");
        }
        else if (prefix == "ordinary")
        {
            builder.AddColumn<int>("source", "rebuild_nodes", type: "INTEGER", nullable: true);
        }
        else
        {
            builder.AddColumnIfNotExists<int>("source", "rebuild_nodes", type: "INTEGER", nullable: true);
        }

        if (prefix == "mixed")
        {
            builder.AddColumnIfNotExists<int>("metadata", "parent_nodes", type: "INTEGER", nullable: true);
        }

        if (prefix == "rename_owner")
        {
            builder.RenameTableIfExists("rebuild_nodes", newName: "renamed_nodes");
        }

        builder.DropColumnIfExists(prefix == "rename_column" ? "renamed_source" : "source",
            prefix == "rename_owner" ? "renamed_nodes" : "rebuild_nodes");
        var schemaBefore = await ReadSchemaAsync(connection);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-missing-source-retained-orphan"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var schemaAfter = await ReadSchemaAsync(connection);
        var ownerRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM rebuild_nodes WHERE id = 1 AND extra = 7 AND derived = 8;");

        var childRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM orphan_children WHERE id = 11 AND owner_id = 999;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        var rowBlocked = expectedCode == "table_rebuild_foreign_key_violation";
        Assert.Equal(rowBlocked ? SafeMigrationAction.RejectDataBlocked : SafeMigrationAction.RejectPrerequisiteMissing,
            report.Assessments[^1].Action);

        Assert.Equal(expectedCode, report.Assessments[^1].AnalysisCode);
        Assert.Contains(rowBlocked ? "foreign_key_validation_failed" : "prerequisite_missing",
            Assert.IsType<InvalidOperationException>(exception).Message, StringComparison.Ordinal);

        Assert.Equal(schemaBefore, schemaAfter);
        Assert.Equal(1, ownerRows);
        Assert.Equal(1, childRows);
    }

    /// <summary>A genuine absent DROP performs no rebuild and cannot charge unrelated native orphan rows.</summary>
    /// <param name="synchronous">Whether execution uses the synchronous migration-command entry point.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingSourceNoOpDoesNotDemandUnusedForeignKeyRowProof(bool synchronous)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateForeignKeyOwnerAsync(connection, orphan: true, sourceExists: false);
        await using var context = new DroppedSourceContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("source", "rebuild_nodes");
        var schemaBefore = await ReadSchemaAsync(connection);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-missing-source-no-rebuild"));

        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);
        var exception = synchronous
            ? Record.Exception(() => ExecuteCommands(context, commands))
            : await Record.ExceptionAsync(() => ExecuteCommandsAsync(context, commands));

        var schemaAfter = await ReadSchemaAsync(connection);
        var ownerRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM rebuild_nodes WHERE id = 1 AND extra = 7 AND derived = 8;");

        var childRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM orphan_children WHERE id = 11 AND owner_id = 999;");

        var enforcement = await ScalarIntAsync(connection, "PRAGMA foreign_keys;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(report.Assessments).Action);
        Assert.Null(exception);
        Assert.Equal(schemaBefore, schemaAfter);
        Assert.Equal(1, ownerRows);
        Assert.Equal(1, childRows);
        Assert.Equal(1, enforcement);
        Assert.Null(context.GetService<IRelationalConnection>().CurrentTransaction);
    }

    /// <summary>A rejected source addition cannot turn a genuinely absent DROP into a row-dependent rebuild.</summary>
    [Fact]
    public async Task RejectedSourceAdditionCannotAuthorizeConditionalRowProof()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateForeignKeyOwnerAsync(connection, orphan: true, sourceExists: false);
        await using var context = new DroppedSourceContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("source", "rebuild_nodes", type: "INTEGER", nullable: false);
        builder.DropColumnIfExists("source", "rebuild_nodes");
        var schemaBefore = await ReadSchemaAsync(connection);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-rejected-source-retained-orphan"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var schemaAfter = await ReadSchemaAsync(connection);
        var ownerRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM rebuild_nodes WHERE id = 1 AND extra = 7 AND derived = 8;");

        var childRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM orphan_children WHERE id = 11 AND owner_id = 999;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
        Assert.Contains("data_blocked", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);

        Assert.Equal(schemaBefore, schemaAfter);
        Assert.Equal(1, ownerRows);
        Assert.Equal(1, childRows);
    }

    /// <summary>Zero provider row counts cannot make real DDL bypass final relationship validation.</summary>
    /// <param name="synchronous">Whether execution uses the synchronous migration-command entry point.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutedZeroAffectedRebuildStillValidatesForeignKeys(bool synchronous)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateForeignKeyOwnerAsync(connection, orphan: true);
        await using var context = new DroppedSourceContext(connection, _internalServices);
        var builder = CreateOperations(context, "drop");
        var baseline = new ZeroAffectedBaseline(context.GetService<ISqliteSafeMigrationsBaselineGenerator>(), context);
        var commands = CreateGenerator(context, baseline).Generate(builder.Operations, context.Model);
        var schemaBefore = await ReadSchemaAsync(connection);

        // Act
        var exception = synchronous
            ? Record.Exception(() => ExecuteCommands(context, commands))
            : await Record.ExceptionAsync(() => ExecuteCommandsAsync(context, commands));

        var schemaAfter = await ReadSchemaAsync(connection);
        var ownerRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM rebuild_nodes WHERE id = 1 AND extra = 7 AND derived = 8 AND source = 42;");

        var childRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM orphan_children WHERE id = 11 AND owner_id = 999;");

        var enforcement = await ScalarIntAsync(connection, "PRAGMA foreign_keys;");

        // Assert
        Assert.True(baseline.CommandsExecuted > 0);
        Assert.Contains("foreign_key_validation_failed", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);

        Assert.Equal(schemaBefore, schemaAfter);
        Assert.Equal(1, ownerRows);
        Assert.Equal(1, childRows);
        Assert.Equal(1, enforcement);
        Assert.Null(context.GetService<IRelationalConnection>().CurrentTransaction);
    }

    /// <summary>
    /// An explicitly removed relationship cannot leave a stale orphan refusal on the later owner rebuild.
    /// </summary>
    [Fact]
    public async Task ExplicitForeignKeyRemovalDischargesTheRetainedRowBlocker()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateForeignKeyOwnerAsync(connection, orphan: true);
        await using var context = new RemovedForeignKeyContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_orphan_owner", "orphan_children");
        builder.AddColumnIfNotExists<int>("source", "rebuild_nodes", type: "INTEGER", nullable: true);
        builder.DropColumnIfExists("source", "rebuild_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-explicit-foreign-key-removal"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var ownerRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM rebuild_nodes WHERE id = 1 AND extra = 7 AND derived = 8;");

        var childRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM orphan_children WHERE id = 11 AND owner_id = 999;");

        var foreignKeys = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_foreign_key_list('orphan_children');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(1, ownerRows);
        Assert.Equal(1, childRows);
        Assert.Equal(0, foreignKeys);
    }

    /// <summary>
    /// Explicitly replacing both relationship and owner lifetimes cannot inherit removed native row evidence.
    /// </summary>
    [Fact]
    public async Task RecreatedOwnerDoesNotInheritRemovedForeignKeyRows()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateForeignKeyOwnerAsync(connection, orphan: true);
        await using var context = new DroppedSourceContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("orphan_children");
        builder.DropTableIfExists("rebuild_nodes");
        builder.CreateTableIfNotExists("rebuild_nodes", table => new
        {
            id = table.Column<int>(type: "INTEGER", nullable: false),
            extra = table.Column<int>(type: "INTEGER", nullable: false),
            derived = table.Column<int>(type: "INTEGER", nullable: true,
                computedColumnSql: "extra + 1", stored: false),
            source = table.Column<int>(type: "INTEGER", nullable: true),
        }, constraints: table => table.PrimaryKey("pk_rebuild_nodes", value => value.id),
            mode: SafeMigrationTableMode.ConvergenceContainer);

        builder.DropColumnIfExists("source", "rebuild_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-recreated-foreign-key-owner"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var ownerRows = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM rebuild_nodes;");
        var childOwners = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM main.sqlite_schema WHERE name = 'orphan_children';");

        var sources = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('rebuild_nodes') WHERE name = 'source';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(0, ownerRows);
        Assert.Equal(0, childOwners);
        Assert.Equal(0, sources);
    }

    /// <summary>Unrelated metadata changes cannot turn unchanged orphan rows into a safe owner rebuild.</summary>
    [Fact]
    public async Task UnrelatedColumnAdditionPreservesTheRetainedForeignKeyRowBlocker()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateForeignKeyOwnerAsync(connection, orphan: true);
        await using var context = new DroppedSourceContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("metadata", "parent_nodes", type: "INTEGER", nullable: true);
        builder.AddColumnIfNotExists<int>("source", "rebuild_nodes", type: "INTEGER", nullable: true);
        builder.DropColumnIfExists("source", "rebuild_nodes");
        var schemaBefore = await ReadSchemaAsync(connection);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-unrelated-column-retained-orphan"));

        var schemaAfter = await ReadSchemaAsync(connection);
        var ownerRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM rebuild_nodes WHERE id = 1 AND extra = 7 AND derived = 8 AND source = 42;");

        var childRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM orphan_children WHERE id = 11 AND owner_id = 999;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, report.Assessments[2].Action);
        Assert.Equal("table_rebuild_foreign_key_violation", report.Assessments[2].AnalysisCode);
        Assert.Equal(schemaBefore, schemaAfter);
        Assert.Equal(1, ownerRows);
        Assert.Equal(1, childRows);
    }

    /// <summary>
    /// Typed writes expire native row evidence without pretending their intended repair already succeeded.
    /// </summary>
    /// <param name="removeOrphan">Whether the typed DELETE actually removes the orphan or affects no rows.</param>
    /// <param name="confirmSource">Whether an explicit matching source confirmation precedes the DROP.</param>
    /// <param name="sourceExists">Whether the source exists before the optional earlier ADD.</param>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public async Task TypedDeleteRequiresFreshRuntimeForeignKeyValidation(
        bool removeOrphan,
        bool confirmSource,
        bool sourceExists
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateForeignKeyOwnerAsync(connection, orphan: true, sourceExists: sourceExists);
        await using var context = new DroppedSourceContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (!sourceExists)
        {
            builder.AddColumnIfNotExists<int>("source", "rebuild_nodes", type: "INTEGER", nullable: true);
        }

        builder.DeleteData("orphan_children", "id", "INTEGER", removeOrphan ? 11 : 123);
        if (confirmSource)
        {
            builder.AddColumnIfNotExists<int>("source", "rebuild_nodes", type: "INTEGER", nullable: true);
        }

        builder.DropColumnIfExists("source", "rebuild_nodes");
        var schemaBefore = await ReadSchemaAsync(connection);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-typed-delete-retained-orphan"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var schemaAfter = await ReadSchemaAsync(connection);
        var ownerRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM rebuild_nodes WHERE id = 1 AND extra = 7 AND derived = 8;");

        var childRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM orphan_children WHERE id = 11 AND owner_id = 999;");

        var sources = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('rebuild_nodes') WHERE name = 'source';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[^1].Action);
        Assert.Equal("projected_data_state_unknown", report.Assessments[^1].AnalysisCode);
        Assert.Equal(sourceExists ? 0 : 1,
            Assert.IsType<SafeMigrationDeferredOrigin>(report.Assessments[^1].DeferredOrigin).OperationOrdinal);
        Assert.Equal(1, ownerRows);
        Assert.Equal(removeOrphan ? 0 : 1, childRows);
        if (removeOrphan)
        {
            Assert.Null(exception);
            Assert.Equal(0, sources);
        }
        else
        {
            Assert.Contains("foreign_key_validation_failed",
                Assert.IsType<InvalidOperationException>(exception).Message, StringComparison.Ordinal);

            Assert.Equal(1, sources);
            if (sourceExists)
            {
                Assert.Equal(schemaBefore, schemaAfter);
            }

        }
    }

    /// <summary>
    /// Creates an independently retained incoming relationship with either valid or orphaned native rows.
    /// </summary>
    private static async Task CreateForeignKeyOwnerAsync(
        SqliteConnection connection,
        bool orphan,
        bool sourceExists = true
    )
    {
        await CreateNativeOwnerAsync(connection, "none");
        var source = sourceExists
            ? "ALTER TABLE rebuild_nodes ADD COLUMN source INTEGER NULL; UPDATE rebuild_nodes SET source = 42; "
            : string.Empty;

        await ExecuteSqlAsync(connection,
            source + "CREATE TABLE orphan_children (id INTEGER NOT NULL CONSTRAINT pk_orphan_children PRIMARY KEY, "
            + "owner_id INTEGER NULL, CONSTRAINT fk_orphan_owner FOREIGN KEY (owner_id) REFERENCES rebuild_nodes(id)); "
            + "PRAGMA foreign_keys = OFF; INSERT INTO orphan_children VALUES (11, "
            + (orphan ? "999" : "1") + "); PRAGMA foreign_keys = ON;");
    }

    /// <summary>Runs the production wrapper with a deliberately adversarial native baseline.</summary>
    private static SqliteSafeMigrationsSqlGenerator CreateGenerator(
        DbContext context,
        ISqliteSafeMigrationsBaselineGenerator baseline
    ) => new(baseline, context.GetService<ISafeMigrationProviderAnalyzer>(),
        context.GetService<SqliteSafeMigrationExecutionState>(),
        context.GetService<MigrationsSqlGeneratorDependencies>());

    /// <summary>Uses real migration-command execution within the wrapper's owned transaction.</summary>
    private static async Task ExecuteCommandsAsync(DbContext context, IReadOnlyList<MigrationCommand> commands)
    {
        var connection = context.GetService<IRelationalConnection>();
        foreach (var command in commands)
        {
            _ = await command.ExecuteNonQueryAsync(connection);
        }
    }

    /// <summary>Uses the same real migration commands at their synchronous transaction boundary.</summary>
    private static void ExecuteCommands(DbContext context, IReadOnlyList<MigrationCommand> commands)
    {
        var connection = context.GetService<IRelationalConnection>();
        foreach (var command in commands)
        {
            _ = command.ExecuteNonQuery(connection);
        }
    }

    /// <summary>Builds only the reviewed source transition, without model-generated migration operations.</summary>
    private static MigrationBuilder CreateOperations(DbContext context, string operation)
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("source", "rebuild_nodes", type: "INTEGER", nullable: true,
            defaultValue: operation == "alter" ? 1 : null);

        switch (operation)
        {
            case "drop":
                builder.DropColumnIfExists("source", "rebuild_nodes");
                break;
            case "alter":
                builder.AlterColumnIfDifferent("rebuild_nodes",
                    new ExpectedColumnDefinition("source", typeof(int), true, storeType: "INTEGER",
                        defaultValue: SafeMigrationDefaultValue.Literal(2)),
                    new ExpectedColumnDefinition("source", typeof(int), true, storeType: "INTEGER",
                        defaultValue: SafeMigrationDefaultValue.Literal(1)),
                    SafeMigrationPolicy.RepairIfSafe);
                break;
            default:
                builder.AddCheckConstraintIfNotExists("ck_source_null", "rebuild_nodes", "source IS NULL");
                break;
        }

        return builder;
    }

    /// <summary>Uses distinct context types so EF's cache never mixes the three target models.</summary>
    private DbContext CreateTargetContext(DbConnection connection, string operation) => operation switch
    {
        "drop" => new DroppedSourceContext(connection, _internalServices),
        "alter" => new AlteredSourceContext(connection, _internalServices),
        _ => new CheckedSourceContext(connection, _internalServices),
    };

    /// <summary>Creates native artifacts independently of the target model used by EF rebuild generation.</summary>
    private static Task CreateNativeOwnerAsync(SqliteConnection connection, string artifact)
    {
        var suffix = artifact switch
        {
            "column" => ", retained_payload TEXT NULL",
            "check" => ", CONSTRAINT retained_check CHECK (extra > 0)",
            "foreign_key" => ", CONSTRAINT retained_fk FOREIGN KEY (extra) REFERENCES parent_nodes (id)",
            _ => string.Empty,
        };

        var after = artifact switch
        {
            "trigger" => "CREATE TRIGGER retained_trigger AFTER UPDATE ON rebuild_nodes BEGIN SELECT 1; END;",
            "index" => "CREATE INDEX retained_index ON rebuild_nodes (extra);",
            _ => string.Empty,
        };

        return ExecuteSqlAsync(connection,
            "CREATE TABLE parent_nodes (id INTEGER NOT NULL PRIMARY KEY); INSERT INTO parent_nodes VALUES (7); "
            + "CREATE TABLE rebuild_nodes (id INTEGER NOT NULL CONSTRAINT pk_rebuild_nodes PRIMARY KEY, "
            + "extra INTEGER NOT NULL, derived INTEGER GENERATED ALWAYS AS (extra + 1) VIRTUAL"
            + suffix + "); INSERT INTO rebuild_nodes (id, extra) VALUES (1, 7); " + after);
    }

    /// <summary>Reads the exact native owner SQL to detect lost constraints or unexpected rebuilds.</summary>
    private static async Task<string> ReadSchemaAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM main.sqlite_schema WHERE type = 'table' AND name = 'rebuild_nodes';";

        return (string)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Counts actual retained artifacts rather than inferring preservation from an exception.</summary>
    private static Task<int> CountArtifactsAsync(SqliteConnection connection, string artifact) =>
        ScalarIntAsync(connection, artifact switch
        {
            "trigger" => "SELECT COUNT(*) FROM main.sqlite_schema WHERE type = 'trigger' "
                + "AND name = 'retained_trigger';",
            "index" => "SELECT COUNT(*) FROM main.sqlite_schema WHERE type = 'index' AND name = 'retained_index';",
            "check" => "SELECT COUNT(*) FROM main.sqlite_schema WHERE type = 'table' AND name = 'rebuild_nodes' "
                + "AND instr(sql, 'retained_check') > 0;",
            "foreign_key" => "SELECT COUNT(*) FROM pragma_foreign_key_list('rebuild_nodes');",
            _ => "SELECT COUNT(*) FROM pragma_table_xinfo('rebuild_nodes') WHERE name = 'retained_payload';",
        });

    /// <summary>Names the exact independent rebuild guard expected for each native artifact.</summary>
    private static string ArtifactCode(string artifact) => artifact switch
    {
        "trigger" => "table_rebuild_trigger",
        "index" => "table_rebuild_unmanaged_index",
        "check" => "table_rebuild_unmanaged_check_constraint",
        "foreign_key" => "table_rebuild_unmanaged_foreign_key",
        _ => "table_rebuild_unmanaged_column",
    };

    /// <summary>Shares a fixed model family without turning model wishes into native ownership.</summary>
    private abstract class RebuildContext(DbConnection connection, IServiceProvider services) : DbContext
    {
        /// <summary>Gets the actual target owner's physical name after any explicit rename.</summary>
        protected virtual string TableName => "rebuild_nodes";

        /// <summary>Gets whether the actual target retains the newly added source.</summary>
        protected virtual bool IncludeSource => false;

        /// <summary>Gets whether the actual target contains the explicitly authored CHECK.</summary>
        protected virtual bool IncludeCheck => false;

        /// <summary>Gets the target default after a default-only ALTER.</summary>
        protected virtual int? SourceDefault => null;

        /// <inheritdoc />
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite(connection);
            optionsBuilder.UseSqliteSafeMigrations();
            optionsBuilder.UseInternalServiceProvider(services);
        }

        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Row>(entity =>
            {
                entity.ToTable(TableName, table =>
                {
                    if (IncludeCheck)
                    {
                        table.HasCheckConstraint("ck_source_null", "source IS NULL");
                    }
                });

                entity.HasKey(value => value.Id).HasName("pk_rebuild_nodes");
                entity.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
                entity.Property(value => value.Extra).HasColumnName("extra");
                entity.Property(value => value.Derived).HasColumnName("derived")
                    .HasComputedColumnSql("extra + 1", stored: false);

                if (IncludeSource)
                {
                    var source = entity.Property(value => value.Source).HasColumnName("source");
                    if (SourceDefault is { } value)
                    {
                        source.HasDefaultValue(value);
                    }
                }
                else
                {
                    entity.Ignore(value => value.Source);
                }
            });
        }

        private sealed class Row
        {
            public int Id { get; set; }

            public int Extra { get; set; }

            public int? Derived { get; set; }

            public int? Source { get; set; }
        }
    }

    /// <summary>Models the actual target after the temporary source is removed.</summary>
    private sealed class DroppedSourceContext(DbConnection connection, IServiceProvider services)
        : RebuildContext(connection, services);

    /// <summary>Models the same physical owner after its explicitly authored rename.</summary>
    private sealed class RenamedOwnerContext(DbConnection connection, IServiceProvider services)
        : RebuildContext(connection, services)
    {
        /// <inheritdoc />
        protected override string TableName => "renamed_nodes";
    }

    /// <summary>Owns the explicitly rebuilt child after its authored relationship removal.</summary>
    private sealed class RemovedForeignKeyContext(DbConnection connection, IServiceProvider services)
        : RebuildContext(connection, services)
    {
        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<RemovedForeignKeyRow>(entity =>
            {
                entity.ToTable("orphan_children");
                entity.HasKey(value => value.Id).HasName("pk_orphan_children");
                entity.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
                entity.Property(value => value.OwnerId).HasColumnName("owner_id");
            });
        }

        private sealed class RemovedForeignKeyRow
        {
            public int Id { get; set; }

            public int? OwnerId { get; set; }
        }
    }

    /// <summary>Models the actual default-only ALTER target.</summary>
    private sealed class AlteredSourceContext(DbConnection connection, IServiceProvider services)
        : RebuildContext(connection, services)
    {
        /// <inheritdoc />
        protected override bool IncludeSource => true;

        /// <inheritdoc />
        protected override int? SourceDefault => 2;
    }

    /// <summary>Models a requested CHECK without claiming its data prerequisites are proven.</summary>
    private sealed class CheckedSourceContext(DbConnection connection, IServiceProvider services)
        : RebuildContext(connection, services)
    {
        /// <inheritdoc />
        protected override bool IncludeSource => true;

        /// <inheritdoc />
        protected override bool IncludeCheck => true;
    }

    /// <summary>
    /// Injects one native integrity violation after real baseline DDL for a non-vacuous safety probe.
    /// </summary>
    private sealed class MutatingForeignKeyBaseline(ISqliteSafeMigrationsBaselineGenerator inner, string sql)
        : ISqliteSafeMigrationsBaselineGenerator
    {
        /// <summary>Gets whether baseline generation emitted the adversarial native commands.</summary>
        public bool MutationGenerated { get; private set; }

        /// <inheritdoc />
        public IReadOnlyList<MigrationCommand> Generate(
            IReadOnlyList<MigrationOperation> operations,
            IModel? model = null,
            MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default,
            IReadOnlyList<MigrationOperation>? guardedOperations = null
        )
        {
            var commands = inner.Generate(operations, model, options, guardedOperations);
            var mutation = inner.Generate([new SqlOperation { Sql = sql }], model, options);
            MutationGenerated = true;

            return commands.Concat(mutation).ToArray();
        }
    }

    /// <summary>Executes actual native baselines while deliberately reporting zero affected rows.</summary>
    private sealed class ZeroAffectedBaseline(ISqliteSafeMigrationsBaselineGenerator inner, DbContext context)
        : ISqliteSafeMigrationsBaselineGenerator
    {
        /// <summary>Gets how many real baseline commands executed before the relationship guard.</summary>
        public int CommandsExecuted { get; private set; }

        /// <inheritdoc />
        public IReadOnlyList<MigrationCommand> Generate(
            IReadOnlyList<MigrationOperation> operations,
            IModel? model = null,
            MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default,
            IReadOnlyList<MigrationOperation>? guardedOperations = null
        ) => inner.Generate(operations, model, options, guardedOperations)
            .Select(command => new ZeroAffectedCommand(command, context, this))
            .ToArray();

        /// <summary>Separates actual native execution from the provider's affected-row result.</summary>
        private sealed class ZeroAffectedCommand(MigrationCommand inner, DbContext context, ZeroAffectedBaseline owner)
            : MigrationCommand(context.GetService<MigrationsSqlGeneratorDependencies>().CommandBuilderFactory
                    .Create().Build(),
                context, context.GetService<MigrationsSqlGeneratorDependencies>().Logger, inner.TransactionSuppressed)
        {
            /// <inheritdoc />
            public override string CommandText => inner.CommandText;

            /// <inheritdoc />
            public override int ExecuteNonQuery(
                IRelationalConnection connection,
                IReadOnlyDictionary<string, object?>? parameterValues = null
            )
            {
                owner.CommandsExecuted++;
                _ = inner.ExecuteNonQuery(connection, parameterValues);

                return 0;
            }
        }
    }
}
