using var context = new SqliteGenerationContext();

var generator = context.GetService<IMigrationsSqlGenerator>();
var runner = BenchmarkRunner.Create(args, "sqlite-results.json", "sqlite");

foreach (var size in new[] { 1, 100, 1000 })
{
    var operations = ColumnBenchmarkWorkload.CreateOperations(size);

    runner.Measure(
        $"sqlite_generation_{size}",
        () => generator.Generate(operations, context.Model)
            .Count);
}

runner.Measure(
    "sqlite_canonical_model_validation",
    () => SafeMigrationRunner.ValidateCanonicalMigrationModelAndCreateFingerprint(
            context,
            "Microsoft.EntityFrameworkCore.Sqlite")
        .Length);

var repairOperations = ColumnBenchmarkWorkload.CreateRepairOperations(1000);
runner.Measure(
    "sqlite_repair_generation_1000",
    () => generator.Generate(repairOperations, context.Model)
        .Count);

var modelManagedOperations = ModelManagedDataBenchmarkWorkload.CreateOperations(
    context.Database.ProviderName!,
    "INTEGER",
    "TEXT");

runner.Measure(
    "sqlite_model_data_generation_384",
    () => generator.Generate(modelManagedOperations, context.Model)
        .Count);

using var runtimeConnection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
runtimeConnection.Open();
CreateRuntimeCatalog(runtimeConnection, 200);
using var runtimeContext = new SqliteGenerationContext(runtimeConnection);
var runtimeRunner = runtimeContext.GetService<ISafeMigrationRunner>();
runner.Measure(
    "sqlite_model_data_analysis_384_tables_200",
    () => runtimeRunner
        .AnalyzeAsync(
            runtimeContext,
            modelManagedOperations,
            new SafeMigrationRunOptions("sqlite-runtime-benchmark"))
        .GetAwaiter()
        .GetResult()
        .Assessments
        .Count);

using var relationalRuntimeConnection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
relationalRuntimeConnection.Open();
CreateRuntimeCatalog(relationalRuntimeConnection, 1000, includeRelationalObjects: true);
using var relationalRuntimeContext = new SqliteGenerationContext(relationalRuntimeConnection);
var relationalRuntimeRunner = relationalRuntimeContext.GetService<ISafeMigrationRunner>();
runner.Measure(
    "sqlite_model_data_analysis_384_tables_1000_relational",
    () => relationalRuntimeRunner
        .AnalyzeAsync(
            relationalRuntimeContext,
            modelManagedOperations,
            new SafeMigrationRunOptions("sqlite-relational-runtime-benchmark"))
        .GetAwaiter()
        .GetResult()
        .Assessments
        .Count);

return runner.Complete();

static void CreateRuntimeCatalog(
    SqliteConnection connection,
    int tableCount,
    bool includeRelationalObjects = false
)
{
    using var transaction = connection.BeginTransaction();
    using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "CREATE TABLE benchmark_model_data ("
        + "id INTEGER NOT NULL PRIMARY KEY, managed_value TEXT NOT NULL);";
    _ = command.ExecuteNonQuery();

    for (var index = 1; index < tableCount; index++)
    {
        command.CommandText = includeRelationalObjects
            ? $"CREATE TABLE benchmark_catalog_{index} ("
                + "id INTEGER NOT NULL PRIMARY KEY, parent_id INTEGER NULL, "
                + "FOREIGN KEY (parent_id) REFERENCES benchmark_model_data(id));"
            : $"CREATE TABLE benchmark_catalog_{index} (id INTEGER NOT NULL PRIMARY KEY);";
        _ = command.ExecuteNonQuery();

        if (includeRelationalObjects)
        {
            command.CommandText = $"CREATE INDEX ix_benchmark_catalog_{index}_parent "
                + $"ON benchmark_catalog_{index}(parent_id);";
            _ = command.ExecuteNonQuery();
        }
    }

    transaction.Commit();
}

internal sealed class SqliteGenerationContext : DbContext
{
    private readonly SqliteConnection? _connection;

    public SqliteGenerationContext()
    { }

    public SqliteGenerationContext(
        SqliteConnection connection
    ) => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

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
