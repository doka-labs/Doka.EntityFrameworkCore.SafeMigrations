if (args is ["--maintenance-attribution", .. var maintenanceArgs])
{
    return await PostgreSqlMaintenanceAttribution.RunAsync(maintenanceArgs, CancellationToken.None);
}

if (args is ["--column-attribution", .. var columnArgs])
{
    return await PostgreSqlColumnAttributionWorkload.RunAsync(columnArgs, CancellationToken.None);
}

if (args is ["--live-catalog", .. var liveCatalogArgs])
{
    return await BenchmarkProgram.RunAsync(
        liveCatalogArgs,
        [
            typeof(PostgreSqlLiveCatalogBenchmarks),
        ],
        CancellationToken.None);
}

if (args is ["--live-column-attribution", .. var liveColumnArgs])
{
    return await BenchmarkProgram.RunAsync(
        liveColumnArgs,
        [
            typeof(PostgreSqlLiveColumnAttributionBenchmarks),
        ],
        CancellationToken.None);
}

return await BenchmarkProgram.RunAsync(
    args,
    [
        typeof(PostgreSqlGenerationBenchmarks),
        typeof(PostgreSqlAnalyzerBenchmarks),
        typeof(PostgreSqlMigrationBenchmarks),
        typeof(PostgreSqlAllocationBenchmarks),
        typeof(PostgreSqlPlaceholderBenchmarks),
        typeof(PostgreSqlGuardBenchmarks),
    ],
    CancellationToken.None);

internal sealed class PostgreSqlGenerationContext : DbContext
{
    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        // WHY: Retain the declared PostgreSQL 14 floor rather than Npgsql 11's default target of 16.
        optionsBuilder.UseNpgsql(
            "Host=127.0.0.1;Port=1;Username=benchmark;Password=benchmark;Database=benchmark",
            provider => provider.SetPostgresVersion(14, 0));
        optionsBuilder.UsePostgreSqlSafeMigrations();
    }
}

[DbContext(typeof(PostgreSqlGenerationContext))]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed class PostgreSqlGenerationContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(
        ModelBuilder modelBuilder
    ) => ArgumentNullException.ThrowIfNull(modelBuilder);
}
