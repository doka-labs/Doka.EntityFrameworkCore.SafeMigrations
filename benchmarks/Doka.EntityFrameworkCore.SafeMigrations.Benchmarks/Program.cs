return await BenchmarkProgram.RunAsync(
    args,
    [
        typeof(CoreColumnBenchmarks),
        typeof(CoreReportViewBenchmarks),
        typeof(CoreModelManagedDataBenchmarks),
        typeof(CoreProjectionBenchmarks),
        typeof(CoreEvidenceBenchmarks),
        typeof(CoreDeepEqualityBenchmarks),
    ],
    CancellationToken.None);
