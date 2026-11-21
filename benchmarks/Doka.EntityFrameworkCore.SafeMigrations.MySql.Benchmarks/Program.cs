if (args is ["--maintenance-attribution", .. var maintenanceArgs])
{
    return await MySqlMaintenanceAttribution.RunAsync(maintenanceArgs, CancellationToken.None);
}

if (args is ["--live-catalog", .. var catalogArgs])
{
    return await BenchmarkProgram.RunAsync(
        catalogArgs,
        [
            typeof(MySqlLiveCatalogBenchmarks),
        ],
        CancellationToken.None);
}

if (args is ["--live-runtime", .. var runtimeArgs])
{
    return await BenchmarkProgram.RunAsync(
        runtimeArgs,
        [
            typeof(MySqlLiveRuntimeBenchmarks),
            typeof(MySqlLiveRuntimeGenerationBenchmarks),
        ],
        CancellationToken.None);
}

return await BenchmarkProgram.RunAsync(
    args,
    [
        typeof(MySqlGenerationBenchmarks),
        typeof(MySqlAllocationBenchmarks),
        typeof(MySqlGeneratedDependencyCatalogBenchmarks),
        typeof(MySqlAnalyzerBenchmarks),
        typeof(MySqlMigrationBenchmarks),
        typeof(MySqlModelFingerprintBenchmarks),
    ],
    CancellationToken.None);

internal sealed class MySqlGenerationContext : DbContext
{
    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=benchmark;Password=benchmark;Database=benchmark;"
            + "Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));

        optionsBuilder.UseMySqlSafeMigrations();
    }
}

[DbContext(typeof(MySqlGenerationContext))]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed class MySqlGenerationContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(
        ModelBuilder modelBuilder
    ) => ArgumentNullException.ThrowIfNull(modelBuilder);
}

internal sealed class MySqlFingerprintContext<TMarker> : DbContext
{
    private readonly int _entityCount;

    public MySqlFingerprintContext(
        int entityCount
    )
    {
        _entityCount = entityCount;
    }

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    ) => optionsBuilder.UseMySql(
        "Server=127.0.0.1;Port=1;User ID=benchmark;Password=benchmark;Database=benchmark",
        MySqlServerVersion.MySql(new Version(8, 4, 11)));

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        for (var entityIndex = 0; entityIndex < _entityCount; entityIndex++)
        {
            var entityName = $"FingerprintEntity{entityIndex.ToString("D4", CultureInfo.InvariantCulture)}";
            modelBuilder.SharedTypeEntity<Dictionary<string, object>>(
                entityName,
                entity =>
                {
                    entity.ToTable(entityName);
                    entity.IndexerProperty<int>("Id");
                    entity.HasKey("Id");

                    for (var propertyIndex = 1; propertyIndex < 10; propertyIndex++)
                    {
                        entity
                            .IndexerProperty<string>($"Value{propertyIndex}")
                            .HasMaxLength(80);
                    }
                });
        }
    }
}

internal sealed class Fingerprint10;

internal sealed class Fingerprint100;

internal sealed class Fingerprint1000;
