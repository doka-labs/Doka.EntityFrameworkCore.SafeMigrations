namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Checks that a safe column drop never strands a retained generated expression.</summary>
public sealed class SqliteGeneratedColumnDropTests : SqliteIntegrationTestBase,
    IClassFixture<SqliteConstraintShapeServiceFixture>
{
    private readonly IServiceProvider _internalServices;

    /// <summary>Reuses provider services while every test owns its database and catalog.</summary>
    /// <param name="services">The class-owned SQLite services.</param>
    public SqliteGeneratedColumnDropTests(SqliteConstraintShapeServiceFixture services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _internalServices = services.Provider;
    }

    /// <summary>Rejects a retained native dependency before a rebuild can execute.</summary>
    /// <param name="expression">The actual native generated expression.</param>
    /// <param name="expectedCode">The bounded dependency diagnosis.</param>
    [Theory]
    [InlineData("source + 1", "generated_column_drop_dependency")]
    [InlineData("\"SoUrCe\" + 1", "generated_column_drop_dependency")]
    [InlineData("[source] + 1", "generated_column_drop_dependency")]
    [InlineData("`source` + 1", "generated_column_drop_dependency")]
    [InlineData("ABS(source)", "generated_column_drop_dependency")]
    [InlineData("CAST(source AS INTEGER)", "generated_column_drop_dependency")]
    [InlineData("CASE WHEN extra > 0 THEN source ELSE 0 END", "generated_column_drop_dependency")]
    [InlineData("source << 1", "generated_column_drop_unproven")]
    public async Task NativeGeneratedDependencyBlocksPreflightWithoutCatalogMutation(
        string expression,
        string expectedCode
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateNativeTableAsync(connection, expression);
        await using var context = new DropContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("source", "drop_nodes");
        var schemaBefore = await ReadSchemaAsync(connection);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-generated-drop"));

        var schemaAfter = await ReadSchemaAsync(connection);
        var retainedRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM drop_nodes WHERE id = 1 AND source = 3 AND extra = 7;");

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal(expectedCode, assessment.AnalysisCode);
        Assert.Equal(schemaBefore, schemaAfter);
        Assert.Equal(1, retainedRows);
    }

    /// <summary>The runtime guard is mandatory even when callers omit optional preflight.</summary>
    /// <param name="synchronous">Whether execution uses the synchronous migration-command API.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeGeneratedDependencyBlocksRuntimeAndPreservesSchemaAndRows(bool synchronous)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateNativeTableAsync(connection, "source + 1");
        await using var context = new DropContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("source", "drop_nodes");
        var schemaBefore = await ReadSchemaAsync(connection);

        // Act
        var exception = synchronous
            ? Record.Exception(() => ExecuteOperations(context, builder.Operations))
            : await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        var schemaAfter = await ReadSchemaAsync(connection);
        var retainedRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM drop_nodes WHERE id = 1 AND source = 3 AND derived = 4;");

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("generated_column_drop_dependency", exception.Message, StringComparison.Ordinal);
        Assert.Equal(schemaBefore, schemaAfter);
        Assert.Equal(1, retainedRows);
    }

    /// <summary>Allows explicit dependency removal and unrelated or already-absent member drops.</summary>
    /// <param name="scenario">The exact accepted drop ordering.</param>
    [Theory]
    [InlineData("unrelated")]
    [InlineData("generated")]
    [InlineData("dependent_first")]
    [InlineData("missing")]
    public async Task IndependentAndExplicitDependentFirstDropsExecuteAndReplay(string scenario)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateNativeTableAsync(connection, "source + 1");
        await using var context = scenario switch
        {
            "unrelated" => new ExtraDropContext(connection, _internalServices),
            "generated" => new DerivedDropContext(connection, _internalServices),
            "dependent_first" => new DependentFirstDropContext(connection, _internalServices),
            _ => new DropContext(connection, _internalServices),
        };

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (scenario == "dependent_first")
        {
            builder.DropColumnIfExists("derived", "drop_nodes");
        }

        var column = scenario switch
        {
            "unrelated" => "extra",
            "generated" => "derived",
            "dependent_first" => "source",
            _ => "missing",
        };

        builder.DropColumnIfExists(column, "drop_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-generated-drop-order"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var replayException = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var retainedRows = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM drop_nodes WHERE id = 1;");
        var remainingColumns = await ScalarIntAsync(connection,
            $"SELECT COUNT(*) FROM pragma_table_xinfo('drop_nodes') WHERE name = '{column}';");

        // Assert
        var diagnostic = Encoding.UTF8.GetString(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
        Assert.True(report.Status == SafeMigrationReportStatus.Ready, diagnostic);
        Assert.Null(exception);
        Assert.Null(replayException);
        Assert.Equal(1, retainedRows);
        Assert.Equal(0, remainingColumns);
    }

    /// <summary>An explicitly removed generated member need not be interpreted to remove its former source.</summary>
    [Fact]
    public async Task OpaqueGeneratedMemberCanBeExplicitlyRemovedBeforeItsSource()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateNativeTableAsync(connection, "source << 1");
        await using var context = new DependentFirstDropContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("derived", "drop_nodes");
        builder.DropColumnIfExists("source", "drop_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-opaque-dependent-first-drop"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var retainedRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM drop_nodes WHERE id = 1 AND extra = 7;");

        var removedColumns = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('drop_nodes') WHERE name IN ('source', 'derived');");

        // Assert
        var diagnostic = Encoding.UTF8.GetString(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
        Assert.True(report.Status == SafeMigrationReportStatus.Ready, diagnostic);
        Assert.All(report.Assessments, value => Assert.Equal(SafeMigrationAction.Apply, value.Action));
        Assert.Equal(1, retainedRows);
        Assert.Equal(0, removedColumns);
    }

    /// <summary>A generated member accepted earlier in the stream remains a dependency of the source drop.</summary>
    [Fact]
    public async Task NewlyAddedGeneratedDependencyBlocksSourceDropAfterAcceptedAddition()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE drop_nodes (id INTEGER NOT NULL CONSTRAINT pk_drop_nodes PRIMARY KEY, "
            + "source INTEGER NOT NULL, extra INTEGER NOT NULL); "
            + "INSERT INTO drop_nodes (id, source, extra) VALUES (1, 3, 7);");

        await using var context = new DropContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operation => operation.AddColumn<int>(
            "derived", "drop_nodes", type: "INTEGER", nullable: true,
            computedColumnSql: "source + 1", stored: false));

        builder.DropColumnIfExists("source", "drop_nodes");
        var schemaBefore = await ReadSchemaAsync(connection);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-added-generated-source-drop"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var schemaAfter = await ReadSchemaAsync(connection);
        var retainedRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM drop_nodes WHERE id = 1 AND source = 3 AND extra = 7 AND derived = 4;");

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal("generated_column_drop_dependency", report.Assessments[1].AnalysisCode);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("generated_column_drop_dependency", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("derived", schemaBefore, StringComparison.Ordinal);
        Assert.Contains("derived", schemaAfter, StringComparison.Ordinal);
        Assert.Equal(1, retainedRows);
    }

    /// <summary>Accepted source presence does not erase a dependency whose native proof lifetime expired.</summary>
    [Fact]
    public async Task AcceptedSourceCannotOverrideExpiredNativeDependencyEvidence()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateNativeTableAsync(connection, "source + 1");
        await using var context = new DropContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("drop_nodes",
            new ExpectedColumnDefinition("source", typeof(int), false, storeType: "INTEGER"),
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.AddColumnIfNotExists<int>("temporary", "drop_nodes", type: "INTEGER", nullable: true);
        builder.DropColumnIfExists("source", "drop_nodes");
        var schemaBefore = await ReadSchemaAsync(connection);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-expired-generated-drop"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var schemaAfter = await ReadSchemaAsync(connection);
        var retainedRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM drop_nodes WHERE id = 1 AND source = 3 AND derived = 4;");

        // Assert
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal("generated_column_drop_unproven", report.Assessments[2].AnalysisCode);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("generated_column_drop_unproven", exception.Message, StringComparison.Ordinal);
        Assert.Equal(schemaBefore, schemaAfter);
        Assert.Equal(1, retainedRows);
    }

    /// <summary>Read-only preflight cannot lend hypothetical removals to a later runtime invocation.</summary>
    [Fact]
    public async Task UnexecutedPreflightDropsCannotDischargeTheRuntimeDependency()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateNativeTableAsync(connection, "source + 1");
        await using var context = new DependentFirstDropContext(connection, _internalServices);
        var proposal = new MigrationBuilder(context.Database.ProviderName!);
        proposal.DropColumnIfExists("derived", "drop_nodes");
        proposal.DropColumnIfExists("source", "drop_nodes");
        var actual = new MigrationBuilder(context.Database.ProviderName!);
        actual.DropColumnIfExists("source", "drop_nodes");
        var schemaBefore = await ReadSchemaAsync(connection);

        // Act
        var proposalReport = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, proposal.Operations, new SafeMigrationRunOptions("sqlite-unexecuted-drop-proposal"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, actual.Operations));
        var schemaAfter = await ReadSchemaAsync(connection);
        var retainedRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM drop_nodes WHERE id = 1 AND source = 3 AND derived = 4;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, proposalReport.Status);
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("generated_column_drop_dependency", exception.Message, StringComparison.Ordinal);
        Assert.Equal(schemaBefore, schemaAfter);
        Assert.Equal(1, retainedRows);
    }

    /// <summary>Recreated owners cannot inherit dependencies from the explicitly removed physical table.</summary>
    [Fact]
    public async Task RecreatedOwnerUsesOnlyItsOwnAcceptedGeneratedSet()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateNativeTableAsync(connection, "source + 1");
        await using var context = new DependentFirstDropContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("drop_nodes");
        builder.CreateTableIfNotExists("drop_nodes", table => new
        {
            id = table.Column<int>(type: "INTEGER", nullable: false),
            source = table.Column<int>(type: "INTEGER", nullable: false),
            extra = table.Column<int>(type: "INTEGER", nullable: false),
        }, constraints: table => table.PrimaryKey("pk_drop_nodes", value => value.id),
            mode: SafeMigrationTableMode.ConvergenceContainer);

        builder.DropColumnIfExists("source", "drop_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-recreated-generated-owner"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var remainingColumns = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('drop_nodes') WHERE name IN ('id', 'extra');");

        var removedColumns = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('drop_nodes') WHERE name IN ('source', 'derived');");

        // Assert
        var diagnostic = Encoding.UTF8.GetString(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
        Assert.True(report.Status == SafeMigrationReportStatus.Ready, diagnostic);
        Assert.Equal(2, remainingColumns);
        Assert.Equal(0, removedColumns);
    }

    /// <summary>Absence is a no-op even when other retained generated SQL lies outside the bounded grammar.</summary>
    [Fact]
    public async Task MissingSourceDoesNotRequireProofOfOtherOpaqueExpressions()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateNativeTableAsync(connection, "source << 1");
        await using var context = new DropContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("missing", "drop_nodes");
        var schemaBefore = await ReadSchemaAsync(connection);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-generated-missing-drop"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var schemaAfter = await ReadSchemaAsync(connection);
        var retainedRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM drop_nodes WHERE id = 1 AND source = 3 AND derived = 6;");

        // Assert
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(report.Assessments).Action);
        Assert.Equal(schemaBefore, schemaAfter);
        Assert.Equal(1, retainedRows);
    }

    /// <summary>Unparsed retained SQL cannot certify that an otherwise unrelated member is unused.</summary>
    [Fact]
    public async Task OpaqueRetainedExpressionCannotAuthorizeAnUnrelatedDrop()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateNativeTableAsync(connection, "source << 1");
        await using var context = new ExtraDropContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("extra", "drop_nodes");
        var schemaBefore = await ReadSchemaAsync(connection);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-opaque-unrelated-drop"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var schemaAfter = await ReadSchemaAsync(connection);
        var retainedRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM drop_nodes WHERE id = 1 AND extra = 7 AND derived = 6;");

        // Assert
        Assert.Equal("generated_column_drop_unproven", Assert.Single(report.Assessments).AnalysisCode);
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("generated_column_drop_unproven", exception.Message, StringComparison.Ordinal);
        Assert.Equal(schemaBefore, schemaAfter);
        Assert.Equal(1, retainedRows);
    }

    /// <summary>Every physical generated member participates, not just the model's chosen candidates.</summary>
    [Fact]
    public async Task UnmappedNativeGeneratedDependencyIsStillRejected()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateNativeTableAsync(connection, "source + 1");
        await ExecuteSqlAsync(connection,
            "ALTER TABLE drop_nodes ADD extra_derived INTEGER GENERATED ALWAYS AS (extra + 1) VIRTUAL;");

        await using var context = new ExtraDropContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("extra", "drop_nodes");
        var schemaBefore = await ReadSchemaAsync(connection);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-unmapped-generated-drop"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var schemaAfter = await ReadSchemaAsync(connection);
        var retainedRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM drop_nodes WHERE id = 1 AND extra = 7 AND extra_derived = 8;");

        // Assert
        Assert.Equal("generated_column_drop_dependency", Assert.Single(report.Assessments).AnalysisCode);
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("generated_column_drop_dependency", exception.Message, StringComparison.Ordinal);
        Assert.Equal(schemaBefore, schemaAfter);
        Assert.Equal(1, retainedRows);
    }

    /// <summary>A proposed dependent removal is not accepted when an independent rebuild guard rejects it.</summary>
    [Fact]
    public async Task RejectedDependentDropCannotDischargeTheRetainedDependency()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateNativeTableAsync(connection, "source + 1");
        await ExecuteSqlAsync(connection,
            "CREATE TRIGGER retained_trigger AFTER UPDATE ON drop_nodes BEGIN SELECT 1; END;");

        await using var context = new DependentFirstDropContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("derived", "drop_nodes");
        builder.DropColumnIfExists("source", "drop_nodes");
        var schemaBefore = await ReadSchemaAsync(connection);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-rejected-dependent-drop"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var schemaAfter = await ReadSchemaAsync(connection);
        var retainedRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM drop_nodes WHERE id = 1 AND source = 3 AND derived = 4;");

        var triggers = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM main.sqlite_schema WHERE type = 'trigger' AND name = 'retained_trigger';");

        // Assert
        Assert.Equal("table_rebuild_trigger", report.Assessments[0].AnalysisCode);
        Assert.Equal("generated_column_drop_dependency", report.Assessments[1].AnalysisCode);
        Assert.All(report.Assessments, value => Assert.Equal(SafeMigrationAction.RejectUnsupported, value.Action));
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("table_rebuild_trigger", exception.Message, StringComparison.Ordinal);
        Assert.Equal(schemaBefore, schemaAfter);
        Assert.Equal(1, retainedRows);
        Assert.Equal(1, triggers);
    }

    /// <summary>A same-name column from another owner is not a generated dependency of this table.</summary>
    [Fact]
    public async Task OtherOwnerGeneratedDependencyCannotBlockAnUnrelatedDrop()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateNativeTableAsync(connection, "source + 1");
        await ExecuteSqlAsync(connection,
            "CREATE TABLE other_nodes (extra INTEGER, derived INTEGER GENERATED ALWAYS AS (extra + 1)); "
            + "INSERT INTO other_nodes (extra) VALUES (11);");

        await using var context = new ExtraDropContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("extra", "drop_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-generated-other-owner"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var retainedRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM drop_nodes WHERE id = 1 AND source = 3 AND derived = 4;");

        var otherRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM other_nodes WHERE extra = 11 AND derived = 12;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(1, retainedRows);
        Assert.Equal(1, otherRows);
    }

    /// <summary>Literal contents and CAST type names are not mistaken for source-column identifier roles.</summary>
    /// <param name="cast">Whether the spelling appears in a CAST type instead of a literal.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonColumnTokenRolesDoNotInventGeneratedDependencies(bool cast)
    {
        // Arrange
        var expression = cast ? "CAST(source AS extra)" : "length('extra') + source";
        await using var connection = await OpenConnectionAsync();
        await CreateNativeTableAsync(connection, expression);
        await using var context = cast
            ? (DropContext)new CastRoleDropContext(connection, _internalServices)
            : new LiteralRoleDropContext(connection, _internalServices);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("extra", "drop_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-generated-drop-token-role"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var retainedRows = await ScalarIntAsync(connection,
            $"SELECT COUNT(*) FROM drop_nodes WHERE id = 1 AND source = 3 AND derived = {(cast ? 3 : 8)};");

        var removedColumns = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('drop_nodes') WHERE name = 'extra';");

        // Assert
        var diagnostic = Encoding.UTF8.GetString(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
        Assert.True(report.Status == SafeMigrationReportStatus.Ready, diagnostic);
        Assert.Equal(1, retainedRows);
        Assert.Equal(0, removedColumns);
    }

    /// <summary>
    /// One structural window reuses native index facts without overwriting accepted index identities.
    /// </summary>
    /// <param name="synchronous">Whether execution uses the synchronous migration-command API.</param>
    /// <param name="different">Whether a later same-name index requests different keys.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DirectDropAndIndexShareRuntimeProofWithoutAComputedCommandBoundary(
        bool synchronous,
        bool different
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateNativeTableAsync(connection, "source + 1");
        await using var context = new ExtraDropIndexContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("extra", "drop_nodes");
        builder.CreateIndexIfNotExists("ix_drop_source", "drop_nodes", ["source"]);
        if (different)
        {
            builder.CreateIndexIfNotExists("ix_drop_source", "drop_nodes", ["id"]);
        }

        var schemaBefore = await ReadSchemaAsync(connection);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-direct-drop-index"));

        var exception = synchronous
            ? Record.Exception(() => ExecuteOperations(context, builder.Operations))
            : await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        var replayException = synchronous
            ? Record.Exception(() => ExecuteOperations(context, builder.Operations))
            : await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        var schemaAfter = await ReadSchemaAsync(connection);
        var retainedRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM drop_nodes WHERE id = 1 AND source = 3 AND derived = 4;");

        var indexes = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_index_list('drop_nodes') WHERE name = 'ix_drop_source';");

        // Assert
        var diagnostic = Encoding.UTF8.GetString(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
        Assert.True(report.Assessments[0].Action == SafeMigrationAction.Apply, diagnostic);
        Assert.True(report.Assessments[1].Action == SafeMigrationAction.Apply, diagnostic);
        Assert.Equal(1, retainedRows);
        if (different)
        {
            Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
            Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[2].Action);
            Assert.IsType<InvalidOperationException>(exception);
            Assert.IsType<InvalidOperationException>(replayException);
            Assert.Contains("decision=different_reject", exception.Message, StringComparison.Ordinal);
            Assert.Equal(schemaBefore, schemaAfter);
            Assert.Equal(0, indexes);
        }
        else
        {
            Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
            Assert.Null(exception);
            Assert.Null(replayException);
            Assert.NotEqual(schemaBefore, schemaAfter);
            Assert.Equal(1, indexes);
        }
    }

    /// <summary>Creates one physical catalog independently of migration generation or model wishes.</summary>
    private static Task CreateNativeTableAsync(SqliteConnection connection, string expression) =>
        ExecuteSqlAsync(connection,
            "CREATE TABLE drop_nodes (id INTEGER NOT NULL CONSTRAINT pk_drop_nodes PRIMARY KEY, "
            + "source INTEGER NOT NULL, extra INTEGER NOT NULL, "
            + $"derived INTEGER GENERATED ALWAYS AS ({expression}) VIRTUAL); "
            + "INSERT INTO drop_nodes (id, source, extra) VALUES (1, 3, 7);");

    /// <summary>Captures the complete native table SQL so refusal cannot silently rebuild the owner.</summary>
    private static async Task<string> ReadSchemaAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM main.sqlite_schema WHERE type = 'table' AND name = 'drop_nodes';";

        return (string)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Exercises the synchronous execution boundary without synchronously blocking an async method.</summary>
    private static void ExecuteOperations(DbContext context, IReadOnlyList<MigrationOperation> operations)
    {
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(operations, context.Model);
        var connection = context.GetService<IRelationalConnection>();
        foreach (var command in commands)
        {
            _ = command.ExecuteNonQuery(connection);
        }
    }

    /// <summary>Supplies exact rebuild ownership without treating the model as dependency evidence.</summary>
    private class DropContext(DbConnection connection, IServiceProvider internalServices) : DbContext
    {
        /// <summary>Gets the target-model members deliberately removed by this fixture's migration.</summary>
        protected virtual int RemovedColumns => 0;

        /// <summary>Gets the exact retained generated source captured by the target model.</summary>
        protected virtual string GeneratedSql => "source + 1";

        /// <summary>Gets whether this target contains the explicitly added standalone source index.</summary>
        protected virtual bool IncludeSourceIndex => false;

        /// <inheritdoc />
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite(connection);
            optionsBuilder.UseSqliteSafeMigrations();
            optionsBuilder.UseInternalServiceProvider(internalServices);
        }

        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Node>(entity =>
            {
                entity.ToTable("drop_nodes");
                entity.HasKey(value => value.Id).HasName("pk_drop_nodes");
                entity.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
                if ((RemovedColumns & 1) != 0)
                {
                    entity.Ignore(value => value.Extra);
                }
                else
                {
                    entity.Property(value => value.Extra).HasColumnName("extra");
                }

                if ((RemovedColumns & 2) != 0)
                {
                    entity.Ignore(value => value.Derived);
                }
                else
                {
                    entity.Property(value => value.Derived).HasColumnName("derived")
                        .HasComputedColumnSql(GeneratedSql, stored: false);
                }

                if ((RemovedColumns & 4) != 0)
                {
                    entity.Ignore(value => value.Source);
                }
                else
                {
                    entity.Property(value => value.Source).HasColumnName("source");
                }

                if (IncludeSourceIndex)
                {
                    entity.HasIndex(value => value.Source).HasDatabaseName("ix_drop_source");
                }
            });
        }

        private sealed class Node
        {
            public int Id { get; set; }

            public int Source { get; set; }

            public int Extra { get; set; }

            public int? Derived { get; set; }
        }
    }

    /// <summary>Models the real target after an unrelated member is removed.</summary>
    private sealed class ExtraDropContext(DbConnection connection, IServiceProvider internalServices)
        : DropContext(connection, internalServices)
    {
        /// <inheritdoc />
        protected override int RemovedColumns => 1;
    }

    /// <summary>Models the real target after the generated member itself is removed.</summary>
    private sealed class DerivedDropContext(DbConnection connection, IServiceProvider internalServices)
        : DropContext(connection, internalServices)
    {
        /// <inheritdoc />
        protected override int RemovedColumns => 2;
    }

    /// <summary>Models the real target after explicit dependent-first source removal.</summary>
    private sealed class DependentFirstDropContext(DbConnection connection, IServiceProvider internalServices)
        : DropContext(connection, internalServices)
    {
        /// <inheritdoc />
        protected override int RemovedColumns => 6;
    }

    /// <summary>Retains a literal containing the dropped member's spelling.</summary>
    private sealed class LiteralRoleDropContext(DbConnection connection, IServiceProvider internalServices)
        : DropContext(connection, internalServices)
    {
        /// <inheritdoc />
        protected override int RemovedColumns => 1;

        /// <inheritdoc />
        protected override string GeneratedSql => "length('extra') + source";
    }

    /// <summary>Retains a CAST whose type spelling is also the dropped member's name.</summary>
    private sealed class CastRoleDropContext(DbConnection connection, IServiceProvider internalServices)
        : DropContext(connection, internalServices)
    {
        /// <inheritdoc />
        protected override int RemovedColumns => 1;

        /// <inheritdoc />
        protected override string GeneratedSql => "CAST(source AS extra)";
    }

    /// <summary>Models both operations in the direct DROP/index structural window.</summary>
    private sealed class ExtraDropIndexContext(DbConnection connection, IServiceProvider internalServices)
        : DropContext(connection, internalServices)
    {
        /// <inheritdoc />
        protected override int RemovedColumns => 1;

        /// <inheritdoc />
        protected override bool IncludeSourceIndex => true;
    }
}
