namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed partial class SqliteSafeMigrationCatalogIntegrationTests
{
    /// <summary>Rejects projected FK safety when the actual ADD populates orphan values.</summary>
    [Fact]
    public async Task ProjectedForeignKey_NonNullAddDefaultCannotProveNullRows()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_parents PRIMARY KEY (Id)); "
            + "CREATE TABLE constraint_children (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_children PRIMARY KEY (Id)); "
            + "INSERT INTO constraint_parents (Id) VALUES (1); "
            + "INSERT INTO constraint_children (Id) VALUES (11);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>(
            "ParentId", "constraint_children", type: "INTEGER", nullable: true, defaultValue: 999);
        builder.CreateIndexIfNotExists(
            "ix_constraint_children_parent_id", "constraint_children", ["ParentId"]);
        builder.AddForeignKeyIfNotExists(
            "fk_constraint_children_parent", "constraint_children", ["ParentId"], "constraint_parents", ["Id"],
            onDelete: ReferentialAction.Cascade);

        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-projected-fk-orphan-default"));

        Assert.NotEqual(SafeMigrationReportStatus.Ready, report.Status);
        Assert.NotEqual(SafeMigrationAction.Apply, report.Assessments[2].Action);
        Assert.Equal(
            0,
            await ScalarIntAsync(connection,
                "SELECT COUNT(*) FROM pragma_table_xinfo('constraint_children') WHERE name = 'ParentId';"));
    }

    /// <summary>Preserves live parent proof for an actual ordered NULL-populating column ADD.</summary>
    /// <param name="explicitNull">Whether the authored ADD uses an explicit literal NULL default.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectedForeignKey_NullPreservingAddAppliesVerifiesAndReplays(bool explicitNull)
    {
        await using var connection = await OpenConnectionAsync();
        await CreateProjectedForeignKeyTablesAsync(connection);
        await using DbContext context = explicitNull
            ? new SqliteProjectedForeignKeyNullDefaultContext(connection, _constraintShapeServices.Provider)
            : CreateContext(connection);

        var column = new ExpectedColumnDefinition(
            "ParentId", typeof(int), isNullable: true, storeType: "INTEGER",
            defaultValue: explicitNull ? SafeMigrationDefaultValue.Literal(null) : SafeMigrationDefaultValue.None);

        var builder = CreateProjectedForeignKeyBuilder(context, column);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-projected-fk-null"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-projected-fk-null"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-projected-fk-null"));

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments, item => Assert.Equal(SafeMigrationAction.Apply, item.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, item => Assert.True(item.PostconditionSatisfied));
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, item => Assert.Equal(SafeMigrationAction.NoOp, item.Action));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM constraint_children WHERE Id = 11 AND ParentId IS NULL;"));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_foreign_key_list('constraint_children') "
            + "WHERE \"table\" = 'constraint_parents' AND \"from\" = 'ParentId' AND \"to\" = 'Id';"));
    }

    /// <summary>Never accepts a future ADD as a prerequisite for an earlier FK.</summary>
    [Fact]
    public async Task ProjectedForeignKey_FutureColumnAddDoesNotAuthorizeEarlierForeignKey()
    {
        await using var connection = await OpenConnectionAsync();
        await CreateProjectedForeignKeyTablesAsync(connection);
        await using var context = CreateContext(connection);
        var builder = CreateProjectedForeignKeyBuilder(
            context, new ExpectedColumnDefinition("ParentId", typeof(int), true, storeType: "INTEGER"));

        var foreignKey = builder.Operations[2];
        builder.Operations.RemoveAt(2);
        builder.Operations.Insert(0, foreignKey);

        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-projected-fk-future-add"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[0].Action);
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('constraint_children') WHERE name = 'ParentId';"));
    }

    /// <summary>Actual generated or required column definitions cannot inherit the model's nullable wishes.</summary>
    /// <param name="generated">Whether the authored ADD computes an orphan rather than requiring a value.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectedForeignKey_ModelNullableDoesNotProveDifferentAuthoredColumn(bool generated)
    {
        await using var connection = await OpenConnectionAsync();
        await CreateProjectedForeignKeyTablesAsync(connection);
        await using var context = CreateContext(connection);
        var column = new ExpectedColumnDefinition(
            "ParentId", typeof(int), isNullable: generated, storeType: "INTEGER",
            computedColumnSql: generated ? "999" : null);

        var builder = CreateProjectedForeignKeyBuilder(context, column);

        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-projected-fk-authored-definition"));

        Assert.NotEqual(SafeMigrationReportStatus.Ready, report.Status);
        Assert.NotEqual(SafeMigrationAction.Apply, report.Assessments[2].Action);
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('constraint_children') WHERE name = 'ParentId';"));
    }

    /// <summary>NULL row proof cannot manufacture missing or unusable principal metadata.</summary>
    /// <param name="principalSql">The complete native principal setup, or empty for a missing principal.</param>
    [Theory]
    [InlineData("")]
    [InlineData("CREATE TABLE constraint_parents (OtherId INTEGER PRIMARY KEY);")]
    [InlineData("CREATE TABLE constraint_parents (Id INTEGER NOT NULL);")]
    [InlineData("CREATE TABLE constraint_parents (Id INTEGER NOT NULL); "
        + "CREATE INDEX ix_parent ON constraint_parents(Id);")]
    [InlineData("CREATE TABLE constraint_parents (Id INTEGER NOT NULL); "
        + "CREATE UNIQUE INDEX ix_parent ON constraint_parents(Id COLLATE NOCASE);")]
    [InlineData("CREATE TABLE constraint_parents (Id INTEGER NOT NULL); "
        + "CREATE UNIQUE INDEX ix_parent ON constraint_parents(Id) WHERE Id > 0;")]
    [InlineData("CREATE TABLE constraint_parents (Id INTEGER NOT NULL); "
        + "CREATE UNIQUE INDEX ix_parent ON constraint_parents(Id + 0);")]
    public async Task ProjectedForeignKey_UnusableLivePrincipalRemainsBlocked(string principalSql)
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, principalSql
            + "CREATE TABLE constraint_children (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_children PRIMARY KEY (Id)); INSERT INTO constraint_children VALUES(11);");
        await using var context = CreateContext(connection);
        var builder = CreateProjectedForeignKeyBuilder(
            context, new ExpectedColumnDefinition("ParentId", typeof(int), true, storeType: "INTEGER"));

        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-projected-fk-parent-metadata"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[2].Action);
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('constraint_children') WHERE name = 'ParentId';"));
        Assert.Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM constraint_children WHERE Id = 11;"));
    }

    /// <summary>Recognizes independent unique constraints and indexes using their actual native identities.</summary>
    /// <param name="independentIndex">Whether the native candidate is an index rather than a constraint.</param>
    /// <param name="descending">Whether the valid FK parent uses descending rather than ascending key order.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ProjectedForeignKey_LiveUniqueCandidateAllowsSparseStream(bool independentIndex, bool descending)
    {
        await using var connection = await OpenConnectionAsync();
        var keySql = descending ? "Id DESC" : "Id";
        await ExecuteSqlAsync(connection,
            independentIndex
                ? "CREATE TABLE constraint_parents (Id INTEGER NOT NULL); "
                    + $"CREATE UNIQUE INDEX ux_parent_id ON constraint_parents({keySql});"
                : $"CREATE TABLE constraint_parents (Id INTEGER NOT NULL, CONSTRAINT uq_parent_id UNIQUE({keySql}));");
        await ExecuteSqlAsync(connection,
            "CREATE TABLE constraint_children (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_children PRIMARY KEY (Id)); INSERT INTO constraint_children VALUES(11);");
        await using var context = CreateContext(connection);
        var builder = CreateProjectedForeignKeyBuilder(
            context, new ExpectedColumnDefinition("ParentId", typeof(int), true, storeType: "INTEGER"));
        var runner = context.GetService<ISafeMigrationRunner>();

        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-projected-fk-unique-parent"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-projected-fk-unique-parent"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, item => Assert.Equal(SafeMigrationAction.Apply, item.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM constraint_children WHERE Id = 11 AND ParentId IS NULL;"));
    }

    /// <summary>Rechecks rows atomically after a write invalidates read-only preflight proof.</summary>
    [Fact]
    public async Task ProjectedForeignKey_InterveningOrphanWriteIsRejectedBeforeBatchMutation()
    {
        await using var connection = await OpenConnectionAsync();
        await CreateProjectedForeignKeyTablesAsync(connection);
        await using var context = CreateContext(connection);
        var builder = CreateProjectedForeignKeyBuilder(
            context, new ExpectedColumnDefinition("ParentId", typeof(int), true, storeType: "INTEGER"));

        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-projected-fk-runtime-orphan"));

        await ExecuteSqlAsync(connection,
            "ALTER TABLE constraint_children ADD COLUMN ParentId INTEGER NULL; "
            + "UPDATE constraint_children SET ParentId = 999;");
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Contains("data_blocked", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_foreign_key_list('constraint_children');"));
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_index_list('constraint_children') "
            + "WHERE name = 'ix_constraint_children_parent_id';"));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM constraint_children WHERE Id = 11 AND ParentId = 999;"));
    }

    /// <summary>Opaque preparation between ADD and FK invalidates earlier NULL row evidence.</summary>
    [Fact]
    public async Task ProjectedForeignKey_InterveningSqlDefersAndRuntimeRejectsOrphans()
    {
        await using var connection = await OpenConnectionAsync();
        await CreateProjectedForeignKeyTablesAsync(connection);
        await using var context = CreateContext(connection);
        var builder = CreateProjectedForeignKeyBuilder(
            context, new ExpectedColumnDefinition("ParentId", typeof(int), true, storeType: "INTEGER"));

        builder.Operations.Insert(2, new SqlOperation
        {
            Sql = "UPDATE constraint_children SET ParentId = 999;",
        });

        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-projected-fk-sql-orphan"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, preflight.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[3].Action);
        Assert.Contains("data_blocked", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_foreign_key_list('constraint_children');"));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM constraint_children WHERE Id = 11 AND ParentId = 999;"));
    }

    /// <summary>Native candidate metadata cannot resurrect an accepted removed key or principal owner.</summary>
    /// <param name="removeTable">Whether the removed prerequisite is the table rather than its unique index.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectedForeignKey_RemovedLivePrerequisiteCannotBeReused(bool removeTable)
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL); "
            + "CREATE UNIQUE INDEX ux_parent_id ON constraint_parents(Id); "
            + "CREATE TABLE constraint_children (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_children PRIMARY KEY (Id)); INSERT INTO constraint_children VALUES(11);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (removeTable)
        {
            builder.DropTableIfExists("constraint_parents");
        }
        else
        {
            builder.DropIndexIfExists("ux_parent_id", "constraint_parents");
        }

        builder.Operations.AddRange(CreateProjectedForeignKeyBuilder(
            context, new ExpectedColumnDefinition("ParentId", typeof(int), true, storeType: "INTEGER")).Operations);

        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-projected-fk-removed-prerequisite"));

        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[3].Action);
    }

    /// <summary>Retains an independent surviving native key after another candidate is dropped.</summary>
    [Fact]
    public async Task ProjectedForeignKey_SurvivingLiveCandidateRemainsUsable()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL); "
            + "CREATE UNIQUE INDEX ux_parent_id ON constraint_parents(Id); "
            + "CREATE UNIQUE INDEX ux_parent_id_survivor ON constraint_parents(Id); "
            + "CREATE TABLE constraint_children (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_children PRIMARY KEY (Id)); INSERT INTO constraint_children VALUES(11);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("ux_parent_id", "constraint_parents");
        builder.Operations.AddRange(CreateProjectedForeignKeyBuilder(
            context, new ExpectedColumnDefinition("ParentId", typeof(int), true, storeType: "INTEGER")).Operations);

        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-projected-fk-surviving-candidate"));

        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, item => Assert.Equal(SafeMigrationAction.Apply, item.Action));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_index_list('constraint_parents') WHERE name = 'ux_parent_id_survivor';"));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM constraint_children WHERE Id = 11 AND ParentId IS NULL;"));
    }

    /// <summary>A live child component combines with a new NULL component without inventing its parent value.</summary>
    [Fact]
    public async Task ProjectedForeignKey_CompositeLiveAndNullColumnsApplyWithoutParentRow()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE composite_parents (TenantId INTEGER NOT NULL, Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_composite_parents PRIMARY KEY(TenantId, Id)); "
            + "CREATE TABLE composite_children (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_composite_children PRIMARY KEY(Id)); INSERT INTO composite_children VALUES(11);");
        await using var context = new SqliteProjectedCompositeForeignKeyContext(
            connection, _constraintShapeServices.Provider);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("ParentId", "composite_children", type: "INTEGER", nullable: true);
        builder.CreateIndexIfNotExists("ix_composite_children_parent", "composite_children", ["Id", "ParentId"]);
        builder.AddForeignKeyIfNotExists(
            "fk_composite_children_parent", "composite_children", ["Id", "ParentId"],
            "composite_parents", ["TenantId", "Id"]);

        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-projected-composite-fk"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-projected-composite-fk"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, item => Assert.Equal(SafeMigrationAction.Apply, item.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM composite_children WHERE Id = 11 AND ParentId IS NULL;"));
        Assert.Equal(0, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM composite_parents;"));
    }

    /// <summary>A preceding ordinary unique index can establish the live principal's missing candidate key.</summary>
    [Fact]
    public async Task ProjectedForeignKey_AcceptedOrdinaryUniqueIndexProvidesCandidateKey()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL); "
            + "CREATE TABLE constraint_children (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_children PRIMARY KEY (Id)); INSERT INTO constraint_children VALUES(11);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("ux_parent_id", "constraint_parents", ["Id"], unique: true);
        builder.Operations.AddRange(CreateProjectedForeignKeyBuilder(
            context, new ExpectedColumnDefinition("ParentId", typeof(int), true, storeType: "INTEGER")).Operations);

        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-projected-fk-accepted-unique"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-projected-fk-accepted-unique"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, item => Assert.Equal(SafeMigrationAction.Apply, item.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM constraint_children WHERE Id = 11 AND ParentId IS NULL;"));
    }

    /// <summary>Accepted collated unique indexes cannot bypass native parent-key collation requirements.</summary>
    [Fact]
    public async Task ProjectedForeignKey_AcceptedWrongCollationUniqueIndexDoesNotProvideCandidateKey()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL); "
            + "CREATE TABLE constraint_children (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_children PRIMARY KEY (Id)); INSERT INTO constraint_children VALUES(11);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(new ExpectedIndexDefinition(
            "ux_parent_id", "constraint_parents",
            [new ExpectedIndexKeyDefinition("Id", collation: new SafeMigrationCollationIdentifier("NOCASE"))],
            unique: true), SafeMigrationPolicy.ThrowIfDifferent);
        builder.Operations.AddRange(CreateProjectedForeignKeyBuilder(
            context, new ExpectedColumnDefinition("ParentId", typeof(int), true, storeType: "INTEGER")).Operations);

        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-projected-fk-accepted-collated-unique"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[3].Action);
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_foreign_key_list('constraint_children');"));
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('constraint_children') WHERE name = 'ParentId';"));
    }

    /// <summary>Converging every raw definition cannot hide an existing key's incompatible collation.</summary>
    /// <param name="keySql">The actual native unique-key definition.</param>
    /// <param name="matches">Whether the native key uses the parent's declared collation.</param>
    [Theory]
    [InlineData("Id", true)]
    [InlineData("Id COLLATE NOCASE", false)]
    public async Task ProjectedForeignKey_AllProjectedDefinitionsDoNotHideNativeCandidateCollation(
        string keySql,
        bool matches)
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, "
            + $"CONSTRAINT uq_parent_id UNIQUE({keySql})); "
            + "CREATE TABLE constraint_children (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_children PRIMARY KEY (Id)); INSERT INTO constraint_children VALUES(11);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("Id", "constraint_parents", type: "INTEGER", nullable: false);
        builder.AddUniqueConstraintIfNotExists("uq_parent_id", "constraint_parents", ["Id"]);
        builder.Operations.AddRange(CreateProjectedForeignKeyBuilder(
            context, new ExpectedColumnDefinition("ParentId", typeof(int), true, storeType: "INTEGER")).Operations);

        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-projected-fk-all-definitions"));

        Assert.Equal(matches ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(matches ? SafeMigrationAction.Apply : SafeMigrationAction.RejectPrerequisiteMissing,
            report.Assessments[4].Action);
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('constraint_children') WHERE name = 'ParentId';"));
    }

    /// <summary>Creates an isolated populated live child without the future FK column.</summary>
    private static Task CreateProjectedForeignKeyTablesAsync(DbConnection connection) => ExecuteSqlAsync(
        connection,
        "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, "
        + "CONSTRAINT pk_constraint_parents PRIMARY KEY (Id)); "
        + "CREATE TABLE constraint_children (Id INTEGER NOT NULL, "
        + "CONSTRAINT pk_constraint_children PRIMARY KEY (Id)); "
        + "INSERT INTO constraint_parents (Id) VALUES (1); "
        + "INSERT INTO constraint_children (Id) VALUES (11);");

    /// <summary>Builds the sparse ordered ADD, supporting index and FK stream without parent convergence.</summary>
    private static MigrationBuilder CreateProjectedForeignKeyBuilder(
        DbContext context,
        ExpectedColumnDefinition column
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("constraint_children", column, SafeMigrationPolicy.ThrowIfDifferent);
        builder.CreateIndexIfNotExists(
            "ix_constraint_children_parent_id", "constraint_children", ["ParentId"]);
        builder.AddForeignKeyIfNotExists(
            "fk_constraint_children_parent", "constraint_children", ["ParentId"], "constraint_parents", ["Id"],
            onDelete: ReferentialAction.Cascade);

        return builder;
    }

    /// <summary>Keeps the target rebuild model aligned with an explicit NULL-default ADD.</summary>
    private sealed class SqliteProjectedForeignKeyNullDefaultContext(
        DbConnection connection,
        IServiceProvider internalServices) : DbContext
    {
        /// <inheritdoc />
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite(connection);
            optionsBuilder.UseSqliteSafeMigrations();

            // WHY: Distinct target models need model-cache entries, not extra
            // singleton provider caches. The existing class fixture owns both.
            optionsBuilder.UseInternalServiceProvider(internalServices);
        }

        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<NullDefaultParent>(entity =>
            {
                entity.ToTable("constraint_parents");
                entity.HasKey(value => value.Id).HasName("pk_constraint_parents");
                entity.Property(value => value.Id).ValueGeneratedNever();
            });

            modelBuilder.Entity<NullDefaultChild>(entity =>
            {
                entity.ToTable("constraint_children");
                entity.HasKey(value => value.Id).HasName("pk_constraint_children");
                entity.Property(value => value.Id).ValueGeneratedNever();
                entity.Property(value => value.ParentId).HasDefaultValueSql("NULL");
                entity.HasIndex(value => value.ParentId).HasDatabaseName("ix_constraint_children_parent_id");
                entity.HasOne<NullDefaultParent>().WithMany().HasForeignKey(value => value.ParentId)
                    .HasConstraintName("fk_constraint_children_parent").OnDelete(DeleteBehavior.Cascade);
            });
        }

        /// <summary>Models the untouched native FK principal.</summary>
        private sealed class NullDefaultParent
        {
            /// <summary>Gets or sets the principal key.</summary>
            public int Id { get; set; }
        }

        /// <summary>Models the FK dependent after adding the NULL-preserving column.</summary>
        private sealed class NullDefaultChild
        {
            /// <summary>Gets or sets the dependent key.</summary>
            public int Id { get; set; }

            /// <summary>Gets or sets the nullable FK reference.</summary>
            public int? ParentId { get; set; }
        }
    }

    /// <summary>Models one FK containing both an unchanged live column and a newly added nullable column.</summary>
    private sealed class SqliteProjectedCompositeForeignKeyContext(
        DbConnection connection,
        IServiceProvider internalServices) : DbContext
    {
        /// <inheritdoc />
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite(connection);
            optionsBuilder.UseSqliteSafeMigrations();

            // WHY: Keep independent CLR model identities in the fixture-owned
            // shared provider instead of growing EF's process-wide cache.
            optionsBuilder.UseInternalServiceProvider(internalServices);
        }

        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CompositeParent>(entity =>
            {
                entity.ToTable("composite_parents");
                entity.HasKey(value => new { value.TenantId, value.Id }).HasName("pk_composite_parents");
                entity.Property(value => value.TenantId).ValueGeneratedNever();
                entity.Property(value => value.Id).ValueGeneratedNever();
            });

            modelBuilder.Entity<CompositeChild>(entity =>
            {
                entity.ToTable("composite_children");
                entity.HasKey(value => value.Id).HasName("pk_composite_children");
                entity.Property(value => value.Id).ValueGeneratedNever();
                entity.HasIndex(value => new { value.Id, value.ParentId })
                    .HasDatabaseName("ix_composite_children_parent");
                entity.HasOne<CompositeParent>().WithMany().HasForeignKey(value => new { value.Id, value.ParentId })
                    .HasConstraintName("fk_composite_children_parent").OnDelete(DeleteBehavior.NoAction);
            });
        }

        /// <summary>Models the native composite principal key.</summary>
        private sealed class CompositeParent
        {
            /// <summary>Gets or sets the first principal key component.</summary>
            public int TenantId { get; set; }

            /// <summary>Gets or sets the second principal key component.</summary>
            public int Id { get; set; }
        }

        /// <summary>Models the accepted composite FK endpoints.</summary>
        private sealed class CompositeChild
        {
            /// <summary>Gets or sets the unchanged live dependent component.</summary>
            public int Id { get; set; }

            /// <summary>Gets or sets the newly materialized NULL dependent component.</summary>
            public int? ParentId { get; set; }
        }
    }
}
