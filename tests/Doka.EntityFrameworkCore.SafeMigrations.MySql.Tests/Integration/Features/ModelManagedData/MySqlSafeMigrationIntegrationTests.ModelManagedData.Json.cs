namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Theory]
    [InlineData(SafeMigrationScaffoldingMode.Strict, DokaMySqlGuidFormat.Char36)]
    [InlineData(SafeMigrationScaffoldingMode.Strict, DokaMySqlGuidFormat.Binary16)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, DokaMySqlGuidFormat.Char36)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, DokaMySqlGuidFormat.Binary16)]
    public async Task ModelManagedJsonValuesConvergeUpdateDeleteAndReplay(
        SafeMigrationScaffoldingMode mode,
        DokaMySqlGuidFormat guidFormat
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var storeType = GuidStoreType(guidFormat);
        var maximumLength = guidFormat == DokaMySqlGuidFormat.Binary16 ? 16 : 36;
        var identifier = Guid.Parse("1458f03f-9acb-4902-bca8-280b613a7c92");

        // WHY: EF and Doka convert every supported JSON CLR seed shape to its
        // provider string before SafeMigrations pairs operations. The
        // model-differ test pins that boundary; this test exercises the exact
        // runtime values reaching the generated provider guards.
        var initialValues = new object?[,]
        {
            {
                identifier,
                "{\"source\":\"HasData\",\"verified\":true}",
                "{\"kind\":\"element\",\"value\":1}",
                "{\"kind\":\"document\",\"value\":1}",
                "{\"kind\":\"node\",\"value\":1}",
                "{\"kind\":\"object\",\"value\":1}",
                "[\"array\",5,true]",
                "null",
                null,
                "{\"source\":\"text\"}",
            },
        };

        var updatedValues = new object?[,]
        {
            {
                "{\"source\":\"HasData\",\"verified\":false}",
                "{\"kind\":\"element\",\"value\":2}",
                "{\"kind\":\"document\",\"value\":2}",
                "{\"kind\":\"node\",\"value\":2}",
                "{\"kind\":\"object\",\"value\":2}",
                "[\"array\",6,false]",
                "null",
                null,
                "{\"source\":\"updated-text\"}",
            },
        };

        var valueColumns = new[]
        {
            "json_text",
            "json_element",
            "json_document",
            "json_node",
            "json_object",
            "json_array",
            "json_null",
            "sql_null",
            "json_like_text",
        };

        var valueColumnTypes = new[]
        {
            "json",
            "json",
            "json",
            "json",
            "json",
            "json",
            "json",
            "json",
            "varchar(128)",
        };

        string[] ensureColumns = ["id", .. valueColumns];
        string[] ensureColumnTypes = [storeType, .. valueColumnTypes];

        var ensure = new MigrationBuilder(context.Database.ProviderName!);
        _ = AddScaffoldedTable(
            ensure,
            mode,
            "json_model_rows",
            table => new
            {
                id = table
                    .Column<Guid>(
                        type: storeType,
                        maxLength: maximumLength,
                        fixedLength: true,
                        nullable: false)
                    .Annotation("Doka:MySql:GuidFormat", guidFormat),
                json_text = table.Column<string>(type: "json", nullable: false),
                json_element = table.Column<string>(type: "json", nullable: false),
                json_document = table.Column<string>(type: "json", nullable: false),
                json_node = table.Column<string>(type: "json", nullable: false),
                json_object = table.Column<string>(type: "json", nullable: false),
                json_array = table.Column<string>(type: "json", nullable: false),
                json_null = table.Column<string>(type: "json", nullable: true),
                sql_null = table.Column<string>(type: "json", nullable: true),
                json_like_text = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
            },
            table => table.PrimaryKey("pk_json_model_rows", value => value.id));
        _ = ensure.EnsureModelManagedDataFromModel(
            "json_model_rows",
            ["id"],
            [storeType],
            ensureColumns,
            ensureColumnTypes,
            initialValues);

        var runner = context.GetService<ISafeMigrationRunner>();
        var ensurePreflight = await runner.AnalyzeAsync(
            context,
            ensure.Operations,
            new SafeMigrationRunOptions($"json-ensure-{mode}-{guidFormat}"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, ensure.Operations, CancellationToken.None);
        await ApplyEquivalentInitialJsonRepresentationAsync(connectionString, identifier, guidFormat);

        var ensureReplay = await runner.AnalyzeAsync(
            context,
            ensure.Operations,
            new SafeMigrationRunOptions($"json-ensure-replay-{mode}-{guidFormat}"),
            CancellationToken.None);

        var ensurePostflight = await runner.VerifyAsync(
            context,
            ensure.Operations,
            new SafeMigrationRunOptions($"json-ensure-postflight-{mode}-{guidFormat}"),
            CancellationToken.None);

        var update = new MigrationBuilder(context.Database.ProviderName!);
        _ = update.UpdateModelManagedDataFromModel(
            "json_model_rows",
            ["id"],
            [storeType],
            new object?[,] { { identifier, }, },
            valueColumns,
            valueColumnTypes,
            RemoveKeyColumn(initialValues),
            updatedValues);

        var updatePreflight = await runner.AnalyzeAsync(
            context,
            update.Operations,
            new SafeMigrationRunOptions($"json-update-{mode}-{guidFormat}"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, update.Operations, CancellationToken.None);

        await ApplyEquivalentUpdatedJsonRepresentationAsync(connectionString, identifier, guidFormat);

        var updateReplay = await runner.AnalyzeAsync(
            context,
            update.Operations,
            new SafeMigrationRunOptions($"json-update-replay-{mode}-{guidFormat}"),
            CancellationToken.None);

        var updatePostflight = await runner.VerifyAsync(
            context,
            update.Operations,
            new SafeMigrationRunOptions($"json-update-postflight-{mode}-{guidFormat}"),
            CancellationToken.None);

        var delete = new MigrationBuilder(context.Database.ProviderName!);
        _ = delete.DeleteModelManagedDataFromModel(
            "json_model_rows",
            ["id"],
            [storeType],
            new object?[,] { { identifier, }, },
            ensureColumns,
            ensureColumnTypes,
            AddKeyColumn(identifier, updatedValues));

        var deletePreflight = await runner.AnalyzeAsync(
            context,
            delete.Operations,
            new SafeMigrationRunOptions($"json-delete-{mode}-{guidFormat}"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, delete.Operations, CancellationToken.None);

        var deleteReplay = await runner.AnalyzeAsync(
            context,
            delete.Operations,
            new SafeMigrationRunOptions($"json-delete-replay-{mode}-{guidFormat}"),
            CancellationToken.None);

        var deletePostflight = await runner.VerifyAsync(
            context,
            delete.Operations,
            new SafeMigrationRunOptions($"json-delete-postflight-{mode}-{guidFormat}"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, ensurePreflight.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, ensureReplay.Assessments[^1].ObservedState);
        Assert.All(ensurePostflight.Assessments, assessment => Assert.True(assessment.PostconditionSatisfied));
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
        Assert.Equal(0, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `json_model_rows`;"));
    }

    [Fact]
    public async Task ModelManagedJsonComparisonRejectsEverySemanticAndStorageBoundary()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);

        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `json_comparison_boundaries` (
                `id` int NOT NULL,
                `scalar_payload` json NOT NULL,
                `object_payload` json NOT NULL,
                `array_payload` json NOT NULL,
                `duplicate_array_payload` json NOT NULL,
                `json_null_payload` json NULL,
                `sql_null_payload` json NULL,
                `json_like_text` varchar(128) NOT NULL,
                PRIMARY KEY (`id`)
            );
            INSERT INTO `json_comparison_boundaries`
                (`id`, `scalar_payload`, `object_payload`, `array_payload`,
                 `duplicate_array_payload`, `json_null_payload`, `sql_null_payload`, `json_like_text`)
            VALUES
                (1, '1', '{ "b": 2, "a": 1 }', '[ 1, 2 ]', '[ 1, 1 ]', 'null', NULL,
                 '{"a":1,"b":2}');
            """);

        var ensure = new MigrationBuilder(context.Database.ProviderName!);
        _ = ensure.EnsureModelManagedDataFromModel(
            "json_comparison_boundaries",
            ["id"],
            ["int"],
            [
                "id",
                "scalar_payload",
                "object_payload",
                "array_payload",
                "duplicate_array_payload",
                "json_null_payload",
                "sql_null_payload",
                "json_like_text",
            ],
            ["int", "json", "json", "json", "json", "json", "json", "varchar(128)"],
            new object?[,]
            {
                { 1, "1", "{\"a\":1,\"b\":2}", "[1,2]", "[1,1]", "null", null, "{\"a\":1,\"b\":2}" },
            });

        var runner = context.GetService<ISafeMigrationRunner>();
        var matching = await runner.AnalyzeAsync(
            context,
            ensure.Operations,
            new SafeMigrationRunOptions("json-semantic-boundaries-matching"),
            CancellationToken.None);

        var mutations = new[]
        {
            "`scalar_payload` = '2'",
            "`object_payload` = '{\"a\":1}'",
            "`object_payload` = '{\"a\":1,\"b\":2,\"c\":3}'",
            "`array_payload` = '[2,1]'",
            "`duplicate_array_payload` = '[1]'",
            "`json_null_payload` = NULL",
            "`sql_null_payload` = 'null'",
            "`json_like_text` = '{ \"a\": 1, \"b\": 2 }'",
        };

        var resets = new[]
        {
            "`scalar_payload` = '1'",
            "`object_payload` = '{\"a\":1,\"b\":2}'",
            "`object_payload` = '{\"a\":1,\"b\":2}'",
            "`array_payload` = '[1,2]'",
            "`duplicate_array_payload` = '[1,1]'",
            "`json_null_payload` = 'null'",
            "`sql_null_payload` = NULL",
            "`json_like_text` = '{\"a\":1,\"b\":2}'",
        };

        for (var index = 0; index < mutations.Length; index++)
        {
            await ExecuteSqlAsync(
                connectionString,
                $"UPDATE `json_comparison_boundaries` SET {mutations[index]} WHERE `id` = 1;");

            var different = await runner.AnalyzeAsync(
                context,
                ensure.Operations,
                new SafeMigrationRunOptions($"json-semantic-boundary-{index}"),
                CancellationToken.None);

            Assert.Equal(
                SafeMigrationObservedState.Different,
                Assert.Single(different.Assessments).ObservedState);

            await ExecuteSqlAsync(
                connectionString,
                $"UPDATE `json_comparison_boundaries` SET {resets[index]} WHERE `id` = 1;");
        }

        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(matching.Assessments).ObservedState);
    }

    private static object?[,] RemoveKeyColumn(
        object?[,] values
    )
    {
        var result = new object?[values.GetLength(0), values.GetLength(1) - 1];
        for (var row = 0; row < result.GetLength(0); row++)
        {
            for (var column = 0; column < result.GetLength(1); column++)
            {
                result[row, column] = values[row, column + 1];
            }
        }

        return result;
    }

    private static object?[,] AddKeyColumn(
        object key,
        object?[,] values
    )
    {
        var result = new object?[values.GetLength(0), values.GetLength(1) + 1];
        for (var row = 0; row < result.GetLength(0); row++)
        {
            result[row, 0] = key;
            for (var column = 0; column < values.GetLength(1); column++)
            {
                result[row, column + 1] = values[row, column];
            }
        }

        return result;
    }

    private static Task ApplyEquivalentInitialJsonRepresentationAsync(
        string connectionString,
        Guid identifier,
        DokaMySqlGuidFormat guidFormat
    ) => ExecuteSqlAsync(
        connectionString,
        $$"""
        UPDATE `json_model_rows`
        SET `json_text` = '{ "verified": true, "source": "HasData" }',
            `json_element` = '{ "value": 1, "kind": "element" }',
            `json_document` = '{ "value": 1, "kind": "document" }',
            `json_node` = '{ "value": 1, "kind": "node" }',
            `json_object` = '{ "value": 1, "kind": "object" }',
            `json_array` = '[ "array", 5, true ]'
        WHERE `id` = {{GuidLiteral(guidFormat, identifier.ToString())}};
        """);

    private static Task ApplyEquivalentUpdatedJsonRepresentationAsync(
        string connectionString,
        Guid identifier,
        DokaMySqlGuidFormat guidFormat
    ) => ExecuteSqlAsync(
        connectionString,
        $$"""
        UPDATE `json_model_rows`
        SET `json_text` = '{ "verified": false, "source": "HasData" }',
            `json_element` = '{ "value": 2, "kind": "element" }',
            `json_document` = '{ "value": 2, "kind": "document" }',
            `json_node` = '{ "value": 2, "kind": "node" }',
            `json_object` = '{ "value": 2, "kind": "object" }',
            `json_array` = '[ "array", 6, false ]'
        WHERE `id` = {{GuidLiteral(guidFormat, identifier.ToString())}};
        """);
}
