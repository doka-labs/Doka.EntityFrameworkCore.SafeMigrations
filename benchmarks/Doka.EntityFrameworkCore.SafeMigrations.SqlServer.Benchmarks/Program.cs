if (args is ["--maintenance-attribution", .. var maintenanceArgs])
{
    return BenchmarkProgram.Run(maintenanceArgs, typeof(SqlServerMaintenanceAttribution));
}

if (args is ["--column-attribution", .. var columnArgs])
{
    return BenchmarkProgram.Run(
        columnArgs,
        typeof(SqlServerColumnAttribution),
        typeof(SqlServerGuardGenerationBenchmarks));
}

return BenchmarkProgram.Run(
    args,
    typeof(SqlServerGenerationBenchmarks),
    typeof(SqlServerAnalyzerBenchmarks),
    typeof(SqlServerRepairBenchmarks),
    typeof(SqlServerModelManagedDataBenchmarks),
    typeof(SqlServerCanonicalModelBenchmarks),
    typeof(SqlServerCatalogCommandAssemblyBenchmarks));
