namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed partial class SqliteSafeMigrationCatalogIntegrationTests
{
    /// <summary>A modular new child can add its nullable FK member before referencing a live parent.</summary>
    [Fact]
    public async Task ProjectedForeignKey_NewChildWithSeparateNullableAddAppliesVerifiesAndReplays()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, CONSTRAINT pk_constraint_parents PRIMARY KEY(Id));");
        await using var context = new SqliteProjectedSeparateAddContext(connection, _constraintShapeServices.Provider);
        var builder = CreateProjectedForeignKeySeparateAddBuilder(context, strict: false);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-new-strict-child-separate-add"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-new-strict-child-separate-add"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-new-strict-child-separate-add"));

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments, item => Assert.Equal(SafeMigrationAction.Apply, item.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, item => Assert.True(item.PostconditionSatisfied));
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, item => Assert.Equal(SafeMigrationAction.NoOp, item.Action));
        Assert.Equal(0, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM constraint_children;"));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_foreign_key_list('constraint_children') "
            + "WHERE \"table\" = 'constraint_parents' AND \"from\" = 'ParentId' AND \"to\" = 'Id';"));
    }

    /// <summary>A separate column does not silently weaken a strict creator's exact original column contract.</summary>
    [Fact]
    public async Task ProjectedForeignKey_NewStrictChildSeparateColumnDoesNotWeakenColumnContract()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, CONSTRAINT pk_constraint_parents PRIMARY KEY(Id));");
        await using var context = new SqliteProjectedSeparateAddContext(connection, _constraintShapeServices.Provider);
        var builder = CreateProjectedForeignKeySeparateAddBuilder(context, strict: true);

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        Assert.Contains("postcondition failed for EnsureTable",
            Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(0, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'constraint_children';"));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'constraint_parents';"));
    }

    /// <summary>A newly accepted empty child can reference an unchanged independently captured native parent.</summary>
    /// <param name="strict">Whether the table creator retains complete strict rather than container semantics.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectedForeignKey_NewEmptyChildUsesLiveParentKey(bool strict)
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, CONSTRAINT pk_constraint_parents PRIMARY KEY(Id));");
        await using var context = CreateContext(connection);
        var builder = CreateProjectedForeignKeyNewOwnerBuilder(context, strict);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-new-child-live-parent"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-new-child-live-parent"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-new-child-live-parent"));

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments, item => Assert.Equal(SafeMigrationAction.Apply, item.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, item => Assert.True(item.PostconditionSatisfied));
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, item => Assert.Equal(SafeMigrationAction.NoOp, item.Action));
        Assert.Equal(0, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM constraint_children;"));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_foreign_key_list('constraint_children') "
            + "WHERE \"table\" = 'constraint_parents';"));
    }

    /// <summary>Only accepted ordered table creation and a real native principal may authorize a future FK.</summary>
    /// <param name="scenario">The missing prerequisite or rejected/future owner condition.</param>
    [Theory]
    [InlineData("future_table")]
    [InlineData("rejected_table")]
    [InlineData("missing_parent")]
    public async Task ProjectedForeignKey_NewOwnerRequiresAcceptedOrderedCreation(string scenario)
    {
        await using var connection = await OpenConnectionAsync();
        if (scenario != "missing_parent")
        {
            await ExecuteSqlAsync(connection,
                "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, "
                + "CONSTRAINT pk_constraint_parents PRIMARY KEY(Id));");
        }

        if (scenario == "rejected_table")
        {
            await ExecuteSqlAsync(connection, "CREATE TABLE constraint_children (Id INTEGER NOT NULL);");
        }

        await using var context = CreateContext(connection);
        var builder = CreateProjectedForeignKeyNewOwnerBuilder(context, strict: scenario == "rejected_table");
        if (scenario == "future_table")
        {
            var foreignKey = builder.Operations[2];
            builder.Operations.RemoveAt(2);
            builder.Operations.Insert(0, foreignKey);
        }

        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-new-owner-prerequisites"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing,
            report.Assessments[scenario == "future_table" ? 0 : 2].Action);
        if (scenario == "rejected_table")
        {
            Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[0].Action);
        }
    }

    /// <summary>Writes between table creation and FK creation invalidate the empty-row certificate.</summary>
    [Fact]
    public async Task ProjectedForeignKey_NewChildWriteDefersAndRuntimeRejectsOrphan()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, CONSTRAINT pk_constraint_parents PRIMARY KEY(Id));");
        await using var context = CreateContext(connection);
        var builder = CreateProjectedForeignKeyNewOwnerBuilder(context);
        builder.Operations.Insert(2, new SqlOperation
        {
            Sql = "INSERT INTO constraint_children(Id, ParentId) VALUES(11, 999);",
        });

        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-new-child-row-mutation"));

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

    /// <summary>Creates the modular table before its standalone index and FK without model/parent seeding.</summary>
    /// <param name="context">The native provider context.</param>
    /// <param name="strict">Whether a rejected existing-table control requires complete exact shape.</param>
    private static MigrationBuilder CreateProjectedForeignKeyNewOwnerBuilder(DbContext context, bool strict = false)
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("constraint_children", table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
                ParentId = table.Column<int>(type: "INTEGER", nullable: true),
            }, constraints: table => table.PrimaryKey("pk_constraint_children", value => value.Id),
            // WHY: The exact same three-operation stream exercises both table
            // modes. Strict creation must preserve the base definition while
            // accepting its authored standalone constraint transition.
            mode: strict ? SafeMigrationTableMode.StrictDefinition : SafeMigrationTableMode.ConvergenceContainer,
            policy: strict ? SafeMigrationPolicy.ThrowIfDifferent : SafeMigrationPolicy.ExistenceOnly);
        builder.CreateIndexIfNotExists(
            "ix_constraint_children_parent_id", "constraint_children", ["ParentId"]);
        builder.AddForeignKeyIfNotExists(
            "fk_constraint_children_parent", "constraint_children", ["ParentId"], "constraint_parents", ["Id"],
            onDelete: ReferentialAction.Cascade);

        return builder;
    }

    /// <summary>Authors a separate ADD to exercise actual FK prerequisite order without model inference.</summary>
    /// <param name="context">The native provider context with its final rebuild target.</param>
    /// <param name="strict">Whether the exact original column set must remain a strict postcondition.</param>
    private static MigrationBuilder CreateProjectedForeignKeySeparateAddBuilder(DbContext context, bool strict)
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("constraint_children", table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
            }, constraints: table => table.PrimaryKey("pk_constraint_children", value => value.Id),
            mode: strict ? SafeMigrationTableMode.StrictDefinition : SafeMigrationTableMode.ConvergenceContainer,
            policy: strict ? SafeMigrationPolicy.ThrowIfDifferent : SafeMigrationPolicy.ExistenceOnly);
        builder.AddColumnIfNotExists<int>("ParentId", "constraint_children", type: "INTEGER", nullable: true);
        builder.CreateIndexIfNotExists("ix_constraint_children_parent_id", "constraint_children", ["ParentId"]);
        builder.AddForeignKeyIfNotExists(
            "fk_constraint_children_parent", "constraint_children", ["ParentId"], "constraint_parents", ["Id"],
            onDelete: ReferentialAction.Cascade);

        return builder;
    }

    /// <summary>Supplies only the final rebuild model; preflight consumes actual operation definitions.</summary>
    private sealed class SqliteProjectedSeparateAddContext(
        DbConnection connection,
        IServiceProvider internalServices) : DbContext
    {
        /// <inheritdoc />
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite(connection);
            optionsBuilder.UseSqliteSafeMigrations();
            optionsBuilder.UseInternalServiceProvider(internalServices);
        }

        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SeparateAddParent>(entity =>
            {
                entity.ToTable("constraint_parents");
                entity.HasKey(value => value.Id).HasName("pk_constraint_parents");
                entity.Property(value => value.Id).ValueGeneratedNever();
            });

            modelBuilder.Entity<SeparateAddChild>(entity =>
            {
                entity.ToTable("constraint_children");
                entity.HasKey(value => value.Id).HasName("pk_constraint_children");
                entity.Property(value => value.Id).ValueGeneratedNever();
                entity.HasIndex(value => value.ParentId).HasDatabaseName("ix_constraint_children_parent_id");
                entity.HasOne<SeparateAddParent>().WithMany().HasForeignKey(value => value.ParentId)
                    .HasConstraintName("fk_constraint_children_parent").OnDelete(DeleteBehavior.Cascade);
            });
        }

        /// <summary>Models the unchanged native principal used by the final rebuild contract.</summary>
        private sealed class SeparateAddParent
        {
            /// <summary>Gets or sets the native principal key.</summary>
            public int Id { get; set; }
        }

        /// <summary>Models the final child after its separately authored column ADD.</summary>
        private sealed class SeparateAddChild
        {
            /// <summary>Gets or sets the native child key.</summary>
            public int Id { get; set; }

            /// <summary>Gets or sets the nullable native parent reference.</summary>
            public int? ParentId { get; set; }
        }
    }
}
