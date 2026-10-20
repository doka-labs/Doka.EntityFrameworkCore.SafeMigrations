namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Guarded constraint removal cannot change the captured set of generated column members.</summary>
    /// <param name="kind">The exact safe constraint-removal operation.</param>
    [Theory]
    [InlineData("check")]
    [InlineData("primary")]
    [InlineData("unique")]
    [InlineData("foreign")]
    public void NativeGeneratedSnapshotSurvivesAcceptedConstraintRemovalPrefix(string kind)
    {
        // Arrange
        var projection = GeneratedSnapshotProjection();
        var removal = ObserveSnapshotOperation(projection, SnapshotConstraintRemoval(kind),
            SafeMigrationObservedState.Matching);

        // Act
        var analysis = ObserveNativeGeneratedDrop(projection);
        var sourceConfirmation = ObserveSnapshotOperation(projection, new EnsureColumnIntent("items", Column("value")),
            SafeMigrationObservedState.Matching);

        var dependency = ((ISafeMigrationProjectedTableSource)projection).HasProjectedGeneratedColumnDependency(
            "items", null, "value", StringComparer.Ordinal);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, removal.Action);
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, sourceConfirmation.Action);
        Assert.False(dependency);
        Assert.True(((ISafeMigrationProjectedTableSource)projection).TryGetProjectedTableState(
            "items", null, out var state));
        Assert.True(state.HasUnknownStructure);
        Assert.False(state.IsNewlyCreated);
    }

    /// <summary>A missing constraint emits no DDL and leaves the native generated snapshot unchanged.</summary>
    /// <param name="kind">The exact safe constraint-removal operation.</param>
    [Theory]
    [InlineData("check")]
    [InlineData("primary")]
    [InlineData("unique")]
    [InlineData("foreign")]
    public void MissingConstraintRemovalDoesNotInvalidateNativeGeneratedSnapshot(string kind)
    {
        // Arrange
        var projection = GeneratedSnapshotProjection();
        var removal = ObserveSnapshotOperation(projection, SnapshotConstraintRemoval(kind),
            SafeMigrationObservedState.Missing);

        // Act
        _ = ObserveNativeGeneratedDrop(projection);
        var dependency = ((ISafeMigrationProjectedTableSource)projection).HasProjectedGeneratedColumnDependency(
            "items", null, "value", StringComparer.Ordinal);

        // Assert
        Assert.Equal(SafeMigrationAction.NoOp, removal.Action);
        Assert.False(dependency);
    }

    /// <summary>Only current unannotated accepted removals can retain a previously fresh generated snapshot.</summary>
    /// <param name="kind">The exact safe constraint-removal operation.</param>
    /// <param name="boundary">The independent reason the old native snapshot cannot be restored.</param>
    [Theory]
    [InlineData("check", "annotated")]
    [InlineData("primary", "annotated")]
    [InlineData("unique", "annotated")]
    [InlineData("foreign", "annotated")]
    [InlineData("check", "rejected")]
    [InlineData("primary", "rejected")]
    [InlineData("unique", "rejected")]
    [InlineData("foreign", "rejected")]
    [InlineData("check", "generated_add")]
    [InlineData("primary", "generated_add")]
    [InlineData("unique", "generated_add")]
    [InlineData("foreign", "generated_add")]
    public void ConstraintRemovalCannotReviveExpiredNativeGeneratedSnapshot(string kind, string boundary)
    {
        // Arrange
        var projection = GeneratedSnapshotProjection();
        if (boundary != "annotated")
        {
            Apply(projection, new EnsureColumnIntent("items", GeneratedDependencyColumn(
                SafeMigrationSql.Identifier("other"))));
        }

        var removal = ObserveSnapshotOperation(projection, SnapshotConstraintRemoval(kind),
            boundary == "rejected" ? SafeMigrationObservedState.Unsupported : SafeMigrationObservedState.Matching,
            annotated: boundary == "annotated");

        // Act
        _ = ObserveNativeGeneratedDrop(projection);
        var dependency = ((ISafeMigrationProjectedTableSource)projection).HasProjectedGeneratedColumnDependency(
            "items", null, "value", StringComparer.Ordinal);

        // Assert
        Assert.Equal(boundary == "rejected" ? SafeMigrationAction.RejectUnsupported : SafeMigrationAction.Apply,
            removal.Action);
        Assert.Null(dependency);
    }

    /// <summary>A guarded constraint removal never bypasses the provider's independent trigger qualification.</summary>
    /// <param name="kind">The exact safe constraint-removal operation.</param>
    [Theory]
    [InlineData("check")]
    [InlineData("primary")]
    [InlineData("unique")]
    [InlineData("foreign")]
    public void ConstraintRemovalCannotBypassNativeGeneratedSnapshotSideEffectGuard(string kind)
    {
        // Arrange
        var guard = new GeneratedSnapshotSideEffectGuard();
        var projection = GeneratedSnapshotProjection(guard);
        var removal = ObserveSnapshotOperation(projection, SnapshotConstraintRemoval(kind),
            SafeMigrationObservedState.Matching);

        // Act
        var analysis = ObserveNativeGeneratedDrop(projection);
        var dependency = ((ISafeMigrationProjectedTableSource)projection).HasProjectedGeneratedColumnDependency(
            "items", null, "value", StringComparer.Ordinal);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, removal.Action);
        Assert.True(guard.SawAcceptedRemoval);
        Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState);
        Assert.Equal("ddl_trigger_unproven", analysis.Code);
        Assert.Null(dependency);
    }

    /// <summary>Accepted index removal preserves only native generated-member snapshot freshness.</summary>
    /// <param name="twoIndexes">Whether two independently accepted index removals precede the native drop.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeGeneratedSnapshotSurvivesOnlyAcceptedIndexRemovalPrefix(bool twoIndexes)
    {
        // Arrange
        var projection = GeneratedSnapshotProjection();
        ObserveAccepted(projection, new DropIndexIntent("ix_value", "items"), SafeMigrationObservedState.Matching);
        if (twoIndexes)
        {
            ObserveAccepted(projection, new DropIndexIntent("ix_other", "items"), SafeMigrationObservedState.Matching);
        }

        // Act
        var analysis = ObserveNativeGeneratedDrop(projection);
        var dependency = ((ISafeMigrationProjectedTableSource)projection).HasProjectedGeneratedColumnDependency(
            "items", null, "value", StringComparer.Ordinal);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.False(dependency);
        Assert.True(((ISafeMigrationProjectedTableSource)projection).TryGetProjectedTableState(
            "items", null, out var state));
        Assert.True(state.HasUnknownStructure);
        Assert.False(state.IsNewlyCreated);
    }

    /// <summary>A non-index mutation cannot revive a stale generated snapshot through later index removal.</summary>
    /// <param name="boundary">The independently invalidating earlier mutation.</param>
    [Theory]
    [InlineData("generated_add")]
    [InlineData("column_add")]
    [InlineData("generated_alter")]
    [InlineData("earlier_column_drop")]
    [InlineData("index_recreate")]
    [InlineData("opaque_sql")]
    [InlineData("unsafe_provider")]
    [InlineData("owner_drop")]
    [InlineData("annotated_safe_index")]
    [InlineData("ordinary_index")]
    [InlineData("unknown_index_owner")]
    [InlineData("annotated_ordinary_index")]
    public void IndexRemovalCannotReviveExpiredNativeGeneratedSnapshot(string boundary)
    {
        // Arrange
        var projection = GeneratedSnapshotProjection();
        ObserveAccepted(projection, new DropIndexIntent("ix_value", "items"), SafeMigrationObservedState.Matching);
        switch (boundary)
        {
            case "generated_add":
                Apply(projection, new EnsureColumnIntent("items", GeneratedDependencyColumn(
                    SafeMigrationSql.Identifier("other"))));
                break;
            case "column_add":
                Apply(projection, new EnsureColumnIntent("items", Column("added")));
                break;
            case "generated_alter":
                projection.ObserveProviderPostcondition(new AlterColumnOperation
                {
                    Table = "items", Name = "derived", ClrType = typeof(int), ColumnType = "int",
                    ComputedColumnSql = "other + 1",
                });
                break;
            case "earlier_column_drop":
                ObserveAccepted(projection, new DropColumnIntent("retired", "items"),
                    SafeMigrationObservedState.Matching);
                break;
            case "index_recreate":
                Apply(projection, new EnsureIndexIntent(new ExpectedIndexDefinition("ix_value", "items",
                    [new ExpectedIndexKeyDefinition(column: "value")])));
                break;
            case "opaque_sql":
                projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1;", });
                break;
            case "unsafe_provider":
                projection.ObserveProviderPostcondition(new AlterTableOperation { Name = "items", });
                break;
            case "owner_drop":
                ObserveAccepted(projection, new DropTableIntent("items"), SafeMigrationObservedState.Matching);
                break;
            case "ordinary_index":
            case "unknown_index_owner":
            case "annotated_ordinary_index":
                var providerDrop = new DropIndexOperation
                {
                    Name = "ix_other", Table = boundary == "unknown_index_owner" ? null : "items",
                };

                if (boundary == "annotated_ordinary_index")
                {
                    providerDrop.AddAnnotation("Test:UnknownNativeEffect", true);
                }

                projection.ObserveProviderPostcondition(providerDrop);
                break;
            case "annotated_safe_index":
                var operation = new SafeMigrationOperation(new DropIndexIntent("ix_other", "items"),
                    SafeMigrationPolicy.ThrowIfDifferent);

                operation.AddAnnotation("Test:UnknownNativeEffect", true);
                var live = Live(SafeMigrationObservedState.Matching);
                var analysis = projection.Project(operation, live);
                var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
                    operation.Policy, analysis.RepairCapability);

                projection.Observe(operation, live, analysis, decision);
                break;
        }

        if (boundary is "generated_add" or "column_add" or "index_recreate" or "annotated_safe_index")
        {
            ObserveAccepted(projection, new DropIndexIntent("ix_after_boundary", "items"),
                SafeMigrationObservedState.Matching);
        }

        // Act
        _ = ObserveNativeGeneratedDrop(projection);
        var dependency = ((ISafeMigrationProjectedTableSource)projection).HasProjectedGeneratedColumnDependency(
            "items", null, "value", StringComparer.Ordinal);

        // Assert
        Assert.Null(dependency);
    }

    /// <summary>The provider's DDL side-effect guard remains authoritative after accepted index removal.</summary>
    [Fact]
    public void IndexRemovalCannotBypassNativeGeneratedSnapshotSideEffectGuard()
    {
        // Arrange
        var guard = new GeneratedSnapshotSideEffectGuard();
        var projection = GeneratedSnapshotProjection(guard);
        ObserveAccepted(projection, new DropIndexIntent("ix_value", "items"), SafeMigrationObservedState.Matching);

        // Act
        var analysis = ObserveNativeGeneratedDrop(projection);
        var dependency = ((ISafeMigrationProjectedTableSource)projection).HasProjectedGeneratedColumnDependency(
            "items", null, "value", StringComparer.Ordinal);

        // Assert
        Assert.True(guard.SawAcceptedRemoval);
        Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState);
        Assert.Equal("ddl_trigger_unproven", analysis.Code);
        Assert.Null(dependency);
    }

    private static SafeMigrationPreflightProjection GeneratedSnapshotProjection(
        ISafeMigrationProjectedDependencyAnalyzer? guard = null
    )
    {
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: guard);
        ObserveAccepted(projection, new EnsureColumnIntent("items", Column("value")),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(projection, new EnsureColumnIntent("items", Column("other")),
            SafeMigrationObservedState.Matching);

        return projection;
    }

    private static SafeMigrationProviderAnalysis ObserveNativeGeneratedDrop(SafeMigrationPreflightProjection projection)
    {
        var operation = new SafeMigrationOperation(new DropColumnIntent("removed_generated", "items"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = WithEmptyGeneratedSurvivors(Live(SafeMigrationObservedState.Matching));
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy, analysis.RepairCapability);

        projection.Observe(operation, live, analysis, decision);

        return analysis;
    }

    private static SafeMigrationIntent SnapshotConstraintRemoval(string kind) => kind switch
    {
        "check" => new DropCheckConstraintIntent("ck_value", "items"),
        "primary" => new DropPrimaryKeyIntent("pk_items", "items"),
        "unique" => new DropUniqueConstraintIntent("uq_value", "items"),
        "foreign" => new DropForeignKeyIntent("fk_value", "items"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static SafeMigrationDecision ObserveSnapshotOperation(
        SafeMigrationPreflightProjection projection,
        SafeMigrationIntent intent,
        SafeMigrationObservedState observedState,
        bool annotated = false
    )
    {
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        if (annotated)
        {
            operation.AddAnnotation("Test:UnknownNativeEffect", true);
        }

        var live = Live(observedState);
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(intent.Kind, analysis.ObservedState,
            operation.Policy, analysis.RepairCapability);

        projection.Observe(operation, live, analysis, decision);

        return decision;
    }

    private sealed class GeneratedSnapshotSideEffectGuard : ISafeMigrationProjectedDependencyAnalyzer
    {
        public bool SawAcceptedRemoval { get; private set; }

        public SafeMigrationProviderAnalysis? ValidateCapturedProjection(SafeMigrationOperation operation,
            SafeMigrationProviderAnalysis liveAnalysis, ISafeMigrationProjectedColumnSource columns)
            => SawAcceptedRemoval && operation.Intent is DropColumnIntent
                ? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Unsupported,
                    SafeMigrationRepairCapability.None, false, "ddl_trigger_unproven")
                : null;

        public SafeMigrationProviderAnalysis ValidateProjectedOperation(SafeMigrationOperation operation,
            SafeMigrationProviderAnalysis projectedAnalysis, ISafeMigrationProjectedColumnSource columns)
            => projectedAnalysis;

        public void ObserveAcceptedOperation(SafeMigrationOperation operation,
            SafeMigrationProviderAnalysis liveAnalysis, SafeMigrationProviderAnalysis analysis,
            SafeMigrationDecision decision)
        {
            SawAcceptedRemoval |= operation.Intent is DropIndexIntent or DropCheckConstraintIntent
                or DropPrimaryKeyIntent or DropUniqueConstraintIntent or DropForeignKeyIntent
                && decision.Action == SafeMigrationAction.Apply;
        }

        public void ObserveProviderOperation(MigrationOperation operation)
        {
        }
    }
}
