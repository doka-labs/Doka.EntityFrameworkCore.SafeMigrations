namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Checks stress instrumentation against fixed reports independent of production projection.</summary>
public sealed class LargeMigrationStressExpectationTests
{
    /// <summary>Accepts only the expected uncertainty after the first model-managed ensure.</summary>
    [Fact]
    public void AssertReport_AcceptsIndependentEnsureFirstTwoCycleReport()
    {
        // Arrange
        var expectation = new LargeMigrationStressExpectation(CreateScenarios(), operationCount: 20);
        var report = CreateReport(CreateGoldenAssessments());

        // Act
        var exception = Record.Exception(() => expectation.AssertReport(report));

        // Assert
        Assert.Null(exception);
    }

    /// <summary>Preserves the first source-state update before later model-managed proofs become unknown.</summary>
    [Fact]
    public void AssertReport_AcceptsIndependentUpdateFirstTwoCycleReport()
    {
        // Arrange
        var expectation = new LargeMigrationStressExpectation(CreateScenarios(updateFirst: true), operationCount: 20);
        var report = CreateReport(CreateUpdateFirstGoldenAssessments());

        // Act
        var exception = Record.Exception(() => expectation.AssertReport(report));

        // Assert
        Assert.Null(exception);
    }

    /// <summary>A prior managed write invalidates later live-data proof without inventing a new data state.</summary>
    /// <param name="scenarioOrdinal">The repeating source scenario requiring live-data proof.</param>
    /// <param name="repeatedOrdinal">The fixed second-cycle assessment after the managed write.</param>
    /// <param name="objectName">The independently fixed column identity.</param>
    [Theory]
    [InlineData(2, 12, "different_column")]
    [InlineData(4, 14, "blocked_column")]
    public void AssertReport_AcceptsIndependentLiveDataProofInvalidation(
        int scenarioOrdinal,
        int repeatedOrdinal,
        string objectName
    )
    {
        // Arrange
        var scenarios = CreateScenarios().ToArray();

        scenarios[scenarioOrdinal] = scenarios[scenarioOrdinal] with { RequiresLiveDataProof = true };

        var expectation = new LargeMigrationStressExpectation(scenarios, operationCount: 20);
        var assessments = CreateGoldenAssessments();

        // WHY: The first-cycle blocker precedes ordinal 7's write and remains
        // proven; only its fixed second-cycle repetition loses the data proof.
        assessments[repeatedOrdinal] = ProvenAssessment(
            repeatedOrdinal,
            SafeMigrationOperationKind.EnsureColumn,
            objectName,
            SafeMigrationObservedState.PrerequisiteMissing,
            SafeMigrationAction.RejectPrerequisiteMissing);

        var report = CreateReport(assessments);

        // Act
        var exception = Record.Exception(() => expectation.AssertReport(report));

        // Assert
        Assert.Null(exception);
    }

