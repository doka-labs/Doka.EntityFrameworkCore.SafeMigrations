if (args is ["--maintenance-attribution", .. var maintenanceArgs])
{
    return SqliteMaintenanceAttribution.Run(maintenanceArgs);
}

return BenchmarkProgram.Run(
    args,
    typeof(SqliteGenerationBenchmarks),
    typeof(SqliteMigrationBenchmarks),
    typeof(SqliteModelManagedDataAnalysisBenchmarks));

internal sealed class SqliteGenerationContext : DbContext
{
    private readonly SqliteConnection? _connection;

    public SqliteGenerationContext()
    { }

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
internal sealed class SqliteGenerationContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(
        ModelBuilder modelBuilder
    ) => ArgumentNullException.ThrowIfNull(modelBuilder);
}
