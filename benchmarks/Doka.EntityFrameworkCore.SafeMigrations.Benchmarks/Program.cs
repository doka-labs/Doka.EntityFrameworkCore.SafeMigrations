return BenchmarkProgram.Run(
    args,
    typeof(CoreColumnBenchmarks),
    typeof(CoreReportViewBenchmarks),
    typeof(CoreModelManagedDataBenchmarks),
    typeof(CoreProjectionBenchmarks),
    typeof(CoreEvidenceBenchmarks));
