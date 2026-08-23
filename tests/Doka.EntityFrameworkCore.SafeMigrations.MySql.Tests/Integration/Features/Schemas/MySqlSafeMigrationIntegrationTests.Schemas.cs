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
