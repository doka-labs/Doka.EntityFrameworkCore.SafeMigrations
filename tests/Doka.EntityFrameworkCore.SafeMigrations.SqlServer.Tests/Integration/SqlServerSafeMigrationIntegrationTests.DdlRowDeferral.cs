namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>
    /// Initial constraints defer unknown server-trigger effects without requiring a server-level grant.
    /// </summary>
    /// <param name="family">The standalone row-dependent constraint or index family.</param>
    /// <param name="databaseOwnerOnly">Whether the caller has database ownership but no server metadata grant.</param>
    [SqlServerLiveTheory]
    [InlineData("primary", false)]
    [InlineData("primary", true)]
    [InlineData("unique", false)]
    [InlineData("unique", true)]
    [InlineData("foreign", false)]
    [InlineData("foreign", true)]
    [InlineData("check", false)]
    [InlineData("check", true)]
    [InlineData("index", false)]
    [InlineData("index", true)]
    public async Task DdlConstraints_InitialCreationDefersOnlyUnprovedServerVisibility(
        string family,
        bool databaseOwnerOnly
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.ddl_parent(Id int NOT NULL PRIMARY KEY); "
            + "CREATE USER ddl_database_owner WITHOUT LOGIN WITH DEFAULT_SCHEMA=dbo; "
            + "ALTER ROLE db_owner ADD MEMBER ddl_database_owner;");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        if (databaseOwnerOnly)
        {
            await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER=N'ddl_database_owner';");
        }

        await using var permissionCommand = context.Database.GetDbConnection().CreateCommand();
        permissionCommand.CommandText = "SELECT COALESCE(HAS_PERMS_BY_NAME(NULL,NULL,N'VIEW ANY DEFINITION'),0);";
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("ddl_created_values", table => new
        {
            Id = table.Column<int>(type: "int", nullable: false),
            Value = table.Column<int>(type: "int", nullable: false),
            ParentId = table.Column<int>(type: "int", nullable: true),
        });

        AppendDdlRowConstraint(builder, family, "ddl_created_values");
        var runner = context.GetService<ISafeMigrationRunner>();
        SafeMigrationRunReport initial;
        SafeMigrationRunReport replay;
        SafeMigrationRunReport postflight;
        int serverVisibility;

        // Act
        try
        {
            serverVisibility = Convert.ToInt32(await permissionCommand.ExecuteScalarAsync(),
                CultureInfo.InvariantCulture);
            initial = await runner.AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sqlserver-ddl-initial-" + family));

            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
            replay = await runner.AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sqlserver-ddl-replay-" + family));

            postflight = await runner.VerifyAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sqlserver-ddl-postflight-" + family));
        }
        finally
        {
            if (databaseOwnerOnly)
            {
                await context.Database.ExecuteSqlRawAsync("REVERT;");
            }
        }

        // Assert
        Assert.Equal(databaseOwnerOnly ? 0 : 1, serverVisibility);
        AssertDdlReportStatus(initial, databaseOwnerOnly ? SafeMigrationReportStatus.RuntimeValidationRequired
            : SafeMigrationReportStatus.Ready);
        Assert.Equal(SafeMigrationAction.Apply, initial.Assessments[0].Action);
        if (databaseOwnerOnly)
        {
            AssertDdlDeferredOrigin(initial.Assessments[1], "projected_ddl_visibility_data_unknown", 0,
                typeof(SafeMigrationOperation), migrationId: null);
        }
        else
        {
            Assert.Equal(SafeMigrationAction.Apply, initial.Assessments[1].Action);
            Assert.Null(initial.Assessments[1].DeferredOrigin);
        }

        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
    }

    /// <summary>Deferred candidate keys keep dependent initial-table operations conditional until runtime.</summary>
    /// <param name="family">The standalone primary, unique-constraint, or unique-index candidate key.</param>
    /// <param name="inlineForeignKey">Whether the child table owns its FK in the initial table definition.</param>
    [SqlServerLiveTheory]
    [InlineData("primary", false)]
    [InlineData("primary", true)]
    [InlineData("unique", false)]
    [InlineData("unique", true)]
    [InlineData("index", false)]
    [InlineData("index", true)]
    public async Task DdlConstraints_DeferredCandidateKeyRetainsDependentInitialTableRuntimeBoundary(
        string family,
        bool inlineForeignKey
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE USER ddl_chain_owner WITHOUT LOGIN WITH DEFAULT_SCHEMA=dbo; "
            + "ALTER ROLE db_owner ADD MEMBER ddl_chain_owner;");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER=N'ddl_chain_owner';");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("ddl_chain_parent",
            table => new { Id = table.Column<int>(type: "int", nullable: false) });
        switch (family)
        {
            case "primary":
                builder.AddPrimaryKeyIfNotExists("K_ddl_chain_parent", "ddl_chain_parent", ["Id"]);
                break;
            case "unique":
                builder.AddUniqueConstraintIfNotExists("K_ddl_chain_parent", "ddl_chain_parent", ["Id"]);
                break;
            case "index":
                builder.CreateIndexIfNotExists("K_ddl_chain_parent", "ddl_chain_parent", ["Id"], unique: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(family));
        }

        builder.CreateTableIfNotExists("ddl_chain_child",
            table => new { ParentId = table.Column<int>(type: "int", nullable: false) },
            constraints: inlineForeignKey
                ? table => table.ForeignKey("FK_ddl_chain_parent", row => row.ParentId, "ddl_chain_parent", "Id")
                : null);
        if (!inlineForeignKey)
        {
            builder.AddForeignKeyIfNotExists("FK_ddl_chain_parent", "ddl_chain_child", ["ParentId"],
                "ddl_chain_parent", ["Id"]);
        }

        var runner = context.GetService<ISafeMigrationRunner>();
        SafeMigrationRunReport initial;
        SafeMigrationRunReport replay;
        SafeMigrationRunReport postflight;

        // Act
        try
        {
            initial = await runner.AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sqlserver-ddl-initial-chain-" + family));

            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
            replay = await runner.AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sqlserver-ddl-initial-chain-replay-" + family));

            postflight = await runner.VerifyAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sqlserver-ddl-initial-chain-postflight-" + family));
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("REVERT;");
        }

        var foreignKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.ddl_chain_child') "
            + "AND name=N'FK_ddl_chain_parent' AND is_disabled=0 AND is_not_trusted=0;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, initial.Status);
        Assert.Equal(SafeMigrationAction.Apply, initial.Assessments[0].Action);
        AssertDdlDeferredOrigin(initial.Assessments[1], "projected_ddl_visibility_data_unknown", 0,
            typeof(SafeMigrationOperation), migrationId: null);
        AssertDdlDeferredOrigin(initial.Assessments[^1], "projected_provider_postcondition_unknown", 1,
            typeof(SafeMigrationOperation), migrationId: null);
        Assert.Equal(1, foreignKeys);
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
    }

    /// <summary>
    /// Pending EF migrations retain the owning migration and preceding DDL ordinal in deferred reports.
    /// </summary>
    [SqlServerLiveFact]
    public async Task DdlConstraints_DatabaseOwnerMigratesPendingInitialStreamAndReplaysHistory()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE USER ddl_history_owner WITHOUT LOGIN WITH DEFAULT_SCHEMA=dbo; "
            + "ALTER ROLE db_owner ADD MEMBER ddl_history_owner;");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER=N'ddl_history_owner';");
        var runner = context.GetService<ISafeMigrationRunner>();
        SafeMigrationRunReport initial;
        SafeMigrationRunReport replay;

        // Act
        try
        {
            initial = await runner.AnalyzePendingMigrationsAsync(context,
                new SafeMigrationRunOptions("sqlserver-ddl-pending-initial"));

            await context.Database.MigrateAsync();
            await context.Database.MigrateAsync();
            replay = await runner.AnalyzePendingMigrationsAsync(context,
                new SafeMigrationRunOptions("sqlserver-ddl-pending-replay"));
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("REVERT;");
        }

        var history = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId=N'"
            + SqlServerHistoryMigration.MigrationIdentifier + "';");

        var trusted = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.safe_history_probe') "
            + "AND name=N'CK_safe_history_probe_Id' AND is_disabled=0 AND is_not_trusted=0;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, initial.Status);
        AssertDdlDeferredOrigin(initial.Assessments[2], "projected_ddl_visibility_data_unknown", 1,
            typeof(SafeMigrationOperation), SqlServerHistoryMigration.MigrationIdentifier);
        AssertDdlDeferredOrigin(initial.Assessments[3], "projected_provider_postcondition_unknown", 2,
            typeof(SafeMigrationOperation), SqlServerHistoryMigration.MigrationIdentifier);
        Assert.Equal(SafeMigrationReportStatus.NoOperations, replay.Status);
        Assert.Equal(1, history);
        Assert.Equal(1, trusted);
    }

    /// <summary>
    /// Known CREATE_TABLE triggers invalidate captured rows; runtime guards reject their unsafe side effects.
    /// </summary>
    /// <param name="family">The constraint or unique-index guard proving fresh row validation.</param>
    /// <param name="insertedId">The identifier inserted by the earlier CREATE_TABLE trigger.</param>
    /// <param name="insertedValue">The candidate-key or CHECK value inserted by that trigger.</param>
    /// <param name="insertedParentId">The principal identifier inserted by that trigger.</param>
    [SqlServerLiveTheory]
    [InlineData("primary", 1, 2, 1)]
    [InlineData("unique", 2, 1, 1)]
    [InlineData("foreign", 2, 2, 999)]
    [InlineData("check", 2, -1, 1)]
    [InlineData("index", 2, 1, 1)]
    public async Task DdlConstraints_EnabledTriggerDefersAndRuntimeRejectsChangedRows(
        string family,
        int insertedId,
        int insertedValue,
        int insertedParentId
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.ddl_parent(Id int NOT NULL PRIMARY KEY); INSERT dbo.ddl_parent VALUES(1); "
            + "CREATE TABLE dbo.ddl_existing_values(Id int NOT NULL, Value int NOT NULL, ParentId int NULL); "
            + "INSERT dbo.ddl_existing_values VALUES(1,1,1); "
            + "CREATE USER ddl_trigger_owner WITHOUT LOGIN WITH DEFAULT_SCHEMA=dbo; "
            + "ALTER ROLE db_owner ADD MEMBER ddl_trigger_owner;");
        var insertedValues = insertedId.ToString(CultureInfo.InvariantCulture) + ","
            + insertedValue.ToString(CultureInfo.InvariantCulture) + ","
            + insertedParentId.ToString(CultureInfo.InvariantCulture);

        // WHY: CREATE TRIGGER owns its batch. The body mutates an unrelated existing
        // table, so neither a new-table emptiness proof nor the old row snapshot is valid.
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER inject_created_table_rows ON DATABASE FOR CREATE_TABLE AS BEGIN SET NOCOUNT ON; "
            + "IF EVENTDATA().value('(/EVENT_INSTANCE/ObjectName)[1]','nvarchar(128)')=N'ddl_trigger_probe' "
            + "INSERT dbo.ddl_existing_values VALUES(" + insertedValues + "); END;");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER=N'ddl_trigger_owner';");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("ddl_trigger_probe",
            table => new { Id = table.Column<int>(type: "int", nullable: false) });
        AppendDdlRowConstraint(builder, family, "ddl_existing_values");
        SafeMigrationRunReport initial;
        Exception? failure;

        // Act
        try
        {
            initial = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sqlserver-ddl-trigger-" + family));

            failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("REVERT;");
        }

        var alteredRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.ddl_existing_values WHERE Id=" + insertedId.ToString(CultureInfo.InvariantCulture)
            + " AND Value=" + insertedValue.ToString(CultureInfo.InvariantCulture)
            + " AND ParentId=" + insertedParentId.ToString(CultureInfo.InvariantCulture) + ";");

        var constraints = await ScalarIntAsync(connectionString,
            "SELECT (SELECT COUNT(*) FROM sys.objects WHERE parent_object_id=OBJECT_ID(N'dbo.ddl_existing_values') "
            + "AND type IN(N'C',N'UQ',N'PK',N'F')) + (SELECT COUNT(*) FROM sys.indexes "
            + "WHERE object_id=OBJECT_ID(N'dbo.ddl_existing_values') AND name=N'IX_ddl_values');");

        var createdTables = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'dbo.ddl_trigger_probe');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, initial.Status);
        AssertDdlDeferredOrigin(initial.Assessments[1], "projected_ddl_trigger_data_unknown", 0,
            typeof(SafeMigrationOperation), migrationId: null);
        Assert.Equal(51003, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(1, alteredRows);
        Assert.Equal(1, createdTables);
        Assert.Equal(0, constraints);
    }

    /// <summary>Unknown DDL side effects never excuse an independent denied row-read permission.</summary>
    [SqlServerLiveFact]
    public async Task DdlConstraints_MissingSelectRemainsBlockedAfterAcceptedCreate()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.ddl_denied_values(Value int NOT NULL); "
            + "INSERT dbo.ddl_denied_values VALUES(1); "
            + "CREATE USER ddl_writer WITHOUT LOGIN WITH DEFAULT_SCHEMA=dbo; "
            + "GRANT VIEW DEFINITION,CREATE TABLE TO ddl_writer; "
            + "GRANT ALTER ON SCHEMA::dbo TO ddl_writer; "
            + "GRANT SELECT ON OBJECT::sys.sql_expression_dependencies TO ddl_writer; "
            + "DENY SELECT ON OBJECT::dbo.ddl_denied_values TO ddl_writer;");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER=N'ddl_writer';");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("ddl_permission_probe",
            table => new { Id = table.Column<int>(type: "int", nullable: false) });
        builder.AddCheckConstraintIfNotExists("CK_ddl_denied", "ddl_denied_values", "[Value]>=0");
        SafeMigrationRunReport report;

        // Act
        try
        {
            report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sqlserver-ddl-independent-permission"));
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("REVERT;");
        }

        var createdTables = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'dbo.ddl_permission_probe');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationObservedState.Unsupported, report.Assessments[1].ObservedState);
        Assert.Equal("check_row_data_unproven", report.Assessments[1].AnalysisCode);
        Assert.Null(report.Assessments[1].DeferredOrigin);
        Assert.Equal(0, createdTables);
    }

    /// <summary>A deferred candidate-key mutation cannot hide a later independently denied CHECK row read.</summary>
    /// <param name="uniqueConstraint">Whether the deferred key is a unique constraint instead of a PK.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DdlConstraints_MissingSelectRemainsBlockedAfterDeferredCandidateKey(
        bool uniqueConstraint
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.ddl_denied_after_key(Value int NOT NULL); "
            + "INSERT dbo.ddl_denied_after_key VALUES(1); "
            + "CREATE USER ddl_conditional_writer WITHOUT LOGIN WITH DEFAULT_SCHEMA=dbo; "
            + "GRANT VIEW DEFINITION,CREATE TABLE TO ddl_conditional_writer; "
            + "GRANT ALTER ON SCHEMA::dbo TO ddl_conditional_writer; "
            + "GRANT SELECT ON OBJECT::sys.sql_expression_dependencies TO ddl_conditional_writer; "
            + "DENY SELECT ON OBJECT::dbo.ddl_denied_after_key TO ddl_conditional_writer;");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER=N'ddl_conditional_writer';");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("ddl_conditional_key_probe",
            table => new { Id = table.Column<int>(type: "int", nullable: false) });
        if (uniqueConstraint)
        {
            builder.AddUniqueConstraintIfNotExists("UQ_ddl_conditional_probe", "ddl_conditional_key_probe", ["Id"]);
        }
        else
        {
            builder.AddPrimaryKeyIfNotExists("PK_ddl_conditional_probe", "ddl_conditional_key_probe", ["Id"]);
        }

        builder.AddCheckConstraintIfNotExists("CK_ddl_denied_after_key", "ddl_denied_after_key", "[Value]>=0");
        SafeMigrationRunReport report;

        // Act
        try
        {
            report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sqlserver-ddl-conditional-independent-permission"));
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("REVERT;");
        }

        var createdTables = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'dbo.ddl_conditional_key_probe');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        AssertDdlDeferredOrigin(report.Assessments[1], "projected_ddl_visibility_data_unknown", 0,
            typeof(SafeMigrationOperation), migrationId: null);
        Assert.Equal(SafeMigrationObservedState.Unsupported, report.Assessments[2].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[2].Action);
        Assert.Equal("check_row_data_unproven", report.Assessments[2].AnalysisCode);
        Assert.Null(report.Assessments[2].DeferredOrigin);
        Assert.Equal(0, createdTables);
    }

    /// <summary>Catalog-collation aliases remain an invariant blocker rather than a DDL deferral.</summary>
    [SqlServerLiveFact]
    public async Task DdlConstraints_CaseAliasedTableNamesRemainBlocked()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE USER ddl_collision_owner WITHOUT LOGIN WITH DEFAULT_SCHEMA=dbo; "
            + "ALTER ROLE db_owner ADD MEMBER ddl_collision_owner;");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER=N'ddl_collision_owner';");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("ddl_collision_values",
            table => new { Value = table.Column<int>(type: "int", nullable: false) });
        builder.AddCheckConstraintIfNotExists("CK_ddl_collision", "DDL_COLLISION_VALUES", "[Value]>=0");
        await using var comparison = context.Database.GetDbConnection().CreateCommand();
        comparison.CommandText = "SELECT CASE WHEN N'ddl_collision_values' COLLATE CATALOG_DEFAULT "
            + "=N'DDL_COLLISION_VALUES' COLLATE CATALOG_DEFAULT THEN 1 ELSE 0 END;";

        SafeMigrationRunReport report;
        int aliases;

        // Act
        try
        {
            aliases = Convert.ToInt32(await comparison.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sqlserver-ddl-independent-identifier-collision"));
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("REVERT;");
        }

        var createdTables = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE name=N'ddl_collision_values';");

        // Assert
        Assert.Equal(1, aliases);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.All(report.Assessments, static assessment =>
        {
            Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
            Assert.Equal("identifier_collation_unproven", assessment.AnalysisCode);
            Assert.Null(assessment.DeferredOrigin);
        });
        Assert.Equal(0, createdTables);
    }

    /// <summary>An absent dependent table cannot hide an existing schema-global FK name on another table.</summary>
    [SqlServerLiveFact]
    public async Task DdlConstraints_ExistingForeignKeyNameOnOtherTableRemainsBlocked()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.ddl_occupied_parent(Id int NOT NULL CONSTRAINT PK_ddl_occupied_parent PRIMARY KEY); "
            + "CREATE TABLE dbo.ddl_occupied_other(ParentId int NULL "
            + "CONSTRAINT FK_ddl_occupied FOREIGN KEY REFERENCES dbo.ddl_occupied_parent(Id));");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("ddl_occupied_child",
            table => new { ParentId = table.Column<int>(type: "int", nullable: true) });
        builder.AddForeignKeyIfNotExists("FK_ddl_occupied", "ddl_occupied_child", ["ParentId"],
            "ddl_occupied_parent", ["Id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-ddl-existing-constraint-namespace"));

        var existingForeignKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.ddl_occupied_other') "
            + "AND name=N'FK_ddl_occupied';");

        var createdTables = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'dbo.ddl_occupied_child');");

        // Assert
        AssertDdlReportStatus(report, SafeMigrationReportStatus.Blocked);
        Assert.Equal(SafeMigrationObservedState.Different, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[1].Action);
        Assert.Null(report.Assessments[1].DeferredOrigin);
        Assert.Equal(1, existingForeignKeys);
        Assert.Equal(0, createdTables);
    }

    /// <summary>A newly created table reserves its own schema-global name before a standalone FK can use it.</summary>
    [SqlServerLiveFact]
    public async Task DdlConstraints_AcceptedTableNameCannotBeReusedForForeignKey()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.ddl_table_name_parent(Id int NOT NULL "
            + "CONSTRAINT PK_ddl_table_name_parent PRIMARY KEY);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("ddl_table_fk_name",
            table => new { ParentId = table.Column<int>(type: "int", nullable: true) });
        builder.AddForeignKeyIfNotExists("ddl_table_fk_name", "ddl_table_fk_name", ["ParentId"],
            "ddl_table_name_parent", ["Id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-ddl-table-object-constraint-namespace"));

        var createdTables = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'dbo.ddl_table_fk_name');");

        // Assert
        AssertDdlReportStatus(report, SafeMigrationReportStatus.Blocked);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationObservedState.Different, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[1].Action);
        Assert.Equal("projected_constraint_name_occupied", report.Assessments[1].AnalysisCode);
        Assert.Null(report.Assessments[1].DeferredOrigin);
        Assert.Equal(0, createdTables);
    }

    /// <summary>Accepted constraint creation reserves the schema-global name before a later FK projection.</summary>
    /// <param name="family">The preceding PK, unique, or CHECK constraint sharing the desired FK name.</param>
    /// <param name="ownerIsChild">Whether the earlier constraint belongs to the child or an unrelated table.</param>
    [SqlServerLiveTheory]
    [InlineData("primary", false)]
    [InlineData("primary", true)]
    [InlineData("unique", false)]
    [InlineData("unique", true)]
    [InlineData("check", false)]
    [InlineData("check", true)]
    public async Task DdlConstraints_AcceptedConstraintNameRemainsReservedBeforeLaterForeignKey(
        string family,
        bool ownerIsChild
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.ddl_reserved_parent(Id int NOT NULL CONSTRAINT PK_ddl_reserved_parent PRIMARY KEY); "
            + "CREATE TABLE dbo.ddl_reserved_other(Id int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("ddl_reserved_child", table => new
        {
            Id = table.Column<int>(type: "int", nullable: false),
            ParentId = table.Column<int>(type: "int", nullable: true),
        });

        var ownerTable = ownerIsChild ? "ddl_reserved_child" : "ddl_reserved_other";
        switch (family)
        {
            case "primary":
                builder.AddPrimaryKeyIfNotExists("FK_ddl_reserved", ownerTable, ["Id"]);
                break;
            case "unique":
                builder.AddUniqueConstraintIfNotExists("FK_ddl_reserved", ownerTable, ["Id"]);
                break;
            case "check":
                builder.AddCheckConstraintIfNotExists("FK_ddl_reserved", ownerTable, "[Id]>=0");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(family));
        }

        builder.AddForeignKeyIfNotExists("FK_ddl_reserved", "ddl_reserved_child", ["ParentId"],
            "ddl_reserved_parent", ["Id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-ddl-projected-constraint-namespace-" + family));

        var createdTables = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'dbo.ddl_reserved_child');");

        var constraints = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.objects WHERE schema_id=SCHEMA_ID(N'dbo') AND name=N'FK_ddl_reserved';");

        // Assert
        AssertDdlReportStatus(report, SafeMigrationReportStatus.Blocked);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationObservedState.Different, report.Assessments[2].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[2].Action);
        Assert.Null(report.Assessments[2].DeferredOrigin);
        Assert.Equal(0, createdTables);
        Assert.Equal(0, constraints);
    }

    /// <summary>Ordinary scalar and strict-table contracts exclude a legacy bound RULE.</summary>
    /// <param name="boundRule">Whether a deprecated RULE is attached to the existing integer column.</param>
    /// <param name="tableContract">Whether the expected scalar is compared inside a strict table definition.</param>
    [SqlServerLiveTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ColumnContract_BoundLegacyRuleDoesNotMatchOrdinaryDefinition(
        bool boundRule,
        bool tableContract
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.rule_bound_values(Value int NOT NULL);");
        if (boundRule)
        {
            // WHY: CREATE RULE must be the only statement in its batch. A rule
            // binding is a physical facet, not an authored CHECK in the safe contract.
            await ExecuteSqlAsync(connectionString, "CREATE RULE dbo.nonnegative_value AS @value>=0;");
            await ExecuteSqlAsync(connectionString,
                "EXEC sys.sp_bindrule N'dbo.nonnegative_value',N'dbo.rule_bound_values.Value';");
        }

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var definition = new ExpectedColumnDefinition("Value", typeof(int), false, "int");
        if (tableContract)
        {
            builder.EnsureTable(new ExpectedTableDefinition("rule_bound_values", [definition]),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }
        else
        {
            builder.EnsureColumn("rule_bound_values", definition, SafeMigrationPolicy.ThrowIfDifferent);
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-column-legacy-rule"));

        var boundColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.rule_bound_values') "
            + "AND name=N'Value' AND rule_object_id<>0;");

        // Assert
        Assert.Equal(boundRule ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(boundRule ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching,
            Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(boundRule ? SafeMigrationAction.RejectDifferent : SafeMigrationAction.NoOp,
            Assert.Single(report.Assessments).Action);
        Assert.Equal(boundRule ? 1 : 0, boundColumns);
    }

    /// <summary>Appends one row-dependent guard without introducing another preceding DDL operation.</summary>
    private static void AppendDdlRowConstraint(
        MigrationBuilder builder,
        string family,
        string table
    )
    {
        switch (family)
        {
            case "primary":
                builder.AddPrimaryKeyIfNotExists("PK_ddl_values", table, ["Id"]);
                break;
            case "unique":
                builder.AddUniqueConstraintIfNotExists("UQ_ddl_values", table, ["Value"]);
                break;
            case "foreign":
                builder.AddForeignKeyIfNotExists("FK_ddl_values_parent", table, ["ParentId"], "ddl_parent", ["Id"]);
                break;
            case "check":
                builder.AddCheckConstraintIfNotExists("CK_ddl_values", table, "[Value]>=0");
                break;
            case "index":
                builder.CreateIndexIfNotExists("IX_ddl_values", table, ["Value"], unique: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(family));
        }
    }

    /// <summary>Checks the complete deferred-origin contract rather than only its aggregate report status.</summary>
    private static void AssertDdlDeferredOrigin(
        SafeMigrationAssessment assessment,
        string analysisCode,
        int operationOrdinal,
        Type operationType,
        string? migrationId
    )
    {
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, assessment.Action);
        Assert.Equal("runtime_validation_required", assessment.Code);
        Assert.Equal(analysisCode, assessment.AnalysisCode);
        Assert.Null(assessment.ObservedState);
        Assert.Null(assessment.PostconditionSatisfied);
        var origin = Assert.IsType<SafeMigrationDeferredOrigin>(assessment.DeferredOrigin);
        Assert.Equal(operationOrdinal, origin.OperationOrdinal);
        Assert.Equal(operationType.FullName, origin.OperationType);
        Assert.Equal(migrationId, origin.MigrationId);
    }

    /// <summary>Preserves exact status expectations while exposing the operation that failed live projection.</summary>
    private static void AssertDdlReportStatus(
        SafeMigrationRunReport report,
        SafeMigrationReportStatus expected
    )
    {
        // WHY: An aggregate Blocked result otherwise hides whether a physical
        // prerequisite, an independent guard, or DDL row uncertainty owned the refusal.
        var details = string.Join("; ", report.Assessments.Select(static assessment =>
            $"ordinal={assessment.Ordinal}, kind={assessment.OperationKind}, state={assessment.ObservedState}, "
            + $"action={assessment.Action}, analysis={assessment.AnalysisCode}, decision={assessment.DecisionCode}"));

        Assert.True(report.Status == expected, $"Expected {expected}, actual {report.Status}. {details}");
    }
}
