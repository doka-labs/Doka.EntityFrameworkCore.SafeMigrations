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
