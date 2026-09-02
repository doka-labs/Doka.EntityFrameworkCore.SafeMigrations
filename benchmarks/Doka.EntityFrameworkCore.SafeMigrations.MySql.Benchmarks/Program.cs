using var context = new MySqlGenerationContext();

var generator = context.GetService<IMigrationsSqlGenerator>();
var planCapture = context.GetService<MySqlSafeMigrationPlanCapture>();
var runner = BenchmarkRunner.Create(args, "mysql-results.json", "mysql");

foreach (var size in new[] { 1, 100, 1000 })
{
    var operations = ColumnBenchmarkWorkload.CreateOperations(size);

    runner.Measure(
        $"mysql_generation_{size}",
        () => generator.Generate(operations, context.Model)
            .Count);

    if (size is 1 or 1000)
    {
        runner.Measure(
            $"mysql_analyzer_build_{size}",
            () => CapturePlans(generator, planCapture, context.Model, operations));
    }
}

runner.Measure(
    "mysql_canonical_model_validation",
    () => SafeMigrationRunner.ValidateCanonicalMigrationModelAndCreateFingerprint(
            context,
            "Doka.EntityFrameworkCore.MySql")
        .Length);

var analyzerOperations = ColumnBenchmarkWorkload.CreateOperations(512);
runner.Measure(
    "mysql_analyzer_build_512",
    () => CapturePlans(generator, planCapture, context.Model, analyzerOperations));

var repairOperations = ColumnBenchmarkWorkload.CreateRepairOperations(1000);
runner.Measure(
    "mysql_repair_generation_1000",
    () => generator.Generate(repairOperations, context.Model)
        .Count);

runner.Measure(
    "mysql_repair_analyzer_build_1000",
    () => CapturePlans(generator, planCapture, context.Model, repairOperations));

var modelManagedOperations = ModelManagedDataBenchmarkWorkload.CreateOperations(
    context.Database.ProviderName!,
    "int",
    "varchar(64)");

runner.Measure(
    "mysql_model_data_generation_384",
    () => generator.Generate(modelManagedOperations, context.Model)
        .Count);
runner.Measure(
    "mysql_model_data_analyzer_build_384",
    () => CapturePlans(generator, planCapture, context.Model, modelManagedOperations));

using var fingerprint10 = new MySqlFingerprintContext<Fingerprint10>(10);
using var fingerprint100 = new MySqlFingerprintContext<Fingerprint100>(100);
using var fingerprint1000 = new MySqlFingerprintContext<Fingerprint1000>(1000);
var fingerprintModel10 = fingerprint10.GetService<IDesignTimeModel>().Model;
var fingerprintModel100 = fingerprint100.GetService<IDesignTimeModel>().Model;
var fingerprintModel1000 = fingerprint1000.GetService<IDesignTimeModel>().Model;

runner.Measure(
    "model_fingerprint_10x10",
    () => SafeMigrationModelFingerprint.Create(fingerprintModel10, "doka_mysql")
        .Length);
runner.Measure(
    "model_fingerprint_100x10",
    () => SafeMigrationModelFingerprint.Create(fingerprintModel100, "doka_mysql")
        .Length);
runner.Measure(
    "model_fingerprint_1000x10",
    () => SafeMigrationModelFingerprint.Create(fingerprintModel1000, "doka_mysql")
        .Length);

return runner.Complete();

static int CapturePlans(
    IMigrationsSqlGenerator generator,
    MySqlSafeMigrationPlanCapture planCapture,
    IModel model,
    IReadOnlyList<MigrationOperation> operations
)
{
    var safeOperations = operations
        .Cast<SafeMigrationOperation>()
        .ToArray();

    using var lease = planCapture.Begin(safeOperations);
    _ = generator.Generate(operations, model);

    return lease.Complete()
        .Length;
}

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
