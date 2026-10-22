namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlGeneratedCaseIntegrationTests
{
    /// <summary>Runtime and script paths refuse dependencies without invoking optional preflight analysis.</summary>
    /// <param name="script">Whether the real emitted script runs instead of synchronous EF commands.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedSourceDropCannotBypassGuardWithoutPreflight(bool script)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var initial = new MigrationBuilder(context.Database.ProviderName!);
        initial.EnsureTable(CaseTable(stored: false), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        await ExecuteOperationsAsync(context, initial.Operations);
        await SeedCaseRowsAsync(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(CaseTable(stored: false), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropColumnIfExists("left_bound", Table);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model,
            script ? MigrationsSqlGenerationOptions.Script : MigrationsSqlGenerationOptions.Default);

        // Act
        var failure = script
            ? await Record.ExceptionAsync(() => ExecuteMigrationScriptAsync(connectionString,
                string.Join(Environment.NewLine, commands.Select(static command => command.CommandText))))
            : Record.Exception(() =>
            {
                foreach (var command in commands)
                {
                    command.ExecuteNonQuery(context.GetService<IRelationalConnection>());
                }
            });

        var rows = await CountExpectedCaseRowsAsync(connectionString);

        // Assert
        if (script)
        {
            var native = Assert.IsType<MySqlException>(failure);
            Assert.Equal(1062, native.Number);
            Assert.Contains("doka_sm_unsupported", native.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal("doka_sm_unsupported", Assert.IsType<SafeMigrationExecutionException>(failure).GuardCode);
        }

        Assert.Equal(5, rows);
    }

    /// <summary>Extra, opaque or storage-drifted members cannot borrow a model's dependency explanation.</summary>
    /// <param name="drift">The actual generated-set divergence from the model candidate.</param>
    [Theory]
    [InlineData("extra")]
    [InlineData("opaque")]
    [InlineData("storage")]
    [InlineData("missing")]
    public async Task GeneratedDropDoesNotBorrowDriftedModelMemberCandidate(string drift)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = new GeneratedDropModelContext(new DbContextOptionsBuilder()
            .UseMySql(connectionString, Fixture.ServerVersion).UseMySqlSafeMigrations().Options);

        var generated = drift == "missing" ? string.Empty
            : ", result int GENERATED ALWAYS AS (" + (drift == "opaque" ? "IF(source > 0, 1, 0)" : "source + 1")
                + ") " + (drift == "storage" ? "STORED" : "VIRTUAL");

        var extra = drift == "extra" ? ", extra int GENERATED ALWAYS AS (source + 2) VIRTUAL" : string.Empty;
        await ExecuteSqlAsync(connectionString, "CREATE TABLE generated_drop_model (id int NOT NULL PRIMARY KEY, "
            + "source int NULL, other int NULL" + generated + extra
            + "); INSERT INTO generated_drop_model (id, source, other) VALUES (1, 7, 11);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("source", "generated_drop_model");
        var targetModel = context.GetService<IDesignTimeModel>().Model;

        // Act
        var analyses = await AnalyzeGeneratedTargetAsync(context, builder.Operations, targetModel);
        var failure = await Record.ExceptionAsync(() =>
            ExecuteGeneratedTargetAsync(context, builder.Operations, targetModel));
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM generated_drop_model;");

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(drift == "missing" ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Unsupported,
            analysis.ObservedState);
        if (drift == "missing")
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal("generated_column_drop_unproven", analysis.Code);
            Assert.IsType<SafeMigrationExecutionException>(failure);
        }

        Assert.Equal(1, rows);
    }

    /// <summary>Dependent removal discharges rename restrictions without losing destination occupancy.</summary>
    /// <param name="occupiedDestination">Whether an independently existing member owns the destination.</param>
    [GeneratedMySqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedMemberRemovalPreservesIndependentRenameDestination(bool occupiedDestination)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var initial = new MigrationBuilder(context.Database.ProviderName!);
        initial.EnsureTable(CaseTable(stored: false), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        await ExecuteOperationsAsync(context, initial.Operations);
        await SeedCaseRowsAsync(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(CaseTable(stored: false), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropColumnIfExists("has_descendants", Table);
        builder.RenameColumnIfExists("left_bound", Table, occupiedDestination ? "right_bound" : "renamed_left");

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-dependent-removal-before-rename"), CancellationToken.None);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var rows = await ScalarIntAsync(connectionString, $"SELECT COUNT(*) FROM `{Table}`;");

        // Assert
        Assert.Equal(occupiedDestination ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready,
            preflight.Status);
        Assert.Equal(occupiedDestination ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching,
            preflight.Assessments[^1].ObservedState);
        if (occupiedDestination)
        {
            Assert.Equal("doka_sm_different", Assert.IsType<SafeMigrationExecutionException>(failure).GuardCode);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.Equal(5, rows);
    }

    /// <summary>Standalone DROP captures generated completeness in its existing classification statement.</summary>
    /// <param name="nativeBatch">Whether native batch transport or sequential fallback executes the statement.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneDropCompletenessAddsNoPrerequisiteRoundtrip(bool nativeBatch)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE generated_drop_plain (id int NULL, payload int NULL);");
        await using var context = CreateContext(connectionString);
        await using var connection = new CatalogClassificationCountingConnection(new MySqlConnection(connectionString),
            nativeBatch: nativeBatch);

        context.Database.SetDbConnection(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("id", "generated_drop_plain");
        var operations = builder.Operations.Cast<SafeMigrationOperation>().ToArray();

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, operations);

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.True(analysis.DropLeavesNoGeneratedColumns);
        Assert.Equal(0, connection.PrerequisiteStatementCount);
        Assert.Equal(nativeBatch ? 0 : 1, connection.ClassificationStatementCount);
        Assert.Equal(nativeBatch ? 1 : 0, connection.NativeBatchExecutionCount);
    }

    /// <summary>Future model candidates authorize dependency reasoning only after exact native set equality.</summary>
    /// <param name="dropSource">Whether the removed member is a generated operand.</param>
    /// <param name="nativeDrift">Whether native generated SQL differs from the untrusted model candidate.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GeneratedDropModelCandidateRequiresFreshNativeEquality(bool dropSource, bool nativeDrift)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = new GeneratedDropModelContext(new DbContextOptionsBuilder()
            .UseMySql(connectionString, Fixture.ServerVersion).UseMySqlSafeMigrations().Options);

        await ExecuteSqlAsync(connectionString, "CREATE TABLE generated_drop_model (id int NOT NULL PRIMARY KEY, "
            + "source int NULL, other int NULL, result int GENERATED ALWAYS AS (`source` + "
            + (nativeDrift ? "2" : "1") + ") VIRTUAL); INSERT INTO generated_drop_model (id, source, other) "
            + "VALUES (1, 7, 11);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists(dropSource ? "source" : "other", "generated_drop_model");
        var targetModel = context.GetService<IDesignTimeModel>().Model;

        // Act
        var analyses = await AnalyzeGeneratedTargetAsync(context, builder.Operations, targetModel);
        var failure = await Record.ExceptionAsync(() =>
            ExecuteGeneratedTargetAsync(context, builder.Operations, targetModel));
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM generated_drop_model;");

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(dropSource ? SafeMigrationObservedState.Unsupported : SafeMigrationObservedState.Matching,
            analysis.ObservedState);
        if (dropSource)
        {
            Assert.Equal(nativeDrift ? "generated_column_drop_unproven" : "generated_column_drop_dependency",
                analysis.Code);
            Assert.IsType<SafeMigrationExecutionException>(failure);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.Equal(1, rows);
    }

    /// <summary>Retained generated dependencies reject a source DROP before native DDL can fail.</summary>
    /// <param name="prefix">The available authored metadata, without treating it as native truth.</param>
    [Theory]
    [InlineData("none")]
    [InlineData("strict")]
    [InlineData("sparse")]
    public async Task GeneratedSourceDropIsRejectedWithoutChangingSchemaOrRows(string prefix)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var initial = new MigrationBuilder(context.Database.ProviderName!);
        initial.EnsureTable(CaseTable(stored: false), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        await ExecuteOperationsAsync(context, initial.Operations);
        await SeedCaseRowsAsync(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (prefix == "strict")
        {
            builder.EnsureTable(CaseTable(stored: false), SafeMigrationTableMode.StrictDefinition,
                SafeMigrationPolicy.ThrowIfDifferent);
        }
        else if (prefix == "sparse")
        {
            builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);
        }

        builder.DropColumnIfExists("left_bound", Table);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-source-drop"), CancellationToken.None);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var source = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
            + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{Table}' AND COLUMN_NAME = 'left_bound';");
        var rows = await CountExpectedCaseRowsAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, preflight.Assessments[^1].ObservedState);
        Assert.Equal(prefix == "none" ? "generated_column_drop_unproven" : "generated_column_drop_dependency",
            preflight.Assessments[^1].AnalysisCode);
        var diagnostic = Assert.IsType<SafeMigrationExecutionException>(failure);
        Assert.Equal("doka_sm_unsupported", diagnostic.GuardCode);
        Assert.Equal(1, source);
        Assert.Equal(5, rows);
    }

    /// <summary>Removing generated members explicitly is legal and can discharge a later source removal.</summary>
    /// <param name="dropSource">Whether the ordinary source is removed after its last generated dependent.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedMemberDropAndExplicitDependentFirstRemovalRemainLegal(bool dropSource)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var initial = new MigrationBuilder(context.Database.ProviderName!);
        initial.EnsureTable(CaseTable(stored: false), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        await ExecuteOperationsAsync(context, initial.Operations);
        await SeedCaseRowsAsync(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("has_descendants", Table);
        if (dropSource)
        {
            builder.DropColumnIfExists("left_bound", Table);
        }

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-explicit-dependent-first"), CancellationToken.None);
        var failure = await Record.ExceptionAsync(async () =>
        {
            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
        });

        var verified = await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-explicit-dependent-first-verify"), CancellationToken.None);
        var rows = await ScalarIntAsync(connectionString, $"SELECT COUNT(*) FROM `{Table}`;");

        // Assert
        Assert.True(preflight.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(preflight));
        Assert.All(preflight.Assessments,
            static assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Null(failure);
        Assert.True(verified.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(verified));
        Assert.Equal(5, rows);
    }

    /// <summary>A complete actual generated set permits a nondependent ordinary member DROP.</summary>
    [Fact]
    public async Task GeneratedOwnerUnrelatedDropKeepsComputedValuesAndSupportsReplay()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var original = CaseTable(stored: false);
        var expected = new ExpectedTableDefinition(Table,
            original.Columns.Append(new ExpectedColumnDefinition("unrelated", typeof(int), true, "int")),
            primaryKey: original.PrimaryKey);

        var initial = new MigrationBuilder(context.Database.ProviderName!);
        initial.EnsureTable(expected, SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        await ExecuteOperationsAsync(context, initial.Operations);
        await SeedCaseRowsAsync(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(expected, SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropColumnIfExists("unrelated", Table);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-unrelated-member-drop"), CancellationToken.None);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var replay = new MigrationBuilder(context.Database.ProviderName!);
        replay.DropColumnIfExists("unrelated", Table);
        var replayReport = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, replay.Operations,
            new SafeMigrationRunOptions("generated-unrelated-member-drop-replay"), CancellationToken.None);

        var replayFailure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, replay.Operations));
        var unrelated = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
            + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{Table}' AND COLUMN_NAME = 'unrelated';");

        var rows = await CountExpectedCaseRowsAsync(connectionString);

        // Assert
        Assert.True(preflight.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(preflight));
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[^1].Action);
        Assert.Null(failure);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replayReport.Assessments).Action);
        Assert.Null(replayFailure);
        Assert.Equal(0, unrelated);
        Assert.Equal(5, rows);
    }

    /// <summary>Supplies the same exact historical model to the established analyzer target-model contract.</summary>
    private static Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeGeneratedTargetAsync(DbContext context,
        List<MigrationOperation> operations, IModel model) =>
        ((ISafeMigrationProviderTargetModelAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>()).AnalyzeAsync(
            context, operations, Enumerable.Repeat<IModel?>(model, operations.Count).ToArray(), CancellationToken.None);

    /// <summary>Exercises ordinary generated commands with the exact complete model passed by a migration.</summary>
    private static async Task ExecuteGeneratedTargetAsync(DbContext context,
        IReadOnlyList<MigrationOperation> operations, IModel model)
    {
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(operations, model);
        var connection = context.GetService<IRelationalConnection>();
        foreach (var command in commands)
        {
            await command.ExecuteNonQueryAsync(connection, cancellationToken: CancellationToken.None);
        }
    }

    /// <summary>Provides a bounded target model whose generated contract is never itself native evidence.</summary>
    private sealed class GeneratedDropModelContext(DbContextOptions options) : DbContext(options)
    {
        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity("generated_drop_model", entity =>
            {
                entity.ToTable("generated_drop_model");
                entity.Property<int>("id").ValueGeneratedNever();
                entity.HasKey("id");
                entity.Property<int?>("source");
                entity.Property<int?>("other");
                entity.Property<int?>("result").HasComputedColumnSql("`source` + 1", stored: false);
            });
        }
    }
}
