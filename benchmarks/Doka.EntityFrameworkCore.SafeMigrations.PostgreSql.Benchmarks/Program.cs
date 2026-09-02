using var context = new PostgreSqlGenerationContext();

var generator = context.GetService<IMigrationsSqlGenerator>();
var catalogBuilder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
    context.GetService<IRelationalTypeMappingSource>(),
    context.GetService<ISqlGenerationHelper>());

var runner = BenchmarkRunner.Create(args, "postgresql-results.json", "postgresql");

foreach (var size in new[] { 1, 100, 1000 })
{
    var operations = ColumnBenchmarkWorkload.CreateOperations(size);

    runner.Measure(
        $"postgresql_generation_{size}",
        () => generator.Generate(operations, context.Model)
            .Count);

    if (size is 1 or 1000)
    {
        runner.Measure($"postgresql_analyzer_build_{size}", () => BuildPlans(catalogBuilder, operations));
    }
}

runner.Measure(
    "postgresql_canonical_model_validation",
    () => SafeMigrationRunner.ValidateCanonicalMigrationModelAndCreateFingerprint(
            context,
            "Npgsql.EntityFrameworkCore.PostgreSQL")
        .Length);

var analyzerOperations = ColumnBenchmarkWorkload.CreateOperations(512);
runner.Measure("postgresql_analyzer_build_512", () => BuildPlans(catalogBuilder, analyzerOperations));

var repairOperations = ColumnBenchmarkWorkload.CreateRepairOperations(1000);
runner.Measure(
    "postgresql_repair_generation_1000",
    () => generator.Generate(repairOperations, context.Model)
        .Count);

runner.Measure(
    "postgresql_repair_analyzer_build_1000",
    () => BuildPlans(catalogBuilder, repairOperations));

var modelManagedOperations = ModelManagedDataBenchmarkWorkload.CreateOperations(
    context.Database.ProviderName!,
    "integer",
    "character varying(64)");

runner.Measure(
    "postgresql_model_data_generation_384",
    () => generator.Generate(modelManagedOperations, context.Model)
        .Count);
runner.Measure(
    "postgresql_model_data_analyzer_build_384",
    () => BuildPlans(catalogBuilder, modelManagedOperations));

return runner.Complete();

static int BuildPlans(
    PostgreSqlSafeMigrationCatalogSqlBuilder catalogBuilder,
    IReadOnlyList<MigrationOperation> operations
)
{
    var count = 0;
    foreach (var operation in operations.Cast<SafeMigrationOperation>())
    {
        _ = catalogBuilder.Build(operation);
        count++;
    }

    return count;
}

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
