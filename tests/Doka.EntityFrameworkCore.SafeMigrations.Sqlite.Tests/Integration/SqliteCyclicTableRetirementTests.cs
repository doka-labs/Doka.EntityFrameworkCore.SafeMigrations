namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Qualifies atomic retirement when removed tables are intentionally absent from the target model.</summary>
public sealed class SqliteCyclicTableRetirementTests : SqliteIntegrationTestBase,
    IClassFixture<SqliteConstraintShapeServiceFixture>
{
    private readonly ServiceProvider _internalServices;

    /// <summary>Shares provider services while each test owns its native database and context.</summary>
    /// <param name="services">The class-owned provider fixture, independent from EF's global provider cache.</param>
    public SqliteCyclicTableRetirementTests(
        SqliteConstraintShapeServiceFixture services
    )
    {
        _internalServices = services.Provider;
    }

    /// <summary>Every new model family uses the fixture rather than allocating a global EF provider.</summary>
    /// <param name="contextKind">The independent model and provider-registration order.</param>
    [Theory]
    [InlineData("retirement")]
    [InlineData("renamed")]
    [InlineData("migration-before")]
    [InlineData("migration-after")]
    public async Task NewContextsUseFixtureOwnedServices(
        string contextKind
    )
    {
        // Arrange
        await using var database = await RetirementDatabase.OpenAsync(false, CancellationToken.None);
        await using DbContext context = contextKind switch
        {
            "retirement" => new RetirementTargetContext(database.Connection, _internalServices),
            "renamed" => new RenamedHostTargetContext(database.Connection, _internalServices),
            "migration-before" => new SqliteRetirementMigrationContext(database.Connection, true, _internalServices),
            "migration-after" => new SqliteRetirementMigrationContext(database.Connection, false, _internalServices),
            _ => throw new ArgumentOutOfRangeException(nameof(contextKind)),
        };

        // Act
        var owner = context
            .GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()
            ?.InternalServiceProvider;

        // Assert
        Assert.Same(_internalServices, owner);
    }

    /// <summary>Real reverse migration preserves its baseline target and host state across reapplication.</summary>
    /// <param name="fileBacked">Whether the independently owned database uses a file.</param>
    /// <param name="safeFirst">Whether SafeMigrations options are registered before the native provider.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ActualMigratorRetiresPopulatedCycleAndReappliesWithExactHistory(
        bool fileBacked,
        bool safeFirst
    )
    {
        // Arrange
        await using var database = await RetirementDatabase.OpenAsync(fileBacked, CancellationToken.None);
        await using var context = new SqliteRetirementMigrationContext(
            database.Connection,
            safeFirst,
            _internalServices);

        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(SqliteRetirementBaselineMigration.MigrationIdentifier, CancellationToken.None);
        await ExecuteSqlAsync(database.Connection,
            "INSERT INTO retained_host VALUES (42, 'retained');",
            CancellationToken.None);

        // Act
        await migrator.MigrateAsync(SqliteRetirementCycleMigration.MigrationIdentifier, CancellationToken.None);
        await ExecuteSqlAsync(database.Connection,
            "INSERT INTO retiring_parent VALUES (1, NULL); INSERT INTO retiring_child VALUES (2, 1); "
            + "UPDATE retiring_parent SET child_id = 2 WHERE id = 1;",
            CancellationToken.None);

        await migrator.MigrateAsync(SqliteRetirementBaselineMigration.MigrationIdentifier, CancellationToken.None);
        var reverseHistory = (await context.Database.GetAppliedMigrationsAsync(CancellationToken.None)).ToArray();
        var removedOwners = await ScalarIntAsync(database.Connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('retiring_parent', 'retiring_child');",
            CancellationToken.None);

        await migrator.MigrateAsync(SqliteRetirementCycleMigration.MigrationIdentifier, CancellationToken.None);
        await migrator.MigrateAsync(SqliteRetirementCycleMigration.MigrationIdentifier, CancellationToken.None);
        var replayHistory = (await context.Database.GetAppliedMigrationsAsync(CancellationToken.None)).ToArray();
        var host = await ScalarIntAsync(database.Connection,
            "SELECT COUNT(*) FROM retained_host WHERE id = 42 AND marker = 'retained';",
            CancellationToken.None);

        var hostIndex = await ScalarIntAsync(database.Connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = 'ix_retained_host_marker';",
            CancellationToken.None);

        var mode = await ScalarIntAsync(database.Connection, "PRAGMA foreign_keys;", CancellationToken.None);

        // Assert
        Assert.Equal([SqliteRetirementBaselineMigration.MigrationIdentifier], reverseHistory);
        Assert.Equal(0, removedOwners);
        Assert.Equal([
                SqliteRetirementBaselineMigration.MigrationIdentifier,
                SqliteRetirementCycleMigration.MigrationIdentifier,
            ],
            replayHistory);

        Assert.Equal(1, host);
        Assert.Equal(1, hostIndex);
        Assert.Equal(1, mode);
    }

    /// <summary>A complete retirement can remove a populated FK cycle without rebuilding a discarded owner.</summary>
    /// <param name="fileBacked">Whether the owned database uses a file instead of memory.</param>
    /// <param name="foreignKeysEnabled">The native enforcement mode that successful execution must preserve.</param>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task PopulatedCycleRetiresWithoutTargetModelAndPreservesHost(
        bool fileBacked,
        bool foreignKeysEnabled
    )
    {
        // Arrange
        await using var database = await RetirementDatabase.OpenAsync(fileBacked, CancellationToken.None);
        await using var context = new RetirementTargetContext(database.Connection, _internalServices);
        await CreateCycleAsync(database.Connection, CancellationToken.None);
        if (!foreignKeysEnabled)
        {
            await ExecuteSqlAsync(database.Connection, "PRAGMA foreign_keys = OFF;", CancellationToken.None);
        }

        var operations = RetirementOperations();

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                operations,
                new SafeMigrationRunOptions("sqlite-cycle-retirement"),
                CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            operations,
            CancellationToken.None));

        var replayException = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            operations,
            CancellationToken.None));

        var retired = await ScalarIntAsync(
            database.Connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('retiring_parent', 'retiring_child');",
            CancellationToken.None);

        var host = await ScalarIntAsync(
            database.Connection,
            "SELECT COUNT(*) FROM retained_host WHERE id = 42 AND marker = 'retained';",
            CancellationToken.None);

        var foreignKeys = await ScalarIntAsync(database.Connection, "PRAGMA foreign_keys;", CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Null(exception);
        Assert.Null(replayException);
        Assert.Equal(0, retired);
        Assert.Equal(1, host);
        Assert.Equal(foreignKeysEnabled
                ? 1
                : 0,
            foreignKeys);
    }

    /// <summary>Retirement retains live artifact checks and preserves the native schema on rejection.</summary>
    /// <param name="artifact">The independently unsupported native artifact.</param>
    /// <param name="expectedCode">The classification that must remain fail-closed.</param>
    [Theory]
    [InlineData("trigger", "table_rebuild_trigger")]
    [InlineData("external-trigger", "table_rebuild_trigger")]
    [InlineData("view", "table_rebuild_view")]
    [InlineData("strict", "strict_table_rebuild")]
    [InlineData("without-rowid", "without_rowid_table_rebuild")]
    [InlineData("provider-option", "table_rebuild_provider_constraint_option")]
    [InlineData("generated", "table_retirement_generated_column")]
    [InlineData("partial-index", "table_retirement_index_expression")]
    [InlineData("expression-index", "table_retirement_index_expression")]
    [InlineData("child-trigger", "table_rebuild_trigger")]
    [InlineData("child-external-trigger", "table_rebuild_trigger")]
    [InlineData("child-view", "table_rebuild_view")]
    [InlineData("child-strict", "strict_table_rebuild")]
    [InlineData("child-without-rowid", "without_rowid_table_rebuild")]
    [InlineData("child-provider-option", "table_rebuild_provider_constraint_option")]
    [InlineData("child-generated", "table_retirement_generated_column")]
    [InlineData("child-partial-index", "table_retirement_index_expression")]
    [InlineData("child-expression-index", "table_retirement_index_expression")]
    public async Task UnsupportedRetirementArtifactPreservesEverything(
        string artifact,
        string expectedCode
    )
    {
        // Arrange
        await using var database = await RetirementDatabase.OpenAsync(false, CancellationToken.None);
        await using var context = new RetirementTargetContext(database.Connection, _internalServices);
        await CreateCycleAsync(database.Connection, artifact, CancellationToken.None);
        var operations = RetirementOperations();
        var before = await ReadSchemaAsync(database.Connection, CancellationToken.None);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                operations,
                new SafeMigrationRunOptions("sqlite-unsupported-retirement-artifact"),
                CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            operations,
            CancellationToken.None));

        var after = await ReadSchemaAsync(database.Connection, CancellationToken.None);
        var rows = await ScalarIntAsync(database.Connection,
            "SELECT COUNT(*) FROM retiring_parent;",
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(expectedCode, report.Assessments[0].AnalysisCode);
        Assert.Contains(expectedCode,
            Assert.IsType<InvalidOperationException>(exception)
                .Message,
            StringComparison.Ordinal);

        Assert.Equal(before, after);
        Assert.Equal(1, rows);
    }

    /// <summary>Surviving incoming relationships cannot borrow the private cycle's retirement authorization.</summary>
    /// <param name="action">The delete action, including actions that could silently mutate retained rows.</param>
    /// <param name="foreignKeysEnabled">Whether native FK enforcement initially protects the database.</param>
    [Theory]
    [InlineData("CASCADE", true)]
    [InlineData("SET NULL", true)]
    [InlineData("RESTRICT", true)]
    [InlineData("CASCADE", false)]
    [InlineData("SET NULL", false)]
    [InlineData("RESTRICT", false)]
    public async Task SurvivingIncomingReferenceBlocksRetirementRegardlessOfNativeMode(
        string action,
        bool foreignKeysEnabled
    )
    {
        // Arrange
        await using var database = await RetirementDatabase.OpenAsync(false, CancellationToken.None);
        await using var context = new RetirementTargetContext(database.Connection, _internalServices);
        await CreateCycleAsync(database.Connection, CancellationToken.None);
        await ExecuteSqlAsync(database.Connection,
            $"CREATE TABLE surviving_child (id INTEGER PRIMARY KEY, parent_id INTEGER NULL "
            + $"REFERENCES retiring_parent(id) ON DELETE {action}); "
            + "INSERT INTO surviving_child VALUES (7, 1);",
            CancellationToken.None);

        if (!foreignKeysEnabled)
        {
            await ExecuteSqlAsync(database.Connection, "PRAGMA foreign_keys = OFF;", CancellationToken.None);
        }

        var operations = RetirementOperations();
        var before = await ReadSchemaAsync(database.Connection, CancellationToken.None);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                operations,
                new SafeMigrationRunOptions("sqlite-retirement-surviving-reference"),
                CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            operations,
            CancellationToken.None));

        var after = await ReadSchemaAsync(database.Connection, CancellationToken.None);
        var retained = await ScalarIntAsync(database.Connection,
            "SELECT COUNT(*) FROM surviving_child WHERE id = 7 AND parent_id = 1;",
            CancellationToken.None);

        var mode = await ScalarIntAsync(database.Connection, "PRAGMA foreign_keys;", CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal("table_drop_foreign_key_dependency", report.Assessments[^1].AnalysisCode);
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(before, after);
        Assert.Equal(1, retained);
        Assert.Equal(foreignKeysEnabled
                ? 1
                : 0,
            mode);
    }

    /// <summary>A later DDL failure rolls back the subsumed FK removal and every preceding table drop.</summary>
    [Fact]
    public async Task LaterNativeFailureRollsBackRetirementAndRestoresForeignKeys()
    {
        // Arrange
        await using var database = await RetirementDatabase.OpenAsync(false, CancellationToken.None);
        await using var context = new RetirementTargetContext(database.Connection, _internalServices);
        await CreateCycleAsync(database.Connection, CancellationToken.None);
        var operations = RetirementOperations();
        operations.Add(new DropTableOperation { Name = "missing_failure_owner" });
        var before = await ReadSchemaAsync(database.Connection, CancellationToken.None);

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            operations,
            CancellationToken.None));

        var after = await ReadSchemaAsync(database.Connection, CancellationToken.None);
        var parent = await ScalarIntAsync(database.Connection,
            "SELECT COUNT(*) FROM retiring_parent WHERE id = 1 AND child_id = 2;",
            CancellationToken.None);

        var mode = await ScalarIntAsync(database.Connection, "PRAGMA foreign_keys;", CancellationToken.None);

        // Assert
        Assert.Contains("missing_failure_owner",
            Assert.IsType<SqliteException>(exception)
                .Message,
            StringComparison.Ordinal);

        Assert.Equal(before, after);
        Assert.Equal(1, parent);
        Assert.Equal(1, mode);
    }

    /// <summary>A canceled request never starts retirement or changes the original enforcement mode.</summary>
    [Fact]
    public async Task CanceledRetirementPreservesSchemaDataAndForeignKeys()
    {
        // Arrange
        await using var database = await RetirementDatabase.OpenAsync(false, CancellationToken.None);
        await using var context = new RetirementTargetContext(database.Connection, _internalServices);
        await CreateCycleAsync(database.Connection, CancellationToken.None);
        var operations = RetirementOperations();
        var before = await ReadSchemaAsync(database.Connection, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            operations,
            cancellation.Token));

        var after = await ReadSchemaAsync(database.Connection, CancellationToken.None);
        var rows = await ScalarIntAsync(database.Connection,
            "SELECT COUNT(*) FROM retiring_parent;",
            CancellationToken.None);

        var mode = await ScalarIntAsync(database.Connection, "PRAGMA foreign_keys;", CancellationToken.None);

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(before, after);
        Assert.Equal(1, rows);
        Assert.Equal(1, mode);
    }

    /// <summary>A DROP in a later SQL window cannot authorize an unmodeled rebuild in the current window.</summary>
    /// <param name="laterDrop">Whether the later owner drop exists across an opaque SQL boundary.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCurrentWindowRetirementRemainsBlocked(
        bool laterDrop
    )
    {
        // Arrange
        await using var database = await RetirementDatabase.OpenAsync(false, CancellationToken.None);
        await using var context = new RetirementTargetContext(database.Connection, _internalServices);
        await CreateCycleAsync(database.Connection, CancellationToken.None);
        var operations = RetirementOperations();
        if (laterDrop)
        {
            operations.Insert(1, new SqlOperation { Sql = "SELECT 1;" });
        }
        else
        {
            operations.RemoveRange(1, 2);
        }

        var before = await ReadSchemaAsync(database.Connection, CancellationToken.None);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                operations,
                new SafeMigrationRunOptions("sqlite-missing-window-retirement"),
                CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            operations,
            CancellationToken.None));

        var after = await ReadSchemaAsync(database.Connection, CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal("table_rebuild_model_missing", report.Assessments[0].AnalysisCode);
        Assert.Contains("table_rebuild_model_missing",
            Assert.IsType<InvalidOperationException>(exception)
                .Message,
            StringComparison.Ordinal);

        Assert.Equal(before, after);
    }

    /// <summary>Native lifetime changes cannot masquerade as an unchanged owner scheduled for deletion.</summary>
    /// <param name="boundary">The otherwise structural owner mutation or separate data-work boundary.</param>
    [Theory]
    [InlineData("rename-source")]
    [InlineData("rename-target")]
    [InlineData("recreate")]
    [InlineData("data")]
    public async Task OwnerLifetimeChangesAndDataBoundariesDoNotAuthorizeRetirement(
        string boundary
    )
    {
        // Arrange
        await using var database = await RetirementDatabase.OpenAsync(false, CancellationToken.None);
        await using var context = new RetirementTargetContext(database.Connection, _internalServices);
        await CreateCycleAsync(database.Connection, CancellationToken.None);
        var operations = RetirementOperations();
        var change = boundary switch
        {
            "rename-source" => (MigrationOperation)new RenameTableOperation
            {
                Name = "retiring_parent",
                NewName = "renamed_parent",
            },
            "rename-target" => new RenameTableOperation
            {
                Name = "retained_host",
                NewName = "retiring_parent",
            },
            "recreate" => new CreateTableOperation { Name = "retiring_parent" },
            "data" => new InsertDataOperation
            {
                Table = "retained_host",
                Columns = ["id", "marker"],
                Values = new object[,] { { 99, "separate-work" } },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
        };

        operations.Insert(1, change);
        var before = await ReadSchemaAsync(database.Connection, CancellationToken.None);

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            operations,
            CancellationToken.None));

        var after = await ReadSchemaAsync(database.Connection, CancellationToken.None);

        // Assert
        Assert.Contains("table_rebuild_model_missing",
            Assert.IsType<InvalidOperationException>(exception)
                .Message,
            StringComparison.Ordinal);

        Assert.Equal(before, after);
    }

    /// <summary>Missing FK names remain no-ops while differently named live relationships retain their guard.</summary>
    [Fact]
    public async Task WrongForeignKeyNameCannotReleaseARealCycleDependency()
    {
        // Arrange
        await using var database = await RetirementDatabase.OpenAsync(false, CancellationToken.None);
        await using var context = new RetirementTargetContext(database.Connection, _internalServices);
        await CreateCycleAsync(database.Connection, CancellationToken.None);
        var operations = RetirementOperations();
        operations[0] = new SafeMigrationOperation(
            new DropForeignKeyIntent("wrong_foreign_key", "retiring_parent"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var before = await ReadSchemaAsync(database.Connection, CancellationToken.None);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                operations,
                new SafeMigrationRunOptions("sqlite-retirement-wrong-fk"),
                CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            operations,
            CancellationToken.None));

        var after = await ReadSchemaAsync(database.Connection, CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal("table_drop_foreign_key_dependency", report.Assessments[1].AnalysisCode);
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(before, after);
    }

    /// <summary>MySQL table metadata cannot silently become a SQLite contract, even for a missing container.</summary>
    /// <param name="convergence">Whether existence-only convergence would otherwise accept a table container.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignProviderTableMetadataRemainsUnsupported(
        bool convergence
    )
    {
        // Arrange
        await using var database = await RetirementDatabase.OpenAsync(false, CancellationToken.None);
        await using var context = new RetirementTargetContext(database.Connection, _internalServices);
        var native = new CreateTableOperation { Name = "foreign_engine" };
        native.Columns.Add(new AddColumnOperation
        {
            Name = "id",
            Table = native.Name,
            ClrType = typeof(int),
        });

        native.AddAnnotation("Doka:MySql:StorageEngine", "InnoDB");
        var definition = SafeMigrationExpectedDefinitionFactory.From(native);
        var operations = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        operations.EnsureTable(definition,
            convergence
                ? SafeMigrationTableMode.ConvergenceContainer
                : SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ExistenceOnly);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                operations.Operations,
                new SafeMigrationRunOptions("sqlite-foreign-table-metadata"),
                CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            operations.Operations,
            CancellationToken.None));

        var owners = await ScalarIntAsync(database.Connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'foreign_engine';",
            CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal("table_annotation", assessment.AnalysisCode);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
        Assert.Contains("table_annotation",
            Assert.IsType<InvalidOperationException>(exception)
                .Message,
            StringComparison.Ordinal);

        Assert.Equal(0, owners);
    }

    /// <summary>A retiring neighbor's rename cannot hide its original artifact behind a future DROP alias.</summary>
    /// <param name="artifact">The surviving artifact on the neighbor without an explicit FK removal.</param>
    /// <param name="renameCount">The number of native owner aliases before final deletion.</param>
    [Theory]
    [InlineData("view", 1)]
    [InlineData("view", 2)]
    [InlineData("trigger", 1)]
    [InlineData("trigger", 2)]
    public async Task RenamedRetiringNeighborCannotLendItsPreviousArtifactIdentity(
        string artifact,
        int renameCount
    )
    {
        // Arrange
        await using var database = await RetirementDatabase.OpenAsync(false, CancellationToken.None);
        await using var context = new RetirementTargetContext(database.Connection, _internalServices);
        await CreateCycleAsync(database.Connection, "child-" + artifact, CancellationToken.None);
        var operations = RetirementOperations();
        operations.RemoveAt(1);
        operations.Insert(1,
            new RenameTableOperation
            {
                Name = "retiring_child",
                NewName = "renamed_child",
            });

        if (renameCount == 2)
        {
            operations.Insert(2,
                new RenameTableOperation
                {
                    Name = "renamed_child",
                    NewName = "final_child",
                });
        }

        operations.Insert(1 + renameCount,
            new SafeMigrationOperation(
                new DropTableIntent(renameCount == 2
                    ? "final_child"
                    : "renamed_child"),
                SafeMigrationPolicy.ThrowIfDifferent));

        var before = await ReadSchemaAsync(database.Connection, CancellationToken.None);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                operations,
                new SafeMigrationRunOptions("sqlite-retirement-neighbor-lifetime"),
                CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            operations,
            CancellationToken.None));

        var after = await ReadSchemaAsync(database.Connection, CancellationToken.None);

        // Assert
        Assert.Equal("table_retirement_owner_lifetime", report.Assessments[0].AnalysisCode);
        Assert.Contains("table_retirement_owner_lifetime",
            Assert.IsType<InvalidOperationException>(exception)
                .Message,
            StringComparison.Ordinal);

        Assert.Equal(before, after);
    }

    /// <summary>A retained principal's rename remains independent from its removed child's retirement.</summary>
    /// <param name="ordinaryRename">Whether the rename is an ordinary EF operation in the guarded batch.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedPrincipalRenameDoesNotBecomeAnUnnecessaryRetirementRefusal(
        bool ordinaryRename
    )
    {
        // Arrange
        await using var database = await RetirementDatabase.OpenAsync(false, CancellationToken.None);
        await using var context = new RenamedHostTargetContext(database.Connection, _internalServices);
        await CreateRetainedPrincipalAsync(database.Connection, CancellationToken.None);

        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        builder.DropForeignKeyIfExists("fk_retiring_parent_host", "retiring_parent");
        if (ordinaryRename)
        {
            builder.RenameTable("retained_host", newName: "renamed_host");
        }
        else
        {
            builder.RenameTableIfExists("retained_host", "renamed_host");
        }

        builder.DropTableIfExists("retiring_parent");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-retained-principal-rename"),
                CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            builder.Operations,
            CancellationToken.None));

        var host = exception is null
            ? await ScalarIntAsync(database.Connection,
                "SELECT COUNT(*) FROM renamed_host WHERE id = 42 AND marker = 'retained';",
                CancellationToken.None)
            : -1;

        var view = exception is null
            ? await ScalarIntAsync(database.Connection,
                "SELECT COUNT(*) FROM retained_view WHERE marker = 'retained';",
                CancellationToken.None)
            : -1;

        // Assert
        var expectedStatus = ordinaryRename
            ? SafeMigrationReportStatus.ReadyWithProviderOperations
            : SafeMigrationReportStatus.Ready;

        Assert.True(report.Status == expectedStatus,
            Encoding.UTF8.GetString(SafeMigrationReportJson.SerializeToUtf8Bytes(report)));

        Assert.Null(exception);
        Assert.Equal(1, host);
        Assert.Equal(1, view);
    }

    /// <summary>Sequence-aware retirement retains incoming relationships on surviving external owners.</summary>
    /// <param name="ordinaryRename">Whether the independent principal rename is an ordinary EF operation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedPrincipalRenameDoesNotEraseAnIncomingRetirementBlocker(
        bool ordinaryRename
    )
    {
        // Arrange
        await using var database = await RetirementDatabase.OpenAsync(false, CancellationToken.None);
        await using var context = new RenamedHostTargetContext(database.Connection, _internalServices);
        await CreateRetainedPrincipalAsync(database.Connection, CancellationToken.None);
        await ExecuteSqlAsync(database.Connection,
            "CREATE TABLE surviving_child (id INTEGER PRIMARY KEY, parent_id INTEGER "
            + "REFERENCES retiring_parent(id) ON DELETE CASCADE); INSERT INTO surviving_child VALUES (7, 1);",
            CancellationToken.None);

        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        builder.DropForeignKeyIfExists("fk_retiring_parent_host", "retiring_parent");
        if (ordinaryRename)
        {
            builder.RenameTable("retained_host", newName: "renamed_host");
        }
        else
        {
            builder.RenameTableIfExists("retained_host", "renamed_host");
        }

        builder.DropTableIfExists("retiring_parent");
        var before = await ReadSchemaAsync(database.Connection, CancellationToken.None);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-renamed-retirement-blocker"),
                CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            builder.Operations,
            CancellationToken.None));

        var after = await ReadSchemaAsync(database.Connection, CancellationToken.None);
        var rows = await ScalarIntAsync(database.Connection,
            "SELECT COUNT(*) FROM surviving_child WHERE id = 7 AND parent_id = 1;",
            CancellationToken.None);

        // Assert
        Assert.Equal("table_drop_foreign_key_dependency", report.Assessments[^1].AnalysisCode);
        Assert.Contains("table_drop_foreign_key_dependency",
            Assert.IsType<InvalidOperationException>(exception)
                .Message,
            StringComparison.Ordinal);

        Assert.Equal(before, after);
        Assert.Equal(1, rows);
    }

    /// <summary>Native terminal-removal evidence is bound to the exact operation, analysis and invocation.</summary>
    [Fact]
    public async Task SequenceAwareRetirementEvidenceCannotBeBorrowedOrReusedAfterReset()
    {
        // Arrange
        await using var database = await RetirementDatabase.OpenAsync(false, CancellationToken.None);
        await using var context = new RetirementTargetContext(database.Connection, _internalServices);
        await CreateCycleAsync(database.Connection, CancellationToken.None);
        var operations = RetirementOperations();
        var operation = (SafeMigrationOperation)operations[^1];
        var analyzer = Assert.IsType<SqliteSafeMigrationProviderAnalyzer>(
            context.GetService<ISafeMigrationProviderAnalyzer>());

        var copy = new SafeMigrationOperation(operation.Intent, operation.Policy);

        // Act
        var analyses = await analyzer.AnalyzeAsync(context, operations, null, CancellationToken.None);
        var captured = analyses[^1];
        var original = analyzer.IsSequenceAwareAnalysis(operation, captured);
        var copiedOperation = analyzer.IsSequenceAwareAnalysis(copy, captured);
        var copiedAnalysis = analyzer.IsSequenceAwareAnalysis(operation,
            new SafeMigrationProviderAnalysis(captured.ObservedState,
                captured.RepairCapability,
                captured.PostconditionSatisfied,
                captured.Code));

        analyzer.ResetDropOnlyProjection(SqliteSafeMigrationCatalog.Read(database.Connection, null));
        var reset = analyzer.IsSequenceAwareAnalysis(operation, captured);

        // Assert
        Assert.True(original);
        Assert.False(copiedOperation);
        Assert.False(copiedAnalysis);
        Assert.False(reset);
    }

    /// <summary>Builds the exact same-window logical removal of the cycle and both owners.</summary>
    private static List<MigrationOperation> RetirementOperations()
    {
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        builder.DropForeignKeyIfExists("fk_retiring_parent_child", "retiring_parent");
        builder.DropTableIfExists("retiring_child");
        builder.DropTableIfExists("retiring_parent");

        return builder.Operations;
    }

    /// <summary>Creates only the synthetic preexisting state, retaining native foreign-key enforcement.</summary>
    private static Task CreateCycleAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken
    ) => CreateCycleAsync(connection, "none", cancellationToken);

    /// <summary>Creates a retiring child and a retained principal with a native rename-preserved view.</summary>
    private static Task CreateRetainedPrincipalAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken
    ) => ExecuteSqlAsync(connection,
        """
        CREATE TABLE retained_host (id INTEGER NOT NULL PRIMARY KEY, marker TEXT NOT NULL);
        INSERT INTO retained_host VALUES (42, 'retained');
        CREATE VIEW retained_view AS SELECT marker FROM retained_host;
        CREATE TABLE retiring_parent (id INTEGER NOT NULL PRIMARY KEY, host_id INTEGER NOT NULL,
            CONSTRAINT fk_retiring_parent_host FOREIGN KEY (host_id) REFERENCES retained_host(id));
        INSERT INTO retiring_parent VALUES (1, 42);
        """,
        cancellationToken);

    /// <summary>Creates the native fixture with one independently selected unsupported table shape.</summary>
    private static async Task CreateCycleAsync(
        SqliteConnection connection,
        string artifact,
        CancellationToken cancellationToken
    )
    {
        var childArtifact = artifact.StartsWith("child-", StringComparison.Ordinal);
        var shape = childArtifact
            ? artifact[6..]
            : artifact;

        var tableOptions = shape switch
        {
            "strict" => " STRICT",
            "without-rowid" => " WITHOUT ROWID",
            _ => string.Empty,
        };

        var generated = shape == "generated"
            ? "derived INTEGER GENERATED ALWAYS AS (id + 1) VIRTUAL,"
            : string.Empty;

        var foreignKeyOptions = shape == "provider-option"
            ? " DEFERRABLE INITIALLY DEFERRED"
            : string.Empty;

        var parentOptions = childArtifact
            ? string.Empty
            : tableOptions;

        var childOptions = childArtifact
            ? tableOptions
            : string.Empty;

        var parentGenerated = childArtifact
            ? string.Empty
            : generated;

        var childGenerated = childArtifact
            ? generated
            : string.Empty;

        var parentForeignKeyOptions = childArtifact
            ? string.Empty
            : foreignKeyOptions;

        var childForeignKeyOptions = childArtifact
            ? foreignKeyOptions
            : string.Empty;

        await ExecuteSqlAsync(connection,
            $$"""
            CREATE TABLE retained_host (id INTEGER NOT NULL PRIMARY KEY, marker TEXT NOT NULL);
            CREATE INDEX ix_retained_host_marker ON retained_host(marker);
            INSERT INTO retained_host VALUES (42, 'retained');
            CREATE TABLE retiring_parent (
                id INTEGER NOT NULL PRIMARY KEY,
                child_id INTEGER NULL,
                {{parentGenerated}}
                CONSTRAINT fk_retiring_parent_child FOREIGN KEY (child_id)
                    REFERENCES retiring_child(id){{parentForeignKeyOptions}}){{parentOptions}};
            CREATE TABLE retiring_child (
                id INTEGER NOT NULL PRIMARY KEY,
                parent_id INTEGER NULL,
                {{childGenerated}}
                CONSTRAINT fk_retiring_child_parent FOREIGN KEY (parent_id)
                    REFERENCES retiring_parent(id){{childForeignKeyOptions}}){{childOptions}};
            INSERT INTO retiring_parent (id, child_id) VALUES (1, NULL);
            INSERT INTO retiring_child (id, parent_id) VALUES (2, 1);
            UPDATE retiring_parent SET child_id = 2 WHERE id = 1;
            """,
            cancellationToken);

        var artifactOwner = childArtifact
            ? "retiring_child"
            : "retiring_parent";

        var artifactColumn = childArtifact
            ? "parent_id"
            : "child_id";

        var artifactSql = shape switch
        {
            "trigger" => $"CREATE TRIGGER retirement_trigger BEFORE UPDATE ON {artifactOwner} BEGIN SELECT 1; END;",
            "external-trigger" => "CREATE TRIGGER retirement_trigger AFTER INSERT ON retained_host "
                + $"BEGIN UPDATE {artifactOwner} SET {artifactColumn} = NULL; END;",
            "view" => $"CREATE VIEW retirement_view AS SELECT id FROM {artifactOwner};",
            "partial-index" => $"CREATE INDEX retirement_index ON {artifactOwner}({artifactColumn}) "
                + $"WHERE {artifactColumn} IS NOT NULL;",
            "expression-index" => $"CREATE INDEX retirement_index ON {artifactOwner}(id + 1);",
            _ => null,
        };

        if (artifactSql is not null)
        {
            await ExecuteSqlAsync(connection, artifactSql, cancellationToken);
        }
    }

    /// <summary>Reads exact schema text so refusal and rollback cannot silently remove native artifacts.</summary>
    private static async Task<string> ReadSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(type || ':' || name || ':' || sql, char(10)) "
            + "FROM (SELECT type, name, sql FROM sqlite_schema ORDER BY type, name);";

        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture)
            ?? string.Empty;
    }

    /// <summary>Maps the retained final schema without manufacturing metadata for retired tables.</summary>
    private sealed class RetirementTargetContext : DbContext
    {
        private readonly SqliteConnection _connection;
        private readonly IServiceProvider _internalServices;

        public RetirementTargetContext(
            SqliteConnection connection,
            IServiceProvider internalServices
        )
        {
            _connection = connection;
            _internalServices = internalServices;
        }

        protected override void OnConfiguring(
            DbContextOptionsBuilder optionsBuilder
        ) => optionsBuilder
            .UseSqlite(_connection)
            .UseSqliteSafeMigrations()
            .UseInternalServiceProvider(_internalServices);

        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            modelBuilder.Entity("RetainedHost",
                entity =>
                {
                    entity.ToTable("retained_host");
                    entity
                        .Property<int>("id")
                        .ValueGeneratedNever();

                    entity
                        .Property<string>("marker")
                        .IsRequired();

                    entity.HasKey("id");
                    entity
                        .HasIndex("marker")
                        .HasDatabaseName("ix_retained_host_marker");
                });
        }
    }

    /// <summary>Maps only the preserved final principal under its renamed native identity.</summary>
    private sealed class RenamedHostTargetContext : DbContext
    {
        private readonly SqliteConnection _connection;
        private readonly IServiceProvider _internalServices;

        public RenamedHostTargetContext(
            SqliteConnection connection,
            IServiceProvider internalServices
        )
        {
            _connection = connection;
            _internalServices = internalServices;
        }

        protected override void OnConfiguring(
            DbContextOptionsBuilder optionsBuilder
        ) => optionsBuilder
            .UseSqlite(_connection)
            .UseSqliteSafeMigrations()
            .UseInternalServiceProvider(_internalServices);

        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            modelBuilder.Entity("RetainedHost",
                entity =>
                {
                    entity.ToTable("renamed_host");
                    entity
                        .Property<int>("id")
                        .ValueGeneratedNever();

                    entity
                        .Property<string>("marker")
                        .IsRequired();

                    entity.HasKey("id");
                });
        }
    }

    /// <summary>Owns a test database and its connection; cleanup remains independent of request cancellation.</summary>
    private sealed class RetirementDatabase : IAsyncDisposable
    {
        private readonly string? _path;

        private RetirementDatabase(
            SqliteConnection connection,
            string? path
        )
        {
            Connection = connection;
            _path = path;
        }

        public SqliteConnection Connection { get; }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            if (_path is not null)
            {
                File.Delete(_path);
            }
        }

        public static async Task<RetirementDatabase> OpenAsync(
            bool fileBacked,
            CancellationToken cancellationToken
        )
        {
            var path = fileBacked
                ? Path.Combine(Path.GetTempPath(), $"sqlite-retirement-{Guid.NewGuid():N}.db")
                : null;

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path ?? ":memory:",
                ForeignKeys = true,
                Pooling = false,
            }.ToString());

            var database = new RetirementDatabase(connection, path);
            try
            {
                await connection.OpenAsync(cancellationToken);

                return database;
            }
            catch
            {
                await database.DisposeAsync();

                throw;
            }
        }
    }
}
