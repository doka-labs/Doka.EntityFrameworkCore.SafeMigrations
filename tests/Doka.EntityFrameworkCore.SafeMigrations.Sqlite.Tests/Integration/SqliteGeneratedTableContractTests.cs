namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Verifies that fresh generated columns retain their declared physical contract.</summary>
public sealed class SqliteGeneratedTableContractTests : SqliteIntegrationTestBase,
    IClassFixture<SqliteConstraintShapeServiceFixture>
{
    private readonly IServiceProvider _internalServices;

    /// <summary>Shares bounded provider services while each test owns its in-memory database.</summary>
    /// <param name="services">The class-owned SQLite services.</param>
    public SqliteGeneratedTableContractTests(SqliteConstraintShapeServiceFixture services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _internalServices = services.Provider;
    }

    /// <summary>Checks creation and replay against the native generated-column catalog.</summary>
    /// <param name="stored">Whether SQLite persists the generated result.</param>
    /// <param name="nullable">Whether the declared result permits null.</param>
    /// <param name="mode">The safe table creation mode.</param>
    [Theory]
    [InlineData(false, false, SafeMigrationTableMode.StrictDefinition)]
    [InlineData(false, true, SafeMigrationTableMode.StrictDefinition)]
    [InlineData(true, false, SafeMigrationTableMode.StrictDefinition)]
    [InlineData(true, true, SafeMigrationTableMode.StrictDefinition)]
    [InlineData(false, false, SafeMigrationTableMode.ConvergenceContainer)]
    [InlineData(true, false, SafeMigrationTableMode.ConvergenceContainer)]
    public async Task FreshGeneratedTablePreservesPhysicalFacetsAndReplays(
        bool stored,
        bool nullable,
        SafeMigrationTableMode mode
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(CreateDefinition(stored, nullable), mode, SafeMigrationPolicy.ThrowIfDifferent);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-generated-fresh"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-generated-fresh"));

        var facetCount = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('generated_nodes') "
            + "WHERE name = 'has_descendants' AND type = 'INTEGER' "
            + $"AND \"notnull\" = {(nullable ? 0 : 1)} AND hidden = {(stored ? 3 : 2)};");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(1, facetCount);
    }

    /// <summary>Keeps genuine generated-column drift fail closed rather than weakening catalog equality.</summary>
    /// <param name="storeType">The actual declared affinity.</param>
    /// <param name="nullable">The actual nullability.</param>
    /// <param name="expression">The actual generated expression.</param>
    /// <param name="storage">The actual generated storage mode.</param>
    [Theory]
    [InlineData("TEXT", false, "\"right_bound\" > \"left_bound\"", "VIRTUAL")]
    [InlineData("INTEGER", true, "\"right_bound\" > \"left_bound\"", "VIRTUAL")]
    [InlineData("INTEGER", false, "\"right_bound\" >= \"left_bound\"", "VIRTUAL")]
    [InlineData("INTEGER", false, "\"right_bound\" > \"left_bound\"", "STORED")]
    public async Task ExistingGeneratedFacetDriftIsRejectedWithoutMutation(
        string storeType,
        bool nullable,
        string expression,
        string storage
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE generated_nodes (id INTEGER NOT NULL CONSTRAINT pk_generated_nodes PRIMARY KEY, "
            + "left_bound INTEGER NOT NULL, right_bound INTEGER NOT NULL, "
            + $"has_descendants {storeType} {(nullable ? string.Empty : "NOT NULL")} "
            + $"GENERATED ALWAYS AS ({expression}) {storage}); "
            + "INSERT INTO generated_nodes (id, left_bound, right_bound) VALUES (1, 2, 3);");

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            CreateDefinition(stored: false, nullable: false),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-generated-drift"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var rowCount = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM generated_nodes WHERE id = 1;");

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(1, rowCount);
    }

    /// <summary>Preserves native provider behavior for operations that do not opt into the safe contract.</summary>
    [Fact]
    public async Task OrdinaryEfGeneratedTableRetainsNativeShapeAndIsNotMisclassifiedAsMatching()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var ordinary = new MigrationBuilder(context.Database.ProviderName!);
        ordinary.CreateTable(
            "generated_nodes",
            table => new
            {
                id = table.Column<int>(type: "INTEGER", nullable: false),
                left_bound = table.Column<long>(type: "INTEGER", nullable: false),
                right_bound = table.Column<long>(type: "INTEGER", nullable: false),
                has_descendants = table.Column<bool>(
                    type: "INTEGER", nullable: false, computedColumnSql: "right_bound > left_bound"),
            },
            constraints: table => table.PrimaryKey("pk_generated_nodes", value => value.id));

        var strict = new MigrationBuilder(context.Database.ProviderName!);
        strict.EnsureTable(
            CreateDefinition(stored: false, nullable: false),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        await ExecuteOperationsAsync(context, ordinary.Operations);
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, strict.Operations, new SafeMigrationRunOptions("sqlite-native-generated-shape"));

        var nativeCount = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('generated_nodes') "
            + "WHERE name = 'has_descendants' AND type = '' AND \"notnull\" = 0 AND hidden = 2;");

        // Assert
        Assert.Equal(1, nativeCount);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(report.Assessments).ObservedState);
    }

    /// <summary>Unrelated safe operations cannot change an ordinary creator in the same structural segment.</summary>
    [Fact]
    public async Task MixedStructuralBatchPreservesOrdinaryFreshGeneratedColumnBehavior()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTable("ordinary_generated", table => new
        {
            source = table.Column<int>(type: "INTEGER", nullable: false),
            derived = table.Column<int>(type: "INTEGER", nullable: false, computedColumnSql: "source"),
        });
        builder.EnsureTable(CreateDefinition(false, false), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        var ordinaryColumns = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('ordinary_generated') "
            + "WHERE name = 'derived' AND type = '' AND \"notnull\" = 0 AND hidden = 2;");

        var guardedColumns = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('generated_nodes') "
            + "WHERE name = 'has_descendants' AND type = 'INTEGER' AND \"notnull\" = 1 AND hidden = 2;");

        // Assert
        Assert.Equal(1, ordinaryColumns);
        Assert.Equal(1, guardedColumns);
    }

    /// <summary>Unrelated safe DDL cannot specialize an ordinary computed ADD in the same structural segment.</summary>
    [Fact]
    public async Task MixedStructuralBatchPreservesOrdinaryComputedAddBehavior()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE ordinary_generated (source INTEGER NOT NULL);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumn<int>("derived", "ordinary_generated", type: "INTEGER", nullable: false,
            computedColumnSql: "source");

        builder.EnsureTable(CreateDefinition(false, false), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        var ordinaryColumns = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('ordinary_generated') "
            + "WHERE name = 'derived' AND type = '' AND \"notnull\" = 0 AND hidden = 2;");

        var guardedColumns = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('generated_nodes') "
            + "WHERE name = 'has_descendants' AND type = 'INTEGER' AND \"notnull\" = 1 AND hidden = 2;");

        // Assert
        Assert.Equal(1, ordinaryColumns);
        Assert.Equal(1, guardedColumns);
    }

    /// <summary>A native ADD folded into a safe creator retains its own distinct declaration contract.</summary>
    /// <param name="ordinaryAdd">Whether the second generated column is an ordinary provider operation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameOwnerFoldRetainsOrdinaryComputedColumnIdentity(bool ordinaryAdd)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(CreateDefinition(false, false), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        var safeCreator = (SafeMigrationOperation)builder.Operations[0];
        var renderer = new SqliteSafeMigrationSqlExpressionRenderer(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var creator = (CreateTableOperation)SafeMigrationStandardOperationFactory.Create(
            safeCreator.Intent, renderer.Render);
        var addition = new AddColumnOperation
        {
            Name = "other_computed",
            Table = "generated_nodes",
            ClrType = typeof(long),
            ColumnType = "INTEGER",
            IsNullable = false,
            ComputedColumnSql = "left_bound",
        };

        var guarded = ordinaryAdd
            ? new MigrationOperation[] { safeCreator, addition }
            : [safeCreator];

        // WHY: Exercise folded input directly so both identity branches remain
        // active even when a native provider version emits ADD separately.
        creator.Columns.Add(addition);
        var baseline = context.GetService<ISqliteSafeMigrationsBaselineGenerator>();
        var commands = baseline.Generate([creator], context.Model, guardedOperations: guarded);

        // Act
        foreach (var command in commands)
        {
            _ = await command.ExecuteNonQueryAsync(context.GetService<IRelationalConnection>());
        }

        var safeColumns = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('generated_nodes') "
            + "WHERE name = 'has_descendants' AND type = 'INTEGER' AND \"notnull\" = 1;");

        var otherColumns = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('generated_nodes') WHERE name = 'other_computed' "
            + (ordinaryAdd ? "AND type = '' AND \"notnull\" = 0;" : "AND type = 'INTEGER' AND \"notnull\" = 1;"));

        // Assert
        Assert.Equal(1, safeColumns);
        Assert.Equal(1, otherColumns);
    }

    /// <summary>Ordinary native rebuild semantics cannot change because an unrelated safe table is adjacent.</summary>
    /// <param name="safeNeighbor">Whether an unrelated safe creator shares the structural segment.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryGeneratedRebuildRetainsNativeBehaviorWithUnrelatedSafeNeighbor(bool safeNeighbor)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE generated_nodes (id INTEGER NOT NULL CONSTRAINT pk_generated_nodes PRIMARY KEY, "
            + "left_bound INTEGER NOT NULL, right_bound INTEGER NOT NULL, label TEXT NOT NULL, "
            + "has_descendants AS (right_bound > left_bound), temporary TEXT NULL, "
            + "CONSTRAINT ck_generated_nodes_bounds CHECK (right_bound >= left_bound)); "
            + "INSERT INTO generated_nodes (id, left_bound, right_bound, label) VALUES (1, 1, 3, 'retained');");

        await using var context = new SqliteGeneratedTableContractContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumn("temporary", "generated_nodes");
        if (safeNeighbor)
        {
            builder.EnsureTable(new ExpectedTableDefinition("safe_unrelated", [
                    new ExpectedColumnDefinition("id", typeof(int), false, storeType: "INTEGER"),
                ]),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        var ordinaryColumns = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('generated_nodes') "
            + "WHERE name = 'has_descendants' AND type = '' AND \"notnull\" = 0 AND hidden = 2;");

        var rows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM generated_nodes WHERE id = 1 AND label = 'retained' AND has_descendants = 1;");

        // Assert
        Assert.Equal(1, ordinaryColumns);
        Assert.Equal(1, rows);
    }

    /// <summary>Declares generated affinity and collation independently of the expression's result.</summary>
    /// <param name="stored">Whether the result is stored rather than virtual.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedTextColumnPreservesExplicitCollation(bool stored)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            new ExpectedTableDefinition("generated_text", [
                new ExpectedColumnDefinition("id", typeof(int), false, storeType: "INTEGER"),
                new ExpectedColumnDefinition("source", typeof(string), false, storeType: "TEXT"),
                new ExpectedColumnDefinition("display", typeof(string), false, storeType: "TEXT",
                    collation: new SafeMigrationCollationIdentifier("NOCASE"),
                    computedExpression: SafeMigrationSql.Identifier("source"), isStored: stored),
            ]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteSqlAsync(connection, "INSERT INTO generated_text (id, source) VALUES (1, 'MixedCase');");
        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-generated-collation"));

        var matchingRows = await ScalarIntAsync(
            connection, "SELECT COUNT(*) FROM generated_text WHERE display = 'mixedcase';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.True(Assert.Single(report.Assessments).PostconditionSatisfied);
        Assert.Equal(1, matchingRows);
    }

    /// <summary>Never accepts another collation merely because generated SQL and affinity match.</summary>
    [Fact]
    public async Task GeneratedCollationDriftIsRejected()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE generated_text (source TEXT NOT NULL, "
            + "display TEXT COLLATE BINARY NOT NULL GENERATED ALWAYS AS (\"source\") VIRTUAL);");

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("generated_text",
            new ExpectedColumnDefinition("display", typeof(string), false, storeType: "TEXT",
                collation: new SafeMigrationCollationIdentifier("NOCASE"),
                computedExpression: SafeMigrationSql.Identifier("source"), isStored: false),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-generated-collation-drift"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal("column_collation", Assert.Single(assessment.Differences).Facet);
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Populated virtual ADD keeps its physical declaration and evaluates existing rows.</summary>
    [Fact]
    public async Task PopulatedVirtualAddKeepsExistingRowsAndReplays()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE generated_nodes (id INTEGER NOT NULL, left_bound INTEGER NOT NULL, "
            + "right_bound INTEGER NOT NULL); INSERT INTO generated_nodes VALUES (1, 2, 3), (2, 3, 3);");

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("generated_nodes", CreateDefinition(false, false).Columns[3],
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var matchingRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM generated_nodes WHERE has_descendants = CASE id WHEN 1 THEN 1 ELSE 0 END;");

        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-populated-generated-add"));

        // Assert
        Assert.Equal(2, matchingRows);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
    }

    /// <summary>Required generated ADD cannot silently accept a NULL produced from existing rows.</summary>
    [Fact]
    public async Task NullProducingRequiredGeneratedAddFailsWithoutMutation()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE generated_nulls (id INTEGER NOT NULL, source INTEGER NULL); "
            + "INSERT INTO generated_nulls VALUES (1, NULL);");

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("generated_nulls",
            new ExpectedColumnDefinition("derived", typeof(long), false, storeType: "INTEGER",
                computedExpression: SafeMigrationSql.Identifier("source"), isStored: false),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var remainingRows = await ScalarIntAsync(
            connection, "SELECT COUNT(*) FROM generated_nulls WHERE source IS NULL;");
        var generatedColumns = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('generated_nulls') WHERE name = 'derived';");

        // Assert
        Assert.IsType<SqliteException>(exception);
        Assert.Equal(1, remainingRows);
        Assert.Equal(0, generatedColumns);
        Assert.Null(context.GetService<IRelationalConnection>().CurrentTransaction);
    }

    /// <summary>Nullable generated ADD permits NULL without weakening the required-column counterpart.</summary>
    [Fact]
    public async Task NullProducingNullableGeneratedAddPreservesNullAndRows()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE generated_nulls (id INTEGER NOT NULL, source INTEGER NULL); "
            + "INSERT INTO generated_nulls VALUES (1, NULL);");

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("generated_nulls",
            new ExpectedColumnDefinition("derived", typeof(long), true, storeType: "INTEGER",
                computedExpression: SafeMigrationSql.Identifier("source"), isStored: false),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-generated-nullable"));

        var matchingRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM generated_nulls WHERE id = 1 AND source IS NULL AND derived IS NULL;");

        // Assert
        Assert.Equal(1, matchingRows);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
    }

    /// <summary>Fresh stored support never authorizes an unsupported stored generated ALTER ADD.</summary>
    /// <param name="populated">Whether the existing table contains a row.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoredGeneratedAddRemainsRejectedBeforeMutation(bool populated)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE generated_nodes (id INTEGER NOT NULL, left_bound INTEGER NOT NULL, "
            + "right_bound INTEGER NOT NULL);" + (populated ? "INSERT INTO generated_nodes VALUES (1, 1, 3);" : ""));

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("generated_nodes", CreateDefinition(true, false).Columns[3],
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-stored-generated-add-boundary"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var columns = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('generated_nodes') WHERE name = 'has_descendants';");

        var rows = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM generated_nodes;");

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
        Assert.Equal("stored_generated_column_add", assessment.AnalysisCode);
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(0, columns);
        Assert.Equal(populated ? 1 : 0, rows);
    }

    /// <summary>Exercises the actual overflow guard through native fresh table creation and evaluation.</summary>
    /// <param name="stored">Whether SQLite persists the computed flag.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchedCaseRetainsInt64BoundaryAndBooleanSemantics(bool stored)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var expression = "\"right_bound\" > CASE WHEN \"left_bound\" = 9223372036854775807 "
            + "THEN \"left_bound\" ELSE \"left_bound\" + 1 END";

        var columns = CreateDefinition(stored, nullable: false).Columns.ToArray();
        columns[3] = new ExpectedColumnDefinition("has_descendants", typeof(bool), false,
            storeType: "INTEGER", computedColumnSql: expression, isStored: stored);

        builder.EnsureTable(new ExpectedTableDefinition("generated_nodes", columns),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteSqlAsync(connection,
            "INSERT INTO generated_nodes (id, left_bound, right_bound) VALUES "
            + "(1, 1, 3), (2, 1, 2), (3, 9223372036854775807, 9223372036854775807), "
            + "(4, 9223372036854775806, 9223372036854775807), "
            + "(5, -9223372036854775808, 9223372036854775807);");

        var matchingRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM generated_nodes "
            + "WHERE has_descendants = CASE WHEN id IN (1, 5) THEN 1 ELSE 0 END;");

        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-generated-case-boundaries"));

        // Assert
        Assert.Equal(5, matchingRows);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
    }

    /// <summary>Retains explicit baseline delegation and catches a baseline that loses the physical contract.</summary>
    /// <param name="correctBaseline">Whether the injected baseline honors the safe physical-facet request.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredBaselineIsInvokedAndIncorrectGeneratedDdlRollsBack(bool correctBaseline)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(CreateDefinition(false, false), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        var dependencies = context.GetService<MigrationsSqlGeneratorDependencies>();
        var native = correctBaseline
            ? context.GetService<SqliteMigrationsSqlGenerator>()
            : new SqliteMigrationsSqlGenerator(dependencies, context.GetService<IRelationalAnnotationProvider>());

        var baseline = new RecordingBaselineGenerator(
            new SqliteSafeMigrationsBaselineGenerator<SqliteMigrationsSqlGenerator>(native));

        var generator = new SqliteSafeMigrationsSqlGenerator(baseline,
            context.GetService<ISafeMigrationProviderAnalyzer>(),
            context.GetService<SqliteSafeMigrationExecutionState>(), dependencies);

        var commands = generator.Generate(builder.Operations, context.Model);

        // Act
        var exception = await Record.ExceptionAsync(async () =>
        {
            foreach (var command in commands)
            {
                _ = await command.ExecuteNonQueryAsync(context.GetService<IRelationalConnection>());
            }
        });

        var tables = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM main.sqlite_schema WHERE type = 'table' AND name = 'generated_nodes';");

        // Assert
        Assert.True(baseline.WasInvoked);
        Assert.True(baseline.PreserveGeneratedColumnFacets);
        Assert.Equal(correctBaseline ? 1 : 0, tables);
        if (correctBaseline)
        {
            Assert.Null(exception);
        }
        else
        {
            Assert.Contains("doka_sm_postcondition", Assert.IsType<InvalidOperationException>(exception).Message,
                StringComparison.Ordinal);
        }

        Assert.Null(context.GetService<IRelationalConnection>().CurrentTransaction);
    }

    /// <summary>Completes the real EF migration lifecycle and records history only after verified creation.</summary>
    [Fact]
    public async Task GeneratedMigrationAppliesReplaysAndRecordsHistory()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var context = new SqliteGeneratedTableContractContext(connection, _internalServices);
        var migrator = context.GetService<IMigrator>();

        // Act
        await migrator.MigrateAsync(SqliteGeneratedTableContractMigration.MigrationIdentifier);
        await migrator.MigrateAsync(SqliteGeneratedTableContractMigration.MigrationIdentifier);
        var history = await context.Database.GetAppliedMigrationsAsync();
        var generatedColumns = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('generated_nodes') "
            + "WHERE name = 'has_descendants' AND type = 'INTEGER' AND \"notnull\" = 1 AND hidden = 2;");

        // Assert
        Assert.Equal(SqliteGeneratedTableContractMigration.MigrationIdentifier, Assert.Single(history));
        Assert.Equal(1, generatedColumns);
    }

    /// <summary>Generated NOT NULL rejection never commits a partially applied migration or its history row.</summary>
    [Fact]
    public async Task NullProducingMigrationRollsBackDdlAndDoesNotAdvanceHistory()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var context = new SqliteGeneratedTableContractContext(connection, _internalServices);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(SqliteGeneratedTableContractMigration.MigrationIdentifier);

        // Act
        var exception = await Record.ExceptionAsync(
            () => migrator.MigrateAsync(SqliteGeneratedNullRejectionMigration.MigrationIdentifier));

        var history = await context.Database.GetAppliedMigrationsAsync();
        var rejectedTables = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM main.sqlite_schema WHERE type = 'table' AND name = 'generated_nulls';");

        // Assert
        Assert.IsType<SqliteException>(exception);
        Assert.Equal(SqliteGeneratedTableContractMigration.MigrationIdentifier, Assert.Single(history));
        Assert.Equal(0, rejectedTables);
        Assert.Null(context.GetService<IRelationalConnection>().CurrentTransaction);
    }

    /// <summary>Native temporary rebuild columns retain generated physical facets without annotations.</summary>
    [Fact]
    public async Task SafeRebuildPreservesUnrelatedGeneratedColumnAndRows()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE generated_nodes (id INTEGER NOT NULL CONSTRAINT pk_generated_nodes PRIMARY KEY, "
            + "left_bound INTEGER NOT NULL, right_bound INTEGER NOT NULL, label TEXT NULL, "
            + "has_descendants INTEGER NOT NULL GENERATED ALWAYS AS (right_bound > left_bound) VIRTUAL, "
            + "CONSTRAINT ck_generated_nodes_bounds CHECK (right_bound >= left_bound)); "
            + "INSERT INTO generated_nodes (id, left_bound, right_bound, label) VALUES (1, 1, 3, 'preserved');");

        await using var context = new SqliteGeneratedTableContractContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent("generated_nodes",
            new ExpectedColumnDefinition("label", typeof(string), false, storeType: "TEXT"),
            new ExpectedColumnDefinition("label", typeof(string), true, storeType: "TEXT"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        var matchingColumns = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('generated_nodes') "
            + "WHERE name = 'has_descendants' AND type = 'INTEGER' AND \"notnull\" = 1 AND hidden = 2;");

        var matchingRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM generated_nodes WHERE id = 1 AND label = 'preserved' AND has_descendants = 1;");

        // Assert
        Assert.Equal(1, matchingColumns);
        Assert.Equal(1, matchingRows);
    }

    /// <summary>Canonical generated SQL retains model ownership through a later independent safe rebuild.</summary>
    [Fact]
    public async Task CreatedGeneratedTableAndChecksSurviveSubsequentModelRebuild()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var context = new SqliteGeneratedTableContractContext(connection, _internalServices);
        await context.GetService<IMigrator>().MigrateAsync(SqliteGeneratedTableContractMigration.MigrationIdentifier);
        await ExecuteSqlAsync(connection,
            "INSERT INTO generated_nodes (id, left_bound, right_bound, label) VALUES (1, 1, 3, 'retained'); "
            + "ALTER TABLE generated_nodes ADD temporary TEXT NULL;");

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("temporary", "generated_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-generated-later-rebuild"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var matchingRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM generated_nodes WHERE id = 1 AND label = 'retained' AND has_descendants = 1;");

        var remainingTemporary = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('generated_nodes') WHERE name = 'temporary';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(1, matchingRows);
        Assert.Equal(0, remainingTemporary);
    }

    /// <summary>Computed-source evidence follows accepted member changes, not the immutable live catalog.</summary>
    /// <param name="change">The referenced or unrelated accepted source mutation.</param>
    /// <param name="ready">Whether the unchanged computed definition retains a complete source proof.</param>
    [Theory]
    [InlineData("unrelated_drop", true)]
    [InlineData("referenced_drop", false)]
    [InlineData("referenced_readd_type", false)]
    [InlineData("referenced_readd_nullable", false)]
    public async Task OrderedColumnChangesDoNotReuseStaleComputedSourceEvidence(string change, bool ready)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var context = new SqliteGeneratedTableContractContext(connection, _internalServices);
        await context.GetService<IMigrator>().MigrateAsync(SqliteGeneratedTableContractMigration.MigrationIdentifier);
        await ExecuteSqlAsync(connection,
            "INSERT INTO generated_nodes (id, left_bound, right_bound, label) VALUES (1, 1, 3, 'preserved');");

        if (change == "unrelated_drop")
        {
            await ExecuteSqlAsync(connection, "ALTER TABLE generated_nodes ADD temporary TEXT NULL;");
        }

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists(change == "unrelated_drop" ? "temporary" : "left_bound", "generated_nodes");
        if (change == "referenced_readd_type")
        {
            builder.AddColumnIfNotExists<string>("left_bound", "generated_nodes", type: "TEXT",
                nullable: false, defaultValue: "");
        }
        else if (change == "referenced_readd_nullable")
        {
            builder.AddColumnIfNotExists<long>("left_bound", "generated_nodes", type: "INTEGER", nullable: true);
        }

        builder.AddColumnIfNotExistsFromModel(operation => operation.AddColumn<bool>(
            "has_descendants", "generated_nodes", type: "INTEGER", nullable: false,
            computedColumnSql: "right_bound > left_bound", stored: false));

        builder.CreateIndexIfNotExists("ix_generated_nodes_flag", "generated_nodes", ["has_descendants"]);
        var versionBefore = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-ordered-generated-source"));

        var versionAfter = await ScalarIntAsync(connection, "PRAGMA schema_version;");
        if (ready)
        {
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        var rows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM generated_nodes WHERE id = 1 AND has_descendants = 1;");

        // Assert
        var diagnostic = Encoding.UTF8.GetString(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
        Assert.True(report.Status == (ready ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked),
            diagnostic);
        Assert.Equal(versionBefore, versionAfter);
        Assert.Equal(1, rows);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[^2].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^1].Action);
        if (!ready)
        {
            // WHY: A rejected DROP cannot invalidate the generated operands.
            // Later observations still describe the untouched native catalog,
            // rather than hypothetical changes that were never accepted.
            Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[0].Action);
            Assert.Equal("generated_column_drop_dependency", report.Assessments[0].AnalysisCode);
        }
    }

    /// <summary>Opaque provider commands cannot revive captured generated operands after a certified rebuild.</summary>
    [Fact]
    public async Task OrdinarySqlExpiresDropOnlyGeneratedSourceCertificate()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var context = new SqliteGeneratedTableContractContext(connection, _internalServices);
        await context.GetService<IMigrator>().MigrateAsync(SqliteGeneratedTableContractMigration.MigrationIdentifier);
        await ExecuteSqlAsync(connection, "ALTER TABLE generated_nodes ADD temporary TEXT NULL;");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("temporary", "generated_nodes");
        builder.Sql("SELECT 1;");
        builder.AddColumnIfNotExistsFromModel(operation => operation.AddColumn<bool>(
            "has_descendants", "generated_nodes", type: "INTEGER", nullable: false,
            computedColumnSql: "right_bound > left_bound", stored: false));

        var versionBefore = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-drop-opaque-source-expiry"));

        var versionAfter = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[^1].Action);
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.NotNull(report.Assessments[^1].DeferredOrigin);
        Assert.Equal(versionBefore, versionAfter);
    }

    /// <summary>A prior rebuild certificate cannot overwrite an accepted index with an old Missing result.</summary>
    /// <param name="sameKeys">Whether the repeated index retains its accepted physical definition.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DropOnlyCertificateDoesNotOverrideAcceptedIndexIdentity(bool sameKeys)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var context = new SqliteGeneratedTableContractContext(connection, _internalServices);
        await context.GetService<IMigrator>().MigrateAsync(SqliteGeneratedTableContractMigration.MigrationIdentifier);
        await ExecuteSqlAsync(connection, "ALTER TABLE generated_nodes ADD temporary TEXT NULL;");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("temporary", "generated_nodes");
        builder.AddColumnIfNotExistsFromModel(operation => operation.AddColumn<bool>(
            "has_descendants", "generated_nodes", type: "INTEGER", nullable: false,
            computedColumnSql: "right_bound > left_bound", stored: false));

        builder.CreateIndexIfNotExists("ix_generated_nodes_flag", "generated_nodes", ["has_descendants"]);
        builder.CreateIndexIfNotExists("ix_generated_nodes_flag", "generated_nodes",
            [sameKeys ? "has_descendants" : "id"]);

        var versionBefore = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-drop-index-proof-expiry"));

        var versionAfter = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Assert
        var diagnostic = Encoding.UTF8.GetString(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
        Assert.True(report.Assessments[0].Action == SafeMigrationAction.Apply, diagnostic);
        Assert.True(report.Assessments[1].Action == SafeMigrationAction.NoOp, diagnostic);
        Assert.True(report.Assessments[2].Action == SafeMigrationAction.Apply, diagnostic);
        Assert.True(report.Assessments[3].Action == (sameKeys
            ? SafeMigrationAction.NoOp : SafeMigrationAction.RejectDifferent), diagnostic);

        Assert.Equal(sameKeys ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(versionBefore, versionAfter);
    }

    /// <summary>Final strict-table probes retain the original migration's real native target model.</summary>
    [Fact]
    public async Task GeneratedRenameLifecycleRetainsDistinctCreateAndRenameTargetModels()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var before = new SqliteGeneratedRenameTargetContext<SqliteGeneratedOriginalShape>(
            connection, _internalServices);

        await using var after = new SqliteGeneratedRenameTargetContext<SqliteGeneratedRenamedShape>(
            connection, _internalServices);

        var oldModel = before.GetService<IDesignTimeModel>().Model;
        var newModel = after.GetService<IDesignTimeModel>().Model;
        var create = new MigrationBuilder(before.Database.ProviderName!);
        create.CreateTableIfNotExists("source_nodes", table => new
        {
            id = table.Column<int>(type: "INTEGER", nullable: false),
            source = table.Column<int>(type: "INTEGER", nullable: false),
            derived = table.Column<int>(type: "INTEGER", nullable: false, computedColumnSql: "\"source\" + 1"),
        }, constraints: table =>
        {
            table.PrimaryKey("pk_source_nodes", value => value.id);
            table.CheckConstraint("ck_source_nodes", "\"source\" >= 0");
        });

        var rename = new MigrationBuilder(after.Database.ProviderName!);
        rename.RenameColumnIfExists("source", "source_nodes", "renamed");
        rename.AddColumnIfNotExistsFromModel(operation => operation.AddColumn<int>(
            "derived", "source_nodes", type: "INTEGER", nullable: false, computedColumnSql: "\"renamed\" + 1"));

        var operations = create.Operations.Concat(rename.Operations).ToArray();
        var analyzer = after.GetService<ISafeMigrationProviderAnalyzer>();
        var targetAnalyzer = (ISafeMigrationProviderTargetModelAnalyzer)analyzer;

        // Act
        await ExecuteOperationsAsync(before, create.Operations);
        await ExecuteSqlAsync(connection, "INSERT INTO source_nodes (id, source) VALUES (1, 7);");
        await ExecuteOperationsAsync(after, rename.Operations);
        var analyses = await targetAnalyzer.AnalyzeAsync(after, operations, [oldModel, newModel, newModel]);
        var probes = SafeMigrationPostflightProjection.CreateRenamedTableProbes(
            operations, analyses, (ISafeMigrationProviderObjectIdentityNormalizer)analyzer);

        var results = await targetAnalyzer.AnalyzeAsync(after,
            probes?.Select(static probe => probe.Operation).ToArray() ?? [],
            probes?.Select(_ => (IModel?)oldModel).ToList());

        var rows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM source_nodes WHERE id = 1 AND renamed = 7 AND derived = 8;");

        // Assert
        Assert.NotSame(oldModel, newModel);
        Assert.NotNull(probes);
        Assert.Equal(0, Assert.Single(probes).OperationOrdinal);
        Assert.True(Assert.Single(results).PostconditionSatisfied);
        Assert.Equal(1, rows);
    }

    /// <summary>A failed guarded generation cannot leak its physical-facet policy into later ordinary EF SQL.</summary>
    [Fact]
    public async Task FailedSafeGenerationRestoresOrdinaryProviderRendering()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var native = Assert.IsType<SqliteSafeMigrationsBaselineSqlGenerator>(
            context.GetService<SqliteMigrationsSqlGenerator>());

        var ordinary = new MigrationBuilder(context.Database.ProviderName!);
        ordinary.CreateTable("generated_scope", table => new
        {
            source = table.Column<int>(type: "INTEGER", nullable: false),
            derived = table.Column<int>(type: "INTEGER", nullable: false, computedColumnSql: "source"),
        });

        // Act
        var exception = Record.Exception(() => native.GenerateSafe(
            [new CreateSequenceOperation { Name = "unsupported_sequence" }], context.Model,
            MigrationsSqlGenerationOptions.Default, guardedOperations: []));

        var commands = native.Generate(ordinary.Operations, context.Model);
        var sql = commands.Single().CommandText;

        // Assert
        Assert.IsType<NotSupportedException>(exception);
        Assert.Single(commands);
        Assert.Contains("\"derived\" AS (source)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"derived\" INTEGER", sql, StringComparison.Ordinal);
    }

    /// <summary>Specializing generated columns preserves native primary, unique, check, FK, and index DDL.</summary>
    [Fact]
    public async Task FreshGeneratedTablePreservesAllNativeConstraintKinds()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE generated_parents (id INTEGER NOT NULL CONSTRAINT pk_generated_parents PRIMARY KEY); "
            + "INSERT INTO generated_parents VALUES (1);");

        await using var context = CreateContext(connection);
        var builder = CreateConstrainedTableBuilder(context);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteSqlAsync(connection,
            "INSERT INTO generated_nodes (id, left_bound, right_bound, parent_id) VALUES (1, 2, 3, 1);");

        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-generated-native-constraints"));

        var foreignKeys = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_foreign_key_list('generated_nodes') "
            + "WHERE \"table\" = 'generated_parents' AND \"from\" = 'parent_id' AND on_delete = 'CASCADE';");

        var indexes = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_index_list('generated_nodes') "
            + "WHERE name = 'ix_generated_nodes_parent';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(1, foreignKeys);
        Assert.Equal(1, indexes);
    }

    /// <summary>Native constraints remain behavioral guards after generated table creation.</summary>
    /// <param name="values">The invalid primary, unique, check, or FK value tuple.</param>
    /// <param name="extendedCode">The native SQLite constraint code.</param>
    [Theory]
    [InlineData("(1, 4, 5, 1)", 1555)]
    [InlineData("(2, 2, 5, 1)", 2067)]
    [InlineData("(2, 4, 3, 1)", 275)]
    [InlineData("(2, 4, 5, 99)", 787)]
    public async Task GeneratedTableNativeConstraintsRejectInvalidRows(string values, int extendedCode)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE generated_parents (id INTEGER NOT NULL CONSTRAINT pk_generated_parents PRIMARY KEY); "
            + "INSERT INTO generated_parents VALUES (1);");

        await using var context = CreateContext(connection);
        await ExecuteOperationsAsync(context, CreateConstrainedTableBuilder(context).Operations);
        await ExecuteSqlAsync(connection,
            "INSERT INTO generated_nodes (id, left_bound, right_bound, parent_id) VALUES (1, 2, 3, 1);");

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteSqlAsync(connection,
            "INSERT INTO generated_nodes (id, left_bound, right_bound, parent_id) VALUES " + values + ";"));

        var preservedRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM generated_nodes WHERE id = 1 AND has_descendants = 1;");

        var totalRows = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM generated_nodes;");

        // Assert
        Assert.Equal(extendedCode, Assert.IsType<SqliteException>(exception).SqliteExtendedErrorCode);
        Assert.Equal(1, preservedRows);
        Assert.Equal(1, totalRows);
    }

    /// <summary>Authors all constraint kinds around one generated column using ordinary provider operations.</summary>
    private static MigrationBuilder CreateConstrainedTableBuilder(DbContext context)
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var columns = CreateDefinition(false, false).Columns.Append(
            new ExpectedColumnDefinition("parent_id", typeof(int), true, storeType: "INTEGER"));

        builder.EnsureTable(new ExpectedTableDefinition("generated_nodes", columns,
                primaryKey: new ExpectedPrimaryKeyDefinition("pk_generated_nodes", "generated_nodes", ["id"]),
                uniqueConstraints: [
                    new ExpectedUniqueConstraintDefinition(
                        "uq_generated_nodes_left", "generated_nodes", ["left_bound"]),
                ],
                checkConstraints: [
                    ExpectedCheckConstraintDefinition.FromExpression("ck_generated_nodes_bounds", "generated_nodes",
                        SafeMigrationSql.Binary(SafeMigrationSql.Identifier("right_bound"),
                            SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
                            SafeMigrationSql.Identifier("left_bound"))),
                ],
                foreignKeys: [
                    new ExpectedForeignKeyDefinition("fk_generated_nodes_parent", "generated_nodes", ["parent_id"],
                        "generated_parents", ["id"], onDelete: ReferentialAction.Cascade),
                ]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        builder.CreateIndexIfNotExists("ix_generated_nodes_parent", "generated_nodes", ["parent_id"]);

        return builder;
    }

    /// <summary>Observes the injected SPI rather than inferring delegation from generated output.</summary>
    private sealed class RecordingBaselineGenerator(ISqliteSafeMigrationsBaselineGenerator inner)
        : ISqliteSafeMigrationsBaselineGenerator
    {
        /// <summary>Gets whether native generation was actually requested.</summary>
        public bool WasInvoked { get; private set; }

        /// <summary>Gets whether the safe path requested the complete generated-column contract.</summary>
        public bool PreserveGeneratedColumnFacets { get; private set; }

        /// <inheritdoc />
        public IReadOnlyList<MigrationCommand> Generate(
            IReadOnlyList<MigrationOperation> operations,
            IModel? model = null,
            MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default,
            IReadOnlyList<MigrationOperation>? guardedOperations = null
        )
        {
            WasInvoked = true;
            PreserveGeneratedColumnFacets = guardedOperations is not null;

            return inner.Generate(operations, model, options, guardedOperations);
        }
    }

    private static ExpectedTableDefinition CreateDefinition(
        bool stored,
        bool nullable
    ) => new(
        "generated_nodes",
        [
            new ExpectedColumnDefinition("id", typeof(int), false, storeType: "INTEGER"),
            new ExpectedColumnDefinition("left_bound", typeof(long), false, storeType: "INTEGER"),
            new ExpectedColumnDefinition("right_bound", typeof(long), false, storeType: "INTEGER"),
            new ExpectedColumnDefinition(
                "has_descendants",
                typeof(bool),
                nullable,
                storeType: "INTEGER",
                computedExpression: SafeMigrationSql.Binary(
                    SafeMigrationSql.Identifier("right_bound"),
                    SafeMigrationSqlBinaryOperator.GreaterThan,
                    SafeMigrationSql.Identifier("left_bound")),
                isStored: stored),
        ],
        primaryKey: new ExpectedPrimaryKeyDefinition("pk_generated_nodes", "generated_nodes", ["id"]));
}
