namespace Doka.EntityFrameworkCore.SafeMigrations.Tests.Infrastructure;

public sealed class BenchmarkRunnerTests
{
    [Fact]
    public void CompleteRejectsBudgetsWithoutMeasurements()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"safe-migrations-{Guid.NewGuid():N}.json");
        var runner = BenchmarkRunner.Create(["--output", outputPath], "unused.json", "core");

        var exception = Assert.Throws<InvalidOperationException>(() => runner.Complete());

        Assert.Contains("Performance budgets were not measured", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void MeasureRejectsUnknownAndDuplicateBenchmarkNames()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"safe-migrations-{Guid.NewGuid():N}.json");
        var runner = BenchmarkRunner.Create(["--output", outputPath], "unused.json", "core");

        var unknown = Assert.Throws<InvalidOperationException>(() => runner.Measure("unknown", static () => 0));
        runner.Measure("core_intent_construction_1", static () => 0);
        var duplicate = Assert.Throws<InvalidOperationException>(
            () => runner.Measure("core_intent_construction_1", static () => 0));

        Assert.Contains("No performance budget exists", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("measured more than once", duplicate.Message, StringComparison.Ordinal);
    }
}
