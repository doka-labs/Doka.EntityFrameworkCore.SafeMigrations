namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Only provider-certified unchanged columns survive accepted sibling drops.</summary>
    /// <param name="certified">Whether the provider validated the bounded rebuild contract.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlterAfterSiblingDropsRequiresProviderCertificate(bool certified)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        ObserveAccepted(projection, new DropColumnIntent("legacy", "items"), SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, new DropColumnIntent("temporary", "items"), SafeMigrationObservedState.Matching);
        var operation = UnchangedColumnAlter();
        var live = SiblingDropAlterProof(certified);

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.Equal(certified ? SafeMigrationRepairCapability.Safe : SafeMigrationRepairCapability.None,
            result.RepairCapability);
        if (certified)
        {
            Assert.Same(live, result);
        }
    }

    /// <summary>A certificate cannot restore source provenance or rows after another mutation.</summary>
    /// <param name="mutation">The operation that invalidates bounded sibling-drop reuse.</param>
    [Theory]
    [InlineData("ordinary-drop")]
    [InlineData("ordinary-alter-table")]
    [InlineData("ordinary-before-drop")]
    [InlineData("target-drop")]
    [InlineData("target-alter")]
    [InlineData("target-rename")]
    [InlineData("other-index")]
    [InlineData("data")]
    [InlineData("opaque")]
    [InlineData("type-transition")]
    [InlineData("live-blocked")]
    [InlineData("recreate")]
    [InlineData("incoming-foreign-key")]
    public void AlterAfterSiblingDropsRejectsInvalidatedCertificate(string mutation)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        if (mutation == "ordinary-before-drop")
        {
            projection.ObserveProviderPostcondition(new AlterTableOperation { Name = "items" });
        }

        if (mutation == "incoming-foreign-key")
        {
            Apply(projection, new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
                "fk_child", "child", ["value"], "items", ["value"])));
        }

        if (mutation == "other-index")
        {
            ObserveAccepted(projection, new DropIndexIntent("ix_other", "items"), SafeMigrationObservedState.Matching);
        }

        ObserveAccepted(projection, new DropColumnIntent("legacy", "items"), SafeMigrationObservedState.Matching);
        var operation = UnchangedColumnAlter();
        var live = SiblingDropAlterProof(certified: true);

        switch (mutation)
        {
            case "ordinary-drop":
                projection.ObserveProviderPostcondition(new DropColumnOperation { Table = "items", Name = "other" });
                break;
            case "ordinary-alter-table":
                projection.ObserveProviderPostcondition(new AlterTableOperation { Name = "items" });
                break;
            case "target-drop":
                ObserveAccepted(projection, new DropColumnIntent("value", "items"),
                    SafeMigrationObservedState.Matching);
                break;
            case "target-alter":
                projection.ObserveProviderPostcondition(new AlterColumnOperation
                {
                    Table = "items",
                    Name = "value",
                    ClrType = typeof(int),
                    ColumnType = "int",
                    IsNullable = true,
                });
                break;
            case "target-rename":
                projection.ObserveProviderPostcondition(new RenameColumnOperation
                {
                    Table = "items",
                    Name = "value",
                    NewName = "renamed",
                });
                break;
            case "data":
                projection.ObserveProviderPostcondition(new InsertDataOperation
                {
                    Table = "other",
                    Columns = ["value"],
                    Values = new object?[,] { { 1 } },
                });
                break;
            case "opaque":
                projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1" });
                break;
            case "type-transition":
                operation = new SafeMigrationOperation(
                    new AlterColumnIntent("items", Column("value"),
                        new ExpectedColumnDefinition("value", typeof(string), true, "TEXT")),
                    SafeMigrationPolicy.RepairIfSafe);
                break;
            case "live-blocked":
                live = Live(SafeMigrationObservedState.DataBlocked);
                break;
            case "recreate":
                ObserveAccepted(projection, new DropTableIntent("items"), SafeMigrationObservedState.Matching);
                Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", [Column("other")]),
                    SafeMigrationTableMode.StrictDefinition));
                ObserveAccepted(projection, new DropColumnIntent("other", "items"),
                    SafeMigrationObservedState.Matching);
                break;
        }

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.NotEqual(SafeMigrationRepairCapability.Safe, result.RepairCapability);
    }

    /// <summary>Creates the exact same-shape nullability transition used by the reuse controls.</summary>
    private static SafeMigrationOperation UnchangedColumnAlter() => new(
        new AlterColumnIntent("items", Column("value"),
            new ExpectedColumnDefinition("value", typeof(int), isNullable: true, storeType: "int")),
        SafeMigrationPolicy.RepairIfSafe);

    /// <summary>Models a provider's source-bound repair and independently controlled reuse certificate.</summary>
    private static SafeMigrationProviderAnalysis SiblingDropAlterProof(
        bool certified
    ) => new(
        SafeMigrationObservedState.Different, SafeMigrationRepairCapability.Safe, false,
        "classified_different_repairable")
    {
        CanReuseAfterUnrelatedColumnDrops = certified,
        RequiresLiveDataProof = true,
    };
}
