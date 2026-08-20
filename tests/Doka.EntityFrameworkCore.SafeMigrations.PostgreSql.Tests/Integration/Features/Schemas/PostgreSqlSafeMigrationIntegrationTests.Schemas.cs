namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task SchemaOperations_AreIdempotent()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var create = new MigrationBuilder(context.Database.ProviderName!);
        create.EnsureSchemaExists("module");
        await ExecuteOperationsAsync(context, create.Operations);
        await ExecuteOperationsAsync(context, create.Operations);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_namespace WHERE nspname = 'module';"));

        var drop = new MigrationBuilder(context.Database.ProviderName!);
        drop.DropSchemaIfExists("module");
        await ExecuteOperationsAsync(context, drop.Operations);
        await ExecuteOperationsAsync(context, drop.Operations);

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_namespace WHERE nspname = 'module';"));
    }

    [Fact]
    public async Task CrossSchemaConvergence_IsQualifiedIdempotentAndCatalogExact()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE public.qualified_parent (id integer NOT NULL PRIMARY KEY); "
            + "CREATE TABLE public.qualified_child (id integer NOT NULL PRIMARY KEY);");
        await using var context = CreateContext(connectionString);

        const string parentSchema = "tenant_reference";
        const string childSchema = "tenant_core";
        const string parentTable = "qualified_parent";
        const string childTable = "qualified_child";

        var parent = new ExpectedTableDefinition(
            parentTable,
            [
                new ExpectedColumnDefinition("id", typeof(int), false, "integer"),
                new ExpectedColumnDefinition("alternate_id", typeof(int), false, "integer"),
            ],
            parentSchema,
            primaryKey: new ExpectedPrimaryKeyDefinition(
                "pk_qualified_parent",
                parentTable,
                ["id", "alternate_id"],
                parentSchema));

        var child = new ExpectedTableDefinition(
            childTable,
            [
                new ExpectedColumnDefinition("id", typeof(int), false, "integer"),
                new ExpectedColumnDefinition("code", typeof(string), true, "character varying(40)", maxLength: 40),
                new ExpectedColumnDefinition("quantity", typeof(int), false, "integer"),
                new ExpectedColumnDefinition("parent_id", typeof(int), true, "integer"),
                new ExpectedColumnDefinition("parent_alternate_id", typeof(int), true, "integer"),
            ],
            childSchema,
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_qualified_child", childTable, ["id"], childSchema),
            uniqueConstraints:
            [
                new ExpectedUniqueConstraintDefinition(
                    "uq_qualified_child_code",
                    childTable,
                    ["code"],
                    childSchema),
            ],
            checkConstraints:
            [
                new ExpectedCheckConstraintDefinition(
                    "ck_qualified_child_quantity",
                    childTable,
                    "quantity >= 0",
                    childSchema),
            ],
            foreignKeys:
            [
                new ExpectedForeignKeyDefinition(
                    "fk_qualified_child_parent",
                    childTable,
                    ["parent_id", "parent_alternate_id"],
                    parentTable,
                    ["id", "alternate_id"],
                    childSchema,
                    parentSchema,
                    ReferentialAction.Cascade,
                    ReferentialAction.SetNull),
            ]);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureSchemaExists(parentSchema);
        builder.EnsureSchemaExists(childSchema);
        builder.ConvergeTable(parent);
        builder.ConvergeTable(
            child,
            [
                new ExpectedIndexDefinition(
                    "ix_qualified_child_parent",
                    childTable,
                    [
                        new ExpectedIndexKeyDefinition(column: "parent_id"),
                        new ExpectedIndexKeyDefinition(column: "parent_alternate_id", descending: true),
                    ],
                    childSchema),
            ]);

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("cross-schema-convergence"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment =>
            Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_constraint co "
                + "JOIN pg_catalog.pg_class child ON child.oid = co.conrelid "
                + "JOIN pg_catalog.pg_namespace child_ns ON child_ns.oid = child.relnamespace "
                + "JOIN pg_catalog.pg_class parent ON parent.oid = co.confrelid "
                + "JOIN pg_catalog.pg_namespace parent_ns ON parent_ns.oid = parent.relnamespace "
                + "WHERE co.conname = 'fk_qualified_child_parent' "
                + "AND child_ns.nspname = 'tenant_core' "
                + "AND parent_ns.nspname = 'tenant_reference';"));
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' "
                + "AND table_name IN ('qualified_parent', 'qualified_child');"));
    }

    [Fact]
    public async Task NonDefaultSchemaRenameAndDropMatrix_IsIdempotentAndIsolated()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE SCHEMA lifecycle_module; CREATE SCHEMA lifecycle_archive; "
            + "CREATE TABLE lifecycle_module.lifecycle_parent (id integer NOT NULL PRIMARY KEY); "
            + "CREATE TABLE lifecycle_module.lifecycle_target ("
            + "id integer NOT NULL, parent_id integer NULL, code text NULL, "
            + "quantity integer NOT NULL, payload text NULL, "
            + "CONSTRAINT pk_lifecycle_target PRIMARY KEY (id), "
            + "CONSTRAINT uq_lifecycle_target_code UNIQUE (code), "
            + "CONSTRAINT ck_lifecycle_target_quantity CHECK (quantity >= 0), "
            + "CONSTRAINT fk_lifecycle_target_parent FOREIGN KEY (parent_id) "
            + "REFERENCES lifecycle_module.lifecycle_parent (id)); "
            + "CREATE INDEX ix_lifecycle_target_payload "
            + "ON lifecycle_module.lifecycle_target (payload); "
            + "CREATE TABLE lifecycle_module.rename_source (old_value integer NULL); "
            + "CREATE INDEX ix_rename_source_old "
            + "ON lifecycle_module.rename_source (old_value); "
            + "CREATE TABLE lifecycle_module.move_source (id integer NOT NULL); "
            + "CREATE TABLE public.lifecycle_target (id integer NOT NULL); "
            + "CREATE TABLE public.renamed_source (id integer NOT NULL); "
            + "CREATE TABLE public.moved_table (id integer NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);

        builder.RenameColumnIfExists(
            "old_value",
            "rename_source",
            "renamed_value",
            "lifecycle_module");
        builder.RenameIndexIfExists(
            "ix_rename_source_old",
            "rename_source",
            "ix_rename_source_new",
            "lifecycle_module");
        builder.RenameTableIfExists(
            "rename_source",
            "renamed_source",
            "lifecycle_module");
        builder.RenameTableIfExists(
            "move_source",
            "moved_table",
            "lifecycle_module",
            "lifecycle_archive");
        builder.DropForeignKeyIfExists(
            "fk_lifecycle_target_parent",
            "lifecycle_target",
            "lifecycle_module");
        builder.DropCheckConstraintIfExists(
            "ck_lifecycle_target_quantity",
            "lifecycle_target",
            "lifecycle_module");
        builder.DropUniqueConstraintIfExists(
            "uq_lifecycle_target_code",
            "lifecycle_target",
            "lifecycle_module");
        builder.DropPrimaryKeyIfExists(
            "pk_lifecycle_target",
            "lifecycle_target",
            "lifecycle_module");
        builder.DropIndexIfExists(
            "ix_lifecycle_target_payload",
            "lifecycle_target",
            "lifecycle_module");
        builder.DropColumnIfExists("payload", "lifecycle_target", "lifecycle_module");
        builder.DropTableIfExists("lifecycle_target", "lifecycle_module");
        builder.DropTableIfExists("lifecycle_parent", "lifecycle_module");
        builder.DropTableIfExists("renamed_source", "lifecycle_module");
        builder.DropTableIfExists("moved_table", "lifecycle_archive");
        builder.DropSchemaIfExists("lifecycle_module");
        builder.DropSchemaIfExists("lifecycle_archive");

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_namespace "
                + "WHERE nspname IN ('lifecycle_module', 'lifecycle_archive');"));
        Assert.Equal(
            3,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' "
                + "AND table_name IN ('lifecycle_target', 'renamed_source', 'moved_table');"));
    }
}
