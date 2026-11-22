if (args is ["--maintenance-attribution", .. var maintenanceArgs])
{
    return await SqliteMaintenanceAttribution.RunAsync(maintenanceArgs, CancellationToken.None);
}

return await BenchmarkProgram.RunAsync(
    args,
    [
        typeof(SqliteGenerationBenchmarks),
        typeof(SqliteMigrationBenchmarks),
        typeof(SqliteModelManagedDataAnalysisBenchmarks),
    ],
    CancellationToken.None);

internal sealed class SqliteGenerationContext : DbContext
{
    private readonly SqliteConnection? _connection;

    public SqliteGenerationContext() { }

    public SqliteGenerationContext(
        SqliteConnection connection
    )
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        if (_connection is null)
        {
            optionsBuilder.UseSqlite("Data Source=:memory:");
        }
        else
        {
            optionsBuilder.UseSqlite(_connection);
        }

        optionsBuilder.UseSqliteSafeMigrations();
    }
}

[DbContext(typeof(SqliteGenerationContext))]
// WHY: EF discovers this target-model snapshot through its DbContext attribute.
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed class SqliteGenerationContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(
        ModelBuilder modelBuilder
    ) => ArgumentNullException.ThrowIfNull(modelBuilder);
}
