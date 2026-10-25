namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationModelManagedDataTests
{
    /// <summary>Snapshots caller-owned model-data values without changing the default safety policy.</summary>
    [Fact]
    public void DefinitionsSnapshotMutableInputs()
    {
        // Arrange
        var values = new object?[,] { { 1, "administrator" } };
        var builder = new MigrationBuilder("test");

        // Act
        builder.EnsureModelManagedDataFromModel(
            "roles",
            ["id"],
            ["int"],
            ["id", "name"],
            ["int", "varchar(64)"],
            values);
        values[0, 1] = "mutated";

        // Assert
        var operation = Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
        var intent = Assert.IsType<EnsureModelManagedDataIntent>(operation.Intent);

        Assert.Equal("administrator", intent.Values.GetValue(0, 1));
        Assert.Equal(SafeMigrationPolicy.ThrowIfDifferent, operation.Policy);
    }

    /// <summary>Rejects independent null and duplicate key inputs before publishing an operation.</summary>
    /// <param name="keyCase">The serializable invalid-key case identifier.</param>
    [Theory]
    [InlineData("null")]
    [InlineData("duplicate")]
    public void DefinitionsRejectInvalidKeys(
        string keyCase
    )
    {
        // Arrange
        var builder = new MigrationBuilder("test");
        var values = keyCase switch
        {
            "null" => new object?[,] { { null } },
            "duplicate" => new object?[,] { { 1 }, { 1 } },
            _ => throw new ArgumentOutOfRangeException(nameof(keyCase), keyCase, "Unknown invalid-key case."),
        };

        // Act
        var exception = Record.Exception(() => builder.EnsureModelManagedDataFromModel(
            "roles",
            ["id"],
            ["int"],
            ["id"],
            ["int"],
            values));

        // Assert
        Assert.IsType<ArgumentException>(exception);
        Assert.Empty(builder.Operations);
    }

    /// <summary>Rejects duplicate non-null candidate-key values independently of primary-key uniqueness.</summary>
    [Fact]
    public void CandidateKeysRejectDuplicateNonNullTargets()
    {
        // Arrange
        var uniqueKeys = new[] { new ExpectedModelManagedDataUniqueKeyDefinition(["code"]), };
        var builder = new MigrationBuilder("test");

        // Act
        var exception = Record.Exception(() => builder.EnsureModelManagedDataFromModel(
            "roles",
            ["id"],
            ["int"],
            ["id", "code"],
            ["int", "varchar(64)"],
            new object?[,] { { 1, "same" }, { 2, "same" } },
            uniqueKeys: uniqueKeys));

        // Assert
        Assert.IsType<ArgumentException>(exception);
        Assert.Empty(builder.Operations);
    }

    /// <summary>Permits multiple absent candidate-key values when the primary keys remain distinct.</summary>
    [Fact]
    public void CandidateKeysPermitMultipleNulls()
    {
        // Arrange
        var uniqueKeys = new[] { new ExpectedModelManagedDataUniqueKeyDefinition(["code"]), };
        var builder = new MigrationBuilder("test");

        // Act
        builder.EnsureModelManagedDataFromModel(
            "roles",
            ["id"],
            ["int"],
            ["id", "code"],
            ["int", "varchar(64)"],
            new object?[,] { { 1, null }, { 2, null } },
            uniqueKeys: uniqueKeys);

        // Assert
        Assert.Single(builder.Operations);
    }

    /// <summary>Freezes an INSERT and its inverse DELETE into an ensure-data source contract.</summary>
    [Fact]
    public void PairerSourceFreezesInsertTransition()
    {
        // Arrange
        var insert = CreateRoleInsert();
        var inverseDelete = CreateRoleDelete();

        // Act
        var result = SafeMigrationModelManagedDataPairer.Pair([insert], [inverseDelete]);

        // Assert
        var ensure = Assert.IsType<EnsureModelManagedDataScaffoldingOperation>(Assert.Single(result));
        var intent = Assert.IsType<EnsureModelManagedDataIntent>(ensure.Intent);

        Assert.Equal("administrator", intent.Values.GetValue(0, 1));
    }

    /// <summary>Freezes UPDATE target and inverse source rows without interchanging their values.</summary>
    [Fact]
    public void PairerSourceFreezesUpdateTransition()
    {
        // Arrange
        var update = CreateRoleUpdate("owner");
        var inverseUpdate = CreateRoleUpdate("administrator");

        // Act
        var result = SafeMigrationModelManagedDataPairer.Pair([update], [inverseUpdate]);

        // Assert
        var transition = Assert.IsType<UpdateModelManagedDataScaffoldingOperation>(Assert.Single(result));
        var intent = Assert.IsType<UpdateModelManagedDataIntent>(transition.Intent);

        Assert.Equal("administrator", intent.OldValues.GetValue(0, 0));
        Assert.Equal("owner", intent.NewValues.GetValue(0, 0));
    }

    /// <summary>Freezes DELETE source rows from the exact inverse INSERT evidence.</summary>
    [Fact]
    public void PairerSourceFreezesDeleteTransition()
    {
        // Arrange
        var deletion = CreateRoleDelete();
        var inverseInsert = CreateRoleInsert();

        // Act
        var result = SafeMigrationModelManagedDataPairer.Pair([deletion], [inverseInsert]);

        // Assert
        var operation = Assert.IsType<DeleteModelManagedDataScaffoldingOperation>(Assert.Single(result));
        var intent = Assert.IsType<DeleteModelManagedDataIntent>(operation.Intent);

        Assert.Equal("administrator", intent.OldValues.GetValue(0, 1));
    }

    [Fact]
    public void PairerSourceFreezesNullableColumnsAddedAroundModelManagedUpdates()
    {
        var addColumn = new AddColumnOperation
        {
            Table = "roles",
            Name = "request_id",
            ClrType = typeof(int),
            ColumnType = "int",
            IsNullable = true,
        };

        var update = new UpdateDataOperation
        {
            Table = "roles",
            KeyColumns = ["id"],
            KeyColumnTypes = ["int"],
            KeyValues = new object?[,] { { 1 } },
            Columns = ["name", "request_id"],
            ColumnTypes = ["varchar(64)", "int"],
            Values = new object?[,] { { "owner", null } },
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

        var dropColumn = new DropColumnOperation
        {
            Table = "roles",
            Name = "request_id",
        };

        var forward = SafeMigrationModelManagedDataPairer.Pair(
            [addColumn, update],
            [dropColumn, inverseUpdate]);

        var forwardUpdate = Assert.IsType<UpdateModelManagedDataScaffoldingOperation>(forward[1]);
        var forwardIntent = Assert.IsType<UpdateModelManagedDataIntent>(forwardUpdate.Intent);

        Assert.Equal(["name", "request_id"], forwardIntent.Columns);
        Assert.Equal("administrator", forwardIntent.OldValues.GetValue(0, 0));
        Assert.Null(forwardIntent.OldValues.GetValue(0, 1));
        Assert.Equal("owner", forwardIntent.NewValues.GetValue(0, 0));
        Assert.Null(forwardIntent.NewValues.GetValue(0, 1));

        var inverse = SafeMigrationModelManagedDataPairer.Pair(
            [dropColumn, inverseUpdate],
            [addColumn, update]);

        var inverseResult = Assert.IsType<UpdateModelManagedDataScaffoldingOperation>(inverse[1]);
        var inverseIntent = Assert.IsType<UpdateModelManagedDataIntent>(inverseResult.Intent);

        Assert.Equal(["name"], inverseIntent.Columns);
        Assert.Equal("owner", inverseIntent.OldValues.GetValue(0, 0));
        Assert.Equal("administrator", inverseIntent.NewValues.GetValue(0, 0));
    }

    [Fact]
    public void PairerSourceFreezesExplicitDefaultsForRequiredAddedColumns()
    {
        var addColumn = new AddColumnOperation
        {
            Table = "roles",
            Name = "request_id",
            ClrType = typeof(int),
            ColumnType = "int",
            IsNullable = false,
            DefaultValue = 0,
        };

        var update = new UpdateDataOperation
        {
            Table = "roles",
            KeyColumns = ["id"],
            KeyColumnTypes = ["int"],
            KeyValues = new object?[,] { { 1 } },
            Columns = ["request_id"],
            ColumnTypes = ["int"],
            Values = new object?[,] { { 7 } },
        };

        var inverseUpdate = new UpdateDataOperation
        {
            Table = "roles",
            KeyColumns = ["id"],
            KeyColumnTypes = ["int"],
            KeyValues = new object?[,] { { 1 } },
            Columns = [],
            ColumnTypes = [],
            Values = new object?[1, 0],
        };

        var result = SafeMigrationModelManagedDataPairer.Pair(
            [addColumn, update],
            [inverseUpdate]);

        var pairedUpdate = Assert.IsType<UpdateModelManagedDataScaffoldingOperation>(result[1]);
        var intent = Assert.IsType<UpdateModelManagedDataIntent>(pairedUpdate.Intent);

        Assert.Equal(0, intent.OldValues.GetValue(0, 0));
        Assert.Equal(7, intent.NewValues.GetValue(0, 0));
    }

    [Theory]
    [InlineData((int)AddedColumnInitialValue.Absent)]
    [InlineData((int)AddedColumnInitialValue.OpaqueSql)]
    [InlineData((int)AddedColumnInitialValue.OpaqueComputedSql)]
    [InlineData((int)AddedColumnInitialValue.MissingRequiredValue)]
    [InlineData((int)AddedColumnInitialValue.FollowingAddition)]
    [InlineData((int)AddedColumnInitialValue.InconsistentStoreType)]
    public void PairerRejectsUnprovenInitialValuesForNewModelManagedColumns(
        int initialValueValue
    )
    {
        var initialValue = (AddedColumnInitialValue)initialValueValue;
        var update = new UpdateDataOperation
        {
            Table = "roles",
            KeyColumns = ["id"],
            KeyColumnTypes = ["int"],
            KeyValues = new object?[,] { { 1 } },
            Columns = ["request_id"],
            ColumnTypes = ["int"],
            Values = new object?[,] { { 7 } },
        };

        var inverseUpdate = new UpdateDataOperation
        {
            Table = "roles",
            KeyColumns = ["id"],
            KeyColumnTypes = ["int"],
            KeyValues = new object?[,] { { 1 } },
            Columns = [],
            ColumnTypes = [],
            Values = new object?[1, 0],
        };

        var operations = new List<MigrationOperation>();
        if (initialValue != AddedColumnInitialValue.Absent)
        {
            var addColumn = new AddColumnOperation
            {
                Table = "roles",
                Name = "request_id",
                ClrType = typeof(int),
                ColumnType = initialValue == AddedColumnInitialValue.InconsistentStoreType
                    ? "bigint"
                    : "int",
                IsNullable = false,
                DefaultValueSql = initialValue == AddedColumnInitialValue.OpaqueSql ? "RAND()" : null,
                ComputedColumnSql = initialValue == AddedColumnInitialValue.OpaqueComputedSql
                    ? "id + 1"
                    : null,
            };

            if (initialValue == AddedColumnInitialValue.FollowingAddition)
            {
                operations.Add(update);
                operations.Add(addColumn);
            }
            else
            {
                operations.Add(addColumn);
                operations.Add(update);
            }
        }
        else
        {
            operations.Add(update);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationModelManagedDataPairer.Pair(operations, [inverseUpdate]));

        Assert.Contains("request_id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PairerRejectsInverseOnlyColumnsWithoutAColumnDrop()
    {
        var update = new UpdateDataOperation
        {
            Table = "roles",
            KeyColumns = ["id"],
            KeyColumnTypes = ["int"],
            KeyValues = new object?[,] { { 1 } },
            Columns = ["name"],
            ColumnTypes = ["varchar(64)"],
            Values = new object?[,] { { "administrator" } },
        };

        var inverseUpdate = new UpdateDataOperation
        {
            Table = "roles",
            KeyColumns = ["id"],
            KeyColumnTypes = ["int"],
            KeyValues = new object?[,] { { 1 } },
            Columns = ["name", "request_id"],
            ColumnTypes = ["varchar(64)", "int"],
            Values = new object?[,] { { "owner", null } },
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationModelManagedDataPairer.Pair([update], [inverseUpdate]));

        Assert.Contains("request_id", exception.Message, StringComparison.Ordinal);
        Assert.Contains("column drop", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Rejects each independently insufficient inverse-evidence contract.</summary>
    /// <param name="evidenceCase">The serializable inverse-evidence case identifier.</param>
    [Theory]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    [InlineData("annotated")]
    public void PairerRejectsInvalidInverseEvidence(
        string evidenceCase
    )
    {
        // Arrange
        var insert = new InsertDataOperation
        {
            Table = "roles",
            Columns = ["id"],
            ColumnTypes = ["int"],
            Values = new object?[,] { { 1 } },
        };

        var inverse = CreateRoleDelete();
        MigrationOperation[] inverses = evidenceCase switch
        {
            "missing" => [],
            "ambiguous" => [inverse, inverse],
            "annotated" => [inverse],
            _ => throw new ArgumentOutOfRangeException(nameof(evidenceCase), evidenceCase, "Unknown evidence case."),
        };

        if (evidenceCase == "annotated")
        {
            insert.AddAnnotation("consumer:annotation", true);
        }

        // Act
        var exception = Record.Exception(() => SafeMigrationModelManagedDataPairer.Pair([insert], inverses));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
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

    /// <summary>Prior child writes cannot certify immutable parent dependency counts.</summary>
    /// <param name="dependencyCount">The original number of incoming dependent rows.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void PreflightDefersParentDependenciesAfterChildWrites(long dependencyCount)
    {
        // Arrange
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

        projection.Observe(childDelete, childAnalysis, childAnalysis, childDecision);

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

        // Act
        var projected = projection.Project(
            parentDelete,
            EvidenceAnalysis(
                SafeMigrationObservedState.DataBlocked,
                [SafeMigrationModelManagedRowState.Source],
                [dependencyCount]));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
        Assert.Equal("projected_model_managed_data_state_unknown", projected.Code);
        Assert.True(projected.IsModelManagedProjectionUnknown);
    }

    /// <summary>Decodes all compact row states and nonnegative dependency counts without losing order.</summary>
    [Fact]
    public void CompactEvidenceParserDecodesProviderResults()
    {
        // Arrange
        const string states = "0123";
        const string dependencies = "0,17";

        // Act
        var evidence = SafeMigrationModelManagedDataEvidence.Parse(states, 4, dependencies, 2, "test");

        // Assert
        Assert.Equal(
            [
                SafeMigrationModelManagedRowState.Missing,
                SafeMigrationModelManagedRowState.Source,
                SafeMigrationModelManagedRowState.Target,
                SafeMigrationModelManagedRowState.Different,
            ],
            evidence.RowStates);
        Assert.Equal([0, 17], evidence.DependencyCounts);
    }

    /// <summary>Rejects independent count, row-state and dependency encoding failures.</summary>
    /// <param name="states">The encoded row states.</param>
    /// <param name="rowCount">The expected number of row states.</param>
    /// <param name="dependencies">The encoded dependency counts.</param>
    /// <param name="dependencyCount">The expected number of dependency counts.</param>
    [Theory]
    [InlineData("01", 1, "", 0)]
    [InlineData("4", 1, "", 0)]
    [InlineData("1", 1, "-1", 1)]
    public void CompactEvidenceParserRejectsMalformedProviderResults(
        string states,
        int rowCount,
        string dependencies,
        int dependencyCount
    )
    {
        // Arrange
        const string provider = "test";

        // Act
        var exception = Record.Exception(() => SafeMigrationModelManagedDataEvidence.Parse(
            states, rowCount, dependencies, dependencyCount, provider));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Rejects the same typed key across batches even when key columns are reordered.</summary>
    [Fact]
    public void ContractValidatorRejectsDuplicateTypedKeysAcrossBatchesAndColumnOrder()
    {
        // Arrange
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

        // Act
        var exception = Record.Exception(() =>
            SafeMigrationModelManagedDataContractValidator.Validate([first, duplicate]));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Accepts independent typed keys across separate model-data batches.</summary>
    [Fact]
    public void ContractValidatorAcceptsDistinctTypedKeysAcrossBatches()
    {
        // Arrange
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

        // Act
        var exception = Record.Exception(() => SafeMigrationModelManagedDataContractValidator.Validate(
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
            ]));

        // Assert
        Assert.Null(exception);
    }

    /// <summary>Creates the exact INSERT fixture shared by the independent pairing transitions.</summary>
    private static InsertDataOperation CreateRoleInsert() => new()
    {
        Table = "roles",
        Columns = ["id", "name"],
        ColumnTypes = ["int", "varchar(64)"],
        Values = new object?[,] { { 1, "administrator" } },
    };

    /// <summary>Creates the exact DELETE fixture shared by the independent pairing transitions.</summary>
    private static DeleteDataOperation CreateRoleDelete() => new()
    {
        Table = "roles",
        KeyColumns = ["id"],
        KeyColumnTypes = ["int"],
        KeyValues = new object?[,] { { 1 } },
    };

    /// <summary>Creates one side of the exact UPDATE transition with its selected scalar value.</summary>
    private static UpdateDataOperation CreateRoleUpdate(
        string value
    ) => new()
    {
        Table = "roles",
        KeyColumns = ["id"],
        KeyColumnTypes = ["int"],
        KeyValues = new object?[,] { { 1 } },
        Columns = ["name"],
        ColumnTypes = ["varchar(64)"],
        Values = new object?[,] { { value } },
    };

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

        projection.Observe(operation, analysis, analysis, decision);
    }

    private enum AddedColumnInitialValue
    {
        Absent,
        OpaqueSql,
        OpaqueComputedSql,
        MissingRequiredValue,
        FollowingAddition,
        InconsistentStoreType,
    }
}
