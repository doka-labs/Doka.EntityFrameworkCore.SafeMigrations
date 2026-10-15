namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the ConstraintsProjectedLivePrerequisites PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlConstraintsProjectedLivePrerequisitesIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlConstraintsProjectedLivePrerequisitesIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>A new nullable endpoint references an unchanged live primary key without parent convergence.</summary>
    /// <param name="onDelete">The independently authored deletion action.</param>
    [Theory]
    [InlineData(ReferentialAction.NoAction)]
    [InlineData(ReferentialAction.Cascade)]
    [InlineData(ReferentialAction.SetNull)]
    public async Task ForeignKeyNewNullableColumnUsesUnchangedLivePrincipalWithoutConvergence(
        ReferentialAction onDelete
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE live_parent (id uuid NOT NULL PRIMARY KEY); "
            + "CREATE TABLE live_child (id integer NOT NULL PRIMARY KEY); "
            + "INSERT INTO live_child VALUES (1), (2);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<Guid>(
            "parent_id", "live_child", type: "uuid", nullable: true));
        builder.CreateIndexIfNotExistsFromModel("ix_live_child_parent_id", "live_child", "parent_id");
        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"],
            "live_parent", ["id"], onDelete: onDelete);
        var runner = context.GetService<ISafeMigrationRunner>();

        // WHY: No parent operation seeds projection; its owner, endpoint and key
        // must be independently certified from the requested live catalog only.
        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-live-fk"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-live-fk-final"), CancellationToken.None);

        var replay = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-live-fk-replay"), CancellationToken.None);

        var nullRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM live_child WHERE parent_id IS NULL;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(3, preflight.Assessments.Count);
        Assert.All(preflight.Assessments, static item => Assert.Equal(SafeMigrationAction.Apply, item.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static item => Assert.True(item.PostconditionSatisfied));
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, static item => Assert.Equal(SafeMigrationAction.NoOp, item.Action));
        Assert.Equal(2, nullRows);
    }

    /// <summary>Metadata compatibility cannot invent a missing or unsuitable live candidate key.</summary>
    /// <param name="parentSql">The independently created principal catalog shape.</param>
    [Theory]
    [InlineData("")]
    [InlineData("CREATE TABLE live_parent (id uuid NOT NULL);")]
    [InlineData("CREATE TABLE live_parent (id text NOT NULL PRIMARY KEY);")]
    [InlineData("CREATE VIEW live_parent AS SELECT NULL::uuid AS id;")]
    [InlineData("CREATE TABLE live_parent (id uuid NOT NULL); CREATE UNIQUE INDEX ux_partial "
        + "ON live_parent (id) WHERE id IS NOT NULL;")]
    [InlineData("CREATE TABLE live_parent (id uuid NOT NULL); CREATE UNIQUE INDEX ux_expression "
        + "ON live_parent ((id::text));")]
    [InlineData("CREATE TABLE live_parent (id uuid NOT NULL, CONSTRAINT uq_deferred "
        + "UNIQUE (id) DEFERRABLE INITIALLY DEFERRED);")]
    public async Task ForeignKeyNewNullableColumnDoesNotInventLivePrincipalPrerequisites(
        string parentSql
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, parentSql
            + "CREATE TABLE live_child (id integer NOT NULL PRIMARY KEY); "
            + "INSERT INTO live_child VALUES (1), (2);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<Guid>(
            "parent_id", "live_child", type: "uuid", nullable: true));
        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"],
            "live_parent", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-live-fk-negative"), CancellationToken.None);

        var createdColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_attribute WHERE attrelid = 'live_child'::regclass "
            + "AND attname = 'parent_id' AND NOT attisdropped;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.False(report.Assessments[1].PostconditionSatisfied);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);
        Assert.Equal(0, createdColumns);
    }

    /// <summary>A nullable non-NULL default cannot prove that existing dependent rows bypass FK validation.</summary>
    [Fact]
    public async Task ForeignKeyNewNullableNonNullDefaultDoesNotBorrowNullRowProof()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE live_parent (id uuid NOT NULL PRIMARY KEY); "
            + "CREATE TABLE live_child (id integer NOT NULL PRIMARY KEY); "
            + "INSERT INTO live_child VALUES (1), (2);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<Guid>(
            "parent_id", "live_child", type: "uuid", nullable: true, defaultValue: Guid.Empty));
        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"],
            "live_parent", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-live-fk-nonnull-default"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);
    }

    /// <summary>A mixed index proves untouched live members separately from its accepted new nullable member.</summary>
    /// <param name="unique">Whether the native NULL-distinct index enforces uniqueness.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedIndexNewNullableColumnUsesUnchangedLiveMember(bool unique)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE mixed_columns (id integer NOT NULL PRIMARY KEY); "
            + "INSERT INTO mixed_columns VALUES (1), (2);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
            "added", "mixed_columns", type: "integer", nullable: true));
        builder.CreateIndexIfNotExists("ix_mixed_columns", "mixed_columns", ["id", "added"], unique: unique);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-live-index"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var indexCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_index WHERE indrelid = 'mixed_columns'::regclass "
            + "AND indexrelid = 'ix_mixed_columns'::regclass AND indisvalid;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, static item => Assert.Equal(SafeMigrationAction.Apply, item.Action));
        Assert.Equal(1, indexCount);
    }

    /// <summary>A preflight NULL certificate never authorizes an orphan inserted before native FK creation.</summary>
    [Fact]
    public async Task ForeignKeyNewNullableColumnRechecksRuntimeOrphansAfterPreflight()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE live_parent (id uuid NOT NULL PRIMARY KEY); "
            + "CREATE TABLE live_child (id integer NOT NULL PRIMARY KEY); "
            + "INSERT INTO live_child VALUES (1), (2);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<Guid>(
            "parent_id", "live_child", type: "uuid", nullable: true));
        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"],
            "live_parent", ["id"]);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-live-fk-runtime-write"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, [builder.Operations[0]]);
        await ExecuteSqlAsync(connectionString,
            "UPDATE live_child SET parent_id = '00000000-0000-0000-0000-000000000000' WHERE id = 1;");

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteOperationsAsync(context, [builder.Operations[1]]));

        var foreignKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_constraint WHERE conrelid = 'live_child'::regclass "
            + "AND conname = 'fk_live_child_parent';");

        var retainedRows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM live_child;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal("P1003", exception.SqlState);
        Assert.Equal(0, foreignKeys);
        Assert.Equal(2, retainedRows);
    }

    /// <summary>Verified current-schema aliases share live metadata in both qualifier directions.</summary>
    /// <param name="qualifyChild">Whether the dependent rather than principal endpoint is explicitly qualified.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeyNewNullableColumnResolvesCurrentSchemaAliases(bool qualifyChild)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE live_parent (id uuid NOT NULL PRIMARY KEY); "
            + "CREATE TABLE live_child (id integer NOT NULL PRIMARY KEY); INSERT INTO live_child VALUES (1);");

        await using var context = CreateContext(connectionString);
        var childSchema = qualifyChild ? "public" : null;
        var parentSchema = qualifyChild ? null : "public";
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("live_child", new ExpectedColumnDefinition("parent_id", typeof(Guid),
            isNullable: true, storeType: "uuid"), SafeMigrationPolicy.ThrowIfDifferent, schema: childSchema);

        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"],
            "live_parent", ["id"], schema: childSchema, principalSchema: parentSchema);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-live-fk-schema"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, static item => Assert.Equal(SafeMigrationAction.Apply, item.Action));
    }

    /// <summary>Sparse empty-table constraints combine actual live members with one independently empty ADD.</summary>
    /// <param name="kind">The constraint family sharing the sparse prerequisite bridge.</param>
    [Theory]
    [InlineData("primary")]
    [InlineData("unique")]
    [InlineData("check")]
    public async Task EmptyTableMixedConstraintUsesUnchangedLiveColumn(string kind)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE mixed_columns (id integer NOT NULL);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
            "added", "mixed_columns", type: "integer", nullable: false));

        switch (kind)
        {
            case "primary": builder.AddPrimaryKeyIfNotExists("pk_mixed", "mixed_columns", ["id", "added"]); break;
            case "unique": builder.AddUniqueConstraintIfNotExists("uq_mixed", "mixed_columns", ["id", "added"]); break;
            case "check":
                builder.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression(
                    "ck_mixed", "mixed_columns", SqlBinary(
                        SqlColumnAndInt("id", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0),
                        SafeMigrationSqlBinaryOperator.And,
                        SqlColumnAndInt("added", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0))),
                    SafeMigrationPolicy.ThrowIfDifferent);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-live-empty-key"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var constraints = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_constraint WHERE conrelid = 'mixed_columns'::regclass "
            + "AND conname IN ('pk_mixed', 'uq_mixed', 'ck_mixed');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, static item => Assert.Equal(SafeMigrationAction.Apply, item.Action));
        Assert.Equal(1, constraints);
    }

    /// <summary>NULL-filled rows cannot establish uniqueness under NULLS NOT DISTINCT semantics.</summary>
    [Fact]
    public async Task NewNullableIndexDoesNotApproveNullsNotDistinctDuplicates()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE mixed_columns (id integer NOT NULL PRIMARY KEY); "
            + "INSERT INTO mixed_columns VALUES (1), (2);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<int>(
            "added", "mixed_columns", type: "integer", nullable: true));

        builder.EnsureIndex(new ExpectedIndexDefinition("ux_nulls_not_distinct", "mixed_columns",
            [new ExpectedIndexKeyDefinition(column: "added")], unique: true, nullsDistinct: false),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-live-null-uniqueness"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);
    }

    /// <summary>Physical drops invalidate only that candidate, never another independently surviving key.</summary>
    /// <param name="survivingAlias">Whether an independently observed ordinary unique index survives.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeyNewNullableColumnHonorsExactLiveCandidateLifetimes(bool survivingAlias)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE live_parent (id uuid NOT NULL, CONSTRAINT pk_native PRIMARY KEY (id)); "
            + (survivingAlias ? "CREATE UNIQUE INDEX ux_surviving ON live_parent (id); " : "")
            + "CREATE TABLE live_child (id integer NOT NULL PRIMARY KEY); INSERT INTO live_child VALUES (1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropPrimaryKeyIfExists("pk_native", "live_parent");
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<Guid>(
            "parent_id", "live_child", type: "uuid", nullable: true));
        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"],
            "live_parent", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-live-fk-key-lifetime"), CancellationToken.None);

        if (survivingAlias)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(survivingAlias ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            report.Status);
        Assert.Equal(survivingAlias ? SafeMigrationAction.Apply : SafeMigrationAction.RejectPrerequisiteMissing,
            report.Assessments[2].Action);
    }

    /// <summary>Provider-resolved collation identities are checked independently from existing NULL values.</summary>
    /// <param name="childCollation">The requested native dependent collation.</param>
    /// <param name="compatible">Whether it resolves to the principal's actual effective collation.</param>
    [Theory]
    [InlineData("C", true)]
    [InlineData("POSIX", true)]
    public async Task ForeignKeyNewNullableColumnQualifiesNativeCollation(string childCollation, bool compatible)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE live_parent (id text COLLATE \"C\" NOT NULL PRIMARY KEY); "
            + "CREATE TABLE live_child (id integer NOT NULL PRIMARY KEY); INSERT INTO live_child VALUES (1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<string>("parent_id", "live_child", type: "text", nullable: true,
            collation: new SafeMigrationCollationIdentifier(childCollation, "pg_catalog"));

        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"],
            "live_parent", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-live-fk-collation"), CancellationToken.None);

        if (compatible)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(compatible ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(compatible ? SafeMigrationAction.Apply : SafeMigrationAction.RejectPrerequisiteMissing,
            report.Assessments[1].Action);
    }

    /// <summary>An exact accepted FK drop permits a new endpoint without borrowing another constraint's name.</summary>
    [Fact]
    public async Task ForeignKeyNewNullableColumnReplacesExactlyDroppedOwnedForeignKey()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE live_parent (id uuid NOT NULL PRIMARY KEY); "
            + "CREATE TABLE live_child (id integer NOT NULL PRIMARY KEY, old_parent_id uuid NULL, "
            + "CONSTRAINT fk_live_child_parent FOREIGN KEY (old_parent_id) REFERENCES live_parent (id)); "
            + "INSERT INTO live_child VALUES (1, NULL), (2, NULL);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_live_child_parent", "live_child");
        builder.AddColumnIfNotExistsFromModel(static operation => operation.AddColumn<Guid>(
            "parent_id", "live_child", type: "uuid", nullable: true));
        builder.CreateIndexIfNotExistsFromModel("ix_live_child_parent", "live_child", "parent_id");
        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"],
            "live_parent", ["id"]);

        // Act
        var runner = context.GetService<ISafeMigrationRunner>();
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-live-fk-owned-replacement"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-live-fk-owned-replacement-final"), CancellationToken.None);

        var endpoint = await ScalarStringAsync(connectionString,
            "SELECT a.attname FROM pg_catalog.pg_constraint co JOIN pg_catalog.pg_attribute a "
            + "ON a.attrelid = co.conrelid AND a.attnum = co.conkey[1] "
            + "WHERE co.conrelid = 'live_child'::regclass AND co.conname = 'fk_live_child_parent';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, static item => Assert.Equal(SafeMigrationAction.Apply, item.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal("parent_id", endpoint);
    }

    /// <summary>Different nondeterministic collations cannot be approved by accepted endpoint definitions.</summary>
    [Fact]
    public async Task ForeignKeyNewNullableColumnRejectsDifferentNondeterministicCollations()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE COLLATION nd_parent (provider = icu, locale = 'und-u-ks-level2', deterministic = false); "
            + "CREATE COLLATION nd_child (provider = icu, locale = 'und-u-ks-level2', deterministic = false); "
            + "CREATE TABLE live_parent (id text COLLATE nd_parent NOT NULL PRIMARY KEY); "
            + "CREATE TABLE live_child (id integer NOT NULL PRIMARY KEY); INSERT INTO live_child VALUES (1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("live_parent", new ExpectedColumnDefinition("id", typeof(string),
            isNullable: false, storeType: "text",
            collation: new SafeMigrationCollationIdentifier("nd_parent", "public")),
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.AddPrimaryKeyIfNotExists("live_parent_pkey", "live_parent", ["id"]);
        builder.AddColumnIfNotExists<string>("parent_id", "live_child", type: "text", nullable: true,
            collation: new SafeMigrationCollationIdentifier("nd_child", "public"));

        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"],
            "live_parent", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-live-fk-nondeterministic"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[2].Action);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[3].Action);
    }

    /// <summary>Sparse live facts cannot bypass an existing uncertified safe-rename postcondition boundary.</summary>
    /// <param name="qualifiedChild">Whether dependent operations use the same explicit creation schema.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeyNewNullableColumnRetainsUncertifiedRenameBoundary(bool qualifiedChild)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE live_parent (id uuid NOT NULL PRIMARY KEY); "
            + "CREATE TABLE live_child (id integer NOT NULL PRIMARY KEY); INSERT INTO live_child VALUES (1); "
            + "CREATE TABLE unrelated_owner (old_member integer NULL);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameColumnIfExists("old_member", "unrelated_owner", "new_member", schema: "public");
        builder.AddColumnIfNotExists<Guid>("parent_id", "live_child", type: "uuid", nullable: true,
            schema: qualifiedChild ? "public" : null);

        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"],
            "live_parent", ["id"], schema: qualifiedChild ? "public" : null, principalSchema: "public");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-live-fk-unrelated-rename"), CancellationToken.None);

        // Assert
        // WHY: PostgreSQL has no independent ordered certificate for this
        // provider rename postcondition. Exact live member facts cannot erase
        // that established boundary, even when all owners use one qualifier.
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.All(report.Assessments.Skip(1), static item =>
        {
            Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, item.Action);
            Assert.Equal("projected_structure_state_unknown", item.AnalysisCode);
            Assert.Null(item.DeferredOrigin);
        });
    }

    /// <summary>The native default integer equality family supports sparse FK endpoints across signed widths.</summary>
    [Fact]
    public async Task ForeignKeyNewNullableColumnQualifiesBuiltinIntegerEqualityFamily()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE live_parent (id bigint NOT NULL PRIMARY KEY); "
            + "CREATE TABLE live_child (id integer NOT NULL PRIMARY KEY); INSERT INTO live_child VALUES (1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("parent_id", "live_child", type: "integer", nullable: true);
        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"],
            "live_parent", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-live-fk-integer-family"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, static item => Assert.Equal(SafeMigrationAction.Apply, item.Action));
    }

    /// <summary>A new empty child references an independently live parent without an inline FK.</summary>
    [Fact]
    public async Task ForeignKeyNewTableUsesUnchangedLivePrincipalWithoutInlineConstraint()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE live_parent (id uuid NOT NULL PRIMARY KEY);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("new_child", table => new
        {
            id = table.Column<int>(type: "integer", nullable: false),
            parent_id = table.Column<Guid>(type: "uuid", nullable: true),
        }, constraints: table => table.PrimaryKey("pk_new_child", row => row.id));

        builder.CreateIndexIfNotExistsFromModel("ix_new_child_parent", "new_child", "parent_id");
        builder.AddForeignKeyIfNotExists("fk_new_child_parent", "new_child", ["parent_id"],
            "live_parent", ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-new-child"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-new-child-final"), CancellationToken.None);

        var replay = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-new-child-replay"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments, static item => Assert.Equal(SafeMigrationAction.Apply, item.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, static item => Assert.Equal(SafeMigrationAction.NoOp, item.Action));
    }

    /// <summary>Empty data cannot make an invalid live TEXT-to-integer check operator physically applicable.</summary>
    [Fact]
    public async Task EmptyMixedCheckDoesNotApproveIncompatibleLiveOperandType()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE mixed_columns (id text NOT NULL);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("added", "mixed_columns", type: "integer", nullable: false);
        builder.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression(
            "ck_mixed_invalid", "mixed_columns", SqlBinary(
                SqlColumnAndInt("id", SafeMigrationSqlBinaryOperator.GreaterThan, 0),
                SafeMigrationSqlBinaryOperator.And,
                SqlColumnAndInt("added", SafeMigrationSqlBinaryOperator.GreaterThan, 0))),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-mixed-check-invalid-type"), CancellationToken.None);

        await ExecuteOperationsAsync(context, [builder.Operations[0]]);
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteOperationsAsync(context, [builder.Operations[1]]));

        // Assert
        Assert.Equal("42883", exception.SqlState);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
    }

}
