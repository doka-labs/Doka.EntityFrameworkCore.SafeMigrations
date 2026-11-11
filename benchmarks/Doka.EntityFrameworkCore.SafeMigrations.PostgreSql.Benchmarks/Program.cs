if (args is ["--maintenance-attribution", .. var maintenanceArgs])
{
    return PostgreSqlMaintenanceAttribution.Run(maintenanceArgs);
}

if (args is ["--column-attribution", .. var columnArgs])
{
    return PostgreSqlColumnAttributionWorkload.Run(columnArgs);
}

return BenchmarkProgram.Run(
    args,
    typeof(PostgreSqlGenerationBenchmarks),
    typeof(PostgreSqlAnalyzerBenchmarks),
    typeof(PostgreSqlMigrationBenchmarks),
    typeof(PostgreSqlAllocationBenchmarks),
    typeof(PostgreSqlPlaceholderBenchmarks),
    typeof(PostgreSqlGuardBenchmarks));

internal sealed class PostgreSqlGenerationContext : DbContext
{
    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseNpgsql("Host=127.0.0.1;Port=1;Username=benchmark;Password=benchmark;Database=benchmark");
        optionsBuilder.UsePostgreSqlSafeMigrations();
    }
}

[DbContext(typeof(PostgreSqlGenerationContext))]
internal sealed class PostgreSqlGenerationContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(
        ModelBuilder modelBuilder
    ) => ArgumentNullException.ThrowIfNull(modelBuilder);
}
