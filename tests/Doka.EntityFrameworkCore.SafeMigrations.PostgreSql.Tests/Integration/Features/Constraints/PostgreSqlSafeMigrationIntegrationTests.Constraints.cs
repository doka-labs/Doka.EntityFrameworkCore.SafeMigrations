namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task ConstraintLifecycle_IsIdempotentAcrossEveryConstraintFamily()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE parents (id integer NOT NULL PRIMARY KEY); "
            + "CREATE TABLE children (id integer NOT NULL, parent_id integer NULL, "
            + "code character varying(30) NULL, quantity integer NOT NULL);");
        await using var context = CreateContext(connectionString);
        var add = new MigrationBuilder(context.Database.ProviderName!);
        add.AddPrimaryKeyIfNotExists("pk_children", "children", ["id"]);
        add.AddUniqueConstraintIfNotExists("uq_children_code", "children", ["code"]);
        add.AddCheckConstraintIfNotExists("ck_children_quantity", "children", "quantity >= 0");
        add.AddForeignKeyIfNotExists(
            "fk_children_parents",
            "children",
            ["parent_id"],
            "parents",
            ["id"],
            onDelete: ReferentialAction.SetNull);
        foreach (var operation in add.Operations)
        {
            await ExecuteOperationsAsync(context, [operation]);
            await ExecuteOperationsAsync(context, [operation]);
        }

        Assert.Equal(
            4,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_constraint co "
                + "JOIN pg_catalog.pg_class c ON c.oid = co.conrelid "
                + "WHERE c.relname = 'children' AND co.conname IN "
                + "('pk_children', 'uq_children_code', 'ck_children_quantity', "
                + "'fk_children_parents');"));

        var drop = new MigrationBuilder(context.Database.ProviderName!);
        drop.DropForeignKeyIfExists("fk_children_parents", "children");
        drop.DropCheckConstraintIfExists("ck_children_quantity", "children");
        drop.DropUniqueConstraintIfExists("uq_children_code", "children");
        drop.DropPrimaryKeyIfExists("pk_children", "children");
        foreach (var operation in drop.Operations)
        {
            await ExecuteOperationsAsync(context, [operation]);
            await ExecuteOperationsAsync(context, [operation]);
        }

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_constraint co "
                + "JOIN pg_catalog.pg_class c ON c.oid = co.conrelid "
                + "WHERE c.relname = 'children' AND co.conname IN "
                + "('pk_children', 'uq_children_code', 'ck_children_quantity', "
                + "'fk_children_parents');"));
    }

    [Fact]
    public async Task ExistingConstraintDefinitionDrift_IsRejectedForEveryConstraintFamily()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE constraint_parents (id integer NOT NULL PRIMARY KEY); "
            + "CREATE TABLE constraint_drift ("
            + "id integer NOT NULL, alternate_id integer NOT NULL, "
            + "code text NULL, alternate_code text NULL, quantity integer NOT NULL, "
            + "parent_id integer NULL, alternate_parent_id integer NULL, "
            + "CONSTRAINT pk_constraint_drift PRIMARY KEY (alternate_id), "
            + "CONSTRAINT uq_constraint_code UNIQUE (alternate_code), "
            + "CONSTRAINT ck_constraint_quantity CHECK (quantity > 10), "
            + "CONSTRAINT fk_constraint_parent FOREIGN KEY (alternate_parent_id) "
            + "REFERENCES constraint_parents (id));");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddPrimaryKeyIfNotExists("pk_constraint_drift", "constraint_drift", ["id"]);
        builder.AddUniqueConstraintIfNotExists("uq_constraint_code", "constraint_drift", ["code"]);
        builder.AddCheckConstraintIfNotExists("ck_constraint_quantity", "constraint_drift", "quantity >= 0");
        builder.AddForeignKeyIfNotExists(
            "fk_constraint_parent",
            "constraint_drift",
            ["parent_id"],
            "constraint_parents",
            ["id"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("test-instance"));
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
            var exception =
                await Assert.ThrowsAsync<PostgresException>(() => ExecuteOperationsAsync(context, [operation]));
            Assert.Equal("P1001", exception.SqlState);
        }
    }

    [Fact]
    public async Task ConstraintAndUniqueIndexDataBlockers_StopBeforeTargetDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE blocker_parents (id integer NOT NULL PRIMARY KEY); "
            + "CREATE TABLE blocker_children ("
            + "id integer NULL, code text NOT NULL, quantity integer NOT NULL, "
            + "parent_id integer NOT NULL); "
            + "INSERT INTO blocker_parents VALUES (1); "
            + "INSERT INTO blocker_children VALUES "
            + "(1, 'duplicate', -1, 999), (1, 'duplicate', 1, 999);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddPrimaryKeyIfNotExists("pk_blocker_children", "blocker_children", ["id"]);
        builder.AddUniqueConstraintIfNotExists("uq_blocker_children_code", "blocker_children", ["code"]);
        builder.AddCheckConstraintIfNotExists("ck_blocker_children_quantity", "blocker_children", "quantity >= 0");
        builder.AddForeignKeyIfNotExists(
            "fk_blocker_children_parent",
            "blocker_children",
            ["parent_id"],
            "blocker_parents",
            ["id"]);
        builder.CreateIndexIfNotExists("ux_blocker_children_code", "blocker_children", ["code"], unique: true);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("test-instance"));
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.All(
            report.Assessments,
            assessment => Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState));

        foreach (var operation in builder.Operations)
        {
            var exception =
                await Assert.ThrowsAsync<PostgresException>(() => ExecuteOperationsAsync(context, [operation]));
            Assert.Equal("P1003", exception.SqlState);
        }

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_constraint co "
                + "JOIN pg_catalog.pg_class c ON c.oid = co.conrelid "
                + "WHERE c.relname = 'blocker_children' AND co.contype = 'c';"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_class c "
                + "WHERE c.relname = 'ux_blocker_children_code' AND c.relkind = 'i';"));
    }
}
