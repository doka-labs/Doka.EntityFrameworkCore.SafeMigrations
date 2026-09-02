namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task EquivalentUniqueAndCheckConstraintsWithDifferentNames_AreIdempotentNoOps()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `constraint_identity` ("
            + "`code` int NULL, `tenant_id` int NULL, `quantity` int NOT NULL, "
            + "CONSTRAINT `uq_constraint_identity_legacy_a` UNIQUE (`code`, `tenant_id`), "
            + "CONSTRAINT `uq_constraint_identity_legacy_b` UNIQUE (`code`, `tenant_id`), "
            + "CONSTRAINT `ck_constraint_identity_legacy_a` CHECK (`quantity` >= 0), "
            + "CONSTRAINT `ck_constraint_identity_legacy_b` CHECK (`quantity` >= 0));");

        var legacyConstraintCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'constraint_identity' "
            + "AND CONSTRAINT_TYPE IN ('UNIQUE', 'CHECK');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddUniqueConstraintIfNotExists(
            "uq_constraint_identity_expected",
            "constraint_identity",
            ["code", "tenant_id"]);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_constraint_identity_expected",
                "constraint_identity",
                SqlColumnAndInt("quantity", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("constraint-identity"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
            });
        Assert.DoesNotContain(
            report.UnexpectedObjects,
            static unexpected => unexpected.ObjectKind is SafeMigrationDatabaseObjectKind.UniqueConstraint
                or SafeMigrationDatabaseObjectKind.CheckConstraint);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS "
                + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'constraint_identity' "
                + "AND CONSTRAINT_NAME IN ('uq_constraint_identity_expected', "
                + "'ck_constraint_identity_expected');"));
        Assert.Equal(
            legacyConstraintCount,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS "
                + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'constraint_identity' "
                + "AND CONSTRAINT_TYPE IN ('UNIQUE', 'CHECK');"));
        Assert.True(legacyConstraintCount >= 3);
    }

    [Fact]
    public async Task DifferentlyNamedUniqueAndCheckConstraintsWithDifferentShapes_RemainApplicable()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `constraint_nonidentity` ("
            + "`code` int NULL, `alternate_code` int NULL, `quantity` int NOT NULL, "
            + "CONSTRAINT `uq_constraint_nonidentity_legacy` UNIQUE (`code`), "
            + "CONSTRAINT `ck_constraint_nonidentity_legacy` CHECK (`quantity` >= 0));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddUniqueConstraintIfNotExists(
            "uq_constraint_nonidentity_expected",
            "constraint_nonidentity",
            ["alternate_code"]);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_constraint_nonidentity_expected",
                "constraint_nonidentity",
                SqlColumnAndInt("quantity", SafeMigrationSqlBinaryOperator.LessThanOrEqual, 100)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var preflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("constraint-nonidentity"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var postflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("constraint-nonidentity-post"));

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(
            preflight.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Missing, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.Apply, assessment.Action);
            });
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
    }

    [Fact]
    public async Task MariaDbTableScopedCheckNames_DoNotCrossMatchCatalogRows()
    {
        if (!Fixture.IsMariaDb || Fixture.ServerVersion.Version < new Version(12, 1))
        {
            return;
        }

        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `check_scope_target` (`value` int NOT NULL, "
            + "CONSTRAINT `ck_shared_identity` CHECK (`value` <= 100));"
            + "CREATE TABLE `check_scope_other` (`value` int NOT NULL, "
            + "CONSTRAINT `ck_shared_identity` CHECK (`value` >= 0));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_shared_identity",
                "check_scope_target",
                SqlColumnAndInt("value", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("check-table-scope"));

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MySqlNonEnforcedCheckWithExpectedName_IsRejectedBeforeDdl()
    {
        if (Fixture.IsMariaDb)
        {
            return;
        }

        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `check_enforcement_drift` (`quantity` int NOT NULL, "
            + "CONSTRAINT `ck_check_enforcement_drift` CHECK (`quantity` >= 0) NOT ENFORCED);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_check_enforcement_drift",
                "check_enforcement_drift",
                SqlColumnAndInt("quantity", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("check-enforcement-drift"));

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MySqlDifferentlyNamedNonEnforcedCheck_DoesNotBlockEnforcedConstraint()
    {
        if (Fixture.IsMariaDb)
        {
            return;
        }

        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `check_enforcement_identity` (`quantity` int NOT NULL, "
            + "CONSTRAINT `ck_check_enforcement_legacy` CHECK (`quantity` >= 0) NOT ENFORCED);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_check_enforcement_expected",
                "check_enforcement_identity",
                SqlColumnAndInt("quantity", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var preflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("check-enforcement-identity"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var postflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("check-enforcement-post"));

        var preflightAssessment = Assert.Single(preflight.Assessments);
        var postflightAssessment = Assert.Single(postflight.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, preflightAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, preflightAssessment.Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, postflightAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, postflightAssessment.Action);
    }

    [Fact]
    public async Task MySqlSchemaScopedConstraintNameCollisions_AreRejectedBeforeDdl()
    {
        if (Fixture.IsMariaDb)
        {
            return;
        }

        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `namespace_owner_parent` (`id` int NOT NULL, PRIMARY KEY (`id`));"
            + "CREATE TABLE `namespace_target_parent` (`id` int NOT NULL, PRIMARY KEY (`id`));"
            + "CREATE TABLE `namespace_owner` ("
            + "`id` int NOT NULL, `quantity` int NOT NULL, `parent_id` int NULL, PRIMARY KEY (`id`), "
            + "CONSTRAINT `ck_namespace_collision` CHECK (`quantity` >= 0), "
            + "CONSTRAINT `fk_namespace_collision` FOREIGN KEY (`parent_id`) "
            + "REFERENCES `namespace_owner_parent` (`id`));"
            + "CREATE TABLE `namespace_target` ("
            + "`id` int NOT NULL, `quantity` int NOT NULL, `parent_id` int NULL, PRIMARY KEY (`id`));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_namespace_collision",
                "namespace_target",
                SqlColumnAndInt("quantity", SafeMigrationSqlBinaryOperator.LessThanOrEqual, 100)),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.AddForeignKeyIfNotExists(
            "fk_namespace_collision",
            "namespace_target",
            ["parent_id"],
            "namespace_target_parent",
            ["id"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("constraint-namespace"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.All(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
            });

        foreach (var operation in builder.Operations)
        {
            var exception = await Assert.ThrowsAsync<MySqlException>(() =>
                ExecuteOperationsAsync(context, [operation], CancellationToken.None));

            Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS "
                + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'namespace_target' "
                + "AND CONSTRAINT_NAME IN ('ck_namespace_collision', 'fk_namespace_collision');"));
    }

    [Fact]
    public async Task MariaDbForeignKeyNameScope_FollowsTheServerVersionBoundary()
    {
        if (!Fixture.IsMariaDb)
        {
            return;
        }

        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `fk_scope_owner_parent` (`id` int NOT NULL, PRIMARY KEY (`id`));"
            + "CREATE TABLE `fk_scope_target_parent` (`id` int NOT NULL, PRIMARY KEY (`id`));"
            + "CREATE TABLE `fk_scope_owner` ("
            + "`id` int NOT NULL, `parent_id` int NULL, PRIMARY KEY (`id`), "
            + "CONSTRAINT `fk_scope_shared` FOREIGN KEY (`parent_id`) "
            + "REFERENCES `fk_scope_owner_parent` (`id`));"
            + "CREATE TABLE `fk_scope_target` ("
            + "`id` int NOT NULL, `parent_id` int NULL, PRIMARY KEY (`id`));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists(
            "fk_scope_shared",
            "fk_scope_target",
            ["parent_id"],
            "fk_scope_target_parent",
            ["id"]);

        var preflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("foreign-key-name-scope"));
        var preflightAssessment = Assert.Single(preflight.Assessments);

        if (Fixture.ServerVersion.Version < new Version(12, 1))
        {
            var exception = await Assert.ThrowsAsync<MySqlException>(() =>
                ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

            Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
            Assert.Equal(SafeMigrationObservedState.Different, preflightAssessment.ObservedState);
            Assert.Equal(SafeMigrationAction.RejectDifferent, preflightAssessment.Action);
            Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                1,
                await ScalarIntAsync(
                    connectionString,
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
                    + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND CONSTRAINT_NAME = 'fk_scope_shared';"));

            return;
        }

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("foreign-key-name-scope-post"));
        var postflightAssessment = Assert.Single(postflight.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, preflightAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, preflightAssessment.Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, postflightAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, postflightAssessment.Action);
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
                + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND CONSTRAINT_NAME = 'fk_scope_shared';"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EquivalentForeignKeyWithDifferentName_IsAnIdempotentNoOp(
        int legacyConstraintCount
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var legacyName = new string('l', 64);
        var secondLegacyName = new string('s', 64);
        var expectedName = new string('e', 64);
        var secondConstraint = legacyConstraintCount == 2
            ? $", CONSTRAINT `{secondLegacyName}` FOREIGN KEY (`parent_id`, `tenant_id`) "
                + "REFERENCES `identity_parents` (`id`, `tenant_id`) ON DELETE CASCADE"
            : string.Empty;

        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `identity_parents` (`id` int NOT NULL, `tenant_id` int NOT NULL, "
            + "PRIMARY KEY (`id`, `tenant_id`));"
            + "CREATE TABLE `identity_children` ("
            + "`id` int NOT NULL, `parent_id` int NOT NULL, `tenant_id` int NOT NULL, PRIMARY KEY (`id`), "
            + $"CONSTRAINT `{legacyName}` FOREIGN KEY (`parent_id`, `tenant_id`) "
            + $"REFERENCES `identity_parents` (`id`, `tenant_id`) ON DELETE CASCADE{secondConstraint});");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists(
            expectedName,
            "identity_children",
            ["parent_id", "tenant_id"],
            "identity_parents",
            ["id", "tenant_id"],
            onDelete: ReferentialAction.Cascade);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("foreign-key-identity"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
        Assert.DoesNotContain(
            report.UnexpectedObjects,
            static unexpected => unexpected.ObjectKind == SafeMigrationDatabaseObjectKind.ForeignKey);
        Assert.Equal(
            legacyConstraintCount,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
                + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'identity_children';"));
    }

    [Fact]
    public async Task ExactNameDrift_IsNeverHiddenByEquivalentAliases()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `identity_precedence_parents` (`id` int NOT NULL, PRIMARY KEY (`id`));"
            + "CREATE TABLE `identity_precedence_children` ("
            + "`id` int NOT NULL, `code` int NULL, `alternate_code` int NULL, "
            + "`quantity` int NOT NULL, `parent_id` int NULL, PRIMARY KEY (`id`), "
            + "CONSTRAINT `uq_identity_precedence_expected` UNIQUE (`alternate_code`), "
            + "CONSTRAINT `uq_identity_precedence_legacy` UNIQUE (`code`), "
            + "CONSTRAINT `ck_identity_precedence_expected` CHECK (`quantity` <= 100), "
            + "CONSTRAINT `ck_identity_precedence_legacy` CHECK (`quantity` >= 0), "
            + "CONSTRAINT `fk_identity_precedence_expected` FOREIGN KEY (`parent_id`) "
            + "REFERENCES `identity_precedence_parents` (`id`) ON DELETE RESTRICT, "
            + "CONSTRAINT `fk_identity_precedence_legacy` FOREIGN KEY (`parent_id`) "
            + "REFERENCES `identity_precedence_parents` (`id`) ON DELETE CASCADE, "
            + "INDEX `ix_identity_precedence_expected` (`alternate_code`), "
            + "INDEX `ix_identity_precedence_legacy` (`code`));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddUniqueConstraintIfNotExists(
            "uq_identity_precedence_expected",
            "identity_precedence_children",
            ["code"]);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_identity_precedence_expected",
                "identity_precedence_children",
                SqlColumnAndInt("quantity", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.AddForeignKeyIfNotExists(
            "fk_identity_precedence_expected",
            "identity_precedence_children",
            ["parent_id"],
            "identity_precedence_parents",
            ["id"],
            onDelete: ReferentialAction.Cascade);
        builder.CreateIndexIfNotExists(
            "ix_identity_precedence_expected",
            "identity_precedence_children",
            ["code"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("identity-precedence"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.All(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
            });

        foreach (var operation in builder.Operations)
        {
            var exception = await Assert.ThrowsAsync<MySqlException>(() =>
                ExecuteOperationsAsync(context, [operation]));

            Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
                + "WHERE CONSTRAINT_SCHEMA = DATABASE() "
                + "AND TABLE_NAME = 'identity_precedence_children';"));
    }

    [Theory]
    [InlineData(ReferentialAction.Cascade, ReferentialAction.Cascade)]
    [InlineData(ReferentialAction.Restrict, ReferentialAction.SetNull)]
    public async Task DifferentlyNamedForeignKeyWithDifferentActions_RemainsApplicable(
        ReferentialAction onUpdate,
        ReferentialAction onDelete
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `identity_action_parents` (`id` int NOT NULL, PRIMARY KEY (`id`));"
            + "CREATE TABLE `identity_action_children` ("
            + "`id` int NOT NULL, `parent_id` int NULL, PRIMARY KEY (`id`), "
            + "CONSTRAINT `fk_identity_action_legacy` FOREIGN KEY (`parent_id`) "
            + "REFERENCES `identity_action_parents` (`id`) ON DELETE CASCADE);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists(
            "fk_identity_action_expected",
            "identity_action_children",
            ["parent_id"],
            "identity_action_parents",
            ["id"],
            onUpdate: onUpdate,
            onDelete: onDelete);

        var preflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("foreign-key-actions"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var assessment = Assert.Single(preflight.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, assessment.Action);
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
                + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'identity_action_children';"));
    }

    [Fact]
    public async Task ObservableConstraintFacetDrift_IsRejectedOneFieldAtATime()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `constraint_matrix_parents` ("
            + "`id` int NOT NULL, `alternate_id` int NOT NULL, "
            + "PRIMARY KEY (`id`, `alternate_id`)); "
            + "CREATE TABLE `constraint_matrix_other_parents` ("
            + "`id` int NOT NULL, `alternate_id` int NOT NULL, "
            + "PRIMARY KEY (`id`, `alternate_id`)); "
            + "CREATE TABLE `constraint_matrix_children` ("
            + "`id` int NOT NULL, `alternate_id` int NOT NULL, "
            + "`code` varchar(30) NULL, `alternate_code` varchar(30) NULL, "
            + "`quantity` int NOT NULL, `parent_id` int NULL, `alternate_parent_id` int NULL);");
        await using var context = CreateContext(connectionString);
        var canonical = new MigrationBuilder(context.Database.ProviderName!);
        canonical.AddPrimaryKeyIfNotExists(
            "pk_constraint_matrix_children",
            "constraint_matrix_children",
            ["id", "alternate_id"]);
        canonical.AddUniqueConstraintIfNotExists(
            "uq_constraint_matrix_code",
            "constraint_matrix_children",
            ["code", "alternate_code"]);
        canonical.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_constraint_matrix_quantity",
                "constraint_matrix_children",
                SqlColumnAndInt("quantity", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);
        canonical.AddForeignKeyIfNotExists(
            "fk_constraint_matrix_parent",
            "constraint_matrix_children",
            ["parent_id", "alternate_parent_id"],
            "constraint_matrix_parents",
            ["id", "alternate_id"],
            onUpdate: ReferentialAction.Cascade,
            onDelete: ReferentialAction.SetNull);

        await ExecuteOperationsAsync(context, canonical.Operations);
        await ExecuteOperationsAsync(context, canonical.Operations);

        var canonicalReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, canonical.Operations, new SafeMigrationRunOptions("constraint-matrix-canonical"));

        Assert.Equal(SafeMigrationReportStatus.Ready, canonicalReport.Status);
        Assert.All(
            canonicalReport.Assessments,
            assessment => Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState));

        await ExecuteSqlAsync(
            connectionString,
            "ALTER TABLE `constraint_matrix_children` "
            + "ADD CONSTRAINT `uq_constraint_matrix_duplicate` UNIQUE (`code`, `alternate_code`), "
            + "ADD CONSTRAINT `ck_constraint_matrix_duplicate` CHECK (`quantity` >= 0), "
            + "ADD CONSTRAINT `fk_constraint_matrix_duplicate` "
            + "FOREIGN KEY (`parent_id`, `alternate_parent_id`) "
            + "REFERENCES `constraint_matrix_parents` (`id`, `alternate_id`) "
            + "ON UPDATE CASCADE ON DELETE SET NULL;");

        var strictDefinition = CreateStrictConstraintMatrixTable(ReferentialAction.SetNull);
        var strict = new MigrationBuilder(context.Database.ProviderName!);
        strict.EnsureTable(
            strictDefinition,
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        var strictReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, strict.Operations, new SafeMigrationRunOptions("constraint-matrix-strict"));

        Assert.Equal(SafeMigrationReportStatus.Ready, strictReport.Status);
        Assert.Equal(
            SafeMigrationObservedState.Matching,
            Assert.Single(strictReport.Assessments)
                .ObservedState);

        var strictAliases = new MigrationBuilder(context.Database.ProviderName!);
        strictAliases.EnsureTable(
            CreateStrictConstraintMatrixTable(ReferentialAction.SetNull, useAliasNames: true),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        var strictAliasReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                strictAliases.Operations,
                new SafeMigrationRunOptions("constraint-matrix-strict-aliases"));

        Assert.Equal(SafeMigrationReportStatus.Ready, strictAliasReport.Status);
        Assert.Equal(
            SafeMigrationObservedState.Matching,
            Assert.Single(strictAliasReport.Assessments)
                .ObservedState);
        Assert.DoesNotContain(
            strictAliasReport.UnexpectedObjects,
            static unexpected => StringComparer.Ordinal.Equals(unexpected.Table, "constraint_matrix_children")
                && (unexpected.ObjectKind is SafeMigrationDatabaseObjectKind.UniqueConstraint
                    or SafeMigrationDatabaseObjectKind.CheckConstraint
                    or SafeMigrationDatabaseObjectKind.ForeignKey));

        var strictDrift = new MigrationBuilder(context.Database.ProviderName!);
        strictDrift.EnsureTable(
            CreateStrictConstraintMatrixTable(ReferentialAction.Cascade),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        var strictDriftReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                strictDrift.Operations,
                new SafeMigrationRunOptions("constraint-matrix-strict-drift"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, strictDriftReport.Status);
        Assert.Equal(
            SafeMigrationObservedState.Different,
            Assert.Single(strictDriftReport.Assessments)
                .ObservedState);

        var variants = new List<IReadOnlyList<MigrationOperation>>();

        var primaryKeyOrder = new MigrationBuilder(context.Database.ProviderName!);
        primaryKeyOrder.AddPrimaryKeyIfNotExists(
            "pk_constraint_matrix_children",
            "constraint_matrix_children",
            ["alternate_id", "id"]);
        variants.Add(primaryKeyOrder.Operations);

        var uniqueColumnOrder = new MigrationBuilder(context.Database.ProviderName!);
        uniqueColumnOrder.AddUniqueConstraintIfNotExists(
            "uq_constraint_matrix_code",
            "constraint_matrix_children",
            ["alternate_code", "code"]);
        variants.Add(uniqueColumnOrder.Operations);

        var checkExpression = new MigrationBuilder(context.Database.ProviderName!);
        checkExpression.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_constraint_matrix_quantity",
                "constraint_matrix_children",
                SqlColumnAndInt("quantity", SafeMigrationSqlBinaryOperator.GreaterThan, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);
        variants.Add(checkExpression.Operations);

        var dependentColumnOrder = new MigrationBuilder(context.Database.ProviderName!);
        dependentColumnOrder.AddForeignKeyIfNotExists(
            "fk_constraint_matrix_parent",
            "constraint_matrix_children",
            ["alternate_parent_id", "parent_id"],
            "constraint_matrix_parents",
            ["id", "alternate_id"],
            onUpdate: ReferentialAction.Cascade,
            onDelete: ReferentialAction.SetNull);
        variants.Add(dependentColumnOrder.Operations);

        var principalTable = new MigrationBuilder(context.Database.ProviderName!);
        principalTable.AddForeignKeyIfNotExists(
            "fk_constraint_matrix_parent",
            "constraint_matrix_children",
            ["parent_id", "alternate_parent_id"],
            "constraint_matrix_other_parents",
            ["id", "alternate_id"],
            onUpdate: ReferentialAction.Cascade,
            onDelete: ReferentialAction.SetNull);
        variants.Add(principalTable.Operations);

        var principalColumnOrder = new MigrationBuilder(context.Database.ProviderName!);
        principalColumnOrder.AddForeignKeyIfNotExists(
            "fk_constraint_matrix_parent",
            "constraint_matrix_children",
            ["parent_id", "alternate_parent_id"],
            "constraint_matrix_parents",
            ["alternate_id", "id"],
            onUpdate: ReferentialAction.Cascade,
            onDelete: ReferentialAction.SetNull);
        variants.Add(principalColumnOrder.Operations);

        var updateAction = new MigrationBuilder(context.Database.ProviderName!);
        updateAction.AddForeignKeyIfNotExists(
            "fk_constraint_matrix_parent",
            "constraint_matrix_children",
            ["parent_id", "alternate_parent_id"],
            "constraint_matrix_parents",
            ["id", "alternate_id"],
            onUpdate: ReferentialAction.NoAction,
            onDelete: ReferentialAction.SetNull);
        variants.Add(updateAction.Operations);

        var deleteAction = new MigrationBuilder(context.Database.ProviderName!);
        deleteAction.AddForeignKeyIfNotExists(
            "fk_constraint_matrix_parent",
            "constraint_matrix_children",
            ["parent_id", "alternate_parent_id"],
            "constraint_matrix_parents",
            ["id", "alternate_id"],
            onUpdate: ReferentialAction.Cascade,
            onDelete: ReferentialAction.Cascade);
        variants.Add(deleteAction.Operations);

        foreach (var operations in variants)
        {
            var report = await context
                .GetService<ISafeMigrationRunner>()
                .AnalyzeAsync(context, operations, new SafeMigrationRunOptions("constraint-matrix-drift"));

            var assessment = Assert.Single(report.Assessments);

            Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
            Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
            Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        }
    }

    private static ExpectedTableDefinition CreateStrictConstraintMatrixTable(
        ReferentialAction onDelete,
        bool useAliasNames = false
    ) => new(
        "constraint_matrix_children",
        [
            new ExpectedColumnDefinition("id", typeof(int), false, "int"),
            new ExpectedColumnDefinition("alternate_id", typeof(int), false, "int"),
            new ExpectedColumnDefinition("code", typeof(string), true, "varchar(30)", maxLength: 30),
            new ExpectedColumnDefinition("alternate_code", typeof(string), true, "varchar(30)", maxLength: 30),
            new ExpectedColumnDefinition("quantity", typeof(int), false, "int"),
            new ExpectedColumnDefinition("parent_id", typeof(int), true, "int"),
            new ExpectedColumnDefinition("alternate_parent_id", typeof(int), true, "int"),
        ],
        primaryKey:
        new ExpectedPrimaryKeyDefinition(
            useAliasNames ? "pk_constraint_matrix_alias" : "pk_constraint_matrix_children",
            "constraint_matrix_children",
            ["id", "alternate_id"]),
        uniqueConstraints
        :
        [
            new ExpectedUniqueConstraintDefinition(
                useAliasNames ? "uq_constraint_matrix_alias" : "uq_constraint_matrix_code",
                "constraint_matrix_children",
                ["code", "alternate_code"]),
        ],
        checkConstraints:
        [
            ExpectedCheckConstraintDefinition.FromExpression(
                useAliasNames ? "ck_constraint_matrix_alias" : "ck_constraint_matrix_quantity",
                "constraint_matrix_children",
                SqlColumnAndInt("quantity", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
        ],
        foreignKeys:
        [
            new ExpectedForeignKeyDefinition(
                useAliasNames ? "fk_constraint_matrix_alias" : "fk_constraint_matrix_parent",
                "constraint_matrix_children",
                ["parent_id", "alternate_parent_id"],
                "constraint_matrix_parents",
                ["id", "alternate_id"],
                onUpdate: ReferentialAction.Cascade,
                onDelete: onDelete),
        ]);
}
