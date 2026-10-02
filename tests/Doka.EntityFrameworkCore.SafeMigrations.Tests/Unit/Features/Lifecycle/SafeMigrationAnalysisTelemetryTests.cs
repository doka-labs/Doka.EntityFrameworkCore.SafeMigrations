namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies bounded analysis scopes without observing unrelated parallel test traces.</summary>
public sealed class SafeMigrationAnalysisTelemetryTests
{
    /// <summary>Emits only fixed stage metadata and the bounded operation count.</summary>
    /// <param name="stage">One supported fixed phase identifier.</param>
    /// <param name="operationCount">The number of operations covered by the phase.</param>
    [Theory]
    [InlineData("provider-classification", 100_000)]
    [InlineData("ordered-projection", 100_000)]
    [InlineData("unexpected-inventory", 100_000)]
    [InlineData("provider-baseline", 100_000)]
    [InlineData("catalog-classification", 512)]
    [InlineData("catalog-batch", 256)]
    [InlineData("catalog-batch", 0)]
    public void AnalysisStageContainsOnlyFixedPrivacySafeTags(string stage, int operationCount)
    {
        // Arrange
        using var parent = new Activity("analysis-telemetry-test").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var listener = CreateListener(parent.TraceId);

        // Act
        using var activity = SafeMigrationTelemetry.StartAnalysisStage(stage, operationCount);

        // Assert
        Assert.NotNull(activity);
        Assert.Equal(SafeMigrationTelemetry.AnalysisStageActivityName, activity.OperationName);
        Assert.Equal(ActivityKind.Internal, activity.Kind);
        Assert.Equal(parent.TraceId, activity.TraceId);
        Assert.Equal(parent.SpanId, activity.ParentSpanId);
        Assert.Equal(2, activity.TagObjects.Count());
        Assert.Equal(stage, activity.GetTagItem("safe_migrations.analysis.stage"));
        Assert.Equal(operationCount, activity.GetTagItem("safe_migrations.operation_count"));
    }

