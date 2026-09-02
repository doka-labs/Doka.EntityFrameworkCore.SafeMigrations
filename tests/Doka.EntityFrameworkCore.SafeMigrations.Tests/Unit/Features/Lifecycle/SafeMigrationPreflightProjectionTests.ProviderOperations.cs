namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    [Fact]
    public void ProviderAddColumnProjectsFollowingNonUniqueIndexPrerequisite()
    {
        var projection = new SafeMigrationPreflightProjection();
        var addColumn = ProviderColumn("customer_id", "shipments", isNullable: false, defaultValue: 0);

        projection.ObserveProviderPostcondition(addColumn);

        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void ProviderAddColumnDoesNotProjectBackwardsInOperationOrder()
    {
        var projection = new SafeMigrationPreflightProjection();
        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false, live);
        projection.ObserveProviderPostcondition(
            ProviderColumn("customer_id", "shipments", isNullable: false, defaultValue: 0));

        Assert.Same(live, analysis);
    }

    [Fact]
    public void ProviderAddColumnInvalidatesCompleteShapeButRetainsExactPrerequisite()
    {
        var projection = new SafeMigrationPreflightProjection();
        var originalTable = new ExpectedTableDefinition("shipments", [Column("id")]);

        Apply(
            projection,
            new EnsureTableIntent(originalTable, SafeMigrationTableMode.StrictDefinition));
        projection.ObserveProviderPostcondition(
            ProviderColumn("customer_id", "shipments", isNullable: false, defaultValue: 0));

        var liveTable = Live(SafeMigrationObservedState.Different);
        var tableAnalysis = projection.Project(
            new SafeMigrationOperation(
                new EnsureTableIntent(originalTable, SafeMigrationTableMode.StrictDefinition),
                SafeMigrationPolicy.ThrowIfDifferent),
            liveTable);

        var indexAnalysis = ProjectIndex(projection, "shipments", "customer_id", unique: false);

        Assert.Same(liveTable, tableAnalysis);
        Assert.Equal(SafeMigrationObservedState.Missing, indexAnalysis.ObservedState);
        Assert.Equal("projected_missing", indexAnalysis.Code);
    }

    [Fact]
    public void OpaqueProviderOperationInvalidatesAllPrerequisiteFacts()
    {
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            ProviderColumn("customer_id", "shipments", isNullable: false, defaultValue: 0));
        projection.ObserveProviderPostcondition(
            new SqlOperation
            {
                Sql = "ALTER TABLE shipments DROP COLUMN customer_id;",
            });

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);
        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false, live);

        Assert.Same(live, analysis);
    }

    [Fact]
    public void OpaqueProviderOperationInvalidatesLiveDataSafety()
    {
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new SqlOperation
            {
                Sql = "INSERT INTO shipments (customer_id) VALUES (7), (7);",
            });

        var analysis = ProjectIndex(
            projection,
            "shipments",
            "customer_id",
            unique: true,
            Live(SafeMigrationObservedState.Missing));

        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_data_state_unknown", analysis.Code);
    }

    [Fact]
    public void ProviderDataOperationsPreserveStructuralFactsForNonUniqueIndex()
    {
        MigrationOperation[] dataOperations =
        [
            new InsertDataOperation
            {
                Table = "shipments",
                Columns = ["id"],
                Values = new object[,] { { 1, }, },
            },
            new UpdateDataOperation
            {
                Table = "shipments",
                KeyColumns = ["id"],
                KeyValues = new object[,] { { 1, }, },
                Columns = ["customer_id"],
                Values = new object[,] { { 7, }, },
            },
            new DeleteDataOperation
            {
                Table = "shipments",
                KeyColumns = ["id"],
                KeyValues = new object[,] { { 1, }, },
            },
        ];

        foreach (var dataOperation in dataOperations)
        {
            var projection = new SafeMigrationPreflightProjection();
            Apply(
                projection,
                new EnsureTableIntent(
                    new ExpectedTableDefinition(
                        "shipments",
                        [Column("id"), Column("customer_id")]),
                    SafeMigrationTableMode.StrictDefinition));

            projection.ObserveProviderPostcondition(dataOperation);

            var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false);

            Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
            Assert.Equal("projected_missing", analysis.Code);
        }
    }

    [Fact]
    public void ProviderDataOperationInvalidatesProjectedUniqueIndexSafety()
    {
        var projection = new SafeMigrationPreflightProjection();
        Apply(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition(
                    "shipments",
                    [Column("id"), Column("customer_id")]),
                SafeMigrationTableMode.StrictDefinition));
        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "shipments",
                Columns = ["id", "customer_id"],
                Values = new object[,] { { 1, 7, }, },
            });

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);
        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: true, live);

        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_data_state_unknown", analysis.Code);
    }

    [Fact]
    public void ProviderDataOperationInvalidatesEveryAdditiveDataSafetyProof()
    {
        var projection = new SafeMigrationPreflightProjection();
        Apply(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition("parents", [Column("id")]),
                SafeMigrationTableMode.StrictDefinition));
        Apply(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition(
                    "shipments",
                    [Column("id"), Column("customer_id")]),
                SafeMigrationTableMode.StrictDefinition));
        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "shipments",
                Columns = ["id", "customer_id"],
                Values = new object[,] { { 1, 7, }, },
            });

        SafeMigrationIntent[] dataDependentOperations =
        [
            new EnsurePrimaryKeyIntent(
                new ExpectedPrimaryKeyDefinition("pk_shipments", "shipments", ["id"])),
            new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition(
                    "uq_shipments_customer_id",
                    "shipments",
                    ["customer_id"])),
            new EnsureCheckConstraintIntent(
                ExpectedCheckConstraintDefinition.FromExpression(
                    "ck_shipments_customer_id",
                    "shipments",
                    SafeMigrationSql.Binary(
                        SafeMigrationSql.Identifier("customer_id"),
                        SafeMigrationSqlBinaryOperator.GreaterThan,
                        SafeMigrationSql.Literal(0)))),
            new EnsureForeignKeyIntent(
                new ExpectedForeignKeyDefinition(
                    "fk_shipments_parents_customer_id",
                    "shipments",
                    ["customer_id"],
                    "parents",
                    ["id"])),
            new EnsureColumnIntent(
                "shipments",
                new ExpectedColumnDefinition(
                    "required_after_seed",
                    typeof(int),
                    isNullable: false,
                    storeType: "int")),
        ];

        foreach (var intent in dataDependentOperations)
        {
            var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
            var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

            var analysis = projection.Project(operation, live);

            Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
            Assert.Equal("projected_data_state_unknown", analysis.Code);
        }
    }

    [Fact]
    public void ProviderDataOperationInvalidatesLiveUniqueIndexSafety()
    {
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "shipments",
                Columns = ["customer_id"],
                Values = new object[,] { { 7, }, },
            });

        var analysis = ProjectIndex(
            projection,
            "shipments",
            "customer_id",
            unique: true,
            Live(SafeMigrationObservedState.Missing));

        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_data_state_unknown", analysis.Code);
    }

    [Fact]
    public void LaterProviderDdlDoesNotEraseEarlierDataUncertainty()
    {
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "shipments",
                Columns = ["id", "customer_id"],
                Values = new object[,] { { 1, 7, }, },
            });
        projection.ObserveProviderPostcondition(
            ProviderColumn("tracking_id", "shipments", isNullable: true));

        var analysis = ProjectIndex(
            projection,
            "shipments",
            "customer_id",
            unique: true,
            Live(SafeMigrationObservedState.Missing));

        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_data_state_unknown", analysis.Code);
    }

    [Fact]
    public void ProviderDataOperationPreservesLiveNonUniqueIndexAnalysis()
    {
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "shipments",
                Columns = ["customer_id"],
                Values = new object[,] { { 7, }, },
            });
        var live = Live(SafeMigrationObservedState.Missing);

        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false, live);

        Assert.Same(live, analysis);
    }

    [Fact]
    public void ProviderDataOperationInvalidatesLiveRequiredColumnSafety()
    {
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "shipments",
                Columns = ["id"],
                Values = new object[,] { { 1, }, },
            });
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent(
                "shipments",
                new ExpectedColumnDefinition(
                    "required_after_seed",
                    typeof(int),
                    isNullable: false,
                    storeType: "int")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Missing));

        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_data_state_unknown", analysis.Code);
    }

    [Fact]
    public void ProviderDataOperationInvalidatesLiveNullabilityTighteningSafety()
    {
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new UpdateDataOperation
            {
                Table = "shipments",
                KeyColumns = ["id"],
                KeyValues = new object[,] { { 1, }, },
                Columns = ["customer_id"],
                Values = new object?[,] { { null, }, },
            });
        var oldColumn = new ExpectedColumnDefinition(
            "customer_id",
            typeof(int),
            isNullable: true,
            storeType: "int");

        var requiredColumn = new ExpectedColumnDefinition(
            "customer_id",
            typeof(int),
            isNullable: false,
            storeType: "int");

        var operation = new SafeMigrationOperation(
            new AlterColumnIntent("shipments", requiredColumn, oldColumn),
            SafeMigrationPolicy.RepairIfSafe);

        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Different));

        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_data_state_unknown", analysis.Code);
    }

    [Fact]
    public void ProviderDataOperationRetainsSafeNullableColumnAddition()
    {
        var projection = new SafeMigrationPreflightProjection();
        Apply(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition("shipments", [Column("id")]),
                SafeMigrationTableMode.StrictDefinition));
        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "shipments",
                Columns = ["id"],
                Values = new object[,] { { 1, }, },
            });

        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent(
                "shipments",
                new ExpectedColumnDefinition(
                    "optional_after_seed",
                    typeof(int),
                    isNullable: true,
                    storeType: "int")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.PrerequisiteMissing));

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void ProviderDataOperationDoesNotTaintLaterCreatedTable()
    {
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "existing_items",
                Columns = ["id"],
                Values = new object[,] { { 1, }, },
            });

        Apply(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition(
                    "shipments",
                    [Column("id"), Column("customer_id")]),
                SafeMigrationTableMode.StrictDefinition));

        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: true);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Theory]
    [InlineData("other_shipments", null, "customer_id")]
    [InlineData("shipments", "tenant", "customer_id")]
    [InlineData("shipments", null, "other_customer_id")]
    public void ProviderAddColumnDoesNotSatisfyDifferentObjectIdentity(
        string providerTable,
        string? providerSchema,
        string providerColumn
    )
    {
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            ProviderColumn(
                providerColumn,
                providerTable,
                isNullable: false,
                defaultValue: 0,
                providerSchema));

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);
        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false, live);

        Assert.Same(live, analysis);
    }

    [Fact]
    public void ProviderDropColumnInvalidatesEarlierProjectedPrerequisite()
    {
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            ProviderColumn("customer_id", "shipments", isNullable: false, defaultValue: 0));

        projection.ObserveProviderPostcondition(
            new DropColumnOperation
            {
                Name = "customer_id",
                Table = "shipments",
            });

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);
        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false, live);

        Assert.Same(live, analysis);
    }

    [Fact]
    public void ProviderRenameSequenceMovesOnlyTheProvenPrerequisite()
    {
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            ProviderColumn("customer_id", "shipments", isNullable: false, defaultValue: 0));
        projection.ObserveProviderPostcondition(
            new RenameColumnOperation
            {
                Name = "customer_id",
                NewName = "account_id",
                Table = "shipments",
            });
        projection.ObserveProviderPostcondition(
            new RenameTableOperation
            {
                Name = "shipments",
                NewName = "deliveries",
            });

        var projected = ProjectIndex(projection, "deliveries", "account_id", unique: false);
        var staleLive = Live(SafeMigrationObservedState.PrerequisiteMissing);
        var stale = ProjectIndex(projection, "shipments", "customer_id", unique: false, staleLive);

        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Same(staleLive, stale);
    }

    [Fact]
    public void ProviderAlterColumnDoesNotInventUniqueSafetyForExistingRows()
    {
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            ProviderColumn("external_id", "items", isNullable: true));

        var initiallySafe = ProjectIndex(projection, "items", "external_id", unique: true);

        projection.ObserveProviderPostcondition(
            new AlterColumnOperation
            {
                Name = "external_id",
                Table = "items",
                ClrType = typeof(int),
                ColumnType = "int",
                IsNullable = false,
                DefaultValue = 0,
            });

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);
        var afterAlter = ProjectIndex(projection, "items", "external_id", unique: true, live);

        Assert.Equal(SafeMigrationObservedState.Missing, initiallySafe.ObservedState);
        Assert.Same(live, afterAlter);
    }

    [Fact]
    public void ProviderCreateAndDropTableProjectAndInvalidateNewTableSafety()
    {
        var projection = new SafeMigrationPreflightProjection();
        var createTable = new CreateTableOperation { Name = "items", };
        createTable.Columns.Add(ProviderColumn("external_id", "items", isNullable: false, defaultValue: 0));

        projection.ObserveProviderPostcondition(createTable);

        var projected = ProjectIndex(projection, "items", "external_id", unique: true);

        projection.ObserveProviderPostcondition(new DropTableOperation { Name = "items", });

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);
        var afterDrop = ProjectIndex(projection, "items", "external_id", unique: true, live);

        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Same(live, afterDrop);
    }

    [Fact]
    public void ProviderDropIndexProjectsExactOrdinaryReplacementAsMissing()
    {
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new DropIndexOperation
            {
                Name = "ix_shipments_customer_id",
                Table = "shipments",
            });

        var analysis = ProjectIndex(
            projection,
            "shipments",
            "customer_id",
            unique: false,
            Live(SafeMigrationObservedState.Different));

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void SafeDropIndexSurvivesOrderedTableAndColumnMetadataAlterations()
    {
        const string indexName = "ix_records_tenant_id_code";
        var projection = new SafeMigrationPreflightProjection();

        ObserveAccepted(
            projection,
            new DropIndexIntent(indexName, "records"),
            SafeMigrationObservedState.Matching);
        projection.ObserveProviderPostcondition(
            new AlterTableOperation
            {
                Name = "records",
                Comment = "target table comment",
                OldTable = new AlterTableOperation
                {
                    Name = "records",
                    Comment = "source table comment",
                },
            });
        projection.ObserveProviderPostcondition(
            new AlterColumnOperation
            {
                Name = "code",
                Table = "records",
                ClrType = typeof(string),
                ColumnType = "varchar(180)",
                IsNullable = false,
                Collation = "utf8mb4_bin",
                Comment = "target code comment",
            });
        projection.ObserveProviderPostcondition(
            new AlterColumnOperation
            {
                Name = "description",
                Table = "records",
                ClrType = typeof(string),
                ColumnType = "varchar(240)",
                IsNullable = false,
                DefaultValue = "target default",
            });

        var replacement = new SafeMigrationOperation(
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    indexName,
                    "records",
                    [
                        new ExpectedIndexKeyDefinition(column: "tenant_id"),
                        new ExpectedIndexKeyDefinition(column: "code", prefixLength: 48),
                    ])),
            SafeMigrationPolicy.ThrowIfDifferent);

        var analysis = projection.Project(
            replacement,
            Live(SafeMigrationObservedState.Different));

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void SafeDropIndexDoesNotSurviveAnOpaqueProviderOperation()
    {
        const string indexName = "ix_shipments_customer_id";
        var projection = new SafeMigrationPreflightProjection();

        ObserveAccepted(
            projection,
            new DropIndexIntent(indexName, "shipments"),
            SafeMigrationObservedState.Matching);
        projection.ObserveProviderPostcondition(
            new SqlOperation
            {
                Sql = "ALTER TABLE shipments ADD INDEX ix_unknown (customer_id);",
            });

        var live = Live(SafeMigrationObservedState.Different);

        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false, live);

        Assert.Same(live, analysis);
    }

    [Theory]
    [InlineData(SafeMigrationObservedState.DataBlocked)]
    [InlineData(SafeMigrationObservedState.PrerequisiteMissing)]
    [InlineData(SafeMigrationObservedState.Unsupported)]
    public void ProviderDropIndexDoesNotOverrideUnsafeReplacementAnalysis(
        SafeMigrationObservedState state
    )
    {
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new DropIndexOperation
            {
                Name = "ix_shipments_customer_id",
                Table = "shipments",
            });
        var live = Live(state);

        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: true, live);

        Assert.Same(live, analysis);
    }

    [Fact]
    public void ProviderDropIndexProjectsAnExactNameReplacementAfterSemanticAliasAnalysis()
    {
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new DropIndexOperation
            {
                Name = "ix_shipments_customer_id",
                Table = "shipments",
            });
        var live = Live(SafeMigrationObservedState.Different);

        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false, live);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void ProviderDropIndexPreservesDuplicateRowEvidenceForUniqueReplacement()
    {
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new DropIndexOperation
            {
                Name = "ix_shipments_customer_id",
                Table = "shipments",
            });
        var live = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None,
            postconditionSatisfied: false,
            "index_replacement_data_blocked");

        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: true, live);

        Assert.Equal(SafeMigrationObservedState.DataBlocked, analysis.ObservedState);
        Assert.Equal("index_replacement_data_blocked", analysis.Code);
    }

    [Fact]
    public void ProviderDropIndexDoesNotProjectASeparateIndexIdentity()
    {
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new DropIndexOperation
            {
                Name = "ix_shipments_other",
                Table = "shipments",
            });
        var live = Live(SafeMigrationObservedState.Different);

        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false, live);

        Assert.Same(live, analysis);
    }

    [Fact]
    public void TablelessProviderDropIndexFailsClosedWithoutAnOwnershipProjection()
    {
        var projection = new SafeMigrationPreflightProjection();
        projection.ObserveProviderPostcondition(
            new DropIndexOperation
            {
                Name = "ix_shipments_customer_id",
                Table = "shipments",
            });
        projection.ObserveProviderPostcondition(
            new DropIndexOperation
            {
                Name = "provider_global_index",
                Table = null,
            });
        var live = Live(SafeMigrationObservedState.Different);

        var analysis = ProjectIndex(projection, "shipments", "customer_id", unique: false, live);

        Assert.Same(live, analysis);
    }

    private static AddColumnOperation ProviderColumn(
        string name,
        string table,
        bool isNullable,
        object? defaultValue = null,
        string? schema = null
    ) => new()
    {
        Name = name,
        Table = table,
        Schema = schema,
        ClrType = typeof(int),
        ColumnType = "int",
        IsNullable = isNullable,
        DefaultValue = defaultValue,
    };

    private static SafeMigrationProviderAnalysis ProjectIndex(
        SafeMigrationPreflightProjection projection,
        string table,
        string column,
        bool unique,
        SafeMigrationProviderAnalysis? live = null
    )
    {
        var operation = new SafeMigrationOperation(
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    $"ix_{table}_{column}",
                    table,
                    [new ExpectedIndexKeyDefinition(column: column)],
                    unique: unique)),
            SafeMigrationPolicy.ThrowIfDifferent);

        return projection.Project(operation, live ?? Live(SafeMigrationObservedState.PrerequisiteMissing));
    }
}
