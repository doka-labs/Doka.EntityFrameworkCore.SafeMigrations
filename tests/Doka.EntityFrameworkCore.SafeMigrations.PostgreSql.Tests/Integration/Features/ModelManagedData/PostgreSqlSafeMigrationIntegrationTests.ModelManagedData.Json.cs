namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task ModelManagedJsonAndJsonbConvergeUpdateDeletePostflightAndReplay()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE json_model_rows ("
            + "id integer NOT NULL PRIMARY KEY, json_payload json NULL, jsonb_payload jsonb NULL, "
            + "json_null_payload json NULL, sql_null_payload jsonb NULL, json_like_text text NOT NULL);"
            + "INSERT INTO json_model_rows VALUES ("
            + "1, '{ \"b\": 2, \"a\": 1 }', '{ \"b\": 2, \"a\": 1 }', 'null', NULL, "
            + "'{\"a\":1,\"b\":2}');");

        await using var context = CreateContext(connectionString);
        var columns = new[]
        {
            "json_payload",
            "jsonb_payload",
            "json_null_payload",
            "sql_null_payload",
            "json_like_text",
        };

        var columnTypes = new[] { "json", "jsonb", "json", "jsonb", "text", };
        var oldValues = new object?[,]
        {
            { "{\"a\":1,\"b\":2}", "{\"a\":1,\"b\":2}", "null", null, "{\"a\":1,\"b\":2}" },
        };

        var newValues = new object?[,]
        {
            { "{\"a\":3,\"b\":4}", "{\"a\":3,\"b\":4}", "null", null, "{\"a\":3,\"b\":4}" },
        };

        var ensure = new MigrationBuilder(context.Database.ProviderName!);
        _ = ensure.EnsureModelManagedDataFromModel(
            "json_model_rows",
            ["id"],
            ["integer"],
            ["id", .. columns],
            ["integer", .. columnTypes],
            new object?[,]
            {
                {
                    1,
                    "{\"a\":1,\"b\":2}",
                    "{\"a\":1,\"b\":2}",
                    "null",
                    null,
                    "{\"a\":1,\"b\":2}",
                },
            });

        var runner = context.GetService<ISafeMigrationRunner>();
        var ensurePreflight = await runner.AnalyzeAsync(
            context,
            ensure.Operations,
            new SafeMigrationRunOptions("postgresql-json-ensure"),
            CancellationToken.None);

        var update = new MigrationBuilder(context.Database.ProviderName!);
        _ = update.UpdateModelManagedDataFromModel(
            "json_model_rows",
            ["id"],
            ["integer"],
            new object?[,] { { 1 } },
            columns,
            columnTypes,
            oldValues,
            newValues);

        var updatePreflight = await runner.AnalyzeAsync(
            context,
            update.Operations,
            new SafeMigrationRunOptions("postgresql-json-update"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, update.Operations, CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "UPDATE json_model_rows SET "
            + "json_payload = '{ \"b\": 4, \"a\": 3 }', "
            + "jsonb_payload = '{ \"b\": 4, \"a\": 3 }' WHERE id = 1;");

        var updateReplay = await runner.AnalyzeAsync(
            context,
            update.Operations,
            new SafeMigrationRunOptions("postgresql-json-update-replay"),
            CancellationToken.None);

        var updatePostflight = await runner.VerifyAsync(
            context,
            update.Operations,
            new SafeMigrationRunOptions("postgresql-json-update-postflight"),
            CancellationToken.None);

        var delete = new MigrationBuilder(context.Database.ProviderName!);
        _ = delete.DeleteModelManagedDataFromModel(
            "json_model_rows",
            ["id"],
            ["integer"],
            new object?[,] { { 1 } },
            ["id", .. columns],
            ["integer", .. columnTypes],
            new object?[,]
            {
                {
                    1,
                    "{\"a\":3,\"b\":4}",
                    "{\"a\":3,\"b\":4}",
                    "null",
                    null,
                    "{\"a\":3,\"b\":4}",
                },
            });

        var deletePreflight = await runner.AnalyzeAsync(
            context,
            delete.Operations,
            new SafeMigrationRunOptions("postgresql-json-delete"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, delete.Operations, CancellationToken.None);

        var deleteReplay = await runner.AnalyzeAsync(
            context,
            delete.Operations,
            new SafeMigrationRunOptions("postgresql-json-delete-replay"),
            CancellationToken.None);

        var deletePostflight = await runner.VerifyAsync(
            context,
            delete.Operations,
            new SafeMigrationRunOptions("postgresql-json-delete-postflight"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(ensurePreflight.Assessments).ObservedState);
        Assert.Equal(
            SafeMigrationObservedState.TransitionReady,
            Assert.Single(updatePreflight.Assessments).ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(updateReplay.Assessments).ObservedState);
        Assert.True(Assert.Single(updatePostflight.Assessments).PostconditionSatisfied);
        Assert.Equal(
            SafeMigrationObservedState.TransitionReady,
            Assert.Single(deletePreflight.Assessments).ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, Assert.Single(deleteReplay.Assessments).ObservedState);
        Assert.True(Assert.Single(deletePostflight.Assessments).PostconditionSatisfied);
        Assert.Equal(0, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM json_model_rows;"));
    }

    [Fact]
    public async Task ModelManagedJsonAndJsonbRejectSemanticAndStorageBoundaries()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE json_comparison_boundaries ("
            + "id integer NOT NULL PRIMARY KEY, scalar_payload json NOT NULL, "
            + "object_payload json NOT NULL, jsonb_object_payload jsonb NOT NULL, "
            + "array_payload jsonb NOT NULL, duplicate_array_payload json NOT NULL, "
            + "json_null_payload json NULL, sql_null_payload jsonb NULL, json_like_text text NOT NULL);"
            + "INSERT INTO json_comparison_boundaries VALUES ("
            + "1, '1', '{ \"b\": 2, \"a\": 1 }', '{ \"b\": 2, \"a\": 1 }', "
            + "'[ 1, 2 ]', '[ 1, 1 ]', 'null', NULL, '{\"a\":1,\"b\":2}');");

        await using var context = CreateContext(connectionString);
        var ensure = new MigrationBuilder(context.Database.ProviderName!);
        _ = ensure.EnsureModelManagedDataFromModel(
            "json_comparison_boundaries",
            ["id"],
            ["integer"],
            [
                "id",
                "scalar_payload",
                "object_payload",
                "jsonb_object_payload",
                "array_payload",
                "duplicate_array_payload",
                "json_null_payload",
                "sql_null_payload",
                "json_like_text",
            ],
            ["integer", "json", "json", "jsonb", "jsonb", "json", "json", "jsonb", "text"],
            new object?[,]
            {
                {
                    1,
                    "1",
                    "{\"a\":1,\"b\":2}",
                    "{\"a\":1,\"b\":2}",
                    "[1,2]",
                    "[1,1]",
                    "null",
                    null,
                    "{\"a\":1,\"b\":2}",
                },
            });

        var runner = context.GetService<ISafeMigrationRunner>();
        var matching = await runner.AnalyzeAsync(
            context,
            ensure.Operations,
            new SafeMigrationRunOptions("postgresql-json-boundaries-matching"),
            CancellationToken.None);

        var mutations = new[]
        {
            "scalar_payload = '2'",
            "object_payload = '{\"a\":1}'",
            "object_payload = '{\"a\":1,\"b\":2,\"c\":3}'",
            "jsonb_object_payload = '{\"a\":1}'",
            "array_payload = '[2,1]'",
            "duplicate_array_payload = '[1]'",
            "json_null_payload = NULL",
            "sql_null_payload = 'null'",
            "json_like_text = '{ \"a\": 1, \"b\": 2 }'",
        };

        var resets = new[]
        {
            "scalar_payload = '1'",
            "object_payload = '{\"a\":1,\"b\":2}'",
            "object_payload = '{\"a\":1,\"b\":2}'",
            "jsonb_object_payload = '{\"a\":1,\"b\":2}'",
            "array_payload = '[1,2]'",
            "duplicate_array_payload = '[1,1]'",
            "json_null_payload = 'null'",
            "sql_null_payload = NULL",
            "json_like_text = '{\"a\":1,\"b\":2}'",
        };

        for (var index = 0; index < mutations.Length; index++)
        {
            await ExecuteSqlAsync(
                connectionString,
                $"UPDATE json_comparison_boundaries SET {mutations[index]} WHERE id = 1;");

            var different = await runner.AnalyzeAsync(
                context,
                ensure.Operations,
                new SafeMigrationRunOptions($"postgresql-json-boundary-{index}"),
                CancellationToken.None);

            Assert.Equal(
                SafeMigrationObservedState.Different,
                Assert.Single(different.Assessments).ObservedState);

            await ExecuteSqlAsync(
                connectionString,
                $"UPDATE json_comparison_boundaries SET {resets[index]} WHERE id = 1;");
        }

        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(matching.Assessments).ObservedState);
    }
}
