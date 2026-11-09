namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies explicit engine contracts against isolated native databases and physical catalog state.</summary>
public sealed class MySqlInnoDbTableIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the shared engine with one owned database per test case.</summary>
    /// <param name="fixture">The initialized engine fixture.</param>
    public MySqlInnoDbTableIntegrationTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Generated scripts preserve explicit engine creation and row-preserving replay.</summary>
    /// <param name="convergence">Whether to generate granular convergence operations.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitEngineScriptOverridesDefaultAndPreservesReplay(
        bool convergence
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = CreateBuilder(context, convergence, SafeMigrationPolicy.ThrowIfDifferent);
        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate(
                builder.Operations,
                context.Model,
                MigrationsSqlGenerationOptions.Script);

        var script = "SET SESSION default_storage_engine=MyISAM;\n"
            + string.Join(Environment.NewLine, commands.Select(static command => command.CommandText));

        // Act
        await ExecuteMigrationScriptAsync(connectionString, script, CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "INSERT INTO `items` (`Id`) VALUES (42);", CancellationToken.None);
        await ExecuteMigrationScriptAsync(connectionString, script, CancellationToken.None);
        var engine = await ScalarStringAsync(connectionString,
            "SELECT ENGINE FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='items';",
            CancellationToken.None);

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `items` WHERE `Id`=42;",
            CancellationToken.None);

        // Assert
        Assert.Equal("InnoDB", engine, ignoreCase: true);
        Assert.Equal(1, rows);
    }

    /// <summary>A generated script must reject engine drift through its owned native guard without mutation.</summary>
    /// <param name="convergence">Whether the script compares only the convergence container.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitEngineScriptRejectsMismatchWithoutMutation(
        bool convergence
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `items` (`Id` int NOT NULL) ENGINE=MyISAM; INSERT INTO `items` VALUES (42);",
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = CreateBuilder(context,
            convergence,
            convergence
                ? SafeMigrationPolicy.RepairIfSafe
                : SafeMigrationPolicy.ExistenceOnly);

        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate(
                builder.Operations,
                context.Model,
                MigrationsSqlGenerationOptions.Script);

        var script = string.Join(Environment.NewLine, commands.Select(static command => command.CommandText));

        // Act
        var exception = await Record.ExceptionAsync(() =>
            ExecuteMigrationScriptAsync(connectionString, script, CancellationToken.None));

        var engine = await ScalarStringAsync(connectionString,
            "SELECT ENGINE FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='items';",
            CancellationToken.None);

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `items` WHERE `Id`=42;",
            CancellationToken.None);

        // Assert
        var native = Assert.IsType<MySqlException>(exception);
        Assert.Equal(1062, native.Number);
        Assert.Contains("doka_sm_unsupported", native.Message, StringComparison.Ordinal);
        Assert.Equal("MyISAM", engine, ignoreCase: true);
        Assert.Equal(1, rows);
    }

    /// <summary>Case-distinct owners cannot lend each other a transactional engine proof.</summary>
    /// <param name="target">The exact native owner whose engine is requested.</param>
    /// <param name="accepted">Whether that owner is actually InnoDB.</param>
    [Theory]
    [InlineData("items", true)]
    [InlineData("ITEMS", false)]
    public async Task EngineEvidencePreservesCaseDistinctOwnerIdentity(
        string target,
        bool accepted
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var ownerMode = await ScalarIntAsync(connectionString,
            "SELECT @@lower_case_table_names;",
            CancellationToken.None);

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `items` (`Id` int NOT NULL) ENGINE=InnoDB; INSERT INTO `items` VALUES (41); "
            + "CREATE TABLE `ITEMS` (`Id` int NOT NULL) ENGINE=MyISAM; INSERT INTO `ITEMS` VALUES (42);",
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExistsFromModel(operationBuilder => operationBuilder
                .CreateTable(target,
                    table => new { Id = table.Column<int>(type: "int", nullable: false) })
                .Annotation("Doka:MySql:StorageEngine", "InnoDB"),
            SafeMigrationPolicy.ExistenceOnly,
            SafeMigrationTableMode.ConvergenceContainer);

        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate(
                builder.Operations,
                context.Model,
                MigrationsSqlGenerationOptions.Script);

        var script = string.Join(Environment.NewLine, commands.Select(static command => command.CommandText));
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("case-distinct-engine-owner"),
            CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            ExecuteMigrationScriptAsync(connectionString, script, CancellationToken.None));

        var lowerRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `items` WHERE `Id`=41;",
            CancellationToken.None);

        var upperRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `ITEMS` WHERE `Id`=42;",
            CancellationToken.None);

        // Assert
        Assert.Equal(0, ownerMode);
        Assert.Equal(accepted
                ? SafeMigrationReportStatus.Ready
                : SafeMigrationReportStatus.Blocked,
            report.Status);

        if (accepted)
        {
            Assert.Null(exception);
        }
        else
        {
            var native = Assert.IsType<MySqlException>(exception);
            Assert.Equal(1062, native.Number);
            Assert.Contains("doka_sm_unsupported", native.Message, StringComparison.Ordinal);
        }

        Assert.Equal(1, lowerRows);
        Assert.Equal(1, upperRows);
    }

    /// <summary>Real complete generated migrations support both directions and idempotent replay.</summary>
    [Fact]
    public async Task CompleteScaffoldedEngineMigrationExecutesBothDirectionsAndReplay()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        using var runtime = new ServiceCollection()
            .AddEntityFrameworkDokaMySql()
            .AddEntityFrameworkDokaMySqlSafeMigrations()
            .BuildServiceProvider();

        var options = new DbContextOptionsBuilder()
            .UseInternalServiceProvider(runtime)
            .UseMySql(connectionString, Fixture.ServerVersion)
            .UseMySqlSafeMigrations()
            .Options;

        await using var context = new MySqlInnoDbTableScaffoldingTests.ExplicitModelContext(options);
        var migration = MySqlInnoDbTableScaffoldingTests.CompileCompleteMigration(context, out _);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        await ExecuteOperationsAsync(context, migration.UpOperations, CancellationToken.None);
        var upReport = await runner.VerifyAsync(context,
            migration.UpOperations,
            new SafeMigrationRunOptions("engine-full-scaffold-up"),
            CancellationToken.None);

        var downPreflight = await runner.AnalyzeAsync(context,
            migration.DownOperations,
            new SafeMigrationRunOptions("engine-full-scaffold-down"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, migration.DownOperations, CancellationToken.None);
        await ExecuteOperationsAsync(context, migration.DownOperations, CancellationToken.None);
        await ExecuteOperationsAsync(context, migration.UpOperations, CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync("INSERT INTO `EngineNodes` (`Id`) VALUES (42);",
            CancellationToken.None);

        await ExecuteOperationsAsync(context, migration.UpOperations, CancellationToken.None);
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `EngineNodes` WHERE `Id`=42;",
            CancellationToken.None);

        var engine = await ScalarStringAsync(connectionString,
            "SELECT ENGINE FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='EngineNodes';",
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, upReport.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, downPreflight.Status);
        Assert.Equal(1, rows);
        Assert.Equal("InnoDB", engine, ignoreCase: true);
    }

    /// <summary>An explicit engine overrides a nontransactional session default on creation and replay.</summary>
    /// <param name="convergence">Whether to use the granular convergence authoring path.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitInnoDbOverridesDefaultAndPreservesReplay(
        bool convergence
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync("SET SESSION default_storage_engine=MyISAM;",
            CancellationToken.None);

        var builder = CreateBuilder(context, convergence, SafeMigrationPolicy.ThrowIfDifferent);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("explicit-engine-create"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync("INSERT INTO `items` (`Id`) VALUES (42);", CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("explicit-engine-replay"),
            CancellationToken.None);

        var engine = await ScalarStringAsync(connectionString,
            "SELECT ENGINE FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='items';",
            CancellationToken.None);

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `items` WHERE `Id`=42;",
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal("InnoDB", engine, ignoreCase: true);
        Assert.Equal(1, rows);
    }

    /// <summary>No conflict policy may silently accept or rewrite an explicitly incompatible physical engine.</summary>
    /// <param name="convergence">Whether only the container shape is compared.</param>
    /// <param name="policy">The requested strict-operation conflict policy.</param>
    [Theory]
    [InlineData(false, SafeMigrationPolicy.ThrowIfDifferent)]
    [InlineData(false, SafeMigrationPolicy.RepairIfSafe)]
    [InlineData(false, SafeMigrationPolicy.ExistenceOnly)]
    [InlineData(true, SafeMigrationPolicy.ThrowIfDifferent)]
    [InlineData(true, SafeMigrationPolicy.RepairIfSafe)]
    public async Task ExistingNontransactionalEngineRemainsBlockedWithoutMutation(
        bool convergence,
        SafeMigrationPolicy policy
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `items` (`Id` int NOT NULL) ENGINE=MyISAM; INSERT INTO `items` VALUES (42);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = CreateBuilder(context, convergence, policy);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("explicit-engine-mismatch"),
            CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            builder.Operations,
            CancellationToken.None));

        var engine = await ScalarStringAsync(connectionString,
            "SELECT ENGINE FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='items';",
            CancellationToken.None);

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `items` WHERE `Id`=42;",
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        AssertOwnedGuardFailure(exception);
        var difference = Assert.Single(report.Assessments[0].Differences);
        Assert.Equal("table_storage_engine", difference.Facet);
        Assert.Equal("InnoDB", difference.Expected);
        Assert.Equal("MyISAM", difference.Actual, ignoreCase: true);
        Assert.Equal("MyISAM", engine, ignoreCase: true);
        Assert.Equal(1, rows);
    }

    /// <summary>Projection uses the accepted explicit engine rather than unrelated session defaults.</summary>
    [Fact]
    public async Task ExplicitEngineAllowsProjectedRequiredAddUnderNontransactionalDefault()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync("SET SESSION default_storage_engine=MyISAM;",
            CancellationToken.None);

        var builder = CreateBuilder(context, convergence: true, SafeMigrationPolicy.ThrowIfDifferent);
        builder.AddColumnIfNotExists<int>("RequiredValue", "items", type: "int", nullable: false);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("explicit-engine-required-add"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("explicit-engine-required-add-after"),
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
    }

    /// <summary>A created explicit engine qualifies a new dependent FK even when session defaults would not.</summary>
    [Fact]
    public async Task ExplicitEngineAllowsProjectedForeignKeyUnderNontransactionalDefault()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `parents` (`Id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB;",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync("SET SESSION default_storage_engine=MyISAM;",
            CancellationToken.None);

        var builder = CreateBuilder(context, convergence: false, SafeMigrationPolicy.ThrowIfDifferent);
        builder.AddForeignKeyIfNotExists("fk_items_parents", "items", ["Id"], "parents", ["Id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("explicit-engine-fk"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("explicit-engine-fk-after"),
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
    }

    /// <summary>Engine metadata does not relax an exact strict-table contract after extra columns appear.</summary>
    /// <param name="explicitEngine">Whether the strict initial definition carries provider engine metadata.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StrictTableStillReportsLaterUnmodeledColumns(
        bool explicitEngine
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (explicitEngine)
        {
            builder.CreateTableIfNotExistsFromModel(AppendTable);
        }
        else
        {
            builder.CreateTableIfNotExists("items",
                table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                });
        }

        builder.AddColumnIfNotExists<int>("LaterValue", "items", type: "int", nullable: true);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var report = await runner.VerifyAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("strict-table-extra-column"),
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, report.Assessments[0].ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, report.Assessments[1].ObservedState);
    }

    /// <summary>Unrelated accepted structural work never erases an existing incompatible engine proof.</summary>
    /// <param name="prior">The earlier operation on the same physical table lifetime.</param>
    [Theory]
    [InlineData("add")]
    [InlineData("confirm")]
    [InlineData("container")]
    [InlineData("rename")]
    public async Task PriorStructuralWorkCannotHideAnEngineMismatch(
        string prior
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var source = prior == "rename"
            ? "source_items"
            : "items";

        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE `{source}` (`Id` int NOT NULL) ENGINE=MyISAM; INSERT INTO `{source}` VALUES (42);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        switch (prior)
        {
            case "add":
                builder.AddColumnIfNotExists<int>("Extra", "items", type: "int", nullable: true);
                break;
            case "confirm":
                builder.CreateTableIfNotExists("items",
                    table => new
                    {
                        Id = table.Column<int>(type: "int", nullable: false),
                    });

                break;
            case "container":
                builder.EnsureTable(new ExpectedTableDefinition("items",
                        [new ExpectedColumnDefinition("Id", typeof(int), isNullable: false, storeType: "int")]),
                    SafeMigrationTableMode.ConvergenceContainer,
                    SafeMigrationPolicy.ExistenceOnly);

                break;
            case "rename":
                builder.RenameTableIfExists("source_items", "items");
                break;
        }

        builder.ConvergeTableFromModel(AppendTable, SafeMigrationPolicy.ThrowIfDifferent);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("engine-proof-lifetime"),
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
    }

    /// <summary>An unproved or wrong newly created engine is never inferred from table existence.</summary>
    /// <param name="replaceExisting">Whether a previous physical lifetime is dropped first.</param>
    /// <param name="ordinaryCreate">Whether an ordinary EF operation explicitly creates MyISAM.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CreatedOwnerCannotInventAnExplicitInnoDbContract(
        bool replaceExisting,
        bool ordinaryCreate
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        if (replaceExisting)
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE TABLE `items` (`Id` int NOT NULL) ENGINE=MyISAM;",
                cancellationToken: CancellationToken.None);
        }

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync("SET SESSION default_storage_engine=MyISAM;",
            CancellationToken.None);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (replaceExisting)
        {
            builder.DropTableIfExists("items");
        }

        if (ordinaryCreate)
        {
            builder
                .CreateTable("items", table => new { Id = table.Column<int>(type: "int", nullable: false) })
                .Annotation("Doka:MySql:StorageEngine", "MyISAM");
        }
        else
        {
            builder.CreateTableIfNotExists("items",
                table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                });
        }

        builder.ConvergeTableFromModel(AppendTable, SafeMigrationPolicy.ThrowIfDifferent);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("unproved-created-engine"),
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
    }

    /// <summary>Native case equivalence and absence of an engine requirement do not create false rejections.</summary>
    /// <param name="targetEngine">The target engine, or null for an unqualified target contract.</param>
    /// <param name="convergence">Whether to compare only the certified container.</param>
    [Theory]
    [InlineData("InnoDB", false)]
    [InlineData("innodb", false)]
    [InlineData(null, false)]
    [InlineData("InnoDB", true)]
    [InlineData("innodb", true)]
    [InlineData(null, true)]
    public async Task CompatibleProjectedEngineContractsRemainAccepted(
        string? targetEngine,
        bool convergence
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExistsFromModel(operationBuilder => operationBuilder
            .CreateTable(
                "items",
                table => new { Id = table.Column<int>(type: "int", nullable: false) })
            .Annotation("Doka:MySql:StorageEngine", "innodb"));

        builder.CreateTableIfNotExistsFromModel(operationBuilder =>
            {
                var operation = operationBuilder.CreateTable("items",
                    table => new
                    {
                        Id = table.Column<int>(type: "int", nullable: false),
                    });

                if (targetEngine is not null)
                {
                    operation.Annotation("Doka:MySql:StorageEngine", targetEngine);
                }
            },
            mode: convergence
                ? SafeMigrationTableMode.ConvergenceContainer
                : SafeMigrationTableMode.StrictDefinition);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("compatible-created-engine"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
    }

    /// <summary>A valid earlier creation cannot qualify unsupported metadata on a subsequent target.</summary>
    /// <param name="metadata">The unsupported target metadata family.</param>
    /// <param name="convergence">Whether the target ignores ordinary shape differences.</param>
    [Theory]
    [InlineData("engine", false)]
    [InlineData("unknown", false)]
    [InlineData("null", false)]
    [InlineData("type", false)]
    [InlineData("whitespace", false)]
    [InlineData("extra", false)]
    [InlineData("engine", true)]
    [InlineData("unknown", true)]
    [InlineData("null", true)]
    [InlineData("type", true)]
    [InlineData("whitespace", true)]
    [InlineData("extra", true)]
    public async Task CreatedEngineProofCannotEraseUnsupportedTargetMetadata(
        string metadata,
        bool convergence
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = CreateBuilder(context, convergence: false, SafeMigrationPolicy.ThrowIfDifferent);
        builder.CreateTableIfNotExistsFromModel(operationBuilder =>
            {
                var operation = operationBuilder.CreateTable("items",
                    table => new
                    {
                        Id = table.Column<int>(type: "int", nullable: false),
                    });

                if (metadata == "unknown")
                {
                    operation.Annotation("Unknown:TableFacet", "InnoDB");

                    return;
                }

                object? engine = metadata switch
                {
                    "engine" => "MyISAM",
                    "null" => null,
                    "type" => true,
                    "whitespace" => " InnoDB ",
                    "extra" => "InnoDB",
                    _ => throw new ArgumentOutOfRangeException(nameof(metadata)),
                };

                operation.Annotation("Doka:MySql:StorageEngine", engine);
                if (metadata == "extra")
                {
                    operation.Annotation("Unknown:TableFacet", true);
                }
            },
            SafeMigrationPolicy.ExistenceOnly,
            convergence
                ? SafeMigrationTableMode.ConvergenceContainer
                : SafeMigrationTableMode.StrictDefinition);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("unsupported-created-engine-target"),
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, report.Assessments[1].ObservedState);
    }

    /// <summary>Table and column renames preserve the complete engine contract for later verification.</summary>
    [Fact]
    public async Task CreatedEngineContractSurvivesTableAndColumnRenames()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = CreateBuilder(context, convergence: false, SafeMigrationPolicy.ThrowIfDifferent);
        builder.RenameColumnIfExists("Id", "items", "RenamedId");
        builder.RenameTableIfExists("items", "renamed_items");
        builder.CreateTableIfNotExistsFromModel(operationBuilder => operationBuilder
            .CreateTable(
                "renamed_items",
                table => new { RenamedId = table.Column<int>(type: "int", nullable: false) })
            .Annotation("Doka:MySql:StorageEngine", "InnoDB"));

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("engine-rename-projection"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("engine-rename-final"),
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
    }

    /// <summary>An authorized replacement creates a new engine lifetime without the retired mismatch.</summary>
    [Fact]
    public async Task ExplicitReplacementDoesNotRetainTheOldEngineMismatch()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `items` (`Id` int NOT NULL) ENGINE=MyISAM;",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("items");
        builder.CreateTableIfNotExistsFromModel(AppendTable);
        builder.CreateTableIfNotExistsFromModel(AppendTable);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("engine-replacement"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("engine-replacement-final"),
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
    }

    private static MigrationBuilder CreateBuilder(
        DbContext context,
        bool convergence,
        SafeMigrationPolicy policy
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (convergence)
        {
            builder.ConvergeTableFromModel(AppendTable, policy);
        }
        else
        {
            builder.CreateTableIfNotExistsFromModel(AppendTable, policy);
        }

        return builder;
    }

    private static void AppendTable(
        MigrationBuilder builder
    ) => builder
        .CreateTable("items", table => new { Id = table.Column<int>(type: "int", nullable: false) })
        .Annotation("Doka:MySql:StorageEngine", "InnoDB");
}
