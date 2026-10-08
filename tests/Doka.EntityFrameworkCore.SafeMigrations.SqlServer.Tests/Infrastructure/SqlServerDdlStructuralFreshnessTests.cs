namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies that arbitrary DDL-trigger effects cannot retain captured structural certificates.</summary>
public sealed class SqlServerDdlStructuralFreshnessTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=ddl_structure_freshness;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>A captured old dependency's accepted drop cannot prove no new trigger-created dependency exists.</summary>
    /// <param name="riskValue">The captured absence, enabled-trigger, or unproved visibility verdict.</param>
    /// <param name="executes">Whether the old dependency is physically dropped rather than absent.</param>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void DroppedCapturedIndex_RequiresCurrentStructuralProof(int riskValue, bool executes)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        using var analyzer = Analyzer(context);
        var widening = new SafeMigrationOperation(new AlterColumnIntent("values",
            new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("Value", typeof(int), false, "int")), SafeMigrationPolicy.RepairIfSafe);

        var safe = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe, false, "captured_safe");

        analyzer.CaptureProjectedIntegerWidening(widening, safe,
            [new SqlServerSafeMigrationProviderAnalyzer.TransitionDependency("index", "dbo", "values", "IX_old")]);
        analyzer.CaptureProjectedDdlRowEffects((SqlServerDdlRowEffectRisk)riskValue);
        analyzer.SetCurrentOperationOrdinal(7);
        var drop = new SafeMigrationOperation(new DropIndexIntent("IX_old", "values"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var state = Analysis(executes ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Missing);
        var different = Analysis(SafeMigrationObservedState.Different);

        // Act
        analyzer.ObserveAcceptedOperation(drop, state, state,
            SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, state.ObservedState, drop.Policy));
        analyzer.SetCurrentOperationOrdinal(8);
        var result = analyzer.ValidateProjectedOperation(widening, different, new EmptyColumns());

        // Assert
        if (riskValue != 0 && executes)
        {
            Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, result.ObservedState);
            Assert.Equal(SafeMigrationRepairCapability.None, result.RepairCapability);
            Assert.Equal(7, result.ProviderDeferredOriginOrdinal);
            Assert.Equal(riskValue == 1 ? "projected_ddl_trigger_structure_unknown"
                : "projected_ddl_visibility_structure_unknown", result.Code);
        }
        else
        {
            Assert.Equal(executes ? SafeMigrationRepairCapability.Safe : SafeMigrationRepairCapability.None,
                result.RepairCapability);
            Assert.Null(result.ProviderDeferredOriginOrdinal);
        }
    }

    /// <summary>Metadata Matching is stale after a trigger-capable drop, even for another table and nonunique index.</summary>
    [Fact]
    public void CoreMatchingShortcut_CannotRetainTriggerMutableMetadata()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        using var analyzer = Analyzer(context);
        analyzer.CaptureProjectedDdlRowEffects(SqlServerDdlRowEffectRisk.EnabledTrigger);
        var projection = new SafeMigrationPreflightProjection(providerOperationProjection: analyzer,
            projectedDependencyAnalyzer: analyzer) { CurrentOperationOrdinal = 3 };

        var drop = new SafeMigrationOperation(new DropIndexIntent("IX_old", "source"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var index = new SafeMigrationOperation(new EnsureIndexIntent(
            new ExpectedIndexDefinition("IX_existing", "other", [new ExpectedIndexKeyDefinition("Value")])),
            SafeMigrationPolicy.ThrowIfDifferent);

        var matching = Analysis(SafeMigrationObservedState.Matching);

        // Act
        projection.Observe(drop, matching, matching,
            SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, matching.ObservedState, drop.Policy));
        projection.CurrentOperationOrdinal = 4;
        var result = projection.Project(index, matching);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, result.ObservedState);
        Assert.Equal("projected_ddl_trigger_structure_unknown", result.Code);
        Assert.Equal(3, result.ProviderDeferredOriginOrdinal);
        Assert.False(result.PostconditionSatisfied);
    }

    /// <summary>An immutable permission refusal cannot be hidden by arbitrary trigger-induced uncertainty.</summary>
    [Fact]
    public void StructuralUncertainty_PreservesInvariantPermissionRefusal()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        using var analyzer = Analyzer(context);
        analyzer.CaptureProjectedDdlRowEffects(SqlServerDdlRowEffectRisk.EnabledTrigger);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: analyzer)
        {
            CurrentOperationOrdinal = 1,
        };

        var drop = new SafeMigrationOperation(new DropIndexIntent("IX_old", "source"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var matching = Analysis(SafeMigrationObservedState.Matching);
        var target = new SafeMigrationOperation(new EnsureColumnIntent("other",
            new ExpectedColumnDefinition("Value", typeof(int), false, "int")), SafeMigrationPolicy.ThrowIfDifferent);

        var rejected = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Unsupported,
            SafeMigrationRepairCapability.None, false, "column_alter_write_permission")
        {
            IsInvariantUnsupported = true,
        };

        // Act
        projection.Observe(drop, matching, matching,
            SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, matching.ObservedState, drop.Policy));
        var result = projection.Project(target, rejected);

        // Assert
        Assert.Same(rejected, result);
        Assert.Null(result.ProviderDeferredOriginOrdinal);
    }

    /// <summary>A dropped owner may be recreated by its DDL trigger and cannot retain a neutral absence shortcut.</summary>
    [Fact]
    public void CoreRemovedOwnerShortcut_DefersToActualTriggerResult()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        using var analyzer = Analyzer(context);
        analyzer.CaptureProjectedDdlRowEffects(SqlServerDdlRowEffectRisk.EnabledTrigger);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: analyzer)
        {
            CurrentOperationOrdinal = 5,
        };

        var drop = new SafeMigrationOperation(new DropTableIntent("source"), SafeMigrationPolicy.ThrowIfDifferent);
        var column = new SafeMigrationOperation(new EnsureColumnIntent("source",
            new ExpectedColumnDefinition("Value", typeof(int), true, "int")), SafeMigrationPolicy.ThrowIfDifferent);

        var matching = Analysis(SafeMigrationObservedState.Matching);

        // Act
        projection.Observe(drop, matching, matching,
            SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, matching.ObservedState, drop.Policy));
        projection.CurrentOperationOrdinal = 6;
        var result = projection.Project(column, matching);

        // Assert
        Assert.Equal("projected_ddl_trigger_structure_unknown", result.Code);
        Assert.Equal(5, result.ProviderDeferredOriginOrdinal);
        Assert.False(result.PostconditionSatisfied);
    }

    /// <summary>Different or absent captured metadata is mutable, not an independent invariant authoring refusal.</summary>
    /// <param name="state">The initial metadata verdict that a prior DDL trigger could change.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Missing)]
    [InlineData(SafeMigrationObservedState.PrerequisiteMissing)]
    [InlineData(SafeMigrationObservedState.Different)]
    [InlineData(SafeMigrationObservedState.DataBlocked)]
    public void MutableMetadataRefusal_DoesNotPretendToPredictTriggerOutcome(SafeMigrationObservedState state)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        using var analyzer = Analyzer(context);
        analyzer.CaptureProjectedDdlRowEffects(SqlServerDdlRowEffectRisk.VisibilityUnproven);
        analyzer.SetCurrentOperationOrdinal(2);
        var drop = new SafeMigrationOperation(new DropIndexIntent("IX_old", "source"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var matching = Analysis(SafeMigrationObservedState.Matching);
        var target = new SafeMigrationOperation(new EnsureIndexIntent(
            new ExpectedIndexDefinition("IX_target", "other", [new ExpectedIndexKeyDefinition("Value")])),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        analyzer.ObserveAcceptedOperation(drop, matching, matching,
            SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, matching.ObservedState, drop.Policy));
        var result = analyzer.ValidateProjectedOperation(target, Analysis(state), new EmptyColumns());

        // Assert
        Assert.Equal("projected_ddl_visibility_structure_unknown", result.Code);
        Assert.Equal(2, result.ProviderDeferredOriginOrdinal);
        Assert.Equal(SafeMigrationRepairCapability.None, result.RepairCapability);
    }

    /// <summary>Creates an analyzer from the provider's registered mapping and identifier services.</summary>
    private static SqlServerSafeMigrationProviderAnalyzer Analyzer(DbContext context)
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    /// <summary>Creates immutable matching, missing, or different live observations.</summary>
    private static SafeMigrationProviderAnalysis Analysis(SafeMigrationObservedState state)
        => new(state, SafeMigrationRepairCapability.None, state == SafeMigrationObservedState.Matching,
            "classified_state");

    private sealed class EmptyColumns : ISafeMigrationProjectedColumnSource
    {
        /// <inheritdoc />
        public bool TryGetProjectedColumn(string table, string? schema, string column,
            [NotNullWhen(true)] out ExpectedColumnDefinition? definition)
        {
            definition = null;

            return false;
        }
    }
}
