namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task ModelManagedUpdateRechecksSourceAndPostconditionAtExecution()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE model_race_roles (id integer NOT NULL PRIMARY KEY, name varchar(64) NOT NULL);"
            + "INSERT INTO model_race_roles (id, name) VALUES (1, 'source');");

        await using var context = CreateContext(connectionString);
        var update = ModelManagedNameUpdate(context.Database.ProviderName!, "model_race_roles");
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            update.Operations,
            new SafeMigrationRunOptions("model-data-source-race"),
            CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            "UPDATE model_race_roles SET name = 'concurrent' WHERE id = 1;");

        _ = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOperationsAsync(context, update.Operations, CancellationToken.None));

        await ExecuteSqlAsync(
            connectionString,
            "UPDATE model_race_roles SET name = 'source' WHERE id = 1;"
            + "CREATE FUNCTION model_race_roles_rewrite() RETURNS trigger LANGUAGE plpgsql AS $$ "
            + "BEGIN NEW.name := 'triggered'; RETURN NEW; END $$;"
            + "CREATE TRIGGER trg_model_race_roles_rewrite BEFORE UPDATE ON model_race_roles "
            + "FOR EACH ROW EXECUTE FUNCTION model_race_roles_rewrite();");

        _ = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOperationsAsync(context, update.Operations, CancellationToken.None));

        Assert.Equal(SafeMigrationObservedState.TransitionReady,
            Assert.Single(preflight.Assessments).ObservedState);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM model_race_roles WHERE id = 1 AND name = 'source';"));
    }

    [Theory]
    [InlineData("CASCADE")]
    [InlineData("NO ACTION")]
    [InlineData("RESTRICT")]
    [InlineData("SET DEFAULT")]
    [InlineData("SET NULL")]
    public async Task ModelManagedDeleteRechecksLateDependenciesBeforeReferentialActions(
        string referentialAction
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE model_dependency_roles (id integer NOT NULL PRIMARY KEY, name varchar(64) NOT NULL);"
            + "CREATE TABLE model_dependency_users (id integer NOT NULL PRIMARY KEY, "
            + "role_id integer NULL DEFAULT 2, CONSTRAINT fk_model_dependency_users_role FOREIGN KEY (role_id) "
            + $"REFERENCES model_dependency_roles (id) ON DELETE {referentialAction});"
            + "INSERT INTO model_dependency_roles (id, name) VALUES (1, 'source'), (2, 'default');");

        await using var context = CreateContext(connectionString);
        var deletion = ModelManagedRoleDeletion(context.Database.ProviderName!);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            deletion.Operations,
            new SafeMigrationRunOptions("model-data-dependency-race"),
            CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            "INSERT INTO model_dependency_users (id, role_id) VALUES (11, 1);");

        _ = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOperationsAsync(context, deletion.Operations, CancellationToken.None));

        Assert.Equal(SafeMigrationObservedState.TransitionReady,
            Assert.Single(preflight.Assessments).ObservedState);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM model_dependency_roles WHERE id = 1;"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM model_dependency_users WHERE id = 11 AND role_id = 1;"));
    }

    [Fact]
    public async Task ModelManagedMutationHonorsCancellationDuringExecution()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE model_cancel_roles (id integer NOT NULL PRIMARY KEY, name varchar(64) NOT NULL);"
            + "INSERT INTO model_cancel_roles (id, name) VALUES (1, 'source');"
            + "CREATE FUNCTION model_cancel_roles_wait() RETURNS trigger LANGUAGE plpgsql AS $$ "
            + "BEGIN PERFORM pg_sleep(10); RETURN NEW; END $$;"
            + "CREATE TRIGGER trg_model_cancel_roles_wait BEFORE UPDATE ON model_cancel_roles "
            + "FOR EACH ROW EXECUTE FUNCTION model_cancel_roles_wait();");

        await using var context = CreateContext(connectionString);
        var update = ModelManagedNameUpdate(context.Database.ProviderName!, "model_cancel_roles");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExecuteOperationsAsync(context, update.Operations, cancellation.Token));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM model_cancel_roles WHERE id = 1 AND name = 'source';"));
    }

    private static MigrationBuilder ModelManagedNameUpdate(
        string providerName,
        string table
    )
    {
        var builder = new MigrationBuilder(providerName);
        _ = builder.UpdateModelManagedDataFromModel(
            table,
            ["id"],
            ["integer"],
            new object?[,] { { 1 } },
            ["name"],
            ["character varying(64)"],
            new object?[,] { { "source" } },
            new object?[,] { { "target" } });

        return builder;
    }

    private static MigrationBuilder ModelManagedRoleDeletion(
        string providerName
    )
    {
        var builder = new MigrationBuilder(providerName);
        _ = builder.DeleteModelManagedDataFromModel(
            "model_dependency_roles",
            ["id"],
            ["integer"],
            new object?[,] { { 1 } },
            ["id", "name"],
            ["integer", "character varying(64)"],
            new object?[,] { { 1, "source" } },
            foreignKeys:
            [
                new ExpectedModelManagedDataForeignKeyDefinition(
                    "model_dependency_users",
                    ["role_id"],
                    ["id"]),
            ]);

        return builder;
    }
}
