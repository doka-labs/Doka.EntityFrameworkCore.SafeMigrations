namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task SchemaOperations_AreClassifiedUnsupportedWithoutDatabaseDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureSchemaExists("independent_schema");
        var runner = context.GetService<ISafeMigrationRunner>();

        var report = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("test-instance"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(
            SafeMigrationObservedState.Unsupported,
            Assert.Single(report.Assessments)
                .ObservedState);
    }

    [Fact]
    public async Task CurrentDatabaseEnsureSchema_IsAnIdempotentNoOp()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureSchemaExists(database);
        var runner = context.GetService<ISafeMigrationRunner>();

        var report = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("current-database-schema"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("current-database-schema-replay"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(report.Assessments).Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
    }

    [Fact]
    public async Task CurrentDatabaseDropSchema_RemainsUnsupportedWithoutDatabaseDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropSchemaIfExists(database);
        var runner = context.GetService<ISafeMigrationRunner>();

        var report = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("current-database-drop-schema"));
        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
        Assert.Equal("schema_operations", assessment.AnalysisCode);
        Assert.Contains("doka_sm_unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = DATABASE();"));
    }

    [Fact]
    public async Task ForeignDatabaseDropSchemaRejectsIdentityBeforeSchemaCapability()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var foreignDatabase = "doka_foreign_" + Guid.NewGuid().ToString("N");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropSchemaIfExists(foreignDatabase);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("foreign-database-drop-schema"));
        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
        Assert.Equal("database_qualifier_mismatch", assessment.AnalysisCode);
        Assert.Contains("doka_sm_unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.SCHEMATA "
                + $"WHERE SCHEMA_NAME = '{foreignDatabase}';"));
    }

    [Fact]
    public async Task CurrentDatabaseQualifierSharesCurrentCatalogInventoryIdentity()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `qualified_inventory` (`Id` int NOT NULL, PRIMARY KEY (`Id`)) ENGINE=InnoDB; "
            + "CREATE INDEX `IX_qualified_inventory_Id` ON `qualified_inventory` (`Id`);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "qualified_inventory",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_qualified_inventory", value => value.Id),
            schema: database);
        builder.CreateIndexIfNotExists(
            "IX_qualified_inventory_Id",
            "qualified_inventory",
            ["Id"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("current-database-inventory"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.DoesNotContain(
            report.UnexpectedObjects,
            value => StringComparer.Ordinal.Equals(value.Table, "qualified_inventory")
                || StringComparer.Ordinal.Equals(value.Name, "qualified_inventory"));
    }

    [Fact]
    public async Task CurrentDatabaseQualifiedPrefixIndex_ExecutesAndReplays()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `qualified_index` (`value` varchar(800) NOT NULL) ENGINE=InnoDB;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexWithPrefixesIfNotExistsFromModel(
            "ix_qualified_index_value",
            "qualified_index",
            "value",
            [191],
            database);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("current-database-index"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("current-database-index-replay"));
        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(preflight.Assessments).Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'qualified_index' "
                + "AND INDEX_NAME = 'ix_qualified_index_value' AND SUB_PART = 191;"));
    }

    [Fact]
    public async Task CurrentDatabaseQualifiedIndexLifecyclePreservesIdentityAndData()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `qualified_index_lifecycle` ("
            + "`id` int NOT NULL, `value` varchar(800) NOT NULL, `other` varchar(800) NOT NULL, "
            + "PRIMARY KEY (`id`)) ENGINE=InnoDB; "
            + "INSERT INTO `qualified_index_lifecycle` VALUES (1, 'value', 'other');");
        await using var context = CreateContext(connectionString);
        var runner = context.GetService<ISafeMigrationRunner>();

        var create = new MigrationBuilder(context.Database.ProviderName!);
        create.CreateIndexWithPrefixesIfNotExistsFromModel(
            "ix_qualified_index_lifecycle_value",
            "qualified_index_lifecycle",
            "value",
            [191],
            database);

        var createReport = await runner.AnalyzeAsync(
            context,
            create.Operations,
            new SafeMigrationRunOptions("qualified-index-create"));
        await ExecuteOperationsAsync(context, create.Operations);
        var replayReport = await runner.AnalyzeAsync(
            context,
            create.Operations,
            new SafeMigrationRunOptions("qualified-index-replay"));

        var drift = new MigrationBuilder(context.Database.ProviderName!);
        drift.CreateIndexWithPrefixesIfNotExistsFromModel(
            "ix_qualified_index_lifecycle_value",
            "qualified_index_lifecycle",
            "other",
            [191],
            database);

        var driftReport = await runner.AnalyzeAsync(
            context,
            drift.Operations,
            new SafeMigrationRunOptions("qualified-index-drift"));

        var rename = new MigrationBuilder(context.Database.ProviderName!);
        rename.RenameIndexIfExists(
            "ix_qualified_index_lifecycle_value",
            "qualified_index_lifecycle",
            "ix_qualified_index_lifecycle_renamed",
            database);

        var renameReport = await runner.AnalyzeAsync(
            context,
            rename.Operations,
            new SafeMigrationRunOptions("qualified-index-rename"));
        await ExecuteOperationsAsync(context, rename.Operations);

        var drop = new MigrationBuilder(context.Database.ProviderName!);
        drop.DropIndexIfExists(
            "ix_qualified_index_lifecycle_renamed",
            "qualified_index_lifecycle",
            database);

        var dropReport = await runner.AnalyzeAsync(
            context,
            drop.Operations,
            new SafeMigrationRunOptions("qualified-index-drop"));
        await ExecuteOperationsAsync(context, drop.Operations);
        var dropReplayReport = await runner.AnalyzeAsync(
            context,
            drop.Operations,
            new SafeMigrationRunOptions("qualified-index-drop-replay"));

        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(createReport.Assessments).Action);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replayReport.Assessments).Action);
        Assert.Equal(SafeMigrationReportStatus.Blocked, driftReport.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, Assert.Single(driftReport.Assessments).Action);
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(renameReport.Assessments).Action);
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(dropReport.Assessments).Action);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(dropReplayReport.Assessments).Action);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'qualified_index_lifecycle' "
                + "AND INDEX_NAME LIKE 'ix_qualified_index_lifecycle%';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `qualified_index_lifecycle` "
                + "WHERE `id` = 1 AND `value` = 'value' AND `other` = 'other';"));
    }

    [Fact]
    public async Task QualifiedDropThenUnqualifiedEnsurePassesPostflightAsOnePhysicalIndex()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `qualified_postflight` (`id` int NOT NULL, PRIMARY KEY (`id`)) ENGINE=InnoDB; "
            + "CREATE INDEX `ix_qualified_postflight_id` ON `qualified_postflight` (`id`);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists(
            "ix_qualified_postflight_id",
            "qualified_postflight",
            database);
        builder.CreateIndexIfNotExists(
            "ix_qualified_postflight_id",
            "qualified_postflight",
            ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("qualified-postflight-preflight"));
        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("qualified-postflight-postflight"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal("postcondition_superseded", postflight.Assessments[0].Code);
        Assert.True(postflight.Assessments[0].PostconditionSatisfied);
        Assert.True(postflight.Assessments[1].PostconditionSatisfied);
    }

    [Fact]
    public async Task CurrentDatabaseQualifiedModelManagedData_ExecutesAndReplays()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `qualified_roles` ("
            + "`id` int NOT NULL, `name` varchar(64) NOT NULL, PRIMARY KEY (`id`)) ENGINE=InnoDB;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel(
            "qualified_roles",
            ["id"],
            ["int"],
            ["id", "name"],
            ["int", "varchar(64)"],
            new object?[,] { { 1, "administrator" } },
            database);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("current-database-model-data"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("current-database-model-data-replay"));
        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(preflight.Assessments).Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `qualified_roles` "
                + "WHERE `id` = 1 AND `name` = 'administrator';"));
    }

    [Fact]
    public async Task ForeignDatabaseQualificationRejectsBeforeIndexOrDataMutation()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var foreignDatabase = "doka_foreign_" + Guid.NewGuid().ToString("N");
        await ExecuteSqlAsync(
            connectionString,
            $"CREATE DATABASE `{foreignDatabase}`; "
            + "CREATE TABLE `qualified_isolation` (`id` int NOT NULL, `value` varchar(800) NOT NULL, "
            + "PRIMARY KEY (`id`)) ENGINE=InnoDB; "
            + $"CREATE TABLE `{foreignDatabase}`.`qualified_isolation` ("
            + "`id` int NOT NULL, `value` varchar(800) NOT NULL, PRIMARY KEY (`id`)) ENGINE=InnoDB;");

        try
        {
            await using var context = CreateContext(connectionString);
            var index = new MigrationBuilder(context.Database.ProviderName!);
            index.CreateIndexWithPrefixesIfNotExistsFromModel(
                "ix_qualified_isolation_value",
                "qualified_isolation",
                "value",
                [191],
                foreignDatabase);

            var data = new MigrationBuilder(context.Database.ProviderName!);
            data.EnsureModelManagedDataFromModel(
                "qualified_isolation",
                ["id"],
                ["int"],
                ["id", "value"],
                ["int", "varchar(800)"],
                new object?[,] { { 1, "foreign" } },
                foreignDatabase);

            var operations = index.Operations.Concat(data.Operations).ToArray();
            var report = await context
                .GetService<ISafeMigrationRunner>()
                .AnalyzeAsync(
                    context,
                    operations,
                    new SafeMigrationRunOptions("foreign-database-isolation"));

            var exception = await Assert.ThrowsAsync<MySqlException>(() =>
                ExecuteOperationsAsync(context, operations));
            var dataException = await Assert.ThrowsAsync<MySqlException>(() =>
                ExecuteOperationsAsync(context, data.Operations));

            Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
            Assert.All(
                report.Assessments,
                assessment =>
                {
                    Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
                    Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
                    Assert.Equal("database_qualifier_mismatch", assessment.AnalysisCode);
                });
            Assert.Contains("doka_sm_unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("doka_sm_unsupported", dataException.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                0,
                await ScalarIntAsync(
                    connectionString,
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                    + $"WHERE TABLE_SCHEMA IN (DATABASE(), '{foreignDatabase}') "
                    + "AND TABLE_NAME = 'qualified_isolation' "
                    + "AND INDEX_NAME = 'ix_qualified_isolation_value';"));
            Assert.Equal(
                0,
                await ScalarIntAsync(
                    connectionString,
                    "SELECT (SELECT COUNT(*) FROM `qualified_isolation`) "
                    + $"+ (SELECT COUNT(*) FROM `{foreignDatabase}`.`qualified_isolation`);"));
        }
        finally
        {
            await ExecuteSqlAsync(connectionString, $"DROP DATABASE IF EXISTS `{foreignDatabase}`;");
        }
    }

    [Fact]
    public async Task ForeignStreamQualifierRejectsBeforeEarlierUnqualifiedTableMutation()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var foreignDatabase = "doka_foreign_" + Guid.NewGuid().ToString("N");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "guarded_stream",
                [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int")]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.AddUniqueConstraintIfNotExists(
            "uq_guarded_stream_id",
            "guarded_stream",
            ["id"],
            foreignDatabase);

        // Act
        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        Assert.Contains("doka_sm_unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'guarded_stream';"));
    }

    [Fact]
    public async Task SchemaQualifiedOperationMatrix_FailsClosedForEveryPublicObjectFamily()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);

        builder.EnsureSchemaExists("tenant_schema");
        builder.DropSchemaIfExists("tenant_schema");
        builder.CreateTableIfNotExists(
            "qualified_table",
            columns: table => new
            {
                Id = table.Column<int>(nullable: false),
            },
            schema: "tenant_schema");
        builder.DropTableIfExists("qualified_table", "tenant_schema");
        builder.RenameTableIfExists("qualified_table", "renamed_table", "tenant_schema");
        builder.RenameTableIfExists("qualified_table", "qualified_table", newSchema: "tenant_archive");
        builder.AddColumnIfNotExists<int>("value", "qualified_table", schema: "tenant_schema");
        builder.DropColumnIfExists("value", "qualified_table", "tenant_schema");
        builder.RenameColumnIfExists("value", "qualified_table", "renamed_value", "tenant_schema");
        builder.AlterColumnIfDifferent(
            "qualified_table",
            new ExpectedColumnDefinition("value", typeof(int), false, "int"),
            new ExpectedColumnDefinition("value", typeof(int), true, "int"),
            SafeMigrationPolicy.RepairIfSafe,
            "tenant_schema");
        builder.CreateIndexIfNotExists("ix_qualified_value", "qualified_table", ["value"], "tenant_schema");
        builder.DropIndexIfExists("ix_qualified_value", "qualified_table", "tenant_schema");
        builder.RenameIndexIfExists(
            "ix_qualified_value",
            "qualified_table",
            "ix_qualified_value_renamed",
            "tenant_schema");
        builder.AddPrimaryKeyIfNotExists("pk_qualified_table", "qualified_table", ["value"], "tenant_schema");
        builder.DropPrimaryKeyIfExists("pk_qualified_table", "qualified_table", "tenant_schema");
        builder.AddUniqueConstraintIfNotExists("uq_qualified_value", "qualified_table", ["value"], "tenant_schema");
        builder.DropUniqueConstraintIfExists("uq_qualified_value", "qualified_table", "tenant_schema");
        builder.AddCheckConstraintIfNotExists("ck_qualified_value", "qualified_table", "value >= 0", "tenant_schema");
        builder.DropCheckConstraintIfExists("ck_qualified_value", "qualified_table", "tenant_schema");
        builder.AddForeignKeyIfNotExists(
            "fk_qualified_parent",
            "qualified_table",
            ["value"],
            "qualified_parent",
            ["id"],
            schema: "tenant_schema");
        builder.AddForeignKeyIfNotExists(
            "fk_qualified_principal",
            "qualified_table",
            ["value"],
            "qualified_parent",
            ["id"],
            principalSchema: "tenant_schema");
        builder.DropForeignKeyIfExists("fk_qualified_parent", "qualified_table", "tenant_schema");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("schema-qualified-matrix"));

        Assert.Equal(22, builder.Operations.Count);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(builder.Operations.Count, report.Assessments.Count);
        Assert.All(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
            });

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, [builder.Operations[2]]));

        Assert.Contains("doka_sm_unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.SCHEMATA "
                + "WHERE SCHEMA_NAME IN ('tenant_schema', 'tenant_archive');"));
    }

    [Fact]
    public async Task SchemaQualifiedExpectations_NeverSuppressCurrentDatabaseInventory()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `legacy` (`id` int NOT NULL, `extra` int NULL); "
            + "CREATE INDEX `ix_legacy_extra` ON `legacy` (`extra`); "
            + "CREATE TABLE `managed` (`id` int NOT NULL, `extra` int NULL); "
            + "CREATE INDEX `ix_managed_extra` ON `managed` (`extra`);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "legacy",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
            },
            schema: "other_schema");
        builder.CreateTableIfNotExists(
            "managed",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
            });

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("schema-inventory-collision"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, report.Assessments[0].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[0].Action);
        Assert.Contains(
            report.UnexpectedObjects,
            value => value.ObjectKind == SafeMigrationDatabaseObjectKind.Table
                && value.Table is null
                && StringComparer.Ordinal.Equals(value.Name, "legacy"));
        Assert.Contains(
            report.UnexpectedObjects,
            value => value.ObjectKind == SafeMigrationDatabaseObjectKind.Column
                && StringComparer.Ordinal.Equals(value.Table, "managed")
                && StringComparer.Ordinal.Equals(value.Name, "extra"));
        Assert.Contains(
            report.UnexpectedObjects,
            value => value.ObjectKind == SafeMigrationDatabaseObjectKind.Index
                && StringComparer.Ordinal.Equals(value.Table, "managed")
                && StringComparer.Ordinal.Equals(value.Name, "ix_managed_extra"));
        Assert.DoesNotContain(
            report.UnexpectedObjects,
            value => value.ObjectKind == SafeMigrationDatabaseObjectKind.Table
                && StringComparer.Ordinal.Equals(value.Name, "managed"));
    }
}
