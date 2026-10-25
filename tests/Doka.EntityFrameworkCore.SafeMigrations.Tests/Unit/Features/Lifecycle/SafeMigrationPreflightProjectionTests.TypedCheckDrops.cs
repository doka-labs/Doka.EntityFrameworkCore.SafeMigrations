namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>A typed CHECK removal changes only its bound inventory and retains unrelated column metadata.</summary>
    [Fact]
    public void TypedCheckDropRetainsSourceAndRemovesOnlyOwnedInventory()
    {
        // Arrange
        var observer = new TypedCheckDropDependencyObserver();
        var projection = TypedCheckDropProjection(observer);
        var drop = new DropCheckConstraintOperation { Name = "ck_value", Table = "items", };
        var rename = new SafeMigrationOperation(new RenameColumnIntent("value", "items", "score"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        projection.ObserveProviderPostcondition(drop);
        var renameAnalysis = projection.Project(rename, Live(SafeMigrationObservedState.Matching));
        var local = projection.Project(new SafeMigrationOperation(new DropCheckConstraintIntent("ck_value", "items"),
            SafeMigrationPolicy.ThrowIfDifferent), Live(SafeMigrationObservedState.Matching));

        var unrelated = projection.Project(new SafeMigrationOperation(
            new DropCheckConstraintIntent("ck_value", "other"), SafeMigrationPolicy.ThrowIfDifferent),
            Live(SafeMigrationObservedState.Matching));

        var hasColumn = ((ISafeMigrationProjectedColumnSource)projection)
            .TryGetProjectedColumn("items", null, "value", out var column);

        // Assert
        Assert.Same(drop, observer.Observed);
        Assert.Equal(SafeMigrationObservedState.Matching, renameAnalysis.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, local.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, unrelated.ObservedState);
        Assert.True(hasColumn);
        Assert.Equal("value", column!.Name);
    }

    /// <summary>Unknown owners and provider annotations cannot acquire a neutral table-preservation proof.</summary>
    /// <param name="unknownOwner">Whether the typed operation lacks an owning table.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedCheckDropWithUnprovenContractRemainsOpaque(bool unknownOwner)
    {
        // Arrange
        var observer = new TypedCheckDropDependencyObserver();
        var projection = TypedCheckDropProjection(observer);
        var drop = new DropCheckConstraintOperation { Name = "ck_value", Table = unknownOwner ? null! : "items", };
        if (!unknownOwner)
        {
            drop.AddAnnotation("Unproven:ProviderEffect", "opaque");
        }

        var operation = new SafeMigrationOperation(new RenameColumnIntent("value", "items", "score"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        projection.ObserveProviderPostcondition(drop);
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Same(drop, observer.Observed);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
    }

    /// <summary>
    /// The provider's DDL-trigger rejection remains authoritative after a precisely typed CHECK drop.
    /// </summary>
    [Fact]
    public void TypedCheckDropCannotBypassProviderSideEffectQualification()
    {
        // Arrange
        var observer = new TypedCheckDropDependencyObserver { RejectAfterDrop = true, };
        var projection = TypedCheckDropProjection(observer);
        var drop = new DropCheckConstraintOperation { Name = "ck_value", Table = "items", };
        var operation = new SafeMigrationOperation(new RenameColumnIntent("value", "items", "score"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        projection.ObserveProviderPostcondition(drop);
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Same(drop, observer.Observed);
        Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState);
        Assert.Equal("ddl_trigger_unproven", analysis.Code);
    }

    private static SafeMigrationPreflightProjection TypedCheckDropProjection(TypedCheckDropDependencyObserver observer)
    {
        var projection = new SafeMigrationPreflightProjection(new RenameAbsenceProjection(),
            projectedDependencyAnalyzer: observer);

        foreach (var table in new[] { "items", "other" })
        {
            var check = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_value", table, "value >= 0", null);
            Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition(table, [Column("value")],
                checkConstraints: [check]), SafeMigrationTableMode.StrictDefinition));
        }

        return projection;
    }

    private sealed class TypedCheckDropDependencyObserver : ISafeMigrationProjectedDependencyAnalyzer
    {
        public MigrationOperation? Observed { get; private set; }

        public bool RejectAfterDrop { get; init; }

        public SafeMigrationProviderAnalysis ValidateProjectedOperation(SafeMigrationOperation operation,
            SafeMigrationProviderAnalysis projectedAnalysis, ISafeMigrationProjectedColumnSource columns)
            => RejectAfterDrop && Observed is DropCheckConstraintOperation
                ? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Unsupported,
                    SafeMigrationRepairCapability.None, false, "ddl_trigger_unproven")
                : projectedAnalysis;

        public void ObserveAcceptedOperation(
            SafeMigrationOperation operation, SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis analysis, SafeMigrationDecision decision)
        {
        }

        public void ObserveProviderOperation(MigrationOperation operation) => Observed = operation;
    }
}
