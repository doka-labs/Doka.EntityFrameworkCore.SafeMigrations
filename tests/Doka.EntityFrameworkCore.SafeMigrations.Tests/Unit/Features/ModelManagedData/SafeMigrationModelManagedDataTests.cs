namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationModelManagedDataTests
{
    [Fact]
    public void DefinitionsSnapshotMutableInputsAndRejectNullOrDuplicateKeys()
    {
        var values = new object?[,] { { 1, "administrator" } };
        var builder = new MigrationBuilder("test");

        builder.EnsureModelManagedDataFromModel(
            "roles",
            ["id"],
            ["int"],
            ["id", "name"],
            ["int", "varchar(64)"],
            values);
        values[0, 1] = "mutated";

        var intent = Assert.IsType<EnsureModelManagedDataIntent>(
            Assert.Single(builder.Operations.Cast<SafeMigrationOperation>()).Intent);

        Assert.Equal("administrator", intent.Values.GetValue(0, 1));
        Assert.Equal(SafeMigrationPolicy.ThrowIfDifferent,
            Assert.Single(builder.Operations.Cast<SafeMigrationOperation>()).Policy);
        Assert.Throws<ArgumentException>(() => builder.EnsureModelManagedDataFromModel(
            "roles",
            ["id"],
            ["int"],
            ["id"],
            ["int"],
            new object?[,] { { null } }));
        Assert.Throws<ArgumentException>(() => builder.EnsureModelManagedDataFromModel(
            "roles",
            ["id"],
            ["int"],
            ["id"],
            ["int"],
            new object?[,] { { 1 }, { 1 } }));
    }

    [Fact]
    public void CandidateKeysRejectDuplicateNonNullTargetsButPermitMultipleNulls()
    {
        var uniqueKeys = new[] { new ExpectedModelManagedDataUniqueKeyDefinition(["code"]), };
        var builder = new MigrationBuilder("test");

        Assert.Throws<ArgumentException>(() => builder.EnsureModelManagedDataFromModel(
            "roles",
            ["id"],
            ["int"],
            ["id", "code"],
            ["int", "varchar(64)"],
            new object?[,] { { 1, "same" }, { 2, "same" } },
            uniqueKeys: uniqueKeys));

        builder.EnsureModelManagedDataFromModel(
            "roles",
            ["id"],
            ["int"],
            ["id", "code"],
            ["int", "varchar(64)"],
            new object?[,] { { 1, null }, { 2, null } },
            uniqueKeys: uniqueKeys);

        Assert.Single(builder.Operations);
    }

    [Fact]
    public void PairerSourceFreezesInsertUpdateAndDeleteTransitions()
    {
        var insert = new InsertDataOperation
        {
            Table = "roles",
            Columns = ["id", "name"],
            ColumnTypes = ["int", "varchar(64)"],
            Values = new object?[,] { { 1, "administrator" } },
        };

        var inverseDelete = new DeleteDataOperation
        {
            Table = "roles",
            KeyColumns = ["id"],
            KeyColumnTypes = ["int"],
            KeyValues = new object?[,] { { 1 } },
        };

        var insertResult = SafeMigrationModelManagedDataPairer.Pair([insert], [inverseDelete]);

        var ensure = Assert.IsType<EnsureModelManagedDataScaffoldingOperation>(Assert.Single(insertResult));
        var ensureIntent = Assert.IsType<EnsureModelManagedDataIntent>(ensure.Intent);

        Assert.Equal("administrator", ensureIntent.Values.GetValue(0, 1));

        var update = new UpdateDataOperation
        {
            Table = "roles",
            KeyColumns = ["id"],
            KeyColumnTypes = ["int"],
            KeyValues = new object?[,] { { 1 } },
            Columns = ["name"],
            ColumnTypes = ["varchar(64)"],
            Values = new object?[,] { { "owner" } },
        };

        var inverseUpdate = new UpdateDataOperation
        {
            Table = "roles",
            KeyColumns = ["id"],
            KeyColumnTypes = ["int"],
            KeyValues = new object?[,] { { 1 } },
            Columns = ["name"],
            ColumnTypes = ["varchar(64)"],
            Values = new object?[,] { { "administrator" } },
        };

        var updateResult = SafeMigrationModelManagedDataPairer.Pair([update], [inverseUpdate]);

        var transition = Assert.IsType<UpdateModelManagedDataScaffoldingOperation>(Assert.Single(updateResult));
        var updateIntent = Assert.IsType<UpdateModelManagedDataIntent>(transition.Intent);

        Assert.Equal("administrator", updateIntent.OldValues.GetValue(0, 0));
        Assert.Equal("owner", updateIntent.NewValues.GetValue(0, 0));

        var deleteResult = SafeMigrationModelManagedDataPairer.Pair([inverseDelete], [insert]);

        var deletion = Assert.IsType<DeleteModelManagedDataScaffoldingOperation>(Assert.Single(deleteResult));
        var deleteIntent = Assert.IsType<DeleteModelManagedDataIntent>(deletion.Intent);

        Assert.Equal("administrator", deleteIntent.OldValues.GetValue(0, 1));
    }

    [Fact]
    public void PairerRejectsMissingAmbiguousAndAnnotatedInverseEvidence()
    {
        var insert = new InsertDataOperation
        {
            Table = "roles",
            Columns = ["id"],
            ColumnTypes = ["int"],
            Values = new object?[,] { { 1 } },
        };

        var inverse = new DeleteDataOperation
        {
            Table = "roles",
            KeyColumns = ["id"],
            KeyColumnTypes = ["int"],
            KeyValues = new object?[,] { { 1 } },
        };

        Assert.Throws<InvalidOperationException>(() => SafeMigrationModelManagedDataPairer.Pair([insert], []));
        Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationModelManagedDataPairer.Pair([insert], [inverse, inverse]));

        insert.AddAnnotation("consumer:annotation", true);

        Assert.Throws<InvalidOperationException>(() => SafeMigrationModelManagedDataPairer.Pair([insert], [inverse]));
    }

    [Fact]
    public void PairerUsesCapturedPrimaryKeyOnlyWhenInverseDropsExactTable()
    {
        var insert = new InsertDataOperation
        {
            Table = "roles",
            Schema = "identity",
            Columns = ["tenant_id", "id", "name"],
            ColumnTypes = ["int", "int", "varchar(64)"],
            Values = new object?[,] { { 7, 1, "administrator" } },
        };

        SafeMigrationModelManagedDataMetadataStore.Set(
            insert,
            new SafeMigrationModelManagedDataMetadata(
                ["tenant_id", "id"],
                ["int", "int"],
                [],
                []));

        var result = SafeMigrationModelManagedDataPairer.Pair(
            [insert],
            [new DropTableOperation { Name = "roles", Schema = "identity", }]);

        var ensure = Assert.IsType<EnsureModelManagedDataScaffoldingOperation>(Assert.Single(result));

        Assert.Equal(["tenant_id", "id"], ensure.Intent.KeyColumns);
        Assert.Equal(7, ensure.Intent.KeyValues.GetValue(0, 0));
        Assert.Equal(1, ensure.Intent.KeyValues.GetValue(0, 1));

        Assert.Throws<InvalidOperationException>(() => SafeMigrationModelManagedDataPairer.Pair(
            [insert],
            [new DropTableOperation { Name = "roles", Schema = "other", }]));
    }

    [Fact]
    public void PairerTreatsInverseRowsAsSubsumedOnlyByExactForwardDropTable()
    {
        var inverseInsert = new InsertDataOperation
        {
            Table = "roles",
            Schema = "identity",
            Columns = ["id"],
            ColumnTypes = ["int"],
            Values = new object?[,] { { 1 } },
        };

        var result = SafeMigrationModelManagedDataPairer.Pair(
            [new DropTableOperation { Name = "roles", Schema = "identity", }],
            [inverseInsert]);

        Assert.IsType<DropTableOperation>(Assert.Single(result));
        Assert.Throws<InvalidOperationException>(() => SafeMigrationModelManagedDataPairer.Pair(
            [new DropTableOperation { Name = "roles", Schema = "other", }],
            [inverseInsert]));
    }

    [Fact]
    public void PairerIndexesOneHundredThousandReorderedRowsAndPreservesForwardOrder()
    {
        const int rowCount = 100_000;
        var values = new object?[rowCount, 2];
        var inverseKeys = new object?[rowCount, 1];
        for (var row = 0; row < rowCount; row++)
        {
            values[row, 0] = row;
            values[row, 1] = $"role-{row.ToString(CultureInfo.InvariantCulture)}";
            inverseKeys[rowCount - row - 1, 0] = row;
        }

        var insert = new InsertDataOperation
        {
            Table = "roles",
            Columns = ["id", "name"],
            ColumnTypes = ["int", "varchar(64)"],
            Values = values,
        };

        var inverseDelete = new DeleteDataOperation
        {
            Table = "roles",
            KeyColumns = ["id"],
            KeyColumnTypes = ["int"],
            KeyValues = inverseKeys,
        };

        var result = SafeMigrationModelManagedDataPairer.Pair([insert], [inverseDelete]);

        var first = Assert.IsType<EnsureModelManagedDataScaffoldingOperation>(result[0]);
        var last = Assert.IsType<EnsureModelManagedDataScaffoldingOperation>(result[^1]);

        Assert.Equal(782, result.Count);
        Assert.Equal(0, first.Intent.KeyValues.GetValue(0, 0));
        Assert.Equal(rowCount - 1, last.Intent.KeyValues.GetValue(last.Intent.RowCount - 1, 0));
    }

    [Theory]
    [InlineData(SafeMigrationPolicy.ExistenceOnly)]
    [InlineData(SafeMigrationPolicy.RepairIfSafe)]
    public void ModelManagedOperationsRejectPoliciesThatCouldAuthorizeOverwrite(
        SafeMigrationPolicy policy
    )
    {
        var intent = new EnsureModelManagedDataIntent(
            "roles",
            ["id"],
            ["int"],
            ["id"],
            ["int"],
            new object?[,] { { 1 } },
            schema: null,
            uniqueKeys: null);

        var exception = Assert.Throws<ArgumentException>(() => new SafeMigrationOperation(intent, policy));

        Assert.Equal("policy", exception.ParamName);
    }

    [Fact]
    public void PreflightProjectsAcceptedModelManagedTransitionsInOrder()
    {
        var projection = new SafeMigrationPreflightProjection();
        var ensure = Operation(
            new EnsureModelManagedDataIntent(
                "roles",
                ["id"],
                ["int"],
                ["id", "name"],
                ["int", "varchar(64)"],
                new object?[,] { { 1, "administrator" } },
                schema: null,
                uniqueKeys: null));

        Accept(projection, ensure, SafeMigrationObservedState.Missing);

        var update = Operation(
            new UpdateModelManagedDataIntent(
                "roles",
                ["id"],
                ["int"],
                new object?[,] { { 1 } },
                ["name"],
                ["varchar(64)"],
                new object?[,] { { "administrator" } },
                new object?[,] { { "owner" } },
                schema: null,
                uniqueKeys: null));

        var projectedUpdate = projection.Project(update, Live(SafeMigrationObservedState.PrerequisiteMissing));

        Assert.Equal(SafeMigrationObservedState.TransitionReady, projectedUpdate.ObservedState);
        Accept(projection, update, SafeMigrationObservedState.PrerequisiteMissing);

        var deletion = Operation(
            new DeleteModelManagedDataIntent(
                "roles",
                ["id"],
                ["int"],
                new object?[,] { { 1 } },
                ["id", "name"],
                ["int", "varchar(64)"],
                new object?[,] { { 1, "owner" } },
                schema: null,
                foreignKeys: null));

        var projectedDelete = projection.Project(deletion, Live(SafeMigrationObservedState.Different));

        Assert.Equal(SafeMigrationObservedState.TransitionReady, projectedDelete.ObservedState);
    }

    [Fact]
    public void PreflightDischargesOnlyExactlyCoveredAcceptedDependencies()
    {
        var projection = new SafeMigrationPreflightProjection();
        var childDelete = Operation(
            new DeleteModelManagedDataIntent(
                "user_roles",
                ["id"],
                ["int"],
                new object?[,] { { 11 } },
                ["id", "role_id"],
                ["int", "int"],
                new object?[,] { { 11, 1 } },
                schema: "identity",
                foreignKeys: null));

        var childAnalysis = EvidenceAnalysis(
            SafeMigrationObservedState.TransitionReady,
            [SafeMigrationModelManagedRowState.Source],
            []);

        var childDecision = SafeMigrationDecisionPlanner.Plan(
            childDelete.Intent.Kind,
            childAnalysis.ObservedState,
            childDelete.Policy,
            childAnalysis.RepairCapability);

        projection.Observe(childDelete, childAnalysis, childDecision);

        var parentDelete = Operation(
            new DeleteModelManagedDataIntent(
                "roles",
                ["id"],
                ["int"],
                new object?[,] { { 1 } },
                ["id", "name"],
                ["int", "varchar(64)"],
                new object?[,] { { 1, "administrator" } },
                schema: "identity",
                foreignKeys:
                [
                    new ExpectedModelManagedDataForeignKeyDefinition(
                        "user_roles",
                        ["role_id"],
                        ["id"],
                        "identity"),
                ]));

        var exactlyCovered = projection.Project(
            parentDelete,
            EvidenceAnalysis(
                SafeMigrationObservedState.DataBlocked,
                [SafeMigrationModelManagedRowState.Source],
                [1]));

        var additionalLiveDependency = projection.Project(
            parentDelete,
            EvidenceAnalysis(
                SafeMigrationObservedState.DataBlocked,
                [SafeMigrationModelManagedRowState.Source],
                [2]));

        Assert.Equal(SafeMigrationObservedState.TransitionReady, exactlyCovered.ObservedState);
        Assert.Equal("projected_dependency_handoff", exactlyCovered.Code);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, additionalLiveDependency.ObservedState);
    }

    [Fact]
    public void CompactEvidenceParserRejectsMalformedOrInconsistentProviderResults()
    {
        var evidence = SafeMigrationModelManagedDataEvidence.Parse(
            "0123",
            4,
            "0,17",
            2,
            "test");

        Assert.Equal(
            [
                SafeMigrationModelManagedRowState.Missing,
                SafeMigrationModelManagedRowState.Source,
                SafeMigrationModelManagedRowState.Target,
                SafeMigrationModelManagedRowState.Different,
            ],
            evidence.RowStates);
        Assert.Equal([0, 17], evidence.DependencyCounts);
        Assert.Throws<InvalidOperationException>(() => SafeMigrationModelManagedDataEvidence.Parse(
            "01",
            1,
            "",
            0,
            "test"));
        Assert.Throws<InvalidOperationException>(() => SafeMigrationModelManagedDataEvidence.Parse(
            "4",
            1,
            "",
            0,
            "test"));
        Assert.Throws<InvalidOperationException>(() => SafeMigrationModelManagedDataEvidence.Parse(
            "1",
            1,
            "-1",
            1,
            "test"));
    }

    [Fact]
    public void ContractValidatorRejectsDuplicateTypedKeysAcrossBatchesAndColumnOrder()
    {
        var first = Operation(
            new EnsureModelManagedDataIntent(
                "roles",
                ["tenant_id", "id"],
                ["int", "int"],
                ["tenant_id", "id", "name"],
                ["int", "int", "varchar(64)"],
                new object?[,] { { 7, 1, "administrator" } },
                schema: "identity",
                uniqueKeys: null));

        var duplicate = Operation(
            new DeleteModelManagedDataIntent(
                "roles",
                ["id", "tenant_id"],
                ["int", "int"],
                new object?[,] { { 1, 7 } },
                ["id", "tenant_id", "name"],
                ["int", "int", "varchar(64)"],
                new object?[,] { { 1, 7, "administrator" } },
                schema: "identity",
                foreignKeys: null));

        Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationModelManagedDataContractValidator.Validate([first, duplicate]));

        SafeMigrationModelManagedDataContractValidator.Validate(
            [
                first,
                Operation(
                    new EnsureModelManagedDataIntent(
                        "roles",
                        ["tenant_id", "id"],
                        ["int", "int"],
                        ["tenant_id", "id", "name"],
                        ["int", "int", "varchar(64)"],
                        new object?[,] { { 7, 2, "member" } },
                        schema: "identity",
                        uniqueKeys: null)),
            ]);
    }

    private static SafeMigrationOperation Operation(
        SafeMigrationIntent intent
    ) => new(intent, SafeMigrationPolicy.ThrowIfDifferent);

    private static SafeMigrationProviderAnalysis Live(
        SafeMigrationObservedState state
    ) => new(state, SafeMigrationRepairCapability.None, postconditionSatisfied: false, "test_live");

    private static SafeMigrationProviderAnalysis EvidenceAnalysis(
        SafeMigrationObservedState state,
        SafeMigrationModelManagedRowState[] rowStates,
        long[] dependencyCounts
    ) => new(state, SafeMigrationRepairCapability.None, postconditionSatisfied: false, "test_live")
    {
        ModelManagedDataEvidence = new SafeMigrationModelManagedDataEvidence(rowStates, dependencyCounts),
    };

    private static void Accept(
        SafeMigrationPreflightProjection projection,
        SafeMigrationOperation operation,
        SafeMigrationObservedState liveState
    )
    {
        var analysis = projection.Project(operation, Live(liveState));
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        projection.Observe(operation, analysis, decision);
    }
}
