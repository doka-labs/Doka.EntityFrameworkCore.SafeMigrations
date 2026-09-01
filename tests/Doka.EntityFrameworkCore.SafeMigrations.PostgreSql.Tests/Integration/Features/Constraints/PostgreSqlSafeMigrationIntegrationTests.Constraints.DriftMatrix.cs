namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task EquivalentUniqueAndCheckConstraintsWithDifferentNames_AreRejectedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE constraint_identity ("
            + "code integer NULL, quantity integer NOT NULL, "
            + "CONSTRAINT uq_constraint_identity_legacy UNIQUE (code), "
            + "CONSTRAINT ck_constraint_identity_legacy CHECK (quantity >= 0));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddUniqueConstraintIfNotExists(
            "uq_constraint_identity_expected",
            "constraint_identity",
            ["code"]);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_constraint_identity_expected",
                "constraint_identity",
                SqlColumnAndInt("quantity", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("constraint-identity"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Collection(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
                Assert.Equal("unique_constraint_semantic_identity_conflict", assessment.Code);
            },
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
                Assert.Equal("check_constraint_semantic_identity_conflict", assessment.Code);
            });

        foreach (var operation in builder.Operations)
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteOperationsAsync(context, [operation]));

            Assert.Equal("P1002", exception.SqlState);
            Assert.Equal("doka_sm_unsupported", exception.MessageText);
        }

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_constraint co "
                + "JOIN pg_catalog.pg_class c ON c.oid = co.conrelid "
                + "WHERE c.relname = 'constraint_identity' "
                + "AND co.conname IN ('uq_constraint_identity_expected', "
                + "'ck_constraint_identity_expected');"));
    }

    [Fact]
    public async Task DifferentlyNamedUniqueAndCheckConstraintsWithDifferentShapes_RemainApplicable()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE constraint_nonidentity ("
            + "code integer NULL, alternate_code integer NULL, quantity integer NOT NULL, "
            + "CONSTRAINT uq_constraint_nonidentity_legacy UNIQUE (code), "
            + "CONSTRAINT ck_constraint_nonidentity_legacy CHECK (quantity >= 0));");

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
    public async Task ExistingPrimaryKeyWithDifferentName_IsRejectedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE primary_key_identity ("
            + "id integer NOT NULL, CONSTRAINT pk_primary_key_legacy PRIMARY KEY (id));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddPrimaryKeyIfNotExists(
            "pk_primary_key_expected",
            "primary_key_identity",
            ["id"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("primary-key-identity"));

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
        Assert.Equal("primary_key_identity_conflict", assessment.Code);
        Assert.Equal("P1002", exception.SqlState);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_constraint co "
                + "JOIN pg_catalog.pg_class c ON c.oid = co.conrelid "
                + "WHERE c.relname = 'primary_key_identity' AND co.contype = 'p';"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EquivalentForeignKeyWithDifferentName_IsRejectedBeforeDdl(
        int legacyConstraintCount
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var legacyName = new string('l', 63);
        var secondLegacyName = new string('s', 63);
        var expectedName = new string('e', 63);
        var secondConstraint = legacyConstraintCount == 2
            ? $", CONSTRAINT \"{secondLegacyName}\" FOREIGN KEY (parent_id) "
                + "REFERENCES identity_parents (id) ON DELETE CASCADE"
            : string.Empty;

        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE identity_parents (id integer NOT NULL PRIMARY KEY);"
            + "CREATE TABLE identity_children ("
            + "id integer NOT NULL PRIMARY KEY, parent_id integer NOT NULL, "
            + $"CONSTRAINT \"{legacyName}\" FOREIGN KEY (parent_id) "
            + $"REFERENCES identity_parents (id) ON DELETE CASCADE{secondConstraint});");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists(
            expectedName,
            "identity_children",
            ["parent_id"],
            "identity_parents",
            ["id"],
            onDelete: ReferentialAction.Cascade);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("foreign-key-identity"));

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
        Assert.Equal("foreign_key_semantic_identity_conflict", assessment.Code);
        Assert.Equal("P1002", exception.SqlState);
        Assert.Equal("doka_sm_unsupported", exception.MessageText);
        Assert.Equal(
            legacyConstraintCount,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_constraint co "
                + "JOIN pg_catalog.pg_class c ON c.oid = co.conrelid "
                + "WHERE c.relname = 'identity_children' AND co.contype = 'f';"));
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
            "CREATE TABLE identity_action_parents (id integer NOT NULL PRIMARY KEY);"
            + "CREATE TABLE identity_action_children ("
            + "id integer NOT NULL PRIMARY KEY, parent_id integer NULL, "
            + "CONSTRAINT fk_identity_action_legacy FOREIGN KEY (parent_id) "
            + "REFERENCES identity_action_parents (id) ON DELETE CASCADE);");

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
                "SELECT COUNT(*) FROM pg_catalog.pg_constraint co "
                + "JOIN pg_catalog.pg_class c ON c.oid = co.conrelid "
                + "WHERE c.relname = 'identity_action_children' AND co.contype = 'f';"));
    }

    [Fact]
    public async Task NonCanonicalConstraintSemantics_AreRejectedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE semantic_primary_key ("
            + "id integer NOT NULL, "
            + "CONSTRAINT pk_semantic_primary_key PRIMARY KEY (id) DEFERRABLE INITIALLY DEFERRED);"
            + "CREATE TABLE semantic_unique ("
            + "code integer NULL, "
            + "CONSTRAINT uq_semantic_unique UNIQUE (code) DEFERRABLE INITIALLY DEFERRED);"
            + "CREATE TABLE semantic_check ("
            + "quantity integer NOT NULL, "
            + "CONSTRAINT ck_semantic_check CHECK (quantity >= 0) NO INHERIT);"
            + "CREATE TABLE semantic_parent ("
            + "id integer NOT NULL, alternate_id integer NOT NULL, PRIMARY KEY (id, alternate_id));"
            + "CREATE TABLE semantic_child ("
            + "parent_id integer NULL, alternate_parent_id integer NULL, "
            + "CONSTRAINT fk_semantic_child FOREIGN KEY (parent_id, alternate_parent_id) "
            + "REFERENCES semantic_parent (id, alternate_id) MATCH FULL DEFERRABLE INITIALLY DEFERRED);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddPrimaryKeyIfNotExists(
            "pk_semantic_primary_key",
            "semantic_primary_key",
            ["id"]);
        builder.AddUniqueConstraintIfNotExists(
            "uq_semantic_unique",
            "semantic_unique",
            ["code"]);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_semantic_check",
                "semantic_check",
                SqlColumnAndInt("quantity", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.AddForeignKeyIfNotExists(
            "fk_semantic_child",
            "semantic_child",
            ["parent_id", "alternate_parent_id"],
            "semantic_parent",
            ["id", "alternate_id"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("constraint-semantics"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(4, report.Assessments.Count);
        Assert.All(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
            });

        foreach (var operation in builder.Operations)
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteOperationsAsync(context, [operation]));

            Assert.Equal("P1001", exception.SqlState);
            Assert.Equal("doka_sm_different", exception.MessageText);
        }
    }

    [Fact]
    public async Task PartitionDerivedConstraints_AreRejectedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE partition_identity_parents ("
            + "tenant_id integer NOT NULL, id integer NOT NULL, PRIMARY KEY (tenant_id, id));"
            + "CREATE TABLE partition_identity_root ("
            + "tenant_id integer NOT NULL, id integer NOT NULL, code integer NULL, parent_id integer NULL, "
            + "CONSTRAINT pk_partition_identity PRIMARY KEY (tenant_id, id), "
            + "CONSTRAINT uq_partition_identity UNIQUE (tenant_id, code), "
            + "CONSTRAINT ck_partition_identity CHECK (id >= 0), "
            + "CONSTRAINT fk_partition_identity FOREIGN KEY (tenant_id, parent_id) "
            + "REFERENCES partition_identity_parents (tenant_id, id)) PARTITION BY RANGE (tenant_id);"
            + "CREATE TABLE partition_identity_leaf PARTITION OF partition_identity_root "
            + "FOR VALUES FROM (0) TO (100);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddPrimaryKeyIfNotExists(
            "pk_partition_identity",
            "partition_identity_leaf",
            ["tenant_id", "id"]);
        builder.AddUniqueConstraintIfNotExists(
            "uq_partition_identity",
            "partition_identity_leaf",
            ["tenant_id", "code"]);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_partition_identity",
                "partition_identity_leaf",
                SqlColumnAndInt("id", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.AddForeignKeyIfNotExists(
            "fk_partition_identity",
            "partition_identity_leaf",
            ["tenant_id", "parent_id"],
            "partition_identity_parents",
            ["tenant_id", "id"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("partition-constraint-identity"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Collection(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
                Assert.Equal("primary_key_identity_conflict", assessment.Code);
            },
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
                Assert.Equal("unique_constraint_semantic_identity_conflict", assessment.Code);
            },
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
            },
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
            });

        for (var index = 0; index < builder.Operations.Count; index++)
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteOperationsAsync(context, [builder.Operations[index]]));

            Assert.Equal(index < 2 ? "P1002" : "P1001", exception.SqlState);
            Assert.Equal(index < 2 ? "doka_sm_unsupported" : "doka_sm_different", exception.MessageText);
        }
    }

    [Fact]
    public async Task PartitionDerivedConstraintDrops_AreRejectedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE partition_drop_parents (tenant_id integer NOT NULL, id integer NOT NULL, "
            + "PRIMARY KEY (tenant_id, id));"
            + "CREATE TABLE partition_drop_root ("
            + "tenant_id integer NOT NULL, id integer NOT NULL, parent_id integer NULL, "
            + "CONSTRAINT ck_partition_drop CHECK (id >= 0), "
            + "CONSTRAINT fk_partition_drop FOREIGN KEY (tenant_id, parent_id) "
            + "REFERENCES partition_drop_parents (tenant_id, id)) PARTITION BY RANGE (tenant_id);"
            + "CREATE TABLE partition_drop_leaf PARTITION OF partition_drop_root "
            + "FOR VALUES FROM (0) TO (100);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropCheckConstraintIfExists("ck_partition_drop", "partition_drop_leaf");
        builder.DropForeignKeyIfExists("fk_partition_drop", "partition_drop_leaf");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("partition-constraint-drop"));

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
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteOperationsAsync(context, [operation]));

            Assert.Equal("P1001", exception.SqlState);
            Assert.Equal("doka_sm_different", exception.MessageText);
        }
    }

    [Fact]
    public async Task UniqueNullsNotDistinct_IsRejectedBeforeDdl()
    {
        if (Fixture.ServerVersion.Major < 15)
        {
            return;
        }

        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE unique_null_semantics ("
            + "code integer NULL, "
            + "CONSTRAINT uq_unique_null_semantics UNIQUE NULLS NOT DISTINCT (code));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddUniqueConstraintIfNotExists(
            "uq_unique_null_semantics",
            "unique_null_semantics",
            ["code"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("unique-null-semantics"));

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal("P1001", exception.SqlState);
        Assert.Equal("doka_sm_different", exception.MessageText);
    }

    [Fact]
    public async Task PartialSetNullForeignKey_IsRejectedBeforeDdl()
    {
        if (Fixture.ServerVersion.Major < 15)
        {
            return;
        }

        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE partial_set_parent ("
            + "tenant_id integer NOT NULL, id integer NOT NULL, PRIMARY KEY (tenant_id, id));"
            + "CREATE TABLE partial_set_child (tenant_id integer NULL, parent_id integer NULL, "
            + "CONSTRAINT fk_partial_set_child FOREIGN KEY (tenant_id, parent_id) "
            + "REFERENCES partial_set_parent (tenant_id, id) ON DELETE SET NULL (parent_id));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists(
            "fk_partial_set_child",
            "partial_set_child",
            ["tenant_id", "parent_id"],
            "partial_set_parent",
            ["tenant_id", "id"],
            onDelete: ReferentialAction.SetNull);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("partial-set-null"));

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal("P1001", exception.SqlState);
        Assert.Equal("doka_sm_different", exception.MessageText);
    }

    [Fact]
    public async Task PostgreSql18ConstraintSemantics_AreRejectedBeforeDdl()
    {
        if (Fixture.ServerVersion.Major < 18)
        {
            return;
        }

        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE EXTENSION btree_gist;"
            + "CREATE TABLE temporal_primary_key ("
            + "id integer NOT NULL, validity daterange NOT NULL, "
            + "CONSTRAINT pk_temporal_primary_key PRIMARY KEY (id, validity WITHOUT OVERLAPS));"
            + "CREATE TABLE non_enforced_check (quantity integer NOT NULL, "
            + "CONSTRAINT ck_non_enforced_check CHECK (quantity >= 0) NOT ENFORCED);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddPrimaryKeyIfNotExists(
            "pk_temporal_primary_key",
            "temporal_primary_key",
            ["id", "validity"]);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_non_enforced_check",
                "non_enforced_check",
                SqlColumnAndInt("quantity", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("postgresql-18-constraints"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(2, report.Assessments.Count);
        Assert.All(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
            });

        foreach (var operation in builder.Operations)
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteOperationsAsync(context, [operation]));

            Assert.Equal("P1001", exception.SqlState);
            Assert.Equal("doka_sm_different", exception.MessageText);
        }
    }

    [Fact]
    public async Task ObservableConstraintFacetDrift_IsRejectedOneFieldAtATime()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE constraint_matrix_parents ("
            + "id integer NOT NULL, alternate_id integer NOT NULL, "
            + "PRIMARY KEY (id, alternate_id)); "
            + "CREATE TABLE constraint_matrix_other_parents ("
            + "id integer NOT NULL, alternate_id integer NOT NULL, "
            + "PRIMARY KEY (id, alternate_id)); "
            + "CREATE TABLE constraint_matrix_children ("
            + "id integer NOT NULL, alternate_id integer NOT NULL, "
            + "code character varying(30) NULL, alternate_code character varying(30) NULL, "
            + "quantity integer NOT NULL, parent_id integer NULL, alternate_parent_id integer NULL);");
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
        ReferentialAction onDelete
    ) => new(
        "constraint_matrix_children",
        [
            new ExpectedColumnDefinition("id", typeof(int), false, "integer"),
            new ExpectedColumnDefinition("alternate_id", typeof(int), false, "integer"),
            new ExpectedColumnDefinition("code", typeof(string), true, "character varying(30)", maxLength: 30),
            new ExpectedColumnDefinition(
                "alternate_code",
                typeof(string),
                true,
                "character varying(30)",
                maxLength: 30),
            new ExpectedColumnDefinition("quantity", typeof(int), false, "integer"),
            new ExpectedColumnDefinition("parent_id", typeof(int), true, "integer"),
            new ExpectedColumnDefinition("alternate_parent_id", typeof(int), true, "integer"),
        ],
        primaryKey:
        new ExpectedPrimaryKeyDefinition(
            "pk_constraint_matrix_children",
            "constraint_matrix_children",
            ["id", "alternate_id"]),
        uniqueConstraints
        :
        [
            new ExpectedUniqueConstraintDefinition(
                "uq_constraint_matrix_code",
                "constraint_matrix_children",
                ["code", "alternate_code"]),
        ],
        checkConstraints:
        [
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_constraint_matrix_quantity",
                "constraint_matrix_children",
                SqlColumnAndInt("quantity", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
        ],
        foreignKeys:
        [
            new ExpectedForeignKeyDefinition(
                "fk_constraint_matrix_parent",
                "constraint_matrix_children",
                ["parent_id", "alternate_parent_id"],
                "constraint_matrix_parents",
                ["id", "alternate_id"],
                onUpdate: ReferentialAction.Cascade,
                onDelete: onDelete),
        ]);
}