    /// <summary>Rejects arbitrary or sensitive phase values rather than adding them to tracing.</summary>
    /// <param name="stage">An unrecognized or absent stage identifier.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("catalog-batch\nforged")]
    [InlineData("private-object-name")]
    public void AnalysisStageRejectsUnrecognizedIdentifiersWithoutEchoingThem(string? stage)
    {
        // Arrange
        using var parent = new Activity("analysis-telemetry-test").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var listener = CreateListener(parent.TraceId);

        // Act
        var exception = Assert.Throws<ArgumentException>(() =>
            SafeMigrationTelemetry.StartAnalysisStage(stage!, operationCount: 1));

        // Assert
        Assert.Equal("stage", exception.ParamName);
        Assert.StartsWith("The analysis stage is not a recognized fixed identifier.", exception.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("private-object-name", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("forged", exception.Message, StringComparison.Ordinal);
        Assert.Same(parent, Activity.Current);
    }

    /// <summary>Rejects negative counts before starting an activity.</summary>
    [Fact]
    public void AnalysisStageRejectsNegativeOperationCounts()
    {
        // Arrange
        using var parent = new Activity("analysis-telemetry-test").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var listener = CreateListener(parent.TraceId);

        // Act
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SafeMigrationTelemetry.StartAnalysisStage("catalog-batch", operationCount: -1));

        // Assert
        Assert.Equal("operationCount", exception.ParamName);
        Assert.Same(parent, Activity.Current);
    }

    /// <summary>Does not allocate a scope when listeners reject the current trace.</summary>
    [Fact]
    public void AnalysisStageRemainsUnsampledOutsideTheListenerTrace()
    {
        // Arrange
        using var parent = new Activity("analysis-telemetry-test").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var listener = CreateListener(ActivityTraceId.CreateRandom());

        // Act
        using var activity = SafeMigrationTelemetry.StartAnalysisStage("catalog-batch", operationCount: 1);

        // Assert
        Assert.Null(activity);
        Assert.Same(parent, Activity.Current);
    }

    /// <summary>Preserves the parent chain and records scope completion in nested analysis phases.</summary>
    [Fact]
    public void AnalysisStageNestingRestoresTheParentAndCompletesEveryScope()
    {
        // Arrange
        using var parent = new Activity("analysis-telemetry-test").SetIdFormat(ActivityIdFormat.W3C).Start();
        var completed = new List<Activity>();
        using var listener = CreateListener(parent.TraceId, completed.Add);
        Activity outer;
        Activity inner;

        // Act
        using (outer = SafeMigrationTelemetry.StartAnalysisStage("provider-classification", 512)!)
        {
            using (inner = SafeMigrationTelemetry.StartAnalysisStage("catalog-batch", 32)!)
            {
                inner.SetTag("safe_migrations.catalog.statement_count", 1);
            }
        }

        // Assert
        Assert.Same(parent, Activity.Current);
        Assert.Equal([inner, outer], completed);
        Assert.Equal(outer.SpanId, inner.ParentSpanId);
        Assert.Equal(parent.SpanId, outer.ParentSpanId);
        Assert.True(outer.IsStopped);
        Assert.True(inner.IsStopped);
        Assert.True(outer.Duration >= inner.Duration);
        Assert.Equal(1, inner.GetTagItem("safe_migrations.catalog.statement_count"));
    }

    /// <summary>Exposes phase identifiers to start callbacks before a long-running phase can stall.</summary>
    [Fact]
    public void AnalysisStageTagsAreAvailableAtStart()
    {
        // Arrange
        using var parent = new Activity("analysis-telemetry-test").SetIdFormat(ActivityIdFormat.W3C).Start();
        object? startedStage = null;
        object? startedOperationCount = null;
        using var listener = CreateListener(parent.TraceId, started: activity =>
        {
            startedStage = activity.GetTagItem("safe_migrations.analysis.stage");
            startedOperationCount = activity.GetTagItem("safe_migrations.operation_count");
        });

        // Act
        using var activity = SafeMigrationTelemetry.StartAnalysisStage("provider-baseline", 100_000);

        // Assert
        Assert.NotNull(activity);
        Assert.Equal("provider-baseline", startedStage);
        Assert.Equal(100_000, startedOperationCount);
    }

    /// <summary>Samples runner and stage scopes under the qualification trace without an explicit run parent.</summary>
    [Fact]
    public void AnalysisStageInheritsTheRunnerTrace()
    {
        // Arrange
        using var parent = new Activity("analysis-telemetry-test").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var listener = CreateListener(parent.TraceId);

        // Act
        using var run = SafeMigrationTelemetry.ActivitySource.StartActivity(SafeMigrationDiagnostics.RunActivityName,
            ActivityKind.Internal, parentContext: default);

        using var stage = SafeMigrationTelemetry.StartAnalysisStage("ordered-projection", 100_000);

        // Assert
        Assert.NotNull(run);
        Assert.NotNull(stage);
        Assert.Equal(parent.TraceId, run.TraceId);
        Assert.Equal(run.TraceId, stage.TraceId);
        Assert.Equal(run.SpanId, stage.ParentSpanId);
    }

    /// <summary>Samples only the current test's trace so parallel tests never share captures.</summary>
    private static ActivityListener CreateListener(
        ActivityTraceId traceId,
        Action<Activity>? completed = null,
        Action<Activity>? started = null
    )
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == SafeMigrationDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                options.Parent.TraceId == traceId ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
            ActivityStarted = activity =>
            {
                if (activity.TraceId == traceId)
                {
                    started?.Invoke(activity);
                }
            },
            ActivityStopped = activity =>
            {
                if (activity.TraceId == traceId)
                {
                    completed?.Invoke(activity);
                }
            },
        };

        ActivitySource.AddActivityListener(listener);

        return listener;
    }
}
