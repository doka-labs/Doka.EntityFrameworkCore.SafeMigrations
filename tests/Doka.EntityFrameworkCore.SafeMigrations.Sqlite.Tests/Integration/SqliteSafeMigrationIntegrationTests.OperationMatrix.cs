namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed partial class SqliteSafeMigrationCatalogIntegrationTests
{
    [Fact]
    public async Task TableLifecycle_CreateRenameDropAndReplayRemainIdempotent()
    {
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var runner = context.GetService<ISafeMigrationRunner>();
        var create = new MigrationBuilder(context.Database.ProviderName!);
        create.CreateTableIfNotExists(
            "lifecycle_source",
            table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_lifecycle_source", value => value.Id));
        var rename = new MigrationBuilder(context.Database.ProviderName!);
        rename.RenameTableIfExists("lifecycle_source", "lifecycle_target");
        var drop = new MigrationBuilder(context.Database.ProviderName!);
        drop.DropTableIfExists("lifecycle_target");

        await ExecuteOperationsAsync(context, create.Operations);
        await ExecuteOperationsAsync(context, create.Operations);
        await ExecuteOperationsAsync(context, rename.Operations);
        await ExecuteOperationsAsync(context, rename.Operations);
        await ExecuteOperationsAsync(context, drop.Operations);
        await ExecuteOperationsAsync(context, drop.Operations);

        var report = await runner.AnalyzeAsync(
            context,
            drop.Operations,
            new SafeMigrationRunOptions("sqlite-table-lifecycle-replay"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
        Assert.True(assessment.PostconditionSatisfied);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema "
                + "WHERE type = 'table' AND name IN ('lifecycle_source', 'lifecycle_target');"));
    }

    [Fact]
    public async Task ColumnLifecycle_DefaultRenameAlterAndDropPreserveExistingRows()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE rebuild_entities (Id INTEGER NOT NULL, Code TEXT NULL, Legacy TEXT NULL, "
            + "CONSTRAINT pk_rebuild_entities PRIMARY KEY (Id), "
            + "CONSTRAINT uq_rebuild_entities_code UNIQUE (Code), "
            + "CONSTRAINT ck_rebuild_entities_code CHECK (length(\"Code\") > 0)); "
            + "INSERT INTO rebuild_entities (Id, Code, Legacy) VALUES (1, 'existing', 'remove');");
        await using var context = CreateContext(connection);
        var add = new MigrationBuilder(context.Database.ProviderName!);
        add.AddColumnIfNotExists<string>(
            "Temporary",
            "rebuild_entities",
            type: "TEXT",
            nullable: false,
            defaultValue: "created");
        var rename = new MigrationBuilder(context.Database.ProviderName!);
        rename.RenameColumnIfExists("Temporary", "rebuild_entities", "Renamed");
        var rebuild = new MigrationBuilder(context.Database.ProviderName!);
        rebuild.DropColumnIfExists("Renamed", "rebuild_entities");
        rebuild.DropColumnIfExists("Legacy", "rebuild_entities");
        rebuild.AlterColumnIfDifferent(
            "rebuild_entities",
            new ExpectedColumnDefinition("Code", typeof(string), isNullable: false, storeType: "TEXT"),
            new ExpectedColumnDefinition("Code", typeof(string), isNullable: true, storeType: "TEXT"),
            SafeMigrationPolicy.RepairIfSafe);

        await ExecuteOperationsAsync(context, add.Operations);
        await ExecuteOperationsAsync(context, rename.Operations);
        await ExecuteOperationsAsync(context, rebuild.Operations);
        await ExecuteOperationsAsync(context, rebuild.Operations);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                rebuild.Operations,
                new SafeMigrationRunOptions("sqlite-column-lifecycle-replay"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM rebuild_entities WHERE Id = 1 AND Code = 'existing';"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_table_xinfo('rebuild_entities') "
                + "WHERE name IN ('Legacy', 'Temporary', 'Renamed');"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_table_xinfo('rebuild_entities') "
                + "WHERE name = 'Code' AND \"notnull\" = 1;"));
    }

    [Fact]
    public async Task GeneratedColumns_DefaultsAndCollationsAreComparedByPhysicalFacet()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE generated_values (a INTEGER NOT NULL, b INTEGER NOT NULL);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "generated_values",
            new ExpectedColumnDefinition(
                "sum_value",
                typeof(int),
                isNullable: false,
                storeType: "INTEGER",
                computedExpression: SafeMigrationSql.Binary(
                    SafeMigrationSql.Identifier("a"),
                    SafeMigrationSqlBinaryOperator.Add,
                    SafeMigrationSql.Identifier("b")),
                isStored: false),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.AddColumnIfNotExists<string>(
            "code",
            "generated_values",
            type: "TEXT",
            nullable: false,
            defaultValue: "default",
            collation: new SafeMigrationCollationIdentifier("NOCASE"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-generated-columns-replay"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_table_xinfo('generated_values') "
                + "WHERE name = 'sum_value' AND hidden = 2 AND \"notnull\" = 1;"));
    }

    [Fact]
    public async Task StoredGeneratedColumnAdd_IsRejectedBeforeDdl()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE generated_values (a INTEGER NOT NULL, b INTEGER NOT NULL);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "generated_values",
            new ExpectedColumnDefinition(
                "sum_value",
                typeof(int),
                isNullable: false,
                storeType: "INTEGER",
                computedExpression: SafeMigrationSql.Binary(
                    SafeMigrationSql.Identifier("a"),
                    SafeMigrationSqlBinaryOperator.Add,
                    SafeMigrationSql.Identifier("b")),
                isStored: true),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-stored-generated-column"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("stored_generated_column_add", assessment.AnalysisCode);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_table_xinfo('generated_values') WHERE name = 'sum_value';"));
    }

    [Theory]
    [InlineData(true, false, "row_version")]
    [InlineData(false, true, "column_comments")]
    public async Task UnsupportedColumnFacets_FailClosedBeforeDdl(
        bool rowVersion,
        bool comment,
        string expectedCode
    )
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE unsupported_columns (id INTEGER NOT NULL);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<byte[]>(
            "unsupported_value",
            "unsupported_columns",
            type: "BLOB",
            nullable: true,
            rowVersion: rowVersion,
            comment: comment ? "unsupported" : null);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-unsupported-column"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal(expectedCode, assessment.AnalysisCode);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_table_xinfo('unsupported_columns') "
                + "WHERE name = 'unsupported_value';"));
    }

    [Fact]
    public async Task SchemaIntent_MainIsNoOpAndForeignSchemaOperationsAreRejected()
    {
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureSchemaExists("main");
        builder.EnsureSchemaExists("attached");
        builder.DropSchemaIfExists("main");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-schema-boundary"));

        Assert.Equal(SafeMigrationObservedState.Matching, report.Assessments[0].ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal("database_qualifier_mismatch", report.Assessments[1].AnalysisCode);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[1].Action);
        Assert.Equal("schema_operations", report.Assessments[2].AnalysisCode);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[2].Action);
    }

    [Fact]
    public async Task VirtualTableMutation_IsRejectedWithoutCatalogDamage()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE VIRTUAL TABLE search_documents USING fts5(content);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<string>(
            "metadata",
            "search_documents",
            type: "TEXT",
            nullable: true);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-virtual-table"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("virtual_table", assessment.AnalysisCode);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema "
                + "WHERE type = 'table' AND name = 'search_documents';"));
    }
}
