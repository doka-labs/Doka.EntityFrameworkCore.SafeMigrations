namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Verifies isolated index removal without losing surviving-column transition evidence.</summary>
public sealed class SqliteIndexDropColumnTransitionIntegrationTests : SqliteIntegrationTestBase,
    IClassFixture<SqliteLosslessAlterServiceFixture>
{
    private readonly IServiceProvider _internalServices;

    /// <summary>Shares only provider caches while each case owns its live SQLite database.</summary>
    /// <param name="services">The class-owned provider services.</param>
    public SqliteIndexDropColumnTransitionIntegrationTests(SqliteLosslessAlterServiceFixture services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _internalServices = services.Provider;
    }

    /// <summary>Repairs same-shape facets or confirms INTEGER affinity after present and interrupted drops.</summary>
    /// <param name="transition">The independently supported target facet.</param>
    /// <param name="indexExists">Whether the index still exists before the upgrade resumes.</param>
    [Theory]
    [InlineData("affinity", true)]
    [InlineData("affinity", false)]
    [InlineData("nullability", true)]
    [InlineData("nullability", false)]
    [InlineData("default", true)]
    [InlineData("default", false)]
    public async Task AcceptedIndexDropPreservesSupportedTransitionAndReplay(string transition, bool indexExists)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        var sourceNullable = transition == "nullability";
        await ExecuteSqlAsync(connection,
            "CREATE TABLE lossless_alter_records (Id INTEGER NOT NULL, Value INTEGER "
            + (sourceNullable ? "NULL" : "NOT NULL")
            + ", CONSTRAINT pk_lossless_alter_records PRIMARY KEY (Id)); "
            + "INSERT INTO lossless_alter_records VALUES (1, -2147483648), (2, 2147483647);"
            + (indexExists ? " CREATE INDEX ix_upgrade_value ON lossless_alter_records (Value);" : string.Empty));

        await using DbContext context = transition == "default"
            ? new SqliteDefaultAlterTestContext(connection, _internalServices)
            : new SqliteLosslessAlterTestContext<long>(connection, _internalServices);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("ix_upgrade_value", "lossless_alter_records");
        builder.AlterColumnIfDifferent("lossless_alter_records",
            new ExpectedColumnDefinition("Value", typeof(long), false, "INTEGER",
                defaultValue: transition == "default" ? SafeMigrationDefaultValue.Literal(7L) : null),
            new ExpectedColumnDefinition("Value", transition == "affinity" ? typeof(int) : typeof(long),
                sourceNullable, "INTEGER"),
            SafeMigrationPolicy.RepairIfSafe);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlite-index-drop-column-upgrade"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlite-index-drop-column-replay"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var verified = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlite-index-drop-column-postflight"));

        var retained = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM lossless_alter_records WHERE (Id=1 AND Value=-2147483648) "
            + "OR (Id=2 AND Value=2147483647);");

        var indexes = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type='index' AND name='ix_upgrade_value';");

        if (transition == "default")
        {
            await ExecuteSqlAsync(connection, "INSERT INTO lossless_alter_records (Id) VALUES (3);");
        }

        var futureDefault = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM lossless_alter_records WHERE Id=3 AND Value=7;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(indexExists ? SafeMigrationAction.Apply : SafeMigrationAction.NoOp,
            preflight.Assessments[0].Action);
        Assert.Equal(transition == "affinity" ? SafeMigrationAction.NoOp : SafeMigrationAction.Repair,
            preflight.Assessments[1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, verified.Status);
        Assert.Equal(2, retained);
        Assert.Equal(0, indexes);
        Assert.Equal(transition == "default" ? 1 : 0, futureDefault);
    }

    /// <summary>Removing an index cannot authorize stale sources, NULL loss or unmodeled rebuild artifacts.</summary>
    /// <param name="control">The source, data or physical rebuild condition that must remain guarded.</param>
    [Theory]
    [InlineData("stale-source")]
    [InlineData("null-row")]
    [InlineData("missing-old")]
    [InlineData("unmanaged-trigger")]
    [InlineData("store-change")]
    public async Task IndexDropDoesNotEraseInvalidSourceOrRebuildBoundaries(string control)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE lossless_alter_records (Id INTEGER NOT NULL, Value INTEGER NULL, "
            + "CONSTRAINT pk_lossless_alter_records PRIMARY KEY (Id)); "
            + "INSERT INTO lossless_alter_records VALUES (1, "
            + (control == "null-row" ? "NULL" : "9007199254740993") + "); "
            + "CREATE INDEX ix_upgrade_value ON lossless_alter_records (Value);"
            + (control == "unmanaged-trigger"
                ? " CREATE TRIGGER caller_trigger AFTER UPDATE ON lossless_alter_records BEGIN SELECT 1; END;"
                : string.Empty));

        await using var context = new SqliteLosslessAlterTestContext<long>(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("ix_upgrade_value", "lossless_alter_records");
        builder.AlterColumnIfDifferent("lossless_alter_records",
            new ExpectedColumnDefinition("Value", typeof(long), false,
                control == "store-change" ? "BIGINT" : "INTEGER"),
            control == "missing-old" ? null : new ExpectedColumnDefinition("Value", typeof(long), true,
                control == "stale-source" ? "TEXT" : "INTEGER"),
            SafeMigrationPolicy.RepairIfSafe);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlite-index-drop-invalid-upgrade"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var retained = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM lossless_alter_records WHERE Id=1 AND "
            + (control == "null-row" ? "Value IS NULL;" : "Value=9007199254740993;"));

        var source = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('lossless_alter_records') "
            + "WHERE name='Value' AND type='INTEGER' AND \"notnull\"=0;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.NotEqual(SafeMigrationAction.Repair, report.Assessments[1].Action);
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(1, retained);
        Assert.Equal(1, source);
    }

    /// <summary>An automatic constraint index cannot certify a supposedly harmless standalone removal.</summary>
    [Fact]
    public async Task ConstraintBackedIndexDropCannotAuthorizeColumnProofReuse()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE lossless_alter_records (Id INTEGER NOT NULL, Value INTEGER NOT NULL UNIQUE, "
            + "CONSTRAINT pk_lossless_alter_records PRIMARY KEY (Id)); "
            + "INSERT INTO lossless_alter_records VALUES (1, 2147483647);");
        await using var context = new SqliteLosslessAlterTestContext<long>(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("sqlite_autoindex_lossless_alter_records_1", "lossless_alter_records");
        builder.AlterColumnIfDifferent("lossless_alter_records",
            new ExpectedColumnDefinition("Value", typeof(long), false, "INTEGER"),
            new ExpectedColumnDefinition("Value", typeof(int), false, "INTEGER"), SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlite-index-drop-constraint-boundary"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var retained = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM lossless_alter_records WHERE Id=1 AND Value=2147483647;");

        var constraint = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_index_list('lossless_alter_records') WHERE origin='u';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[0].Action);
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(1, retained);
        Assert.Equal(1, constraint);
    }

    /// <summary>SQL between removal and repair expires row evidence; runtime guards reject NULL loss.</summary>
    [Fact]
    public async Task OpaqueSqlAfterIndexDropCannotReviveStaleNullabilityProof()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE lossless_alter_records (Id INTEGER NOT NULL, Value INTEGER NULL, "
            + "CONSTRAINT pk_lossless_alter_records PRIMARY KEY (Id)); "
            + "INSERT INTO lossless_alter_records VALUES (1, 2147483647); "
            + "CREATE INDEX ix_upgrade_value ON lossless_alter_records (Value);");
        await using var context = new SqliteLosslessAlterTestContext<long>(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("ix_upgrade_value", "lossless_alter_records");
        builder.Sql("UPDATE lossless_alter_records SET Value=NULL WHERE Id=1;");
        builder.AlterColumnIfDifferent("lossless_alter_records",
            new ExpectedColumnDefinition("Value", typeof(long), false, "INTEGER"),
            new ExpectedColumnDefinition("Value", typeof(long), true, "INTEGER"), SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlite-index-drop-opaque-boundary"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var retained = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM lossless_alter_records WHERE Id=1 AND Value IS NULL;");

        var nullable = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('lossless_alter_records') "
            + "WHERE name='Value' AND \"notnull\"=0;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(1, retained);
        Assert.Equal(1, nullable);
    }

    /// <summary>A preservation certificate requires the captured native standalone index and accepted role.</summary>
    /// <param name="control">The independently varied capture or physical identity condition.</param>
    /// <param name="expected">Whether this exact input may retain intrinsic source evidence.</param>
    [Theory]
    [InlineData("exact", true)]
    [InlineData("case-alias", true)]
    [InlineData("uncaptured", false)]
    [InlineData("other-owner", false)]
    [InlineData("other-name", false)]
    [InlineData("constraint-index", false)]
    [InlineData("schema-qualified", false)]
    [InlineData("rejected-analysis", false)]
    public async Task IndexDropCertificateRequiresCapturedStandaloneIdentity(string control, bool expected)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE lossless_alter_records (Id INTEGER NOT NULL, Value INTEGER NOT NULL UNIQUE); "
            + "CREATE INDEX ix_upgrade_value ON lossless_alter_records (Value);");
        await using var context = new SqliteLosslessAlterTestContext<long>(connection, _internalServices);
        var analyzer = (SqliteSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var initial = new SafeMigrationOperation(new DropIndexIntent("ix_upgrade_value", "lossless_alter_records"),
            SafeMigrationPolicy.ThrowIfDifferent);

        if (control != "uncaptured")
        {
            await analyzer.AnalyzeAsync(context, [initial]);
        }

        var intent = new DropIndexIntent(control switch
        {
            "other-name" => "ix_missing",
            "constraint-index" => "sqlite_autoindex_lossless_alter_records_1",
            "case-alias" => "IX_UPGRADE_VALUE",
            _ => "ix_upgrade_value",
        }, control == "other-owner" ? "other_records" : "lossless_alter_records",
            control == "schema-qualified" ? "main" : null);

        var state = control == "rejected-analysis"
            ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching;

        var analysis = new SafeMigrationProviderAnalysis(state, SafeMigrationRepairCapability.None, false, "probe");

        // Act
        var preserved = analyzer.PreservesColumnSourcesAfterIndexDrop(intent, analysis);

        // Assert
        Assert.Equal(expected, preserved);
    }
}
