return BenchmarkProgram.Run(
    args,
    typeof(CoreColumnBenchmarks),
    typeof(CoreReportViewBenchmarks),
    typeof(CoreModelManagedDataBenchmarks));
