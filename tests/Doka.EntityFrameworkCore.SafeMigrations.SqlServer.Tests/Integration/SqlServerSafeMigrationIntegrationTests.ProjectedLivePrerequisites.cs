namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>
    /// An empty owner cannot make a computed dependent operand eligible under the native FK contract.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ForeignKey_NewContainerComputedOperandRemainsBlocked()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.sparse_parent (Id int NOT NULL CONSTRAINT PK_sparse_parent PRIMARY KEY);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Operations.Add(new SafeMigrationOperation(new EnsureTableIntent(
            new ExpectedTableDefinition("sparse_child", [new ExpectedColumnDefinition("Id", typeof(int), false,
                "int")]), SafeMigrationTableMode.ConvergenceContainer), SafeMigrationPolicy.ThrowIfDifferent));
        builder.AddColumnIfNotExists<int>("ParentId", "sparse_child", type: "int", nullable: true,
            computedColumnSql: "1", stored: true);
        builder.AddForeignKeyIfNotExists("FK_sparse_child_parent", "sparse_child", ["ParentId"],
            "sparse_parent", ["Id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-sparse-computed-negative"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, report.Assessments[2].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[2].Action);
    }

    /// <summary>A convergence container can add a later FK operand to its accepted empty native lifetime.</summary>
    [SqlServerLiveFact]
    public async Task ForeignKey_NewContainerThenNullableOperandAppliesAndVerifies()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.sparse_parent (Id int NOT NULL CONSTRAINT PK_sparse_parent PRIMARY KEY);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Operations.Add(new SafeMigrationOperation(new EnsureTableIntent(
            new ExpectedTableDefinition("sparse_child", [new ExpectedColumnDefinition("Id", typeof(int), false,
                "int")]), SafeMigrationTableMode.ConvergenceContainer), SafeMigrationPolicy.ThrowIfDifferent));
        builder.AddColumnIfNotExists<int>("ParentId", "sparse_child", type: "int", nullable: true);
        builder.CreateIndexIfNotExists("IX_sparse_child_ParentId", "sparse_child", ["ParentId"]);
        builder.AddForeignKeyIfNotExists("FK_sparse_child_parent", "sparse_child", ["ParentId"],
            "sparse_parent", ["Id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-sparse-new-container"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-sparse-new-container-postflight"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, static assessment => Assert.Equal(SafeMigrationAction.Apply,
            assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
    }

    /// <summary>A later existence-only type proposal preserves the accepted native operand contract.</summary>
    /// <param name="existenceOnly">Whether the different proposal may be an existence-only no-op.</param>
    [SqlServerLiveTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForeignKey_SparseAcceptedDefinitionSurvivesDifferentProposal(
        bool existenceOnly
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.sparse_parent (Id int NOT NULL CONSTRAINT PK_sparse_parent PRIMARY KEY); "
            + "CREATE TABLE dbo.sparse_child (Id int NOT NULL CONSTRAINT PK_sparse_child PRIMARY KEY); "
            + "INSERT INTO dbo.sparse_child VALUES (1), (2);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("ParentId", "sparse_child", type: "int", nullable: true);
        builder.AddColumnIfNotExists<string>("ParentId", "sparse_child", type: "nvarchar(20)", nullable: true,
            policy: existenceOnly ? SafeMigrationPolicy.ExistenceOnly : SafeMigrationPolicy.ThrowIfDifferent);
        builder.CreateIndexIfNotExists("IX_sparse_child_ParentId", "sparse_child", ["ParentId"]);
        builder.AddForeignKeyIfNotExists("FK_sparse_child_parent", "sparse_child", ["ParentId"],
            "sparse_parent", ["Id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-sparse-accepted-definition"));

        if (existenceOnly)
        {
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        var physicalColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id "
            + "WHERE c.object_id = OBJECT_ID(N'dbo.sparse_child', N'U') AND c.name = N'ParentId' "
            + "AND ty.name = N'int';");

        // Assert
        Assert.Equal(existenceOnly ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            report.Status);
        Assert.Equal(existenceOnly ? SafeMigrationAction.NoOp : SafeMigrationAction.RejectDifferent,
            report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[3].Action);
        Assert.Equal(existenceOnly ? 1 : 0, physicalColumns);
    }

    /// <summary>Combines a newly added NULL column with an untouched live principal and survives replay.</summary>
    [SqlServerLiveFact]
    public async Task ForeignKey_NewNullableColumnAndLiveParentApplyReplayAndVerify()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.sparse_parent (Id int NOT NULL CONSTRAINT PK_sparse_parent PRIMARY KEY); "
            + "CREATE TABLE dbo.sparse_child (Id int NOT NULL CONSTRAINT PK_sparse_child PRIMARY KEY); "
            + "INSERT INTO dbo.sparse_parent VALUES (7); INSERT INTO dbo.sparse_child VALUES (1), (2);");
        await using var context = CreateContext(connectionString);
        var builder = SparseForeignKeyOperations(context);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-sparse-live-parent"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-sparse-live-parent-postflight"));

        var nullRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.sparse_child WHERE ParentId IS NULL;");

        var trusted = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.foreign_keys WHERE name = N'FK_sparse_child_parent' "
            + "AND is_disabled = 0 AND is_not_trusted = 0;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(3, preflight.Assessments.Count);
        Assert.All(preflight.Assessments, static assessment => Assert.Equal(SafeMigrationAction.Apply,
            assessment.Action));
        Assert.Equal(2, nullRows);
        Assert.Equal(1, trusted);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
    }

    /// <summary>Rejects incompatible storage or a missing live candidate key without applying earlier DDL.</summary>
    /// <param name="hasKey">Whether the untouched parent owns a native candidate key.</param>
    /// <param name="parentType">The native parent storage compared with the newly added integer column.</param>
    [SqlServerLiveTheory]
    [InlineData(false, "int")]
    [InlineData(true, "bigint")]
    public async Task ForeignKey_NewNullableColumnDoesNotInventLiveKeyOrCompatibleStorage(
        bool hasKey,
        string parentType
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var key = hasKey ? " CONSTRAINT PK_sparse_parent PRIMARY KEY" : string.Empty;
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE dbo.sparse_parent (Id {parentType} NOT NULL{key}); "
            + "CREATE TABLE dbo.sparse_child (Id int NOT NULL CONSTRAINT PK_sparse_child PRIMARY KEY); "
            + "INSERT INTO dbo.sparse_child VALUES (1), (2);");
        await using var context = CreateContext(connectionString);
        var builder = SparseForeignKeyOperations(context);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-sparse-parent-negative"));

        var addedColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.sparse_child', N'U') "
            + "AND name = N'ParentId';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, report.Assessments[2].ObservedState);
        Assert.Equal(0, addedColumns);
    }

    /// <summary>A concurrent writer after preflight cannot insert an orphan through a stale all-NULL proof.</summary>
    [SqlServerLiveFact]
    public async Task ForeignKey_SparseNullableProofRechecksRowsWrittenAfterPreflight()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.sparse_parent (Id int NOT NULL CONSTRAINT PK_sparse_parent PRIMARY KEY); "
            + "CREATE TABLE dbo.sparse_child (Id int NOT NULL CONSTRAINT PK_sparse_child PRIMARY KEY); "
            + "INSERT INTO dbo.sparse_child VALUES (1);");
        await using var context = CreateContext(connectionString);
        var builder = SparseForeignKeyOperations(context);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-sparse-concurrent-orphan"));

        await ExecuteOperationsAsync(context, builder.Operations.Take(2).ToArray());
        await ExecuteSqlAsync(connectionString, "UPDATE dbo.sparse_child SET ParentId = 999 WHERE Id = 1;");
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var foreignKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.foreign_keys WHERE name = N'FK_sparse_child_parent';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(51003, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(0, foreignKeys);
    }

    /// <summary>One newly NULL operand permits composite FK rows without inventing the untouched operand.</summary>
    [SqlServerLiveFact]
    public async Task ForeignKey_SparseCompositeCombinesLiveAndProjectedDependentColumns()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.sparse_parent (TenantId int NOT NULL, Id int NOT NULL, "
            + "CONSTRAINT PK_sparse_parent PRIMARY KEY (TenantId, Id)); "
            + "CREATE TABLE dbo.sparse_child (Id int NOT NULL PRIMARY KEY, TenantId int NOT NULL); "
            + "INSERT INTO dbo.sparse_child VALUES (1, 123), (2, 456);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("ParentId", "sparse_child", type: "int", nullable: true);
        builder.CreateIndexIfNotExists("IX_sparse_child_parent", "sparse_child", ["TenantId", "ParentId"]);
        builder.AddForeignKeyIfNotExists("FK_sparse_child_parent", "sparse_child", ["TenantId", "ParentId"],
            "sparse_parent", ["TenantId", "Id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-sparse-composite"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-sparse-composite-postflight"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
    }

    /// <summary>New NULL operands do not weaken SQL Server's unfiltered unique-key semantics.</summary>
    /// <param name="unique">Whether the sparse mixed-column index requires native uniqueness.</param>
    /// <param name="filtered">Whether a supported predicate excludes every freshly materialized NULL tuple.</param>
    [SqlServerLiveTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SparseMixedIndex_KeepsNativeMultipleNullUniqueness(
        bool unique,
        bool filtered
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.sparse_index (Id int NOT NULL PRIMARY KEY, TenantId int NOT NULL); "
            + "INSERT INTO dbo.sparse_index VALUES (1, 7), (2, 7);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("Code", "sparse_index", type: "int", nullable: true);
        builder.CreateIndexIfNotExists("IX_sparse_index", "sparse_index", ["TenantId", "Code"], unique: unique,
            filter: filtered ? "[Code] IS NOT NULL" : null);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-sparse-mixed-index"));

        // Assert
        Assert.Equal(unique && !filtered ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready,
            report.Status);
        Assert.Equal(unique && !filtered ? SafeMigrationAction.RejectPrerequisiteMissing : SafeMigrationAction.Apply,
            report.Assessments[1].Action);
        if (unique && !filtered)
        {
            Assert.Equal("projected_key_data_state_unknown", report.Assessments[1].AnalysisCode);
        }
    }

    /// <summary>A new NULL disjunct does not invent the independent empty-owner CHECK certificate.</summary>
    [SqlServerLiveFact]
    public async Task SparseCheck_MixesRequestedLiveAndNewNullOperands()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.sparse_check (Id int NOT NULL PRIMARY KEY, Value int NOT NULL); "
            + "INSERT INTO dbo.sparse_check VALUES (1, -7), (2, -9);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("Code", "sparse_check", type: "int", nullable: true);
        builder.AddCheckConstraintIfNotExists("CK_sparse_check", "sparse_check", "[Code] IS NULL OR [Value] > 0");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-sparse-check"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);
    }

    /// <summary>A current empty-owner row certificate retains the native CHECK operand-type boundary.</summary>
    /// <param name="liveType">The ordinary live scalar storage used beside the new required integer column.</param>
    /// <param name="supported">Whether the provider's integer CHECK contract supports that live operand.</param>
    [SqlServerLiveTheory]
    [InlineData("int", true)]
    [InlineData("varchar(20)", false)]
    [InlineData("bit", false)]
    public async Task SparseCheck_EmptyOwnerStillRequiresSupportedNativeOperands(
        string liveType,
        bool supported
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE dbo.sparse_check (Id int NOT NULL PRIMARY KEY, Value {liveType} NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("Code", "sparse_check", type: "int", nullable: false);
        builder.AddCheckConstraintIfNotExists("CK_sparse_check", "sparse_check", "[Code] > 0 AND [Value] > 0");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-sparse-empty-check"));

        // Assert
        Assert.Equal(supported ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(supported ? SafeMigrationAction.Apply : SafeMigrationAction.RejectUnsupported,
            report.Assessments[1].Action);
        if (!supported)
        {
            Assert.Equal("check_column_type_unproven", report.Assessments[1].AnalysisCode);
        }
    }

    /// <summary>An entirely new empty owner retains the same physical CHECK scalar boundary.</summary>
    /// <param name="supported">Whether both authored scalar operands use the supported integer contract.</param>
    [SqlServerLiveTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SparseCheck_NewOwnerStillRequiresSupportedAuthoredOperands(
        bool supported
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (supported)
        {
            builder.CreateTableIfNotExists("sparse_check", table => new
            {
                Code = table.Column<int>(type: "int", nullable: false),
                Value = table.Column<int>(type: "int", nullable: false),
            });
        }
        else
        {
            builder.CreateTableIfNotExists("sparse_check", table => new
            {
                Code = table.Column<int>(type: "int", nullable: false),
                Value = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
            });
        }

        builder.AddCheckConstraintIfNotExists("CK_sparse_check", "sparse_check", "[Code] > 0 AND [Value] > 0");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-new-owner-check"));

        // Assert
        Assert.Equal(supported ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(supported ? SafeMigrationAction.Apply : SafeMigrationAction.RejectUnsupported,
            report.Assessments[1].Action);
    }

    /// <summary>NULL rows do not make physically different endpoint collations compatible.</summary>
    /// <param name="compatible">Whether the projected operand has the native parent's exact collation.</param>
    [SqlServerLiveTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForeignKey_SparseCharacterOperandRequiresNativeCollation(
        bool compatible
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.sparse_parent (Id char(36) COLLATE Latin1_General_100_BIN2 NOT NULL "
            + "CONSTRAINT PK_sparse_parent PRIMARY KEY); "
            + "CREATE TABLE dbo.sparse_child (Id int NOT NULL PRIMARY KEY); "
            + "INSERT INTO dbo.sparse_child VALUES (1), (2);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operation => operation.AddColumn<string>("ParentId", "sparse_child",
            type: "char(36)", nullable: true, collation: compatible ? "Latin1_General_100_BIN2"
                : "Latin1_General_100_CI_AS"));
        builder.CreateIndexIfNotExists("IX_sparse_child_parent", "sparse_child", ["ParentId"]);
        builder.AddForeignKeyIfNotExists("FK_sparse_child_parent", "sparse_child", ["ParentId"],
            "sparse_parent", ["Id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-sparse-collation"));

        // Assert
        Assert.Equal(compatible ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(compatible ? SafeMigrationAction.Apply : SafeMigrationAction.RejectPrerequisiteMissing,
            report.Assessments[2].Action);
    }

    /// <summary>Constraint-backed indexes cannot become independent live certificates after an accepted drop.</summary>
    /// <param name="unique">Whether the parent candidate is a UNIQUE constraint rather than a primary key.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKey_SparseLiveKeyCannotSurviveItsOwningConstraintDrop(
        bool unique
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.sparse_parent (Id int NOT NULL CONSTRAINT K_sparse_parent "
            + (unique ? "UNIQUE" : "PRIMARY KEY") + "); "
            + "CREATE TABLE dbo.sparse_child (Id int NOT NULL PRIMARY KEY); INSERT dbo.sparse_child VALUES (1);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("ParentId", "sparse_child", type: "int", nullable: true);
        if (unique)
        {
            builder.DropUniqueConstraintIfExists("K_sparse_parent", "sparse_parent");
        }
        else
        {
            builder.DropPrimaryKeyIfExists("K_sparse_parent", "sparse_parent");
        }

        builder.AddForeignKeyIfNotExists("FK_sparse_child_parent", "sparse_child", ["ParentId"],
            "sparse_parent", ["Id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-sparse-dropped-candidate"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[2].Action);
    }

    /// <summary>A new NULL column can reference the surviving native key on the same existing physical owner.</summary>
    [SqlServerLiveFact]
    public async Task ForeignKey_SparseSelfReferenceUsesTheUntouchedNativeKey()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.sparse_self (Id int NOT NULL CONSTRAINT PK_sparse_self PRIMARY KEY); "
            + "INSERT INTO dbo.sparse_self VALUES (1), (2);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("ParentId", "sparse_self", type: "int", nullable: true);
        builder.CreateIndexIfNotExists("IX_sparse_self", "sparse_self", ["ParentId"]);
        builder.AddForeignKeyIfNotExists("FK_sparse_self", "sparse_self", ["ParentId"], "sparse_self", ["Id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-sparse-self"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-sparse-self-postflight"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
    }

    /// <summary>Builds the isolated three-operation stream without parent or child convergence containers.</summary>
    private static MigrationBuilder SparseForeignKeyOperations(
        DbContext context
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("ParentId", "sparse_child", type: "int", nullable: true);
        builder.CreateIndexIfNotExists("IX_sparse_child_ParentId", "sparse_child", ["ParentId"]);
        builder.AddForeignKeyIfNotExists("FK_sparse_child_parent", "sparse_child", ["ParentId"],
            "sparse_parent", ["Id"]);

        return builder;
    }
}
