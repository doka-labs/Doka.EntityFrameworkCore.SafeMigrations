var runner = BenchmarkRunner.Create(args, "core-results.json", "core");

foreach (var size in new[] { 1, 100, 1000 })
{
    var report = ColumnBenchmarkWorkload.CreateReport(size);

    runner.Measure(
        $"core_intent_construction_{size}",
        () => ColumnBenchmarkWorkload.CreateOperations(size)
            .Count);
    runner.Measure($"decision_planner_{size}", () => ColumnBenchmarkWorkload.RunPlanner(size));
    runner.Measure(
        $"report_json_{size}",
        () => SafeMigrationReportJson.SerializeToUtf8Bytes(report)
            .Length);
}

var blockingReportViewWorkload = ColumnBenchmarkWorkload.CreateBlockingReportViewWorkload(
    count: 50_000,
    blockerCount: 8);

runner.Measure(
    "report_view_blocking_50000",
    () => SafeMigrationReportJson.SerializeToUtf8Bytes(
            blockingReportViewWorkload,
            SafeMigrationReportSelection.BlockingOnly)
        .Length);

runner.Measure(
    "model_data_intent_construction_384",
    () => ModelManagedDataBenchmarkWorkload.CreateOperations(
            "benchmark",
            "integer",
            "varchar(64)")
        .Count);

var modelManagedOperations = ModelManagedDataBenchmarkWorkload.CreateOperations(
    "benchmark",
    "integer",
    "varchar(64)");

runner.Measure(
    "model_data_contract_fingerprint_384",
    () => SafeMigrationContractFingerprint.Create(modelManagedOperations)
        .Length);

return runner.Complete();
