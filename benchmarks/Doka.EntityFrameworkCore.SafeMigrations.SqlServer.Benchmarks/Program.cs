if (args is ["--live-catalog", .. var liveCatalogArgs])
{
    return await BenchmarkProgram.RunAsync(
        liveCatalogArgs,
        [
            typeof(SqlServerLiveCatalogBenchmarks),
        ],
        CancellationToken.None);
}

if (args is ["--maintenance-attribution", .. var maintenanceArgs])
{
    return await BenchmarkProgram.RunAsync(
        maintenanceArgs,
        [
            typeof(SqlServerMaintenanceAttribution),
        ],
        CancellationToken.None);
}

if (args is ["--column-attribution", .. var columnArgs])
{
    return await BenchmarkProgram.RunAsync(
        columnArgs,
        [
            typeof(SqlServerColumnAttribution),
            typeof(SqlServerGuardGenerationBenchmarks),
        ],
        CancellationToken.None);
}

return await BenchmarkProgram.RunAsync(
    args,
    [
        typeof(SqlServerGenerationBenchmarks),
        typeof(SqlServerAnalyzerBenchmarks),
        typeof(SqlServerRepairBenchmarks),
        typeof(SqlServerModelManagedDataBenchmarks),
        typeof(SqlServerCanonicalModelBenchmarks),
        typeof(SqlServerCatalogCommandAssemblyBenchmarks),
        typeof(SqlServerComputedDropCaptureBenchmarks),
        typeof(SqlServerMatchingTransitionCaptureBenchmarks),
    ],
    CancellationToken.None);