    /// <summary>Rejects an original Different or DataBlocked state after its data proof is invalidated.</summary>
    /// <param name="scenarioOrdinal">The scenario incorrectly retaining its second-cycle live-data proof.</param>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void AssertReport_RejectsOriginalBlockerAfterLiveDataProofInvalidation(
        int scenarioOrdinal
    )
    {
        // Arrange
        var scenarios = CreateScenarios().ToArray();

        scenarios[scenarioOrdinal] = scenarios[scenarioOrdinal] with { RequiresLiveDataProof = true };

        var expectation = new LargeMigrationStressExpectation(scenarios, operationCount: 20);
        var report = CreateReport(CreateGoldenAssessments());

        // Act
        var exception = Record.Exception(() => expectation.AssertReport(report));

        // Assert
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(exception);
    }

    /// <summary>Rejects each independently corrupted field of otherwise valid deferred evidence.</summary>
    /// <param name="field">The single deferred assessment field to corrupt.</param>
    [Theory]
    [InlineData("action")]
    [InlineData("observed_state")]
    [InlineData("postcondition_false")]
    [InlineData("postcondition_true")]
    [InlineData("code")]
    [InlineData("analysis_code")]
    [InlineData("decision_code")]
    [InlineData("impact")]
    [InlineData("origin_ordinal")]
    [InlineData("origin_type")]
    [InlineData("origin_migration")]
    [InlineData("differences")]
    public void AssertReport_RejectsMalformedDeferredEvidence(
        string field
    )
    {
        // Arrange
        var expectation = new LargeMigrationStressExpectation(CreateScenarios(), operationCount: 20);
        var assessments = CreateGoldenAssessments();

        assessments[8] = CorruptDeferredEvidence(assessments[8], field);

        var report = CreateReport(assessments);

        // Act
        var exception = Record.Exception(() => expectation.AssertReport(report));

        // Assert
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(exception);
    }

    /// <summary>A runtime deferral cannot erase a proven blocker or precede the first managed mutation.</summary>
    /// <param name="ordinal">The independently proven assessment to replace with a deferred assessment.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(12)]
    public void AssertReport_RejectsDeferralOfProvenOrUnmutatedState(
        int ordinal
    )
    {
        // Arrange
        var expectation = new LargeMigrationStressExpectation(CreateScenarios(), operationCount: 20);
        var assessments = CreateGoldenAssessments();
        var original = assessments[ordinal];

        assessments[ordinal] = DeferredAssessment(
            ordinal,
            original.OperationKind!.Value,
            original.ObjectName!,
            originOrdinal: ordinal == 12 ? 9 : 0);

        var report = CreateReport(assessments);

        // Act
        var exception = Record.Exception(() => expectation.AssertReport(report));

        // Assert
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(exception);
    }

    /// <summary>The first model-managed update retains its source-state proof and must not be deferred.</summary>
    [Fact]
    public void AssertReport_RejectsDeferralOfFirstManagedUpdate()
    {
        // Arrange
        var expectation = new LargeMigrationStressExpectation(CreateScenarios(updateFirst: true), operationCount: 20);
        var assessments = CreateUpdateFirstGoldenAssessments();

        assessments[7] = DeferredAssessment(
            7,
            SafeMigrationOperationKind.UpdateModelManagedData,
            "managed_update",
            originOrdinal: 6);

        var report = CreateReport(assessments);

        // Act
        var exception = Record.Exception(() => expectation.AssertReport(report));

        // Assert
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(exception);
    }

    /// <summary>Separately pins the constructor boundary for an inconsistent deferred action and origin.</summary>
    /// <param name="runtimeAction">Whether the assessment requests runtime validation.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Assessment_RejectsInconsistentDeferredOrigin(
        bool runtimeAction
    )
    {
        // Arrange
        var action = runtimeAction ? SafeMigrationAction.ValidateAtRuntime : SafeMigrationAction.Apply;
        var origin = runtimeAction
            ? null
            : new SafeMigrationDeferredOrigin(null, 7, typeof(SafeMigrationOperation).FullName!);

        // Act
        var exception = Record.Exception(() => new SafeMigrationAssessment(
            8,
            typeof(SafeMigrationOperation).FullName!,
            isSafeOperation: true,
            SafeMigrationOperationKind.UpdateModelManagedData,
            "managed_update",
            observedState: null,
            action,
            postconditionSatisfied: null,
            "runtime_validation_required",
            "projected_model_managed_data_state_unknown",
            "runtime_validation_required",
            SafeMigrationOperationalImpact.Unknown,
            differences: null,
            origin));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>Allows a bounded prefix without changing the full qualification count.</summary>
    /// <param name="dialect">The provider fixture whose operations are populated.</param>
    [Theory]
    [InlineData((int)LargeMigrationStressDialect.MySql)]
    [InlineData((int)LargeMigrationStressDialect.PostgreSql)]
    [InlineData((int)LargeMigrationStressDialect.SqlServer)]
    public void Populate_PreservesRequestedBoundedOperationCount(
        int dialect
    )
    {
        // Arrange
        var builder = new MigrationBuilder("instrumentation_provider");

        // Act
        var expectation = LargeMigrationStressContract.Populate(
            builder,
            (LargeMigrationStressDialect)dialect,
            operationCount: 20);

        // Assert
        Assert.NotNull(expectation);
        Assert.Equal(20, builder.Operations.Count);
        Assert.Equal(100_000, LargeMigrationStressContract.OperationCount);
    }

    /// <summary>Rejects out-of-contract prefix sizes before adding migration operations.</summary>
    /// <param name="operationCount">The invalid size requested by the fixture.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(100_001)]
    [InlineData(int.MaxValue)]
    public void Populate_RejectsOutOfRangeOperationCountWithoutMutation(
        int operationCount
    )
    {
        // Arrange
        var builder = new MigrationBuilder("instrumentation_provider");

        // Act
        var exception = Record.Exception(() => LargeMigrationStressContract.Populate(
            builder,
            LargeMigrationStressDialect.MySql,
            operationCount));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Empty(builder.Operations);
    }

    /// <summary>Prevents a checker count outside the bounded qualification contract.</summary>
    /// <param name="operationCount">The invalid report size requested by the checker.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(100_001)]
    [InlineData(int.MaxValue)]
    public void Expectation_RejectsOutOfRangeOperationCount(
        int operationCount
    )
    {
        // Arrange
        var scenarios = CreateScenarios();

        // Act
        var exception = Record.Exception(() => new LargeMigrationStressExpectation(
            scenarios,
            operationCount: operationCount));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    private static IReadOnlyList<LargeMigrationStressScenario> CreateScenarios(
        bool updateFirst = false
    ) =>
    [
        new(static (_, _) => { }, static _ => "matching_column", SafeMigrationOperationKind.EnsureColumn,
            SafeMigrationObservedState.Matching, SafeMigrationAction.NoOp, true,
            SafeMigrationOperationalImpact.NotApplicable, false, false),
        new(static (_, _) => { }, static _ => "missing_table", SafeMigrationOperationKind.EnsureTable,
            SafeMigrationObservedState.Missing, SafeMigrationAction.Apply, false,
            SafeMigrationOperationalImpact.NotApplicable, false, false),
        new(static (_, _) => { }, static _ => "different_column", SafeMigrationOperationKind.EnsureColumn,
            SafeMigrationObservedState.Different, SafeMigrationAction.RejectDifferent, false,
            SafeMigrationOperationalImpact.NotApplicable, false, false),
        new(static (_, _) => { }, static _ => "unsupported_index", SafeMigrationOperationKind.EnsureIndex,
            SafeMigrationObservedState.Unsupported, SafeMigrationAction.RejectUnsupported, false,
            SafeMigrationOperationalImpact.NotApplicable, false, false),
        new(static (_, _) => { }, static _ => "blocked_column", SafeMigrationOperationKind.EnsureColumn,
            SafeMigrationObservedState.DataBlocked, SafeMigrationAction.RejectDataBlocked, false,
            SafeMigrationOperationalImpact.NotApplicable, false, false),
        new(static (_, _) => { }, static _ => "dependent_column", SafeMigrationOperationKind.EnsureColumn,
            SafeMigrationObservedState.PrerequisiteMissing, SafeMigrationAction.RejectPrerequisiteMissing, false,
            SafeMigrationOperationalImpact.NotApplicable, false, false),
        new(static (_, _) => { }, static _ => "repaired_column", SafeMigrationOperationKind.AlterColumn,
            SafeMigrationObservedState.Different, SafeMigrationAction.Repair, false,
            SafeMigrationOperationalImpact.TableRewritePossible, false, true),
        new(static (_, _) => { }, _ => updateFirst ? "managed_update" : "managed_ensure",
            updateFirst
                ? SafeMigrationOperationKind.UpdateModelManagedData
                : SafeMigrationOperationKind.EnsureModelManagedData,
            updateFirst ? SafeMigrationObservedState.TransitionReady : SafeMigrationObservedState.Missing,
            SafeMigrationAction.Apply, false, SafeMigrationOperationalImpact.NotApplicable, false, false),
        new(static (_, _) => { }, _ => updateFirst ? "managed_ensure" : "managed_update",
            updateFirst
                ? SafeMigrationOperationKind.EnsureModelManagedData
                : SafeMigrationOperationKind.UpdateModelManagedData,
            updateFirst ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.TransitionReady,
            SafeMigrationAction.Apply, false, SafeMigrationOperationalImpact.NotApplicable, false, false),
        new(static (_, _) => { }, static _ => "managed_delete", SafeMigrationOperationKind.DeleteModelManagedData,
            SafeMigrationObservedState.Missing, SafeMigrationAction.NoOp, true,
            SafeMigrationOperationalImpact.NotApplicable, false, false),
    ];

    private static SafeMigrationAssessment[] CreateGoldenAssessments()
    {
        // WHY: Fixed ordinals, states, and origins prevent the checker and its
        // regression oracle from agreeing on the same projection mistake.
        return
        [
            ProvenAssessment(0, SafeMigrationOperationKind.EnsureColumn, "matching_column",
                SafeMigrationObservedState.Matching, SafeMigrationAction.NoOp, postconditionSatisfied: true),
            ProvenAssessment(1, SafeMigrationOperationKind.EnsureTable, "missing_table",
                SafeMigrationObservedState.Missing, SafeMigrationAction.Apply),
            ProvenAssessment(2, SafeMigrationOperationKind.EnsureColumn, "different_column",
                SafeMigrationObservedState.Different, SafeMigrationAction.RejectDifferent),
            ProvenAssessment(3, SafeMigrationOperationKind.EnsureIndex, "unsupported_index",
                SafeMigrationObservedState.Unsupported, SafeMigrationAction.RejectUnsupported),
            ProvenAssessment(4, SafeMigrationOperationKind.EnsureColumn, "blocked_column",
                SafeMigrationObservedState.DataBlocked, SafeMigrationAction.RejectDataBlocked),
            ProvenAssessment(5, SafeMigrationOperationKind.EnsureColumn, "dependent_column",
                SafeMigrationObservedState.PrerequisiteMissing, SafeMigrationAction.RejectPrerequisiteMissing),
            ProvenAssessment(6, SafeMigrationOperationKind.AlterColumn, "repaired_column",
                SafeMigrationObservedState.Different, SafeMigrationAction.Repair,
                impact: SafeMigrationOperationalImpact.TableRewritePossible),
            ProvenAssessment(7, SafeMigrationOperationKind.EnsureModelManagedData, "managed_ensure",
                SafeMigrationObservedState.Missing, SafeMigrationAction.Apply),
            DeferredAssessment(8, SafeMigrationOperationKind.UpdateModelManagedData, "managed_update", 7),
            DeferredAssessment(9, SafeMigrationOperationKind.DeleteModelManagedData, "managed_delete", 8),
            ProvenAssessment(10, SafeMigrationOperationKind.EnsureColumn, "matching_column",
                SafeMigrationObservedState.Matching, SafeMigrationAction.NoOp, postconditionSatisfied: true),
            ProvenAssessment(11, SafeMigrationOperationKind.EnsureTable, "missing_table",
                SafeMigrationObservedState.Missing, SafeMigrationAction.Apply),
            ProvenAssessment(12, SafeMigrationOperationKind.EnsureColumn, "different_column",
                SafeMigrationObservedState.Different, SafeMigrationAction.RejectDifferent),
            ProvenAssessment(13, SafeMigrationOperationKind.EnsureIndex, "unsupported_index",
                SafeMigrationObservedState.Unsupported, SafeMigrationAction.RejectUnsupported),
            ProvenAssessment(14, SafeMigrationOperationKind.EnsureColumn, "blocked_column",
                SafeMigrationObservedState.DataBlocked, SafeMigrationAction.RejectDataBlocked),
            ProvenAssessment(15, SafeMigrationOperationKind.EnsureColumn, "dependent_column",
                SafeMigrationObservedState.PrerequisiteMissing, SafeMigrationAction.RejectPrerequisiteMissing),
            ProvenAssessment(16, SafeMigrationOperationKind.AlterColumn, "repaired_column",
                SafeMigrationObservedState.Matching, SafeMigrationAction.NoOp, postconditionSatisfied: true),
            DeferredAssessment(17, SafeMigrationOperationKind.EnsureModelManagedData, "managed_ensure", 9),
            DeferredAssessment(18, SafeMigrationOperationKind.UpdateModelManagedData, "managed_update", 17),
            DeferredAssessment(19, SafeMigrationOperationKind.DeleteModelManagedData, "managed_delete", 18),
        ];
    }

    private static SafeMigrationAssessment[] CreateUpdateFirstGoldenAssessments()
    {
        var assessments = CreateGoldenAssessments();

        assessments[7] = ProvenAssessment(7, SafeMigrationOperationKind.UpdateModelManagedData, "managed_update",
            SafeMigrationObservedState.TransitionReady, SafeMigrationAction.Apply);
        assessments[8] = DeferredAssessment(8, SafeMigrationOperationKind.EnsureModelManagedData, "managed_ensure", 7);
        assessments[17] = DeferredAssessment(
            17, SafeMigrationOperationKind.UpdateModelManagedData, "managed_update", 9);
        assessments[18] = DeferredAssessment(
            18, SafeMigrationOperationKind.EnsureModelManagedData, "managed_ensure", 17);

        return assessments;
    }

    private static SafeMigrationAssessment ProvenAssessment(
        int ordinal,
        SafeMigrationOperationKind kind,
        string objectName,
        SafeMigrationObservedState state,
        SafeMigrationAction action,
        bool postconditionSatisfied = false,
        SafeMigrationOperationalImpact impact = SafeMigrationOperationalImpact.NotApplicable
    ) => new(
        ordinal,
        typeof(SafeMigrationOperation).FullName!,
        isSafeOperation: true,
        kind,
        objectName,
        state,
        action,
        postconditionSatisfied,
        "source_state_proven",
        "source_state_proven",
        "source_state_proven",
        impact,
        differences: null,
        deferredOrigin: null);

    private static SafeMigrationAssessment DeferredAssessment(
        int ordinal,
        SafeMigrationOperationKind kind,
        string objectName,
        int originOrdinal
    ) => new(
        ordinal,
        typeof(SafeMigrationOperation).FullName!,
        isSafeOperation: true,
        kind,
        objectName,
        observedState: null,
        SafeMigrationAction.ValidateAtRuntime,
        postconditionSatisfied: null,
        "runtime_validation_required",
        "projected_model_managed_data_state_unknown",
        "runtime_validation_required",
        SafeMigrationOperationalImpact.Unknown,
        differences: null,
        new SafeMigrationDeferredOrigin(null, originOrdinal, typeof(SafeMigrationOperation).FullName!));

    private static SafeMigrationAssessment CorruptDeferredEvidence(
        SafeMigrationAssessment original,
        string field
    ) => new(
        original.Ordinal,
        original.OperationType,
        original.IsSafeOperation,
        original.OperationKind,
        original.ObjectName,
        field == "observed_state" ? SafeMigrationObservedState.TransitionReady : original.ObservedState,
        field == "action" ? SafeMigrationAction.Apply : original.Action,
        field switch
        {
            "postcondition_false" => false,
            "postcondition_true" => true,
            _ => original.PostconditionSatisfied,
        },
        field == "code" ? "matching" : original.Code,
        field == "analysis_code" ? "projected_matching" : original.AnalysisCode,
        field == "decision_code" ? "matching" : original.DecisionCode,
        field == "impact" ? SafeMigrationOperationalImpact.NotApplicable : original.OperationalImpact,
        field == "differences"
            ? [new SafeMigrationFacetDifference("column_nullability", "nullable", "not_nullable")]
            : original.Differences,
        field == "action"
            ? null
            : new SafeMigrationDeferredOrigin(
                field == "origin_migration" ? "202601010001_WrongOrigin" : null,
                field == "origin_ordinal" ? 6 : 7,
                field == "origin_type"
                    ? typeof(AddColumnOperation).FullName!
                    : typeof(SafeMigrationOperation).FullName!));

    private static SafeMigrationRunReport CreateReport(
        IReadOnlyList<SafeMigrationAssessment> assessments
    ) => new(
        SafeMigrationReportMode.Preflight,
        SafeMigrationReportStatus.Blocked,
        DateTimeOffset.UnixEpoch,
        "instrumentation-instance",
        new SafeMigrationProviderEnvironment("npgsql_postgresql", "postgresql", "18.6"),
        targetMigrationId: null,
        $"safe-relational-model:v1:npgsql_postgresql:sha256:{new string('a', 64)}",
        new string('b', 64),
        assessments);
}
