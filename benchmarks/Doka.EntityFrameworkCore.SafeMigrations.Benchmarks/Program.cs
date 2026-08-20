var runner = BenchmarkRunner.Create(args, "core-results.json");

foreach (var size in new[] { 1, 100, 1000 })
{
    var report = ColumnBenchmarkWorkload.CreateReport(size);

    runner.Measure(
        $"core_intent_construction_{size}",
        () => ColumnBenchmarkWorkload.CreateOperations(size)
            .Count);
    runner.Measure(
        $"decision_planner_{size}",
        () => ColumnBenchmarkWorkload.RunPlanner(size));
    runner.Measure(
        $"report_json_{size}",
        () => SafeMigrationReportJson.SerializeToUtf8Bytes(report)
            .Length);
}

return runner.Complete();
