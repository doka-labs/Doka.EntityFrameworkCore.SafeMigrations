namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>A populated existing child needs no parent convergence before adding a nullable FK.</summary>
    /// <param name="guidStorage">Whether the new endpoint uses the provider's CHAR(36) GUID contract.</param>
    /// <param name="onDelete">The independently authored parent deletion action.</param>
    [Theory]
    [InlineData(false, ReferentialAction.NoAction)]
    [InlineData(false, ReferentialAction.Cascade)]
    [InlineData(false, ReferentialAction.SetNull)]
    [InlineData(true, ReferentialAction.NoAction)]
    [InlineData(true, ReferentialAction.Cascade)]
    [InlineData(true, ReferentialAction.SetNull)]
    public async Task ForeignKeyNewNullableColumnUsesUnchangedLivePrincipalWithoutConvergence(
        bool guidStorage,
        ReferentialAction onDelete
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var principalType = guidStorage ? "char(36)" : "int";

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` " + principalType + " NOT NULL PRIMARY KEY) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `live_child` (`id`) VALUES (1), (2);");

        await using var context = CreateContext(connectionString);
        var operations = NullableLivePrincipalOperations(context, guidStorage, onDelete);
        var runner = context.GetService<ISafeMigrationRunner>();

        // WHY: The source contains no parent Ensure operation. Recreating or
        // converging the parent would hide the sparse live-prerequisite defect.
        // Act
        var preflight = await runner.AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("sparse-live-fk"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, operations,
            new SafeMigrationRunOptions("sparse-live-fk-final"), CancellationToken.None);

        var replay = await runner.AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("sparse-live-fk-replay"), CancellationToken.None);

        var nullRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `live_child` WHERE `parent_id` IS NULL;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(3, preflight.Assessments.Count);
        Assert.All(preflight.Assessments, static assessment =>
            Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(2, nullRows);
    }

    /// <summary>Builds exactly the three safe operations needed by the isolated sparse-owner reproduction.</summary>
    /// <param name="context">The active provider context.</param>
    /// <param name="guidStorage">Whether endpoints use CHAR(36) GUID storage rather than integer storage.</param>
    /// <param name="onDelete">The requested parent deletion action.</param>
    /// <returns>Nullable addition, index and FK, without any unrelated table convergence.</returns>
    private static List<MigrationOperation> NullableLivePrincipalOperations(
        DbContext context,
        bool guidStorage,
        ReferentialAction onDelete
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (guidStorage)
        {
            builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<Guid>(
                    "parent_id", "live_child", type: "char(36)", nullable: true, fixedLength: true, maxLength: 36)
                .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None));
        }
        else
        {
            builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
                "parent_id", "live_child", type: "int", nullable: true));
        }

        builder.CreateIndexIfNotExistsFromModel("ix_live_child_parent_id", "live_child", "parent_id");
        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"],
            "live_parent", ["id"], onDelete: onDelete);

        return builder.Operations;
    }

    /// <summary>Sparse projection must not convert missing or incompatible live metadata into an FK proof.</summary>
    /// <param name="principalSql">The generic independently created principal shape.</param>
    /// <param name="characterColumn">Whether the new dependent column uses character storage.</param>
    [Theory]
    [InlineData("", false)]
    [InlineData("CREATE TABLE `live_parent` (`id` int NOT NULL) ENGINE=InnoDB", false)]
    [InlineData("CREATE VIEW `live_parent` AS SELECT 1 AS `id`", false)]
    [InlineData("CREATE TABLE `live_parent` (`id` bigint NOT NULL PRIMARY KEY) ENGINE=InnoDB", false)]
    [InlineData("CREATE TABLE `live_parent` (`id` int unsigned NOT NULL PRIMARY KEY) ENGINE=InnoDB", false)]
    [InlineData("CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=MyISAM", false)]
    [InlineData("CREATE TABLE `live_parent` (`id` varchar(36) NOT NULL, UNIQUE KEY `uq_parent_id` (`id`(12))) "
        + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci", true)]
    [InlineData("CREATE TABLE `live_parent` (`id` varchar(36) NOT NULL PRIMARY KEY) "
        + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci", true)]
    public async Task ForeignKeySparseLivePrerequisitesRejectMissingOrIncompatibleCatalog(
        string principalSql,
        bool characterColumn
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB "
            + "DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; INSERT INTO `live_child` VALUES (1), (2);"
            + (principalSql.Length == 0 ? string.Empty : principalSql + ";"));

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("live_child", characterColumn
            ? new ExpectedColumnDefinition("parent_id", typeof(string), isNullable: true,
                storeType: "varchar(36)", maxLength: 36)
            : new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true, storeType: "int"),
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"], "live_parent", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-fk-negative"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'live_child' AND COLUMN_NAME = 'parent_id';"));
    }

    /// <summary>A nullable column with a non-NULL default does not materialize the NULL exemption.</summary>
    [Fact]
    public async Task ForeignKeySparseNonNullDefaultDoesNotProveNullRows()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "INSERT INTO `live_child` VALUES (1), (2);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("live_child", new ExpectedColumnDefinition("parent_id", typeof(int),
            isNullable: true, storeType: "int", defaultValue: SafeMigrationDefaultValue.Literal(999)),
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"], "live_parent", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-fk-default"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);
    }

    /// <summary>Matching column/key declarations cannot erase different inherited physical collations.</summary>
    /// <param name="newMember">Whether the child endpoint is newly added rather than already live and matching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForeignKeySparseMatchingPrincipalDeclarationsCannotHideInheritedCollationDrift(bool newMember)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` varchar(36) NOT NULL PRIMARY KEY) ENGINE=InnoDB "
            + "DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY"
            + (newMember ? string.Empty : ", `parent_id` varchar(36) NULL") + ") ENGINE=InnoDB "
            + "DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci; "
            + "INSERT INTO `live_child` (`id`) VALUES (1), (2);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("live_child", new ExpectedColumnDefinition("parent_id", typeof(string),
            isNullable: true, storeType: "varchar(36)", maxLength: 36), SafeMigrationPolicy.ThrowIfDifferent);

        builder.EnsureColumn("live_parent", new ExpectedColumnDefinition("id", typeof(string),
            isNullable: false, storeType: "varchar(36)", maxLength: 36), SafeMigrationPolicy.ThrowIfDifferent);

        builder.AddPrimaryKeyIfNotExists("pk_live_parent", "live_parent", ["id"]);
        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"], "live_parent", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-fk-inherited-drift"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[2].Action);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[3].Action);
    }

    /// <summary>A composite index can combine an unchanged live member with a newly materialized NULL member.</summary>
    /// <param name="kind">Zero for nonunique, one for unique index, two for unique constraint.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SparseCompositeIndexCombinesLiveAndNewNullableMembers(int kind)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `code` int NOT NULL) ENGINE=InnoDB; "
            + "INSERT INTO `live_child` VALUES (1, 7), (2, 7);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
            "new_value", "live_child", type: "int", nullable: true));

        if (kind == 2)
        {
            builder.AddUniqueConstraintIfNotExists("uq_live_child_mixed", "live_child", ["code", "new_value"]);
        }
        else
        {
            builder.CreateCompositeIndexIfNotExistsFromModel("ix_live_child_mixed", "live_child", ["code", "new_value"],
                unique: kind == 1);
        }

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-mixed-index"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var replay = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-mixed-index-replay"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments, static assessment =>
            Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(2, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `live_child` WHERE `new_value` IS NULL;"));
    }

    /// <summary>Removing or replacing the requested live candidate cannot revive its original snapshot.</summary>
    /// <param name="kind">The primary, unique-constraint, backing-index or replacement mutation.</param>
    [Theory]
    [InlineData("primary")]
    [InlineData("unique")]
    [InlineData("index")]
    [InlineData("replacement")]
    public async Task ForeignKeySparseCandidateKeyRemovalRemainsBlocking(string kind)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var keySql = kind is "primary" or "replacement" ? "PRIMARY KEY (`id`)" : "UNIQUE KEY `uq_parent_id` (`id`)";

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL, `code` int NOT NULL, " + keySql + ") ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "INSERT INTO `live_child` VALUES (1), (2);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (kind is "primary" or "replacement")
        {
            builder.DropPrimaryKeyIfExists("pk_live_parent", "live_parent");
        }
        else if (kind == "unique")
        {
            builder.DropUniqueConstraintIfExists("uq_parent_id", "live_parent");
        }
        else
        {
            builder.DropIndexIfExists("uq_parent_id", "live_parent");
        }

        if (kind == "replacement")
        {
            builder.AddPrimaryKeyIfNotExists("pk_live_parent", "live_parent", ["code"]);
        }

        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
            "parent_id", "live_child", type: "int", nullable: true));

        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"], "live_parent", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-fk-removed-key"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[^1].Action);
    }

    /// <summary>Runtime still rejects an orphan introduced after the catalog/NULL projection was approved.</summary>
    [Fact]
    public async Task ForeignKeySparseRuntimeRevalidatesRowsAfterPreflight()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "INSERT INTO `live_child` VALUES (1), (2);");

        await using var context = CreateContext(connectionString);
        var operations = NullableLivePrincipalOperations(context, guidStorage: false, ReferentialAction.NoAction);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("sparse-fk-raced-row"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, operations.Take(2).ToArray(), CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "UPDATE `live_child` SET `parent_id` = 999 WHERE `id` = 1;");
        var exception = await Assert.ThrowsAsync<MySqlException>(async () =>
            await ExecuteOperationsAsync(context, [operations[2]], CancellationToken.None));

        var final = await runner.AnalyzeAsync(context, [operations[2]],
            new SafeMigrationRunOptions("sparse-fk-raced-final"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Contains("doka_sm_data_blocked", exception.Message, StringComparison.Ordinal);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, Assert.Single(final.Assessments).Action);
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'live_child' AND CONSTRAINT_NAME = 'fk_live_child_parent';"));
    }

    /// <summary>
    /// A sparse self-reference uses the existing table lifetime, not a newly created owner assumption.
    /// </summary>
    [Fact]
    public async Task ForeignKeySparseNullableSelfReferenceUsesLivePrimaryKey()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "INSERT INTO `live_child` VALUES (1), (2);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
            "parent_id", "live_child", type: "int", nullable: true));

        builder.AddForeignKeyIfNotExists("fk_live_child_self", "live_child", ["parent_id"], "live_child", ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-self-fk"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var final = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-self-fk-final"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, final.Status);
        Assert.All(final.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(2, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `live_child` WHERE `parent_id` IS NULL;"));
    }

    /// <summary>MATCH SIMPLE exempts a composite FK when its accepted newly added member is NULL.</summary>
    [Fact]
    public async Task ForeignKeySparseCompositeEndpointCombinesLiveAndNewMembers()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`tenant_id` int NOT NULL, `id` int NOT NULL, "
            + "PRIMARY KEY (`tenant_id`, `id`)) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `tenant_id` int NOT NULL) ENGINE=InnoDB; "
            + "INSERT INTO `live_child` VALUES (1, 777), (2, 888);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
            "parent_id", "live_child", type: "int", nullable: true));

        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["tenant_id", "parent_id"],
            "live_parent", ["tenant_id", "id"]);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-composite-fk"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var final = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-composite-fk-final"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, final.Status);
        Assert.All(final.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
    }

    /// <summary>A different surviving unique alias remains sufficient after one actual candidate is removed.</summary>
    [Fact]
    public async Task ForeignKeySparseIndependentUniqueCandidateSurvivesOtherCandidateDrop()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL, UNIQUE KEY `uq_removed` (`id`), "
            + "UNIQUE KEY `uq_surviving` (`id`)) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "INSERT INTO `live_child` VALUES (1), (2);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("uq_removed", "live_parent");
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
            "parent_id", "live_child", type: "int", nullable: true));

        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"], "live_parent", ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-surviving-key"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var final = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-surviving-key-final"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, final.Status);
        Assert.All(final.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
    }

    /// <summary>A colliding FK symbol is not authorized by new dependent columns.</summary>
    [Fact]
    public async Task ForeignKeySparseDatabaseScopedSymbolCollisionRemainsBlocking()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `other_child` (`id` int NOT NULL, CONSTRAINT `fk_live_child_parent` "
            + "FOREIGN KEY (`id`) REFERENCES `live_parent` (`id`)) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "INSERT INTO `live_child` VALUES (1), (2);");

        await using var context = CreateContext(connectionString);
        var operations = NullableLivePrincipalOperations(context, guidStorage: false, ReferentialAction.NoAction);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var version = MySqlServerVersion.AutoDetect(connection, MySqlServerVersionCompatibilityMode.AllowUnsupported);
        var databaseScopedNames = !version.IsMariaDb || version.Version < new Version(12, 1);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("sparse-fk-symbol"), CancellationToken.None);

        // Assert
        Assert.Equal(databaseScopedNames ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready,
            report.Status);

        Assert.Equal(databaseScopedNames ? SafeMigrationAction.RejectPrerequisiteMissing : SafeMigrationAction.Apply,
            report.Assessments[^1].Action);
    }

    /// <summary>SET NULL cannot target a required newly added member, even when the child is genuinely empty.</summary>
    [Fact]
    public async Task ForeignKeySparseSetNullRequiresActualNullableEndpoint()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB;");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
            "parent_id", "live_child", type: "int", nullable: false));

        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"], "live_parent", ["id"],
            onDelete: ReferentialAction.SetNull);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-fk-required-set-null"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[^1].Action);
    }

    /// <summary>A declared actual same-owner removal releases its symbol for a new nullable endpoint.</summary>
    [Fact]
    public async Task ForeignKeySparseOwnedSymbolCanBeReusedOnlyAfterAcceptedRemoval()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `old_parent_id` int NULL, "
            + "CONSTRAINT `fk_live_child_parent` FOREIGN KEY (`old_parent_id`) REFERENCES `live_parent` (`id`)) "
            + "ENGINE=InnoDB; INSERT INTO `live_child` (`id`) VALUES (1), (2);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_live_child_parent", "live_child");
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
            "parent_id", "live_child", type: "int", nullable: true));

        builder.CreateIndexIfNotExistsFromModel("ix_live_child_new_parent", "live_child", "parent_id");
        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"], "live_parent", ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-owned-symbol"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var final = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-owned-symbol-final"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments, static assessment =>
            Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, final.Status);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE WHERE CONSTRAINT_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'live_child' AND CONSTRAINT_NAME = 'fk_live_child_parent' "
            + "AND COLUMN_NAME = 'parent_id';"));
    }

    /// <summary>
    /// The row-binding barrier preserves native-compatible string lengths and native parent-key policies.
    /// </summary>
    /// <param name="nonstandardKey">Whether the principal uses a nonunique leftmost key permitted by MariaDB.</param>
    /// <param name="precedingIndex">Whether an accepted earlier child index requires namespace-only capture.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExistingForeignKeyNativeReferenceCompatibilityIsPreserved(
        bool nonstandardKey,
        bool precedingIndex
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var keySql = nonstandardKey ? "KEY `ix_parent_id` (`id`, `code`)" : "PRIMARY KEY (`id`)";

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` varchar(36) NOT NULL, `code` int NOT NULL, " + keySql
            + ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `parent_id` varchar(20) NULL) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `live_child` (`id`) VALUES (1), (2);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (precedingIndex)
        {
            builder.CreateIndexIfNotExistsFromModel("ix_explicit_child", "live_child", "parent_id");
        }

        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"], "live_parent", ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();
        var nativeRejectsKey = nonstandardKey && !Fixture.IsMariaDb;

        // WHY: The new comparison-binding guard is not the sparse projection's
        // stricter full-key certificate. MariaDB retains its native leftmost
        // nonunique-key contract; clean MySQL 8.4+ rejects that native DDL.
        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-reference-compatibility"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(async () =>
            await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        var final = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-reference-compatibility-final"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        if (nativeRejectsKey)
        {
            Assert.IsType<MySqlException>(exception);
            Assert.Equal(SafeMigrationReportStatus.Blocked, final.Status);
        }
        else
        {
            Assert.Null(exception);
            Assert.Equal(SafeMigrationReportStatus.Ready, final.Status);
            Assert.True(final.Assessments[^1].PostconditionSatisfied);
        }
    }

    /// <summary>
    /// Current-database qualifiers and sparse facts survive an unrelated recursive rename-source probe.
    /// </summary>
    [Fact]
    public async Task ForeignKeySparseQualifiedOwnerSurvivesUnrelatedRenameSourceAnalysis()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `unrelated_before` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "INSERT INTO `live_child` VALUES (1), (2);");

        await using var context = CreateContext(connectionString);
        var schema = new MySqlConnectionStringBuilder(connectionString).Database;
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
            "parent_id", "live_child", type: "int", nullable: true));

        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"], "live_parent", ["id"],
            schema: schema);

        builder.RenameTableIfExists("unrelated_before", "unrelated_after");
        builder.CreateIndexIfNotExistsFromModel("ix_unrelated_id", "unrelated_after", "id");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-qualified-rename"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var final = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-qualified-rename-final"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, final.Status);
        Assert.True(final.Assessments[1].PostconditionSatisfied);
    }

    /// <summary>
    /// A canceled new provider invocation cannot expose a previous sparse snapshot to a later stream.
    /// </summary>
    [Fact]
    public async Task ForeignKeySparseFactsAreClearedBeforeCanceledReanalysis()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "INSERT INTO `live_child` VALUES (1), (2);");

        await using var context = CreateContext(connectionString);
        var operations = NullableLivePrincipalOperations(context, guidStorage: false, ReferentialAction.NoAction);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var source = Assert.IsAssignableFrom<ISafeMigrationLivePrerequisiteSource>(analyzer);
        using var cancellation = new CancellationTokenSource();

        // Act
        var first = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("sparse-facts-first"), CancellationToken.None);

        var capturedBeforeCancellation = source.HasLiveColumn("live_parent", schema: null, "id");
        cancellation.Cancel();
        var exception = await Record.ExceptionAsync(async () =>
            await analyzer.AnalyzeAsync(context, operations.Cast<SafeMigrationOperation>().ToArray(),
                cancellation.Token));

        var capturedAfterCancellation = source.HasLiveColumn("live_parent", schema: null, "id");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, first.Status);
        Assert.True(capturedBeforeCancellation);
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.False(capturedAfterCancellation);
    }

    /// <summary>Mixed live/new PK and CHECK prerequisites require a genuine current empty-table certificate.</summary>
    /// <param name="check">Whether the new constraint is a CHECK rather than a composite primary key.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SparseMixedRequiredConstraintUsesLiveColumnsOnlyWithCurrentEmptyProof(bool check)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `live_child` (`id` int NOT NULL) ENGINE=InnoDB;");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
            "new_value", "live_child", type: "int", nullable: false));

        if (check)
        {
            builder.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression(
                "ck_live_child_mixed", "live_child",
                SqlColumnAndColumn("id", SafeMigrationSqlBinaryOperator.LessThanOrEqual, "new_value")),
                SafeMigrationPolicy.ThrowIfDifferent);
        }
        else
        {
            builder.AddPrimaryKeyIfNotExists("pk_live_child_mixed", "live_child", ["id", "new_value"]);
        }

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-empty-required-constraint"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var final = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-empty-required-constraint-final"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments, static assessment =>
            Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, final.Status);
    }

    /// <summary>
    /// A newly accepted child table still needs independently certified unchanged principal metadata.
    /// </summary>
    /// <param name="separateAdd">Whether the FK member is added after an empty convergence-container creation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeySparseNewChildUsesUntouchedLivePrincipal(bool separateAdd)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB;");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var columns = new List<ExpectedColumnDefinition>
        {
            new("id", typeof(int), isNullable: false, storeType: "int"),
        };

        if (!separateAdd)
        {
            columns.Add(new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true, storeType: "int"));
        }

        builder.EnsureTable(new ExpectedTableDefinition("new_child", columns,
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_new_child", "new_child", ["id"])),
            separateAdd ? SafeMigrationTableMode.ConvergenceContainer : SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        if (separateAdd)
        {
            builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
                "parent_id", "new_child", type: "int", nullable: true));
        }

        builder.CreateIndexIfNotExistsFromModel("ix_new_child_parent", "new_child", "parent_id");
        builder.AddForeignKeyIfNotExists("fk_new_child_parent", "new_child", ["parent_id"], "live_parent", ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-new-child"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var final = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-new-child-final"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments, static assessment =>
            Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, final.Status);
    }

    /// <summary>A retained auto-index symbol cannot authorize creation on unrelated new columns.</summary>
    [Fact]
    public async Task ForeignKeySparseOwnedSymbolWithoutNewCoveringIndexIsBlocked()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `old_parent_id` int NULL, "
            + "CONSTRAINT `fk_live_child_parent` FOREIGN KEY (`old_parent_id`) REFERENCES `live_parent` (`id`)) "
            + "ENGINE=InnoDB; INSERT INTO `live_child` (`id`) VALUES (1), (2);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_live_child_parent", "live_child");
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
            "parent_id", "live_child", type: "int", nullable: true));

        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"], "live_parent", ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-index-symbol-collision"), CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations.Take(2).ToArray(), CancellationToken.None);
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            [builder.Operations[2]], CancellationToken.None));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, preflight.Assessments[2].Action);
        Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(exception).Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Duplicate key name", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>SQL keyword spelling must not erase or invent native member-domain differences.</summary>
    /// <param name="memberDrift">Whether literal case or order differs from the live principal domain.</param>
    [Theory]
    [InlineData("none")]
    [InlineData("case")]
    [InlineData("order")]
    public async Task ForeignKeySparseEnumDomainPreservesMemberSpelling(string memberDrift)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` enum('A','b') COLLATE utf8mb4_unicode_ci NOT NULL PRIMARY KEY) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; INSERT INTO `live_child` VALUES (1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var storeType = memberDrift switch
        {
            "none" => "ENUM('A','b')",
            "case" => "ENUM('a','b')",
            "order" => "ENUM('b','A')",
            _ => throw new UnreachableException(),
        };

        builder.EnsureColumn("live_child", new ExpectedColumnDefinition("parent_id", typeof(string),
            isNullable: true, storeType: storeType), SafeMigrationPolicy.ThrowIfDifferent);

        builder.CreateIndexIfNotExistsFromModel("ix_live_child_parent", "live_child", "parent_id");
        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"], "live_parent", ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-enum-domain"), CancellationToken.None);

        if (memberDrift == "none")
        {
            preflight.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        }

        // Assert
        Assert.Equal(memberDrift == "none" ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            preflight.Status);
        Assert.Equal(memberDrift == "none" ? SafeMigrationAction.Apply : SafeMigrationAction.RejectPrerequisiteMissing,
            preflight.Assessments[2].Action);
    }

    /// <summary>Plain safe creation uses actual database defaults unless an endpoint collation is explicit.</summary>
    /// <param name="shape">The creation/default/key boundary being qualified.</param>
    [Theory]
    [InlineData("inherited")]
    [InlineData("explicit")]
    [InlineData("collation_drift")]
    [InlineData("missing_key")]
    [InlineData("missing_parent")]
    public async Task ForeignKeySparseNewChildQualifiesNativeCreationDefaults(string shape)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        var databaseCollation = shape == "inherited" ? "utf8mb4_unicode_ci" : "utf8mb4_general_ci";
        await ExecuteSqlAsync(connectionString, "ALTER DATABASE `"
            + database.Replace("`", "``", StringComparison.Ordinal)
            + "` CHARACTER SET utf8mb4 COLLATE " + databaseCollation + ";");

        if (shape != "missing_parent")
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE TABLE `live_parent` (`id` varchar(36) COLLATE utf8mb4_unicode_ci NOT NULL"
                + (shape == "missing_key" ? ")" : " PRIMARY KEY)")
                + " ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;");
        }

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var explicitCollation = shape is "explicit" or "missing_key" or "missing_parent"
            ? new SafeMigrationCollationIdentifier("utf8mb4_unicode_ci") : null;

        builder.EnsureTable(new ExpectedTableDefinition("new_child",
            [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int"),
                new ExpectedColumnDefinition("parent_id", typeof(string), isNullable: true, storeType: "varchar(36)",
                    collation: explicitCollation)],
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_new_child", "new_child", ["id"])),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        builder.CreateIndexIfNotExistsFromModel("ix_new_child_parent", "new_child", "parent_id");
        builder.AddForeignKeyIfNotExists("fk_new_child_parent", "new_child", ["parent_id"], "live_parent", ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();
        var accepted = shape is "inherited" or "explicit";

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-native-creation-defaults"), CancellationToken.None);

        if (accepted)
        {
            preflight.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        }

        // Assert
        Assert.Equal(accepted ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(accepted ? SafeMigrationAction.Apply : SafeMigrationAction.RejectPrerequisiteMissing,
            preflight.Assessments[2].Action);
        Assert.Equal(accepted ? 1 : 0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'new_child';"));
    }

    /// <summary>Native child-index direction and optimizer visibility do not impose parent-key restrictions.</summary>
    /// <param name="shape">The native covering-index form which avoids the occupied automatic symbol.</param>
    [Theory]
    [InlineData("descending")]
    [InlineData("trailing_descending")]
    [InlineData("optimizer_hidden")]
    public async Task ForeignKeyNativeSupportingIndexVariantsRemainUsable(string shape)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `parent_id` int NULL, `old_id` int NULL, "
            + "INDEX `fk_live_child_parent` (`old_id`)) ENGINE=InnoDB; INSERT INTO `live_child` (`id`) VALUES (1);");

        await ExecuteSqlAsync(connectionString, "CREATE INDEX `ix_child_cover` ON `live_child` (`parent_id`"
            + (shape == "descending" ? " DESC" : shape == "trailing_descending" ? ", `id` DESC" : string.Empty) + ");");

        if (shape == "optimizer_hidden")
        {
            await ExecuteSqlAsync(connectionString, "ALTER TABLE `live_child` ALTER INDEX `ix_child_cover` "
                + (Fixture.IsMariaDb ? "IGNORED" : "INVISIBLE") + ";");
        }

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"], "live_parent", ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();
        var supported = shape != "descending" || Fixture.IsMariaDb;

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-child-supporting-index"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            builder.Operations, CancellationToken.None));

        var final = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-child-supporting-index-final"), CancellationToken.None);

        // Assert
        Assert.Equal(supported ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(supported ? SafeMigrationAction.Apply : SafeMigrationAction.RejectDifferent,
            preflight.Assessments[0].Action);
        Assert.Equal(supported ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, final.Status);
        if (supported)
        {
            Assert.Null(exception);
        }
        else
        {
            Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(exception).Message,
                StringComparison.Ordinal);
        }
    }

    /// <summary>Principal DESC lookup is independent of MySQL's automatic child-index prefix suppression.</summary>
    [Fact]
    public async Task ForeignKeySparsePrincipalDescendingKeyUsesNativeEngineContract()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL, UNIQUE INDEX `uq_parent_desc` (`id` DESC)) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "INSERT INTO `live_child` VALUES (1);");

        await using var context = CreateContext(connectionString);
        var operations = NullableLivePrincipalOperations(context, guidStorage: false, ReferentialAction.NoAction);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("sparse-principal-desc"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, operations.Take(2).ToArray(), CancellationToken.None);
        var nativeFailure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString,
            "ALTER TABLE `live_child` ADD CONSTRAINT `fk_live_child_parent` FOREIGN KEY (`parent_id`) "
            + "REFERENCES `live_parent` (`id`);"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[2].Action);
        Assert.Null(nativeFailure);
    }

    /// <summary>Trailing native expressions must not falsely certify a retained-symbol child prefix.</summary>
    /// <param name="expressionKind">The scalar, quoted-wrapper or multi-valued native expression.</param>
    /// <param name="cascade">Whether native parent deletion must delete the dependent instead of rejecting.</param>
    [Theory]
    [InlineData("scalar", false)]
    [InlineData("multi", false)]
    [InlineData("quoted", false)]
    [InlineData("scalar", true)]
    [InlineData("multi", true)]
    [InlineData("quoted", true)]
    public async Task ForeignKeyNativeTrailingExpressionMatchesNativeIndexEligibility(
        string expressionKind,
        bool cascade
    )
    {
        // Arrange
        var multiValued = expressionKind == "multi";
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `parent_id` int NULL, "
            + "`old_id` int NULL, `values_json` JSON NOT NULL, INDEX `fk_live_child_parent` (`old_id`)) ENGINE=InnoDB; "
            + "INSERT INTO `live_child` (`id`, `values_json`) VALUES (1, '[]');");

        if (Fixture.IsMariaDb)
        {
            // MariaDB has no MySQL expression-index syntax. Its native stored
            // generated trailing part exercises the same complete-index boundary.
            await ExecuteSqlAsync(connectionString,
                "ALTER TABLE `live_child` ADD `derived_id` int GENERATED ALWAYS AS (ABS(`id`)) STORED; "
                + "CREATE INDEX `ix_child_expression` ON `live_child` (`parent_id`, `derived_id`);");
        }
        else
        {
            var expression = expressionKind switch
            {
                "multi" => "CAST(`values_json` AS UNSIGNED ARRAY)",
                "quoted" => "CAST(CONCAT('cast(', 'value array)') AS CHAR(32))",
                "scalar" => "ABS(`id`)",
                _ => throw new UnreachableException(),
            };

            await ExecuteSqlAsync(connectionString,
                "CREATE INDEX `ix_child_expression` ON `live_child` (`parent_id`, ("
                + expression + "));");
        }

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"], "live_parent", ["id"],
            onDelete: cascade ? ReferentialAction.Cascade : ReferentialAction.NoAction);
        var runner = context.GetService<ISafeMigrationRunner>();
        var supported = !multiValued || Fixture.IsMariaDb;

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-trailing-expression-index"), CancellationToken.None);

        var nativeIndexFacts = Fixture.IsMariaDb ? "stored generated trailing column" : await ScalarStringAsync(
            connectionString, "SELECT JSON_ARRAYAGG(JSON_OBJECT('ordinal', SEQ_IN_INDEX, 'column', COLUMN_NAME, "
            + "'direction', COLLATION, 'expression', EXPRESSION, 'type', INDEX_TYPE)) "
            + "FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'live_child' AND INDEX_NAME = 'ix_child_expression';");

        var managedFailure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            builder.Operations, CancellationToken.None));

        Exception? restrictedDelete = null;
        if (supported)
        {
            await ExecuteSqlAsync(connectionString,
                "INSERT INTO `live_parent` VALUES (1); UPDATE `live_child` SET `parent_id` = 1 WHERE `id` = 1;");

            restrictedDelete = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString,
                "DELETE FROM `live_parent` WHERE `id` = 1;"));
        }

        // Assert
        Assert.Equal(supported ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(supported ? SafeMigrationAction.Apply : SafeMigrationAction.RejectUnsupported,
            preflight.Assessments[0].Action);
        if (!supported)
        {
            Assert.Equal("foreign_key_multi_valued_supporting_index", preflight.Assessments[0].AnalysisCode);
            Assert.Contains("doka_sm_unsupported", Assert.IsType<MySqlException>(managedFailure).Message,
                StringComparison.Ordinal);
        }
        else if (cascade)
        {
            Assert.Null(managedFailure);
            Assert.Null(restrictedDelete);
        }
        else
        {
            Assert.Null(managedFailure);
            Assert.True(restrictedDelete is MySqlException, "Native supporting-index facts: " + nativeIndexFacts);
        }

        Assert.Equal(!supported || cascade ? 0 : 1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `live_child` WHERE `parent_id` = 1;"));
    }

    /// <summary>Another index or an existing semantic FK cannot certify unsafe native multi-valued lookup.</summary>
    /// <param name="shape">The native symbol, FK or additional-index order being evaluated.</param>
    /// <param name="cascade">The requested native deletion behavior.</param>
    [Theory]
    [InlineData("free_symbol", false)]
    [InlineData("occupied_symbol", false)]
    [InlineData("matching", false)]
    [InlineData("alias", false)]
    [InlineData("ordinary_before", false)]
    [InlineData("ordinary_after", false)]
    [InlineData("free_symbol", true)]
    [InlineData("occupied_symbol", true)]
    [InlineData("matching", true)]
    [InlineData("alias", true)]
    [InlineData("ordinary_before", true)]
    [InlineData("ordinary_after", true)]
    public async Task ForeignKeyNativeMultiValuedRiskCannotBorrowNamespaceOrOtherIndex(string shape, bool cascade)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `parent_id` int NULL, "
            + "`old_id` int NULL, `values_json` JSON NOT NULL) ENGINE=InnoDB; "
            + "INSERT INTO `live_child` (`id`, `values_json`) VALUES (1, '[]');");

        if (shape == "occupied_symbol")
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE INDEX `fk_live_child_parent` ON `live_child` (`old_id`);");
        }

        if (shape == "ordinary_before")
        {
            await ExecuteSqlAsync(connectionString, "CREATE INDEX `ix_ordinary` ON `live_child` (`parent_id`);");
        }

        await ExecuteSqlAsync(connectionString, Fixture.IsMariaDb
            ? "ALTER TABLE `live_child` ADD `derived_id` int GENERATED ALWAYS AS (ABS(`id`)) STORED; "
                + "CREATE INDEX `ix_child_multi` ON `live_child` (`parent_id`, `derived_id`);"
            : "CREATE INDEX `ix_child_multi` ON `live_child` (`parent_id`, (CAST(`values_json` AS UNSIGNED ARRAY)));");

        if (shape == "ordinary_after")
        {
            await ExecuteSqlAsync(connectionString, "CREATE INDEX `ix_ordinary` ON `live_child` (`parent_id`);");
        }

        var existing = shape is "matching" or "alias";
        if (existing)
        {
            var name = shape == "matching" ? "fk_live_child_parent" : "fk_semantic_alias";
            await ExecuteSqlAsync(connectionString, "ALTER TABLE `live_child` ADD CONSTRAINT `" + name
                + "` FOREIGN KEY (`parent_id`) REFERENCES `live_parent` (`id`)"
                + (cascade ? " ON DELETE CASCADE" : string.Empty) + ";");
        }

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"], "live_parent", ["id"],
            onDelete: cascade ? ReferentialAction.Cascade : ReferentialAction.NoAction);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-multi-index-risk"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            builder.Operations, CancellationToken.None));

        var final = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-multi-index-risk-final"), CancellationToken.None);

        // Assert
        Assert.Equal(Fixture.IsMariaDb ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            preflight.Status);
        Assert.Equal(Fixture.IsMariaDb
                ? existing ? SafeMigrationAction.NoOp : SafeMigrationAction.Apply
                : SafeMigrationAction.RejectUnsupported,
            preflight.Assessments[0].Action);

        Assert.Equal(Fixture.IsMariaDb ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            final.Status);
        if (Fixture.IsMariaDb)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal("foreign_key_multi_valued_supporting_index", preflight.Assessments[0].AnalysisCode);
            Assert.False(final.Assessments[0].PostconditionSatisfied);
            Assert.Contains("doka_sm_unsupported", Assert.IsType<MySqlException>(failure).Message,
                StringComparison.Ordinal);
        }

        Assert.Equal(Fixture.IsMariaDb || existing ? 1 : 0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'live_child';"));
    }

    /// <summary>
    /// Explicit removal and a genuinely new NULL endpoint retire risk without borrowing old row integrity.
    /// </summary>
    /// <param name="newOwner">Whether the accepted owner itself is recreated instead of replacing one column.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeyNativeMultiValuedRiskCanBeRetiredByAcceptedPhysicalRemovals(bool newOwner)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `parent_id` int NULL, "
            + "`values_json` JSON NOT NULL) ENGINE=InnoDB; "
            + "INSERT INTO `live_child` (`id`, `values_json`) VALUES (1, '[]');");

        await ExecuteSqlAsync(connectionString, Fixture.IsMariaDb
            ? "ALTER TABLE `live_child` ADD `derived_id` int GENERATED ALWAYS AS (ABS(`id`)) STORED; "
                + "CREATE INDEX `ix_child_multi` ON `live_child` (`parent_id`, `derived_id`);"
            : "CREATE INDEX `ix_child_multi` ON `live_child` (`parent_id`, (CAST(`values_json` AS UNSIGNED ARRAY)));");

        await ExecuteSqlAsync(connectionString,
            "ALTER TABLE `live_child` ADD CONSTRAINT `fk_live_child_parent` FOREIGN KEY (`parent_id`) "
            + "REFERENCES `live_parent` (`id`);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (newOwner)
        {
            builder.DropTableIfExists("live_child");
            builder.EnsureTable(new ExpectedTableDefinition("live_child",
                [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int"),
                    new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true, storeType: "int")],
                primaryKey: new ExpectedPrimaryKeyDefinition("pk_live_child", "live_child", ["id"])),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }
        else
        {
            builder.DropForeignKeyIfExists("fk_live_child_parent", "live_child");
            builder.DropIndexIfExists("ix_child_multi", "live_child");
            builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
                "fresh_parent_id", "live_child", type: "int", nullable: true));
        }

        var endpoint = newOwner ? "parent_id" : "fresh_parent_id";
        builder.CreateIndexIfNotExistsFromModel("ix_new_parent", "live_child", endpoint);
        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", [endpoint], "live_parent", ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-multi-index-retirement"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var final = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-multi-index-retirement-final"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[^1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, final.Status);
        Assert.Equal(newOwner ? 0 : 1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `live_child` WHERE `" + endpoint + "` IS NULL;"));
    }

    /// <summary>
    /// Removing an unsafe index while its FK remains must not erase runtime evidence of old orphan rows.
    /// </summary>
    /// <param name="alias">Whether the surviving physical FK differs from the requested semantic symbol.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeyNativeMultiValuedDropCannotEraseUnsafeConstraintHistory(bool alias)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `live_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `live_child` (`id` int NOT NULL PRIMARY KEY, `parent_id` int NULL, "
            + "`values_json` JSON NOT NULL) ENGINE=InnoDB; "
            + "INSERT INTO `live_child` (`id`, `values_json`) VALUES (1, '[]');");

        await ExecuteSqlAsync(connectionString, Fixture.IsMariaDb
            ? "ALTER TABLE `live_child` ADD `derived_id` int GENERATED ALWAYS AS (ABS(`id`)) STORED; "
                + "CREATE INDEX `ix_child_multi` ON `live_child` (`parent_id`, `derived_id`);"
            : "CREATE INDEX `ix_child_multi` ON `live_child` (`parent_id`, (CAST(`values_json` AS UNSIGNED ARRAY)));");

        var nativeName = alias ? "fk_semantic_alias" : "fk_live_child_parent";
        await ExecuteSqlAsync(connectionString, "ALTER TABLE `live_child` ADD CONSTRAINT `" + nativeName
            + "` FOREIGN KEY (`parent_id`) REFERENCES `live_parent` (`id`);");

        if (!Fixture.IsMariaDb)
        {
            // WHY: The native MVI lookup itself can miss an empty-array row.
            // Reproduce that integrity failure without disabling FK checks.
            await ExecuteSqlAsync(connectionString,
                "INSERT INTO `live_parent` VALUES (999); UPDATE `live_child` SET `parent_id`=999; "
                + "DELETE FROM `live_parent` WHERE `id`=999;");
        }

        await ExecuteSqlAsync(connectionString, "CREATE INDEX `ix_ordinary` ON `live_child` (`parent_id`);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("ix_child_multi", "live_child");
        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"], "live_parent", ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-multi-index-history"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            builder.Operations, CancellationToken.None));

        // Assert
        Assert.Equal(Fixture.IsMariaDb ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            preflight.Status);
        if (Fixture.IsMariaDb)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal(SafeMigrationAction.RejectUnsupported, preflight.Assessments[0].Action);
            Assert.Contains("doka_sm_unsupported", Assert.IsType<MySqlException>(failure).Message,
                StringComparison.Ordinal);
            Assert.Equal(1, await ScalarIntAsync(connectionString,
                "SELECT COUNT(DISTINCT INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='live_child' AND INDEX_NAME='ix_child_multi';"));
        }
    }
}
